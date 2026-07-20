// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	// Guest entry stubs must not run above CLR-managed frames on a CLR-created thread:
	// the suspension/stack-walk machinery then has to traverse a frame chain that is
	// interleaved with guest stubs carrying no CLR unwind info, and any window in which
	// the thread-mode bookkeeping disagrees with the actual stack (import dispatch, VEH
	// redirection) fail-fasts the runtime — the audio_output_thread ~7th-cycle
	// "attempted to call a UnmanagedCallersOnly method from managed code" crash.
	//
	// Guest execution therefore runs on dedicated raw OS threads whose run loop is
	// emitted native code. While guest code executes there is not a single managed
	// frame on the thread and it sits in preemptive mode, so the GC ignores it
	// entirely. The only managed activity on the thread is the reverse-P/Invoke import
	// dispatch and the per-run prologue/epilogue, which the CLR supports on foreign
	// threads (lazy attach, preemptive between calls). The managed orchestrator
	// (RunGuestThread / the main execution thread) parks in a preemptive wait for the
	// duration of the run, so its own frame chain is never walked across guest frames.
	private static readonly bool NativeGuestWorkersDisabled =
		string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS"), "1", StringComparison.Ordinal);

	private readonly object _nativeWorkerGate = new();
	private readonly List<NativeGuestExecutor> _allNativeWorkers = new();
	private readonly Stack<NativeGuestExecutor> _idleNativeWorkers = new();
	private bool _nativeWorkersDisposed;
	private int _nativeWorkerCreationFailedLogged;

	// Runs an emitted guest entry stub on a pooled native worker. The historical
	// inline calli remains available only through the explicit diagnostic override;
	// silently using it after worker creation fails can fail-fast the CLR process.
	//
	// Callers set the Active* thread-statics before emitting the stub and read the
	// yield/forced-exit flags right after this returns, so the worker outcome is
	// copied back into this thread's statics before returning.
	private unsafe ulong RunGuestEntryStub(void* entryStub, ulong hostRspSlot)
	{
		if (NativeGuestWorkersDisabled)
		{
			_activeGuestHardwareExceptionRip = 0;
			_activeGuestHardwareExceptionCode = 0;
			_activeGuestHardwareExceptionAccessType = 0;
			_activeGuestHardwareExceptionAccessAddress = 0;
			_activeGuestHardwareExceptionRegisters = null;
			_hostThreading.SetTlsValue(_hostRspSlotTlsIndex, (nint)hostRspSlot);
			return CallNativeEntry(entryStub);
		}

		var worker = RentNativeGuestExecutor();
		if (worker is null)
		{
			throw new InvalidOperationException(
				"Failed to create a native guest worker thread; unsafe inline execution was refused.");
		}
		try
		{
			var state = _activeGuestThreadState;
			var nativeReturn = worker.Run(
				_activeCpuContext!,
				state,
				GuestThreadExecution.CurrentGuestThreadHandle,
				_activeEntryReturnSentinelRip,
				_activeGuestReturnSlotAddress,
				(nint)hostRspSlot,
				(nint)entryStub,
				state?.AffinityMask ?? 0,
				out var yieldRequested,
				out var yieldReason,
				out var forcedExit,
				out var hardwareExceptionRip,
				out var hardwareExceptionCode,
				out var hardwareExceptionAccessType,
				out var hardwareExceptionAccessAddress,
				out var hardwareExceptionRegisters);
			_activeGuestThreadYieldRequested = yieldRequested;
			_activeGuestThreadYieldReason = yieldReason;
			_activeForcedGuestExit = forcedExit;
			_activeGuestHardwareExceptionRip = hardwareExceptionRip;
			_activeGuestHardwareExceptionCode = hardwareExceptionCode;
			_activeGuestHardwareExceptionAccessType = hardwareExceptionAccessType;
			_activeGuestHardwareExceptionAccessAddress = hardwareExceptionAccessAddress;
			_activeGuestHardwareExceptionRegisters = hardwareExceptionRegisters;
			return nativeReturn;
		}
		finally
		{
			ReturnNativeGuestExecutor(worker);
		}
	}

	private NativeGuestExecutor? RentNativeGuestExecutor()
	{
		lock (_nativeWorkerGate)
		{
			if (_nativeWorkersDisposed)
			{
				return null;
			}
			if (_idleNativeWorkers.Count > 0)
			{
				return _idleNativeWorkers.Pop();
			}
		}
		var worker = NativeGuestExecutor.TryCreate(this);
		if (worker is null)
		{
			if (Interlocked.Exchange(ref _nativeWorkerCreationFailedLogged, 1) == 0)
			{
				Console.Error.WriteLine(
					"[LOADER][ERROR] Failed to create a native guest worker thread; refusing unsafe inline guest execution.");
			}
			return null;
		}
		lock (_nativeWorkerGate)
		{
			if (_nativeWorkersDisposed)
			{
				worker.Dispose();
				return null;
			}
			_allNativeWorkers.Add(worker);
		}
		return worker;
	}

	private void ReturnNativeGuestExecutor(NativeGuestExecutor worker)
	{
		lock (_nativeWorkerGate)
		{
			if (!_nativeWorkersDisposed)
			{
				_idleNativeWorkers.Push(worker);
				return;
			}
		}
		worker.Dispose();
	}

	private void DisposeNativeGuestExecutors()
	{
		NativeGuestExecutor[] workers;
		lock (_nativeWorkerGate)
		{
			if (_nativeWorkersDisposed)
			{
				return;
			}
			_nativeWorkersDisposed = true;
			workers = _allNativeWorkers.ToArray();
			_allNativeWorkers.Clear();
			_idleNativeWorkers.Clear();
		}
		foreach (var worker in workers)
		{
			worker.Dispose();
		}
	}

	// A pooled raw OS thread that executes guest entry stubs. The run loop is emitted
	// native code (no CLR unwind info required — nothing ever unwinds through it):
	//
	//   loop: WaitForSingleObject(work);
	//         if (*stopFlag) { SetEvent(done); ExitThread(0); }
	//         rax = RunPrologue(self);      // managed: binds per-run ambient, returns stub
	//         if (rax != 0) eax = rax();    // guest entry stub — zero managed frames below
	//         RunEpilogue(self, eax);       // managed: captures outcome, restores ambient
	//         SetEvent(done); goto loop;
	//
	// Workers carry no per-guest identity of their own: the prologue rebinds guest TLS,
	// the host-RSP slot, the Active* thread-statics and the GuestThreadExecution ambient
	// on every run, so a worker can be reused for any guest thread.
	private sealed unsafe class NativeGuestExecutor : IDisposable
	{
		private const uint LoopStubSize = 512u;
		private const uint WorkerStackReservation = 4u * 1024u * 1024u;

		private static nint _waitForSingleObjectAddress;
		private static nint _setEventAddress;
		private static nint _exitThreadAddress;

		private readonly DirectExecutionBackend _backend;
		private nint _workEvent;
		private nint _doneEvent;

		// RunPrologue/RunEpilogue compile to the host ABI. The host platform
		// adapts them to the Win64 ABI used by the emitted worker loop.
		private GCHandle _selfHandle;
		private void* _controlBlock;
		private void* _loopStub;
		private nint _threadHandle;
		private uint _nativeThreadId;

		// Single in-flight run; publication is ordered by the work/done event pair.
		private CpuContext? _runContext;
		private GuestThreadState? _runState;
		private ulong _runGuestThreadHandle;
		private ulong _runSentinelRip;
		private ulong _runReturnSlotAddress;
		private nint _runHostRspSlot;
		private nint _runEntryStub;
		private ulong _runAffinityMask;
		private ulong _runNativeResult;
		private bool _runYieldRequested;
		private string? _runYieldReason;
		private bool _runForcedExit;
		private ulong _runHardwareExceptionRip;
		private uint _runHardwareExceptionCode;
		private ulong _runHardwareExceptionAccessType;
		private ulong _runHardwareExceptionAccessAddress;
		private CpuRegisterSnapshot? _runHardwareExceptionRegisters;
		private bool _runPrologueFailed;

		// Prologue -> epilogue carry, only touched on the worker thread.
		private DirectExecutionBackend? _prevBackend;
		private CpuContext? _prevContext;
		private ulong _prevSentinel;
		private ulong _prevReturnSlot;
		private bool _prevForcedExit;
		private bool _prevYieldRequested;
		private string? _prevYieldReason;
		private ulong _prevGuestStackStart;
		private ulong _prevGuestStackEnd;
		private ulong _prevHardwareExceptionRip;
		private uint _prevHardwareExceptionCode;
		private ulong _prevHardwareExceptionAccessType;
		private ulong _prevHardwareExceptionAccessAddress;
		private CpuRegisterSnapshot? _prevHardwareExceptionRegisters;
		private GuestThreadState? _prevState;
		private ulong _prevGuestThreadHandle;
		private nint _prevHostRspSlot;
		private int _prevHostThreadId;
		private bool _entered;

		private NativeGuestExecutor(DirectExecutionBackend backend)
		{
			_backend = backend;
		}

		public static NativeGuestExecutor? TryCreate(DirectExecutionBackend backend)
		{
			if (!EnsureHostRuntimeExports(backend._hostSymbols))
			{
				return null;
			}
			var executor = new NativeGuestExecutor(backend);
			if (!executor.Initialize())
			{
				executor.Dispose();
				return null;
			}
			return executor;
		}

		private static bool EnsureHostRuntimeExports(IHostSymbolResolver symbols)
		{
			if (_exitThreadAddress != 0)
			{
				return _waitForSingleObjectAddress != 0 && _setEventAddress != 0;
			}
			_waitForSingleObjectAddress = symbols.GetAddress(HostRuntimeFunction.WaitForSingleObject);
			_setEventAddress = symbols.GetAddress(HostRuntimeFunction.SetEvent);
			_exitThreadAddress = symbols.GetAddress(HostRuntimeFunction.ExitThread);
			return _waitForSingleObjectAddress != 0 && _setEventAddress != 0 && _exitThreadAddress != 0;
		}

		private bool Initialize()
		{
			_selfHandle = GCHandle.Alloc(this);
			_controlBlock = (void*)_backend._hostMemory.Allocate(0, 4096u, HostPageProtection.ReadWrite);
			if (_controlBlock == null)
			{
				return false;
			}
			_loopStub = (void*)_backend._hostMemory.Allocate(0, LoopStubSize, HostPageProtection.ReadWrite);
			if (_loopStub == null)
			{
				return false;
			}

			var prologuePtr = (nint)(delegate* unmanaged<nint, nint>)&RunPrologue;
			var epiloguePtr = (nint)(delegate* unmanaged<nint, ulong, void>)&RunEpilogue;
			var executorHandle = GCHandle.ToIntPtr(_selfHandle);
			prologuePtr = _backend._hostNativeInterop.AdaptGuestAbiCallback(prologuePtr);
			epiloguePtr = _backend._hostNativeInterop.AdaptGuestAbiCallback(epiloguePtr);
			_workEvent = _backend._hostNativeInterop.CreateWorkerEvent();
			_doneEvent = _backend._hostNativeInterop.CreateWorkerEvent();
			if (_workEvent == 0 || _doneEvent == 0)
			{
				return false;
			}

			byte* code = (byte*)_loopStub;
			int offset = 0;
			void Emit(byte value) => code[offset++] = value;
			void EmitMovRcxImm64(ulong value)
			{
				Emit(0x48); Emit(0xB9);
				*(ulong*)(code + offset) = value;
				offset += sizeof(ulong);
			}
			void EmitMovRaxImm64(ulong value)
			{
				Emit(0x48); Emit(0xB8);
				*(ulong*)(code + offset) = value;
				offset += sizeof(ulong);
			}
			void EmitCallRax()
			{
				Emit(0xFF); Emit(0xD0);
			}
			void EmitSetDoneEvent()
			{
				EmitMovRcxImm64((ulong)_doneEvent);
				EmitMovRaxImm64((ulong)_setEventAddress);
				EmitCallRax();
			}

			// Thread entry leaves RSP ≡ 8 (mod 16); after this every call below happens
			// at RSP ≡ 0 with shadow space available.
			Emit(0x48); Emit(0x83); Emit(0xEC); Emit(0x28); // sub rsp, 0x28
			int loopStart = offset;
			EmitMovRcxImm64((ulong)_workEvent);
			Emit(0xBA);                                     // mov edx, INFINITE
			*(uint*)(code + offset) = 0xFFFFFFFFu;
			offset += sizeof(uint);
			EmitMovRaxImm64((ulong)_waitForSingleObjectAddress);
			EmitCallRax();
			EmitMovRaxImm64((ulong)_controlBlock);
			Emit(0x83); Emit(0x38); Emit(0x00);             // cmp dword [rax], 0
			Emit(0x0F); Emit(0x85);                         // jne stop
			int stopJump = offset;
			offset += sizeof(int);
			EmitMovRcxImm64((ulong)executorHandle);
			EmitMovRaxImm64((ulong)prologuePtr);
			EmitCallRax();                                  // rax = entry stub, or 0 on failure
			Emit(0x48); Emit(0x85); Emit(0xC0);             // test rax, rax
			Emit(0x0F); Emit(0x84);                         // je skipEntry
			int skipJump = offset;
			offset += sizeof(int);
			EmitCallRax();                                  // guest entry stub -> rax
			int skipEntryOffset = offset;
			Emit(0x48); Emit(0x89); Emit(0xC2);             // mov rdx, rax
			EmitMovRcxImm64((ulong)executorHandle);
			EmitMovRaxImm64((ulong)epiloguePtr);
			EmitCallRax();
			EmitSetDoneEvent();
			Emit(0xE9);                                     // jmp loop
			*(int*)(code + offset) = loopStart - (offset + sizeof(int));
			offset += sizeof(int);
			int stopOffset = offset;
			// Signal done so a Run() racing teardown cannot park forever; a stopped
			// worker is never re-rented, so the stale signal is unobservable.
			EmitSetDoneEvent();
			Emit(0x31); Emit(0xC9);                         // xor ecx, ecx
			EmitMovRaxImm64((ulong)_exitThreadAddress);
			EmitCallRax();
			Emit(0xCC);                                     // int3 (ExitThread never returns)
			*(int*)(code + stopJump) = stopOffset - (stopJump + sizeof(int));
			*(int*)(code + skipJump) = skipEntryOffset - (skipJump + sizeof(int));

			uint oldProtect = 0;
			if (!_backend._hostMemory.Protect((ulong)_loopStub, LoopStubSize, HostPageProtection.ReadExecute, out oldProtect))
			{
				return false;
			}
			_backend._hostMemory.FlushInstructionCache((ulong)_loopStub, LoopStubSize);
			_threadHandle = _backend._hostThreading.CreateNativeThread(
				(nint)_loopStub,
				0,
				WorkerStackReservation,
				out _nativeThreadId);
			if (_threadHandle == 0)
			{
				return false;
			}
			if (LogThreadMode)
			{
				TraceThreadMode($"worker_created native_tid={_nativeThreadId} loop=0x{(ulong)_loopStub:X16}");
			}
			return true;
		}

		public ulong Run(
			CpuContext context,
			GuestThreadState? state,
			ulong guestThreadHandle,
			ulong sentinelRip,
			ulong returnSlotAddress,
			nint hostRspSlot,
			nint entryStub,
			ulong affinityMask,
			out bool yieldRequested,
			out string? yieldReason,
			out bool forcedExit,
			out ulong hardwareExceptionRip,
			out uint hardwareExceptionCode,
			out ulong hardwareExceptionAccessType,
			out ulong hardwareExceptionAccessAddress,
			out CpuRegisterSnapshot? hardwareExceptionRegisters)
		{
			_runContext = context;
			_runState = state;
			_runGuestThreadHandle = guestThreadHandle;
			_runSentinelRip = sentinelRip;
			_runReturnSlotAddress = returnSlotAddress;
			_runHostRspSlot = hostRspSlot;
			_runEntryStub = entryStub;
			_runAffinityMask = affinityMask;
			_runPrologueFailed = true;
			_runYieldRequested = false;
			_runYieldReason = null;
			_runForcedExit = false;
			_runHardwareExceptionRip = 0;
			_runHardwareExceptionCode = 0;
			_runHardwareExceptionAccessType = 0;
			_runHardwareExceptionAccessAddress = 0;
			_runHardwareExceptionRegisters = null;
			SignalWorkAvailable();
			WaitWorkCompleted();
			_runContext = null;
			_runState = null;
			yieldRequested = _runYieldRequested;
			yieldReason = _runYieldReason;
			forcedExit = _runForcedExit;
			hardwareExceptionRip = _runHardwareExceptionRip;
			hardwareExceptionCode = _runHardwareExceptionCode;
			hardwareExceptionAccessType = _runHardwareExceptionAccessType;
			hardwareExceptionAccessAddress = _runHardwareExceptionAccessAddress;
			hardwareExceptionRegisters = _runHardwareExceptionRegisters;
			if (_runPrologueFailed)
			{
				throw new InvalidOperationException("Native guest worker failed to bind the run ambient (prologue fault)");
			}
			return _runNativeResult;
		}

		private void SignalWorkAvailable()
		{
			if (_workEvent != 0)
			{
				_ = _backend._hostNativeInterop.SignalWorkerEvent(_workEvent);
			}
		}

		private void WaitWorkCompleted()
		{
			_ = _backend._hostNativeInterop.WaitWorkerEvent(_doneEvent, -1);
		}

		[UnmanagedCallersOnly]
		private static nint RunPrologue(nint executorHandle)
		{
			try
			{
				var executor = (NativeGuestExecutor)GCHandle.FromIntPtr(executorHandle).Target!;
				return executor.EnterRun();
			}
			catch (Exception ex)
			{
				try
				{
					Console.Error.WriteLine(
						$"[LOADER][ERROR] Native guest worker prologue failed: {ex.GetType().Name}: {ex.Message}");
				}
				catch
				{
				}
				return 0;
			}
		}

		[UnmanagedCallersOnly]
		private static void RunEpilogue(nint executorHandle, ulong nativeResult)
		{
			try
			{
				var executor = (NativeGuestExecutor)GCHandle.FromIntPtr(executorHandle).Target!;
				executor.ExitRun(nativeResult);
			}
			catch (Exception ex)
			{
				try
				{
					Console.Error.WriteLine(
						$"[LOADER][ERROR] Native guest worker epilogue failed: {ex.GetType().Name}: {ex.Message}");
				}
				catch
				{
				}
			}
		}

		private nint EnterRun()
		{
			var backend = _backend;
			_prevBackend = _activeExecutionBackend;
			_prevContext = _activeCpuContext;
			_prevSentinel = _activeEntryReturnSentinelRip;
			_prevReturnSlot = _activeGuestReturnSlotAddress;
			_prevForcedExit = _activeForcedGuestExit;
			_prevYieldRequested = _activeGuestThreadYieldRequested;
			_prevYieldReason = _activeGuestThreadYieldReason;
			_prevGuestStackStart = _activeGuestStackStart;
			_prevGuestStackEnd = _activeGuestStackEnd;
			_prevHardwareExceptionRip = _activeGuestHardwareExceptionRip;
			_prevHardwareExceptionCode = _activeGuestHardwareExceptionCode;
			_prevHardwareExceptionAccessType = _activeGuestHardwareExceptionAccessType;
			_prevHardwareExceptionAccessAddress = _activeGuestHardwareExceptionAccessAddress;
			_prevHardwareExceptionRegisters = _activeGuestHardwareExceptionRegisters;
			_prevState = _activeGuestThreadState;
			_prevHostRspSlot = backend._hostThreading.GetTlsValue(backend._hostRspSlotTlsIndex);
			_prevGuestThreadHandle = GuestThreadExecution.EnterGuestThread(_runGuestThreadHandle);
			_entered = true;
			_activeExecutionBackend = backend;
			_activeCpuContext = _runContext;
			_activeEntryReturnSentinelRip = _runSentinelRip;
			_activeGuestReturnSlotAddress = _runReturnSlotAddress;
			_activeForcedGuestExit = false;
			_activeGuestThreadYieldRequested = false;
			_activeGuestThreadYieldReason = null;
			_activeGuestThreadState = _runState;
			_activeGuestHardwareExceptionRip = 0;
			_activeGuestHardwareExceptionCode = 0;
			_activeGuestHardwareExceptionAccessType = 0;
			_activeGuestHardwareExceptionAccessAddress = 0;
			_activeGuestHardwareExceptionRegisters = null;
			BindActiveGuestStackRange(_runContext!);
			backend.BindTlsBase(_runContext!);
			backend._hostThreading.SetTlsValue(backend._hostRspSlotTlsIndex, _runHostRspSlot);
			if (_runState is { } state)
			{
				_prevHostThreadId = Volatile.Read(ref state.HostThreadId);
				Volatile.Write(ref state.HostThreadId, unchecked((int)backend._hostThreading.CurrentThreadId));
			}
			if (_runAffinityMask != 0)
			{
				backend.ApplyGuestThreadAffinity(_runAffinityMask);
			}
			_runPrologueFailed = false;
			if (LogThreadMode)
			{
				TraceThreadMode(
					$"worker_enter guest=0x{_runGuestThreadHandle:X16} stub=0x{(ulong)_runEntryStub:X16}");
			}
			return _runEntryStub;
		}

		private void ExitRun(ulong nativeResult)
		{
			_runNativeResult = nativeResult;
			_runYieldRequested = _activeGuestThreadYieldRequested;
			_runYieldReason = _activeGuestThreadYieldReason;
			_runForcedExit = _activeForcedGuestExit;
			_runHardwareExceptionRip = _activeGuestHardwareExceptionRip;
			_runHardwareExceptionCode = _activeGuestHardwareExceptionCode;
			_runHardwareExceptionAccessType = _activeGuestHardwareExceptionAccessType;
			_runHardwareExceptionAccessAddress = _activeGuestHardwareExceptionAccessAddress;
			_runHardwareExceptionRegisters = _activeGuestHardwareExceptionRegisters;
			if (!_entered)
			{
				return;
			}
			_entered = false;
			if (_runState is { } state)
			{
				Volatile.Write(ref state.HostThreadId, _prevHostThreadId);
			}
			_backend._hostThreading.SetTlsValue(_backend._hostRspSlotTlsIndex, _prevHostRspSlot);
			GuestThreadExecution.RestoreGuestThread(_prevGuestThreadHandle);
			_activeExecutionBackend = _prevBackend;
			_activeCpuContext = _prevContext;
			_activeEntryReturnSentinelRip = _prevSentinel;
			_activeGuestReturnSlotAddress = _prevReturnSlot;
			_activeForcedGuestExit = _prevForcedExit;
			_activeGuestThreadYieldRequested = _prevYieldRequested;
			_activeGuestThreadYieldReason = _prevYieldReason;
			_activeGuestStackStart = _prevGuestStackStart;
			_activeGuestStackEnd = _prevGuestStackEnd;
			_activeGuestHardwareExceptionRip = _prevHardwareExceptionRip;
			_activeGuestHardwareExceptionCode = _prevHardwareExceptionCode;
			_activeGuestHardwareExceptionAccessType = _prevHardwareExceptionAccessType;
			_activeGuestHardwareExceptionAccessAddress = _prevHardwareExceptionAccessAddress;
			_activeGuestHardwareExceptionRegisters = _prevHardwareExceptionRegisters;
			_activeGuestThreadState = _prevState;
			_prevBackend = null;
			_prevContext = null;
			_prevState = null;
			_prevYieldReason = null;
			if (LogThreadMode)
			{
				TraceThreadMode(
					$"worker_exit guest=0x{_runGuestThreadHandle:X16} result=0x{nativeResult:X16} yield={_runYieldRequested}");
			}
		}

		public void Dispose()
		{
			if (_controlBlock != null)
			{
				*(int*)_controlBlock = 1;
			}
			try
			{
				SignalWorkAvailable();
			}
			catch (ObjectDisposedException)
			{
			}
			var exited = _threadHandle == 0;
			if (_threadHandle != 0)
			{
				// The worker signals done immediately before its native exit path.
				// This is a reliable completion proof on POSIX, where probing a
				// returned-but-unjoined pthread can keep reporting it as live.
				exited = _backend._hostNativeInterop.WaitWorkerEvent(
					_doneEvent,
					1000);
				if (exited)
				{
					_backend._hostThreading.JoinExitedThread(_threadHandle);
				}
				_backend._hostThreading.CloseThreadHandle(_threadHandle);
				_threadHandle = 0;
			}
			if (!exited)
			{
				// The worker is still parked in guest code (teardown should have unwound
				// it first). Leak the stub, control block, events and GC handle rather
				// than have the thread execute freed memory.
				Console.Error.WriteLine(
					$"[LOADER][WARN] Native guest worker tid={_nativeThreadId} did not stop; leaking its run loop.");
				return;
			}
			if (_loopStub != null)
			{
				_backend._hostMemory.Free((ulong)_loopStub);
				_loopStub = null;
			}
			if (_controlBlock != null)
			{
				_backend._hostMemory.Free((ulong)_controlBlock);
				_controlBlock = null;
			}
			if (_selfHandle.IsAllocated)
			{
				_selfHandle.Free();
			}
			if (_workEvent != 0)
			{
				_backend._hostNativeInterop.CloseWorkerEvent(_workEvent);
				_workEvent = 0;
			}
			if (_doneEvent != 0)
			{
				_backend._hostNativeInterop.CloseWorkerEvent(_doneEvent);
				_doneEvent = 0;
			}
		}
	}
}
