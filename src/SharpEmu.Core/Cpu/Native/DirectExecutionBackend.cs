// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native.Windows;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Logging;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend : INativeCpuBackend, IGuestThreadScheduler, IDisposable
{
	public const int StallWatchdogExitCode = 6;

	private static readonly SharpEmuLogger Log = SharpEmuLog.For("Native");
	private const int ImportLoopHistoryLength = 2048;

	private const int ImportLoopWideDiversityWindow = 768;

	private const int DefaultImportLoopGuardSeconds = 5;

	private enum ImportStubKind : byte
	{
		Normal = 0,
		BootstrapBridge = 1,
		KernelDynlibDlsym = 2,
		Il2CppApiLookupSymbol = 3,
	}

	[Flags]
	private enum ImportStubTraceFlags : byte
	{
		None = 0,
		Memset = 1 << 0,
		CxaAtexit = 1 << 1,
		RawArgs = 1 << 2,
		StackChkFail = 1 << 3,
		PeriodicEvery1000 = 1 << 4,
		PeriodicEvery128 = 1 << 5,
	}

	private readonly struct ImportStubEntry
	{
		public ulong Address { get; }

		public string Nid { get; }

		public ExportedFunction? Export { get; }

		public ImportStubKind Kind { get; }

		public ImportStubTraceFlags TraceFlags { get; }

		public bool IsNoBlockLeaf { get; }

		public ImportStubEntry(ulong address, string nid, ExportedFunction? export)
		{
			Address = address;
			Nid = nid;
			Export = export;
			Kind = ClassifyKind(nid);
			TraceFlags = ClassifyTraceFlags(nid);
			IsNoBlockLeaf = IsNoBlockLeafImport(nid);
		}

		private static ImportStubKind ClassifyKind(string nid) => nid switch
		{
			RuntimeStubNids.BootstrapBridge => ImportStubKind.BootstrapBridge,
			RuntimeStubNids.KernelDynlibDlsym or "LwG8g3niqwA" => ImportStubKind.KernelDynlibDlsym,
			"r8mvOaWdi28" => ImportStubKind.Il2CppApiLookupSymbol,
			_ => ImportStubKind.Normal,
		};

		private static ImportStubTraceFlags ClassifyTraceFlags(string nid) => nid switch
		{
			"8zTFvBIAIN8" => ImportStubTraceFlags.Memset,
			"tsvEmnenz48" => ImportStubTraceFlags.CxaAtexit | ImportStubTraceFlags.PeriodicEvery1000,
			"bzQExy189ZI" or "8G2LB+A3rzg" => ImportStubTraceFlags.RawArgs,
			"Ou3iL1abvng" => ImportStubTraceFlags.StackChkFail,
			"rTXw65xmLIA" => ImportStubTraceFlags.PeriodicEvery128,
			_ => ImportStubTraceFlags.None,
		};
	}

	private sealed class ImportSetupCheckpoint(
		ImportStubEntry[] previousEntries,
		List<(ulong Address, byte[] OriginalBytes)> patchedStubs,
		int attemptAllocationStart)
	{
		public ImportStubEntry[] PreviousEntries { get; } = previousEntries;

		public List<(ulong Address, byte[] OriginalBytes)> PatchedStubs { get; } = patchedStubs;

		public int AttemptAllocationStart { get; } = attemptAllocationStart;
	}

	private sealed class TlsSetupCheckpoint(
		nint handlerAddress,
		int patchStubOffset,
		int allocationStart,
		Dictionary<(int DestinationRegister, int Displacement, bool Is64Bit, int MemorySize, bool SignExtend), nint> loadHelpers,
		Dictionary<(int SourceRegister, int Displacement, bool Is64Bit), nint> storeHelpers,
		Dictionary<(int Displacement, int ImmediateValue, bool Is64Bit), nint> immediateStoreHelpers,
		Dictionary<(NativeTlsInstructionKind Kind, int DestinationRegister, int Displacement, bool Is64Bit), nint> stackCanaryHelpers)
	{
		public nint HandlerAddress { get; } = handlerAddress;

		public int PatchStubOffset { get; } = patchStubOffset;

		public int AllocationStart { get; } = allocationStart;

		public Dictionary<(int DestinationRegister, int Displacement, bool Is64Bit, int MemorySize, bool SignExtend), nint> LoadHelpers { get; } = loadHelpers;

		public Dictionary<(int SourceRegister, int Displacement, bool Is64Bit), nint> StoreHelpers { get; } = storeHelpers;

		public Dictionary<(int Displacement, int ImmediateValue, bool Is64Bit), nint> ImmediateStoreHelpers { get; } = immediateStoreHelpers;

		public Dictionary<(NativeTlsInstructionKind Kind, int DestinationRegister, int Displacement, bool Is64Bit), nint> StackCanaryHelpers { get; } = stackCanaryHelpers;
	}

	private readonly record struct DeferredBootstrapTraceEntry(
		long DispatchIndex,
		ulong Op,
		ulong SymbolPointer,
		ulong OutputPointer,
		ulong ReturnRip);

#pragma warning disable CS0649
	private struct EXCEPTION_POINTERS
	{
		public unsafe EXCEPTION_RECORD* ExceptionRecord;

		public unsafe void* ContextRecord;
	}

	private struct EXCEPTION_RECORD
	{
		public uint ExceptionCode;

		public uint ExceptionFlags;

		public unsafe EXCEPTION_RECORD* ExceptionRecord;

		public unsafe void* ExceptionAddress;

		public uint NumberParameters;

		public unsafe fixed ulong ExceptionInformation[15];
	}
#pragma warning restore CS0649

	private delegate int ExceptionHandlerDelegate(void* exceptionInfo);

	private const ulong SYSTEM_RESERVED = 34359738368uL;

	private const ulong CODE_BASE_OFFSET = 4294967296uL;

	private const ulong CODE_BASE_INCR = 268435456uL;

	private const ulong FallbackTlsScanSize = 33554432uL;

	// The 0x7FFx window is Windows-specific; dyld and Rosetta reserve that
	// range on macOS, so POSIX guest threads use the lower 0x6FFx window.
	// The POSIX stack base sits a further 1GB down: the import-stub region
	// descends from 0x7000_0000_0000 on the same 16MB grid and reaches
	// 0x6FFF_C000_0000 at its 64-module limit, which would otherwise consume
	// the top stack slots (on Windows the two bands are ~15TB apart).
	private static readonly ulong GuestThreadStackBaseAddress =
		OperatingSystem.IsWindows() ? 0x7FFF_E000_0000UL : 0x6FFF_A000_0000UL;

	private static readonly ulong GuestThreadTlsBaseAddress =
		OperatingSystem.IsWindows() ? 0x7FFE_0000_0000UL : 0x6FFE_0000_0000UL;

	private const ulong GuestThreadStackSize = 0x0020_0000UL;

	private const ulong GuestThreadTlsSize = 0x0001_0000UL;

	// Matches CpuDispatcher: static TLS blocks use Variant II below the TCB.
	private const ulong GuestThreadTlsPrefixSize = GuestTlsTemplate.StartupStaticTlsReservation;

	private const ulong GuestThreadRegionStride = 0x0100_0000UL;

	private const int GuestThreadRegionSlotCount = 256;

	private const uint PAGE_EXECUTE_READWRITE = 64u;

	private const uint PAGE_READWRITE = 4u;

	private const uint PAGE_EXECUTE_READ = 32u;

	private const int TlsHandlerRegionSize = 16384;

	private const ulong TlsModuleAllocStart = 140726751354880uL;

	private const ulong TlsModuleAllocStride = 65536uL;

	private readonly IModuleManager _moduleManager;

	private nint _tlsHandlerAddress;

	private readonly List<nint> _tlsHandlerAllocations = new();

	private nint _tlsBaseAddress;

	private nint _ownedTlsBaseAddress;

	private bool _ownsTlsBaseAddress;

	private uint _guestTlsBaseTlsIndex = uint.MaxValue;

	private uint _hostRspSlotTlsIndex = uint.MaxValue;

	private nint _tlsGetValueAddress;

	private nint _queryPerformanceCounterAddress;

	private nint _switchToThreadAddress;

	private nint _sleepAddress;

	private int _tlsPatchStubOffset;
	private readonly Dictionary<(int DestinationRegister, int Displacement, bool Is64Bit, int MemorySize, bool SignExtend), nint> _tlsLoadHelpers = new();
	private readonly Dictionary<(int SourceRegister, int Displacement, bool Is64Bit), nint> _tlsStoreHelpers = new();
	private readonly Dictionary<(int Displacement, int ImmediateValue, bool Is64Bit), nint> _tlsImmediateStoreHelpers = new();
	private readonly Dictionary<(NativeTlsInstructionKind Kind, int DestinationRegister, int Displacement, bool Is64Bit), nint> _tlsStackCanaryHelpers = new();

	private nint _unresolvedReturnStub;

	private nint _guestReturnStub;

	private nint _rawExceptionHandler;

	private nint _rawExceptionHandlerStub;

	private nint _exceptionHandler;

	private nint _exceptionHandlerStub;

	private nint _unhandledFilterStub;

	private nint _lowIndexedTableScratch;

	private nint _stackGuardCompareScratch;

	private nint _nullObjectStoreScratch;

	private readonly Dictionary<uint, nint> _tlsModuleBases = new Dictionary<uint, nint>();

	private ulong _entryPoint;

	private CpuContext? _cpuContext;

	[ThreadStatic]
	private static DirectExecutionBackend? _activeExecutionBackend;

	[ThreadStatic]
	private static CpuContext? _activeCpuContext;

	[ThreadStatic]
	private static ulong _activeEntryReturnSentinelRip;

	[ThreadStatic]
	private static ulong _activeGuestReturnSlotAddress;

	[ThreadStatic]
	private static bool _activeForcedGuestExit;

	[ThreadStatic]
	private static ulong _activeGuestStackStart;

	[ThreadStatic]
	private static ulong _activeGuestStackEnd;

	[ThreadStatic]
	private static ulong _activeGuestHardwareExceptionRip;

	[ThreadStatic]
	private static uint _activeGuestHardwareExceptionCode;

	[ThreadStatic]
	private static ulong _activeGuestHardwareExceptionAccessType;

	[ThreadStatic]
	private static ulong _activeGuestHardwareExceptionAccessAddress;

	[ThreadStatic]
	private static CpuRegisterSnapshot? _activeGuestHardwareExceptionRegisters;

	[ThreadStatic]
	private static ulong _activeGuestHardwareExceptionThreadHandle;

	[ThreadStatic]
	private static bool _activeGuestThreadYieldRequested;

	[ThreadStatic]
	private static string? _activeGuestThreadYieldReason;

	[ThreadStatic]
	private static GuestThreadState? _activeGuestThreadState;

	// The process/module entry executes on one dedicated native worker. Keep its
	// hot import count separate from scheduled guest threads so dispatch does not
	// need a second global atomic increment for every import.
	private int _sessionEntryImportCount;

	[ThreadStatic]
	private static DirectExecutionBackend? _importCounterOwner;

	[ThreadStatic]
	private static long _nextImportDispatchIndex;

	[ThreadStatic]
	private static long _importDispatchBlockEnd;

	private ImportStubEntry[] _importEntries = Array.Empty<ImportStubEntry>();

	private readonly object _sessionImportEntryHitsGate = new();
	private int[] _sessionImportEntryHits = Array.Empty<int>();

	private readonly List<nint> _importHandlerTrampolines = new List<nint>();

	private const int GuestContextTransferFrameQwords = 15;

	private readonly object _guestContextTransferStubGate = new();

	private readonly ThreadLocal<nint> _guestContextTransferFrames = new(
		static () => (nint)NativeMemory.AllocZeroed(GuestContextTransferFrameQwords, sizeof(ulong)),
		trackAllValues: true);

	private nint _guestContextTransferStub;

	private long _importDispatchCount;

	private const int ImportDispatchBlockSize = 256;

	private KeyValuePair<string, ulong>[] _runtimeSymbolsByAddress = Array.Empty<KeyValuePair<string, ulong>>();

	private readonly ConcurrentDictionary<string, ulong> _runtimeSymbolsByName =
		new(StringComparer.Ordinal);

	// Keep in sync with SelfLoader import-stub mapping constants.
	private const ulong ImportStubRegionCanonicalBase = 0x0000_7000_0000_0000UL;

	private const ulong ImportStubRegionAddressStride = 0x0000_0000_0100_0000UL;

	private const ulong LazyImportStubSlotSize = 0x10;

	private const ulong ImportStubRegionPageSize = 0x1000UL;

	private const string KernelDynlibDlsymAerolibNid = "LwG8g3niqwA";

	private readonly object _lazyDlsymStubGate = new();

	private readonly Dictionary<string, ulong> _lazyDlsymStubCache = new(StringComparer.Ordinal);

	private ulong _lazyImportStubPoolBase;

	private ulong _lazyImportStubNextSlot;

	private ulong _lazyImportStubPoolLimit;

	private bool _lazyImportStubPoolMapped;

	private RecentImportTraceBuffer? _recentImportTrace;

	private readonly DeferredBootstrapTraceEntry[] _deferredBootstrapTrace = new DeferredBootstrapTraceEntry[32];

	private int _deferredBootstrapTraceCount;

	private int _deferredBootstrapTraceWriteIndex;

	private readonly object _deferredBootstrapTraceGate = new();

	private readonly string[] _distinctImportNidHistory = new string[128];

	private int _distinctImportNidHistoryCount;

	private int _distinctImportNidHistoryWriteIndex;

	private string _lastDistinctImportNid = string.Empty;

	private int _consecutiveStrlenImports;

	private bool _strlenPreludeLogged;

	private bool _logStrlenImports;

	private bool _logStrlenBursts;

	private bool _logGuestContext;

	private bool _logGuestThreads;

	private bool _logUsleep;

	private bool _logFiber;

	private bool _logBootstrap;

	private bool _logAllImports;

	private bool _logImportSetup;

	private bool _logImportFrames;

	private bool _logImportRecent;

	private bool _logStackCheck;

	private string? _probeImportReturn;

	private string? _importFilter;

	private bool _disableImportLoopGuard;

	private int _importLoopGuardSeconds;

	private readonly HashSet<ulong> _patchedResolverReturnSites = new HashSet<ulong>();

	private readonly HashSet<ulong> _patchedTlsImmediateThunkTargets = new HashSet<ulong>();

	private readonly HashSet<ulong> _contextualUnresolvedReturnSites = new HashSet<ulong>();

	private readonly object _lazyCommitRangeGate = new object();

	private readonly List<LazyCommitRange> _prtLazyCommitRanges = new List<LazyCommitRange>();

	private ulong _returnFallbackTarget;

	private static int _rawSentinelRecoveries;

	private int _lastReportedRawSentinelRecoveries;

	private static ulong _globalFallbackTarget;

	private static ulong _globalUnresolvedReturnStub;

	private nint _hostRspSlotStorage;

	private bool _patchedEa020eLookupCall;

	private ulong _entryReturnSentinelRip;

	private readonly ulong[] _importLoopSignatures = new ulong[ImportLoopHistoryLength];

	private readonly ulong[] _importLoopNidHashes = new ulong[ImportLoopHistoryLength];

	private readonly ulong[] _importLoopReturnRips = new ulong[ImportLoopHistoryLength];

	private int _importLoopSignatureCount;

	private int _importLoopSignatureWriteIndex;

	private int _importLoopPatternHits;

	private long _importLoopPatternStartTimestamp;

	private readonly Dictionary<string, ulong> _importNidHashCache = new Dictionary<string, ulong>(StringComparer.Ordinal);

	private enum GuestThreadRunState
	{
		Ready,
		Running,
		Blocked,
		Exited,
		Faulted,
	}

	private enum GuestNativeCallExitReason
	{
		Returned,
		Blocked,
		ForcedExit,
		Exception,
	}

	private sealed class GuestThreadState
	{
		public ulong ThreadHandle { get; init; }

		public ulong EntryPoint { get; init; }

		public ulong Argument { get; init; }

		public string Name { get; init; } = string.Empty;

		public int Priority { get; init; }

		public ulong AffinityMask { get; init; }

		public CpuContext Context { get; init; } = null!;

		public GuestThreadRunState State { get; set; }

		public ulong ExitValue { get; set; }

		public string? BlockReason { get; set; }

		public long ImportCount;

		public string? LastImportNid;

		public ulong LastReturnRip;

		public Thread? HostThread { get; set; }

        public int HostThreadId;

        public GuestContinuationRunner? ContinuationRunner { get; set; }

        public ulong ExceptionStackBase { get; set; }

		public bool ReapRequested { get; set; }
	}

	private sealed class GuestContinuationRunner : IDisposable
	{
		private readonly ulong _guestThreadHandle;
		private readonly object _runGate = new();
		private readonly AutoResetEvent _workAvailable = new(false);
		private readonly AutoResetEvent _workCompleted = new(false);
		private readonly Thread _thread;
		private Action? _work;
		private volatile bool _stopping;

		public GuestContinuationRunner(ulong guestThreadHandle, ThreadPriority priority)
		{
			_guestThreadHandle = guestThreadHandle;
			_thread = new Thread(ThreadMain)
			{
				IsBackground = true,
				Name = $"GuestContinuation-{guestThreadHandle:X}",
				Priority = priority,
			};
			_thread.Start();
		}

		public bool IsCurrentThread => ReferenceEquals(Thread.CurrentThread, _thread);

		public void Run(Action work)
		{
			lock (_runGate)
			{
				_work = work;
				_workAvailable.Set();
				_workCompleted.WaitOne();
				_work = null;
			}
		}

		private void ThreadMain()
		{
			var previousGuestThreadHandle = GuestThreadExecution.EnterGuestThread(_guestThreadHandle);
			if (LogThreadMode)
			{
				TraceThreadMode($"runner_start guest=0x{_guestThreadHandle:X16}");
			}
			try
			{
				while (true)
				{
					_workAvailable.WaitOne();
					if (_stopping)
					{
						return;
					}

					if (LogThreadMode)
					{
						_threadModeCycleId = Interlocked.Increment(ref _threadModeCycleCounter);
						TraceThreadMode($"runner_run guest=0x{_guestThreadHandle:X16}");
					}
					try
					{
						_work?.Invoke();
					}
					finally
					{
						_workCompleted.Set();
					}
				}
			}
			finally
			{
				if (LogThreadMode)
				{
					TraceThreadMode($"runner_stop guest=0x{_guestThreadHandle:X16}");
				}
				GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
			}
		}

		public void Dispose()
		{
			_stopping = true;
			_workAvailable.Set();
			if (!IsCurrentThread)
			{
				_thread.Join(500);
			}
			_workAvailable.Dispose();
			_workCompleted.Dispose();
		}
	}

	private readonly record struct LazyCommitRange(ulong BaseAddress, ulong Size);

	private readonly object _guestThreadGate = new object();

	// Diagnostic owner tracking for _guestThreadGate; written only while the
	// gate is held, read lock-free by the stall watchdog's periodic snapshot.
	private volatile string? _gateOwnerSite;
	private int _gateOwnerManagedThreadId;
	private long _gateAcquireTimestamp;

	private GateHolder LockGate(string site)
	{
		Monitor.Enter(_guestThreadGate);
		_gateOwnerSite = site;
		Volatile.Write(ref _gateOwnerManagedThreadId, Environment.CurrentManagedThreadId);
		Volatile.Write(ref _gateAcquireTimestamp, Stopwatch.GetTimestamp());
		return new GateHolder(this);
	}

	private readonly struct GateHolder : IDisposable
	{
		private readonly DirectExecutionBackend _owner;

		public GateHolder(DirectExecutionBackend owner)
		{
			_owner = owner;
		}

		public void Dispose()
		{
			_owner._gateOwnerSite = null;
			Volatile.Write(ref _owner._gateOwnerManagedThreadId, 0);
			Monitor.Exit(_owner._guestThreadGate);
		}
	}

	private readonly Queue<GuestThreadState> _readyGuestThreads = new Queue<GuestThreadState>();

	// Once set, guest worker threads are unwound to the host at their next import
	// dispatch and Pump refuses to start new ones. This must happen before the
	// runtime frees trampolines or guest memory: workers that keep running native
	// guest code during teardown execute freed pages (observed as an execute-AV in
	// a MEM_FREE module region plus a CLR "UnmanagedCallersOnly" fatal from a Pump
	// thread entering a freed stub).
	private volatile bool _guestTeardownRequested;

	private volatile bool _hostShutdownRequested;

	private static volatile DirectExecutionBackend? _activeSessionBackend;

	private int _readyGuestThreadCount;

	private int _guestThreadCount;

	private readonly Dictionary<ulong, GuestThreadState> _guestThreads = new Dictionary<ulong, GuestThreadState>();

	private int _guestThreadPumpDepth;

	private bool _guestThreadYieldRequested;

	private string? _guestThreadYieldReason;

	private bool _forcedGuestExit;

	private ulong _lastAvTraceRip;

	private ulong _lastAvTraceType;

	private ulong _lastAvTraceTarget;

	private int _lastAvTraceRepeatCount;

	private long _lastProgressTimestamp;

	private int _stallWatchdogTriggered;

	private volatile bool _stallWatchdogStop;

	private Thread? _stallWatchdogThread;

	private GCHandle _selfHandle;

	private nint _selfHandlePtr;

	private int _disposeStarted;

	private readonly IHostPlatform _hostPlatform;

	private readonly IHostThreading _hostThreading;

	private readonly IHostSymbolResolver _hostSymbols;

	private readonly IHostNativeInterop _hostNativeInterop;

	private readonly IHostMemory _hostMemory;

	private readonly IHostFaultHandling _faultHandling;

	private readonly bool _usePosixSignalHandling;

	private const int MinTlsPatchInstructionBytes = 8;
	private const int MaxX86InstructionBytes = 15;
	private const ulong MaxTlsScanChunkBytes = 0x0100_0000;
	private const uint NativeEntryStubSize = 512u;
	private const ulong HostRspSlotSize = sizeof(ulong);

	private delegate ulong ImportGatewayDelegate(nint backendHandle, int importIndex, nint argPackPtr);
	private delegate int RawExceptionHandlerDelegate(void* exceptionInfo);
	private static readonly ImportGatewayDelegate ImportGatewayDelegateInstance = ImportDispatchGatewayManaged;
	private static readonly RawExceptionHandlerDelegate RawVectoredHandlerDelegateInstance = RawVectoredHandlerManaged;
	private static readonly RawExceptionHandlerDelegate RawUnhandledFilterDelegateInstance = RawUnhandledFilterManaged;

	private static readonly nint ImportGatewayPtr = ResolveImportGatewayPtr();

	// Emitted import trampolines use the Win64 ABI. Managed callbacks use the
	// host ABI, so POSIX needs a small Win64-to-SysV register-shuffling thunk.
	private static nint ResolveImportGatewayPtr()
	{
		var managedPtr = Marshal.GetFunctionPointerForDelegate(ImportGatewayDelegateInstance);
		return RuntimeInformation.ProcessArchitecture != Architecture.X64
			? managedPtr
			: HostPlatform.Current.NativeInterop.AdaptGuestAbiCallback(managedPtr);
	}

	private static readonly nint RawVectoredHandlerPtrManaged =
		Marshal.GetFunctionPointerForDelegate(RawVectoredHandlerDelegateInstance);

	private static readonly nint RawUnhandledFilterPtrManaged =
		Marshal.GetFunctionPointerForDelegate(RawUnhandledFilterDelegateInstance);

	private const int CTX_MXCSR = Win64ContextOffsets.Mxcsr;

	private const int CTX_RAX = Win64ContextOffsets.Rax;

	private const int CTX_RCX = Win64ContextOffsets.Rcx;

	private const int CTX_RDX = Win64ContextOffsets.Rdx;

	private const int CTX_RBX = Win64ContextOffsets.Rbx;

	private const int CTX_RSP = Win64ContextOffsets.Rsp;

	private const int CTX_RBP = Win64ContextOffsets.Rbp;

	private const int CTX_RSI = Win64ContextOffsets.Rsi;

	private const int CTX_RDI = Win64ContextOffsets.Rdi;

	private const int CTX_R8 = Win64ContextOffsets.R8;

	private const int CTX_R9 = Win64ContextOffsets.R9;

	private const int CTX_R10 = Win64ContextOffsets.R10;

	private const int CTX_R11 = Win64ContextOffsets.R11;

	private const int CTX_R12 = Win64ContextOffsets.R12;

	private const int CTX_R13 = Win64ContextOffsets.R13;

	private const int CTX_R14 = Win64ContextOffsets.R14;

	private const int CTX_R15 = Win64ContextOffsets.R15;

	private const int CTX_RIP = Win64ContextOffsets.Rip;

	private ExceptionHandlerDelegate? _handlerDelegate;

	private GCHandle _handlerHandle;

	private ExceptionHandlerDelegate? _unhandledFilterDelegate;

	private GCHandle _unhandledFilterHandle;

	[ThreadStatic]
	private static int _vectoredHandlerDepth;

	private static int _nestedVehTraceCount;

	// SHARPEMU_LOG_THREAD_MODE=1 — GC thread-mode corruption investigation. Traces
	// every managed<->guest transition per guest-thread run cycle so the last event
	// before a ReversePInvokeBadTransition FailFast identifies the corrupting path.
	private static readonly bool LogThreadMode =
		string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_THREAD_MODE"), "1", StringComparison.Ordinal);

	private static long _threadModeCycleCounter;

	[ThreadStatic]
	private static long _threadModeCycleId;

	[ThreadStatic]
	private static int _threadModeGatewayDepth;

	[ThreadStatic]
	private static long _threadModeGatewayCalls;

	[ThreadStatic]
	private static bool _threadModeGatewayFirstLogged;

	private static void TraceThreadMode(string message)
	{
		// Prefer the platform injected into the backend bound to this thread;
		// the ambient is set on every guest/import/VEH transition this tracer
		// observes. The singleton fallback only covers pre-run traces, where a
		// constructed backend implies the platform resolved successfully.
		var threading = _activeExecutionBackend?._hostThreading ?? HostPlatform.Current.Threading;
		Console.Error.WriteLine(
			$"[THREADMODE] {message} cycle={_threadModeCycleId} tid={threading.CurrentThreadId} managed={Environment.CurrentManagedThreadId}");
		Console.Error.Flush();
	}

	private const uint PAGE_EXECUTE = 16u;

	private const uint PAGE_EXECUTE_WRITECOPY = 128u;

	private const uint PAGE_GUARD = 256u;

	private const uint PAGE_NOACCESS = 1u;

	private const uint DBG_PRINTEXCEPTION_C = 0x40010006u;

	private const uint DBG_PRINTEXCEPTION_WIDE_C = 0x4001000Au;

	private const uint MS_VC_THREADNAME_EXCEPTION = 0x406D1388u;

	private const uint MSVC_CPP_EXCEPTION = 0xE06D7363u;

	private const uint HostXmmSaveAreaSize = 0xA0u;

	private readonly record struct HostThreadContextSnapshot(
		bool IsValid,
		ulong Rip,
		ulong Rsp,
		ulong Rbp,
		ulong Rax,
		ulong Rbx,
		ulong Rcx,
		ulong Rdx);

	public string BackendName => "native-backend";

	public string? LastError { get; private set; }

	public CpuTrapInfo? LastTrapInfo { get; private set; }

	public int LastSessionImportsHit { get; private set; }

	public int LastSessionUniqueNidsHit { get; private set; }

	public string? LastImportResolutionTrace { get; private set; }

	public IReadOnlyList<CpuImportTraceEntry>? LastImportTraceEntries { get; private set; }

	public ulong? LastEntryReturnValue { get; private set; }

	private unsafe static ulong ReadCtxU64(void* contextRecord, int offset)
	{
		return *(ulong*)((byte*)contextRecord + offset);
	}

	private unsafe static ulong CallNativeEntry(void* entry)
	{
		var nativeEntry = (delegate* unmanaged[Cdecl]<ulong>)entry;
		if (!LogThreadMode)
		{
			return nativeEntry();
		}

		_threadModeGatewayFirstLogged = false;
		var dispatchesBefore = _threadModeGatewayCalls;
		TraceThreadMode($"native_enter entry=0x{(ulong)entry:X16}");
		var result = nativeEntry();
		TraceThreadMode($"native_exit result=0x{result:X16} dispatches={_threadModeGatewayCalls - dispatchesBefore}");
		return result;
	}

	private unsafe static void WriteCtxU64(void* contextRecord, int offset, ulong value)
	{
		*(ulong*)((byte*)contextRecord + offset) = value;
	}

	private unsafe static uint ReadCtxU32(void* contextRecord, int offset)
	{
		return *(uint*)((byte*)contextRecord + offset);
	}

	private unsafe static void WriteCtxU32(void* contextRecord, int offset, uint value)
	{
		*(uint*)((byte*)contextRecord + offset) = value;
	}

	private bool HasActiveExecutionThread => ReferenceEquals(_activeExecutionBackend, this);

	private CpuContext? ActiveCpuContext => HasActiveExecutionThread ? _activeCpuContext : _cpuContext;

	private ulong ActiveEntryReturnSentinelRip
	{
		get => HasActiveExecutionThread ? _activeEntryReturnSentinelRip : _entryReturnSentinelRip;
		set
		{
			if (HasActiveExecutionThread)
			{
				_activeEntryReturnSentinelRip = value;
			}
			else
			{
				_entryReturnSentinelRip = value;
			}
		}
	}

	private ulong ActiveGuestReturnSlotAddress =>
		HasActiveExecutionThread ? _activeGuestReturnSlotAddress : 0;

	private bool ActiveForcedGuestExit
	{
		get => _hostShutdownRequested ||
			(HasActiveExecutionThread ? _activeForcedGuestExit : _forcedGuestExit);
		set
		{
			if (HasActiveExecutionThread)
			{
				_activeForcedGuestExit = value;
			}
			else
			{
				_forcedGuestExit = value;
			}
		}
	}

	private bool ActiveGuestThreadYieldRequested
	{
		get => HasActiveExecutionThread ? _activeGuestThreadYieldRequested : _guestThreadYieldRequested;
		set
		{
			if (HasActiveExecutionThread)
			{
				_activeGuestThreadYieldRequested = value;
			}
			else
			{
				_guestThreadYieldRequested = value;
			}
		}
	}

	private string? ActiveGuestThreadYieldReason
	{
		get => HasActiveExecutionThread ? _activeGuestThreadYieldReason : _guestThreadYieldReason;
		set
		{
			if (HasActiveExecutionThread)
			{
				_activeGuestThreadYieldReason = value;
			}
			else
			{
				_guestThreadYieldReason = value;
			}
		}
	}

	private static void RestoreActiveExecutionThread(
		DirectExecutionBackend? previousBackend,
		CpuContext? previousContext,
		ulong previousSentinel,
		ulong previousReturnSlotAddress,
		bool previousForcedExit,
		bool previousYieldRequested,
		string? previousYieldReason)
	{
		_activeExecutionBackend = previousBackend;
		_activeCpuContext = previousContext;
		_activeEntryReturnSentinelRip = previousSentinel;
		_activeGuestReturnSlotAddress = previousReturnSlotAddress;
		_activeForcedGuestExit = previousForcedExit;
		_activeGuestThreadYieldRequested = previousYieldRequested;
		_activeGuestThreadYieldReason = previousYieldReason;
	}

	private static void BindActiveGuestStackRange(CpuContext context)
	{
		_activeGuestStackStart = 0;
		_activeGuestStackEnd = 0;
		if (context.Memory is IGuestStackMemory stackMemory &&
			stackMemory.TryGetStackRange(context[CpuRegister.Rsp], out var start, out var end))
		{
			_activeGuestStackStart = start;
			_activeGuestStackEnd = end;
		}
	}

	private static void RefreshActiveGuestStackRange(CpuContext context)
	{
		var stackPointer = context[CpuRegister.Rsp];
		if (_activeGuestStackStart != 0 &&
			stackPointer >= _activeGuestStackStart &&
			stackPointer < _activeGuestStackEnd)
		{
			return;
		}

		BindActiveGuestStackRange(context);
	}

	internal static bool IsActiveGuestStackPointer(ulong stackPointer) =>
		_activeGuestStackStart != 0 &&
		stackPointer >= _activeGuestStackStart &&
		stackPointer < _activeGuestStackEnd;

	private bool ApplyActiveGuestHardwareException(CpuContext context, out string? detail)
	{
		if (_activeGuestHardwareExceptionCode == 0)
		{
			detail = null;
			return false;
		}

		context.Rip = _activeGuestHardwareExceptionRip;
		Span<byte> opcode = stackalloc byte[1];
		var accessKind = _activeGuestHardwareExceptionCode == 0xC0000005u
			? _activeGuestHardwareExceptionAccessType switch
			{
				0 => CpuMemoryAccessKind.Read,
				1 => CpuMemoryAccessKind.Write,
				8 => CpuMemoryAccessKind.Execute,
				_ => CpuMemoryAccessKind.Unknown,
			}
			: (CpuMemoryAccessKind?)null;
		LastTrapInfo = new CpuTrapInfo(
			_activeGuestHardwareExceptionRip,
			context.Memory.TryRead(_activeGuestHardwareExceptionRip, opcode) ? opcode[0] : (byte)0,
			_activeGuestHardwareExceptionCode,
			accessKind.HasValue ? _activeGuestHardwareExceptionAccessAddress : null,
			accessKind,
			registers: _activeGuestHardwareExceptionRegisters,
			guestThreadHandle: _activeGuestHardwareExceptionThreadHandle != 0
				? _activeGuestHardwareExceptionThreadHandle
				: GetCurrentGuestThreadHandle());
		detail =
			$"Guest hardware exception 0x{_activeGuestHardwareExceptionCode:X8} " +
			$"at RIP=0x{_activeGuestHardwareExceptionRip:X16}.";
		return true;
	}

	public unsafe DirectExecutionBackend(IModuleManager moduleManager, IHostPlatform? hostPlatform = null, IHostFaultHandling? faultHandling = null)
	{
		GuestThreadBlocking.BeginExecution();
		_moduleManager = moduleManager ?? throw new ArgumentNullException("moduleManager");
		_hostPlatform = hostPlatform ?? HostPlatform.Current;
		_hostThreading = _hostPlatform.Threading;
		_hostSymbols = _hostPlatform.Symbols;
		_hostNativeInterop = _hostPlatform.NativeInterop;
		_hostMemory = _hostPlatform.Memory;
		_usePosixSignalHandling = !OperatingSystem.IsWindows() && faultHandling is null;
		_faultHandling = faultHandling ?? (OperatingSystem.IsWindows()
			? new WindowsFaultHandling(_hostMemory)
			: NullHostFaultHandling.Instance);
		try
		{
			_selfHandle = GCHandle.Alloc(this);
			_selfHandlePtr = GCHandle.ToIntPtr(_selfHandle);
			_guestTlsBaseTlsIndex = _hostThreading.AllocateTlsSlot();
			_hostRspSlotTlsIndex = _hostThreading.AllocateTlsSlot();
			if (_guestTlsBaseTlsIndex == uint.MaxValue || _hostRspSlotTlsIndex == uint.MaxValue)
			{
				throw new OutOfMemoryException("Failed to allocate native TLS slots");
			}
			_tlsGetValueAddress = _hostSymbols.GetAddress(HostRuntimeFunction.TlsGetValue);
			if (_tlsGetValueAddress == 0)
			{
				throw new InvalidOperationException("Failed to resolve kernel32!TlsGetValue");
			}
			_queryPerformanceCounterAddress = _hostSymbols.GetAddress(HostRuntimeFunction.QueryPerformanceCounter);
			if (_queryPerformanceCounterAddress == 0)
			{
				throw new InvalidOperationException("Failed to resolve kernel32!QueryPerformanceCounter");
			}
			_switchToThreadAddress = _hostSymbols.GetAddress(HostRuntimeFunction.SwitchToThread);
			_sleepAddress = _hostSymbols.GetAddress(HostRuntimeFunction.Sleep);
			if (_switchToThreadAddress == 0 || _sleepAddress == 0)
			{
				throw new InvalidOperationException("Failed to resolve kernel32 thread timing functions");
			}
			_tlsBaseAddress = (nint)_hostMemory.Allocate(0, 4096u, HostPageProtection.ReadWrite);
			if (_tlsBaseAddress == 0)
			{
				throw new OutOfMemoryException("Failed to allocate TLS base");
			}
			_ownedTlsBaseAddress = _tlsBaseAddress;
			_ownsTlsBaseAddress = true;
			SeedTlsLayout(_tlsBaseAddress);
			_hostRspSlotStorage = (nint)_hostMemory.Allocate(0, 4096u, HostPageProtection.ReadWrite);
			if (_hostRspSlotStorage == 0)
			{
				throw new OutOfMemoryException("Failed to allocate host stack slot storage");
			}
			_unresolvedReturnStub = CreateUnresolvedReturnStub();
			if (_unresolvedReturnStub == 0)
			{
				throw new OutOfMemoryException("Failed to create unresolved return stub");
			}
			_guestReturnStub = CreateGuestReturnStub();
			if (_guestReturnStub == 0)
			{
				throw new OutOfMemoryException("Failed to create guest return stub");
			}
			SetupExceptionHandler();
		}
		catch
		{
			try
			{
				Dispose();
			}
			catch
			{
				// Preserve the construction failure; cleanup is best-effort when a
				// host primitive has already failed partway through initialization.
			}

			throw;
		}
	}

	public bool TryExecute(CpuContext context, ulong entryPoint, Generation generation, IReadOnlyDictionary<ulong, string> importStubs, IReadOnlyDictionary<string, ulong> runtimeSymbols, CpuExecutionOptions executionOptions, NativeEntryReturnContract returnContract, out OrbisGen2Result result)
	{
		Console.Error.WriteLine("[LOADER][INFO] === Execute START ===");
		Console.Error.WriteLine($"[LOADER][INFO] EntryPoint: 0x{entryPoint:X16}, ImportStubs: {importStubs.Count}");
		Console.Error.WriteLine($"[LOADER][INFO] RuntimeSymbols: {runtimeSymbols.Count}");
		Console.Error.WriteLine(_moduleManager.TryGetExport("QrZZdJ8XsX0", out ExportedFunction export) ? ("[LOADER][INFO] ExportCheck fputs: " + export.LibraryName + ":" + export.Name) : "[LOADER][INFO] ExportCheck fputs: MISSING");
		Console.Error.WriteLine(_moduleManager.TryGetExport("L-Q3LEjIbgA", out ExportedFunction export2) ? ("[LOADER][INFO] ExportCheck map_direct: " + export2.LibraryName + ":" + export2.Name) : "[LOADER][INFO] ExportCheck map_direct: MISSING");
		_entryPoint = entryPoint;
		_cpuContext = context;
		_returnFallbackTarget = context[CpuRegister.Rsi];
		Volatile.Write(ref _globalFallbackTarget, _returnFallbackTarget);
		Volatile.Write(ref _globalUnresolvedReturnStub, (ulong)_unresolvedReturnStub);
		result = OrbisGen2Result.ORBIS_GEN2_OK;
		LastError = null;
		LastTrapInfo = null;
		LastSessionImportsHit = 0;
		LastSessionUniqueNidsHit = 0;
		_sessionImportEntryHits = Array.Empty<int>();
		LastImportResolutionTrace = null;
		LastImportTraceEntries = null;
		LastEntryReturnValue = null;
		_sessionEntryImportCount = 0;
		var workerImportsBefore = GetTotalGuestThreadImports();
		InitializeRuntimeSymbolIndex(runtimeSymbols);
		ResetLazyDlsymStubState();
		lock (_deferredBootstrapTraceGate)
		{
			_deferredBootstrapTraceCount = 0;
			_deferredBootstrapTraceWriteIndex = 0;
		}
		_distinctImportNidHistoryCount = 0;
		_distinctImportNidHistoryWriteIndex = 0;
		_lastDistinctImportNid = string.Empty;
		_consecutiveStrlenImports = 0;
		_strlenPreludeLogged = false;
		_logStrlenImports = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_STRLEN"), "1", StringComparison.Ordinal);
		_logStrlenBursts = _logStrlenImports ||
			string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_STRLEN_BURSTS"), "1", StringComparison.Ordinal);
		_logGuestContext = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_CONTEXT"), "1", StringComparison.Ordinal);
		_logGuestThreads = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUEST_THREADS"), "1", StringComparison.Ordinal);
		_logUsleep = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_USLEEP"), "1", StringComparison.Ordinal);
		_logFiber = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FIBER"), "1", StringComparison.Ordinal);
		_logBootstrap = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_BOOTSTRAP"), "1", StringComparison.Ordinal);
		_logAllImports = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_ALL_IMPORTS"), "1", StringComparison.Ordinal);
		_logImportSetup = IsImportSetupTracingEnabled(
			Environment.GetEnvironmentVariable("SHARPEMU_LOG_IMPORT_SETUP"));
		_logImportFrames = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_IMPORT_FRAMES"), "1", StringComparison.Ordinal);
		_logImportRecent = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_IMPORT_RECENT"), "1", StringComparison.Ordinal);
		var importTraceCapacity = executionOptions.ImportTraceLimit > 0
			? Math.Min(executionOptions.ImportTraceLimit, 4096)
			: _logImportRecent ? 64 : 0;
		_recentImportTrace = importTraceCapacity == 0
			? null
			: new RecentImportTraceBuffer(importTraceCapacity);
		_logStackCheck = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_STACK_CHK"), "1", StringComparison.Ordinal);
		_probeImportReturn = Environment.GetEnvironmentVariable("SHARPEMU_PROBE_IMPORT_RET");
		_importFilter = Environment.GetEnvironmentVariable("SHARPEMU_LOG_IMPORT_FILTER");
		_disableImportLoopGuard = string.Equals(
			Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD"),
			"1",
			StringComparison.Ordinal);
		_importLoopGuardSeconds = GetImportLoopGuardSeconds();
		_entryReturnSentinelRip = 0uL;
		_forcedGuestExit = false;
		_hostShutdownRequested = false;
		_guestTeardownRequested = false;
		_activeSessionBackend = this;
		var shutdownRegistration = HostSessionControl.RegisterShutdownHandler(RequestHostShutdown);
		_importLoopSignatureCount = 0;
		_importLoopSignatureWriteIndex = 0;
		_importLoopPatternHits = 0;
		_importLoopPatternStartTimestamp = 0;
		_importNidHashCache.Clear();
		lock (_importResultLogSampleGate)
		{
			_importResultLogSamples.Clear();
		}
		lock (_lazyCommitRangeGate)
		{
			_prtLazyCommitRanges.Clear();
		}
		ClearGuestThreads();
		_contextualUnresolvedReturnSites.Clear();
		_stallWatchdogTriggered = 0;
		_stallWatchdogStop = false;
		_patchedEa020eLookupCall = false;
		MarkExecutionProgress();
		BindTlsBase(context);
		var previousGuestThreadScheduler = GuestThreadExecution.Scheduler;
		var previousInterruptDeliverer =
			GuestThreadBlocking.DeliverInterruptForCurrentThread;
		GuestThreadExecution.Scheduler = this;
		GuestThreadBlocking.DeliverInterruptForCurrentThread =
			DeliverPendingGuestExceptionInPlaceForCurrentThread;
		ImportSetupCheckpoint? importSetupCheckpoint = null;
		TlsSetupCheckpoint? tlsSetupCheckpoint = null;
		try
		{
			if (!SetupImportStubs(importStubs, out var completedImportSetup))
			{
				if (string.IsNullOrEmpty(LastError))
				{
					LastError = "SetupImportStubs failed";
				}
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			_sessionImportEntryHits = new int[_importEntries.Length];
			importSetupCheckpoint = completedImportSetup;
			tlsSetupCheckpoint = CaptureTlsSetupCheckpoint();
			if (!TryCreateTlsHandler())
			{
				RollbackTlsSetup(tlsSetupCheckpoint);
				tlsSetupCheckpoint = null;
				RollbackImportSetup(importSetupCheckpoint);
				importSetupCheckpoint = null;
				if (string.IsNullOrEmpty(LastError))
				{
					LastError = "Failed to create TLS handler";
				}
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			if (!PatchTlsPatterns(context.Memory))
			{
				RollbackTlsSetup(tlsSetupCheckpoint);
				tlsSetupCheckpoint = null;
				RollbackImportSetup(importSetupCheckpoint);
				importSetupCheckpoint = null;
				LastError = "TLS patch preparation failed for one or more recognized guest instructions";
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			tlsSetupCheckpoint = null;
			importSetupCheckpoint = null;
			return ExecuteEntry(context, entryPoint, returnContract, out result);
		}
		catch (Exception ex)
		{
			if (tlsSetupCheckpoint is not null)
			{
				RollbackTlsSetup(tlsSetupCheckpoint);
			}
			if (importSetupCheckpoint is not null)
			{
				RollbackImportSetup(importSetupCheckpoint);
			}
			LastError = "Exception in TryExecute: " + ex.GetType().Name + ": " + ex.Message;
			Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
			Console.Error.WriteLine("[LOADER][ERROR] Stack trace: " + ex.StackTrace);
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
			return false;
		}
		finally
		{
			var workerImportsAfter = GetTotalGuestThreadImports();
			var workerImports = Math.Max(workerImportsAfter - workerImportsBefore, 0);
			LastSessionImportsHit = SaturateImportCount(
				workerImports,
				Volatile.Read(ref _sessionEntryImportCount));
			LastSessionUniqueNidsHit = CountSessionUniqueImportNids();
			var importTraceSnapshot = executionOptions.ImportTraceLimit > 0
				? _recentImportTrace?.BuildSnapshot(
					executionOptions.ImportTraceLimit,
					LastTrapInfo?.GuestThreadHandle)
				: null;
			LastImportResolutionTrace = importTraceSnapshot?.Formatted;
			LastImportTraceEntries = importTraceSnapshot?.Entries;
			shutdownRegistration.Dispose();
			if (ReferenceEquals(_activeSessionBackend, this))
			{
				_activeSessionBackend = null;
			}
			DrainDeferredBootstrapTraces();
			GuestThreadExecution.Scheduler = previousGuestThreadScheduler;
			GuestThreadBlocking.DeliverInterruptForCurrentThread =
				previousInterruptDeliverer;
			Console.Error.WriteLine("[LOADER][INFO] === Execute END (LastError: " + (LastError ?? "null") + ") ===");
		}
	}

	private TlsSetupCheckpoint CaptureTlsSetupCheckpoint() => new(
		_tlsHandlerAddress,
		_tlsPatchStubOffset,
		_tlsHandlerAllocations.Count,
		new Dictionary<(int DestinationRegister, int Displacement, bool Is64Bit, int MemorySize, bool SignExtend), nint>(_tlsLoadHelpers),
		new Dictionary<(int SourceRegister, int Displacement, bool Is64Bit), nint>(_tlsStoreHelpers),
		new Dictionary<(int Displacement, int ImmediateValue, bool Is64Bit), nint>(_tlsImmediateStoreHelpers),
		new Dictionary<(NativeTlsInstructionKind Kind, int DestinationRegister, int Displacement, bool Is64Bit), nint>(_tlsStackCanaryHelpers));

	private void RollbackTlsSetup(TlsSetupCheckpoint checkpoint)
	{
		var currentHandlerAddress = _tlsHandlerAddress;
		var currentHandlerTracked = false;
		for (var index = _tlsHandlerAllocations.Count - 1; index >= checkpoint.AllocationStart; index--)
		{
			var allocation = _tlsHandlerAllocations[index];
			currentHandlerTracked |= allocation == currentHandlerAddress;
			if (allocation != 0)
			{
				_hostMemory.Free((ulong)allocation);
			}
			_tlsHandlerAllocations.RemoveAt(index);
		}
		if (currentHandlerAddress != 0 &&
			currentHandlerAddress != checkpoint.HandlerAddress &&
			!currentHandlerTracked)
		{
			_hostMemory.Free((ulong)currentHandlerAddress);
		}

		_tlsHandlerAddress = checkpoint.HandlerAddress;
		_tlsPatchStubOffset = checkpoint.PatchStubOffset;
		_tlsLoadHelpers.Clear();
		foreach (var helper in checkpoint.LoadHelpers)
		{
			_tlsLoadHelpers.Add(helper.Key, helper.Value);
		}
		_tlsStoreHelpers.Clear();
		foreach (var helper in checkpoint.StoreHelpers)
		{
			_tlsStoreHelpers.Add(helper.Key, helper.Value);
		}
		_tlsImmediateStoreHelpers.Clear();
		foreach (var helper in checkpoint.ImmediateStoreHelpers)
		{
			_tlsImmediateStoreHelpers.Add(helper.Key, helper.Value);
		}
		_tlsStackCanaryHelpers.Clear();
		foreach (var helper in checkpoint.StackCanaryHelpers)
		{
			_tlsStackCanaryHelpers.Add(helper.Key, helper.Value);
		}
	}

	internal void RequestHostShutdown(string reason)
	{
		_hostShutdownRequested = true;
		_forcedGuestExit = true;
		_guestTeardownRequested = true;
		GuestThreadBlocking.RequestShutdown();
		LastError = string.IsNullOrWhiteSpace(reason)
			? "Host shutdown requested."
			: $"Host shutdown requested: {reason}";
		Console.Error.WriteLine($"[LOADER][INFO] {LastError}");
	}

	internal unsafe bool SetupImportStubs(IReadOnlyDictionary<ulong, string> importStubs) =>
		SetupImportStubs(importStubs, out _);

	private unsafe bool SetupImportStubs(
		IReadOnlyDictionary<ulong, string> importStubs,
		out ImportSetupCheckpoint checkpoint)
	{
		Console.Error.WriteLine($"[LOADER][INFO] Setting up {importStubs.Count} import stubs...");
		var previousEntries = _importEntries;
		var existingEntries = new Dictionary<ulong, ImportStubEntry>(previousEntries.Length);
		foreach (var entry in previousEntries)
		{
			existingEntries.TryAdd(entry.Address, entry);
		}

		var newImportCount = 0;
		var reusedCount = 0;
		foreach (var (stubAddress, nid) in importStubs)
		{
			if (!existingEntries.TryGetValue(stubAddress, out var existingEntry))
			{
				newImportCount++;
				continue;
			}

			if (!string.Equals(existingEntry.Nid, nid, StringComparison.Ordinal))
			{
				checkpoint = new ImportSetupCheckpoint(
					previousEntries,
					new List<(ulong Address, byte[] OriginalBytes)>(),
					_importHandlerTrampolines.Count);
				LastError =
					$"Import stub 0x{stubAddress:X16} is already registered for {existingEntry.Nid}, not {nid}";
				return false;
			}

			reusedCount++;
		}

		var expandedEntries = new ImportStubEntry[previousEntries.Length + newImportCount];
		previousEntries.CopyTo(expandedEntries, 0);
		var attemptAllocationStart = _importHandlerTrampolines.Count;
		var patchedStubs = new List<(ulong Address, byte[] OriginalBytes)>();
		var setupCheckpoint = new ImportSetupCheckpoint(previousEntries, patchedStubs, attemptAllocationStart);
		checkpoint = setupCheckpoint;
		var importAddresses = new HashSet<ulong>(existingEntries.Keys);
		foreach (var stubAddress in importStubs.Keys)
		{
			importAddresses.Add(stubAddress);
		}
		var localIndex = 0;
		var patchedCount = reusedCount;
		var redirectCount = 0;

		bool TryPatchTracked(ulong stubAddress, nint targetAddress)
		{
			var originalBytes = new ReadOnlySpan<byte>((void*)stubAddress, 16).ToArray();
			patchedStubs.Add((stubAddress, originalBytes));
			if (!PatchImportStub((nint)stubAddress, targetAddress))
			{
				return false;
			}

			return true;
		}

		bool FailAttempt(string error)
		{
			LastError = error;
			RollbackImportSetup(setupCheckpoint);
			return false;
		}

		try
		{
			foreach (var (stubAddress, nid) in importStubs)
			{
				if (existingEntries.ContainsKey(stubAddress))
				{
					continue;
				}

				_ = _moduleManager.TryGetExport(nid, out var resolvedExport);
				var entryIndex = previousEntries.Length + localIndex;
				expandedEntries[entryIndex] = new ImportStubEntry(stubAddress, nid, resolvedExport);
				if ((stubAddress >= 34393242112L && stubAddress <= 34393242624L) ||
					(stubAddress >= 34393258496L && stubAddress <= 34393259008L))
				{
					if (resolvedExport is not null)
					{
						Console.Error.WriteLine($"[LOADER][INFO] ImportStubMap: 0x{stubAddress:X16} -> {resolvedExport.LibraryName}:{resolvedExport.Name} ({nid})");
					}
					else
					{
						Console.Error.WriteLine($"[LOADER][INFO] ImportStubMap: 0x{stubAddress:X16} -> {nid}");
					}
				}

				if (TryResolveDirectImportTarget(nid, out var targetAddress, out var resolvedSymbol) &&
					!importAddresses.Contains(targetAddress))
				{
					if (_logImportSetup)
					{
						Console.Error.WriteLine($"[LOADER][DEBUG] SetupImportStubs: Direct bridge for {nid} -> 0x{targetAddress:X16}");
					}
					if (!TryPatchTracked(stubAddress, (nint)targetAddress))
					{
						return FailAttempt($"Failed to patch direct import stub at 0x{stubAddress:X16}");
					}
					redirectCount++;
					patchedCount++;
					if (redirectCount <= 48)
					{
						Console.Error.WriteLine(
							$"[LOADER][INFO] LLE redirect: 0x{stubAddress:X16} {nid} -> {resolvedSymbol}@0x{targetAddress:X16}");
					}
					localIndex++;
					continue;
				}

				if (TryCreateNativeImportIntrinsic(nid, out var intrinsicAddress))
				{
					if (!TryPatchTracked(stubAddress, intrinsicAddress))
					{
						return FailAttempt($"Failed to patch native intrinsic import stub at 0x{stubAddress:X16}");
					}
					patchedCount++;
					localIndex++;
					continue;
				}

				var trampoline = CreateImportHandlerTrampoline(entryIndex);
				if (trampoline == 0)
				{
					return FailAttempt("Failed to create import trampoline for NID " + nid);
				}
				if (_logImportSetup)
				{
					Console.Error.WriteLine($"[LOADER][DEBUG] SetupImportStubs: Trampoline for {nid} -> 0x{trampoline:X16}");
				}
				if (!TryPatchTracked(stubAddress, trampoline))
				{
					return FailAttempt($"Failed to patch import stub at 0x{stubAddress:X16}");
				}
				patchedCount++;
				localIndex++;
			}

			_importEntries = expandedEntries;
			Console.Error.WriteLine(
				$"[LOADER][INFO] Setup {patchedCount}/{importStubs.Count} import stubs " +
				$"(reused={reusedCount}, direct bridge, lle_redirects={redirectCount})");
			return patchedCount == importStubs.Count;
		}
		catch
		{
			RollbackImportSetup(setupCheckpoint);
			throw;
		}
	}

	private unsafe void RollbackImportSetup(ImportSetupCheckpoint checkpoint)
	{
		for (var index = checkpoint.PatchedStubs.Count - 1; index >= 0; index--)
		{
			var (address, originalBytes) = checkpoint.PatchedStubs[index];
			uint oldProtection = 0;
			if (!_hostMemory.Protect(address, (nuint)originalBytes.Length, HostPageProtection.ReadWrite, out oldProtection))
			{
				Console.Error.WriteLine($"[LOADER][ERROR] Failed to restore import stub at 0x{address:X16} during setup rollback");
				continue;
			}

			try
			{
				originalBytes.CopyTo(new Span<byte>((void*)address, originalBytes.Length));
			}
			finally
			{
				_hostMemory.ProtectRaw(address, (nuint)originalBytes.Length, oldProtection, out _);
				_hostMemory.FlushInstructionCache(address, (nuint)originalBytes.Length);
			}
		}

		for (var index = _importHandlerTrampolines.Count - 1; index >= checkpoint.AttemptAllocationStart; index--)
		{
			var allocation = _importHandlerTrampolines[index];
			if (allocation != 0)
			{
				_hostMemory.Free((ulong)allocation);
			}
			_importHandlerTrampolines.RemoveAt(index);
		}

		_importEntries = checkpoint.PreviousEntries;
	}

	internal unsafe bool TryCreateNativeImportIntrinsic(string nid, out nint address)
	{
		if (IsHlePreferredNid(nid))
		{
			address = 0;
			return false;
		}

		if (nid == "1jfXLRVzisc" &&
			string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_USLEEP"), "1", StringComparison.Ordinal))
		{
			address = 0;
			return false;
		}

		ReadOnlySpan<byte> code = nid switch
		{
			"-2IRUCO--PM" =>
			[
				0x0F, 0x31,
				0x48, 0xC1, 0xE2, 0x20,
				0x48, 0x09, 0xD0,
				0xC3,
			],
			"fgxnMeTNUtY" =>
			[
				0x48, 0x83, 0xEC, 0x28,
				0x48, 0x8D, 0x4C, 0x24, 0x20,
				0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0xFF, 0xD0,
				0x48, 0x8B, 0x44, 0x24, 0x20,
				0x48, 0x83, 0xC4, 0x28,
				0xC3,
			],
			"1jfXLRVzisc" =>
			[
				0x48, 0x85, 0xFF,
				0x74, 0x1D,
				0x48, 0x81, 0xFF, 0xE8, 0x03, 0x00, 0x00,
				0x73, 0x17,
				0x48, 0x83, 0xEC, 0x28,
				0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0xFF, 0xD0,
				0x48, 0x83, 0xC4, 0x28,
				0x31, 0xC0,
				0xC3,
				0x48, 0x89, 0xF8,
				0x48, 0x05, 0xE7, 0x03, 0x00, 0x00,
				0x31, 0xD2,
				0xB9, 0xE8, 0x03, 0x00, 0x00,
				0x48, 0xF7, 0xF1,
				0x89, 0xC1,
				0x48, 0x83, 0xEC, 0x28,
				0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0xFF, 0xD0,
				0x48, 0x83, 0xC4, 0x28,
				0x31, 0xC0,
				0xC3,
			],
			"j4ViWNHEgww" =>
			[
				0x31, 0xC0,
				0x48, 0xC7, 0xC1, 0xFF, 0xFF, 0xFF, 0xFF,
				0xF2, 0xAE,
				0x48, 0xF7, 0xD1,
				0x48, 0x8D, 0x41, 0xFF,
				0xC3,
			],
			"5jNubw4vlAA" =>
			[
				0x31, 0xC0,
				0x48, 0x85, 0xF6,
				0x74, 0x0E,
				0x80, 0x3C, 0x07, 0x00,
				0x74, 0x08,
				0x48, 0xFF, 0xC0,
				0x48, 0x39, 0xF0,
				0x72, 0xF2,
				0xC3,
			],
			"LHMrG7e8G78" or "WkkeywLJcgU" =>
			[
				0x31, 0xC0,
				0x66, 0x83, 0x3C, 0x47, 0x00,
				0x74, 0x05,
				0x48, 0xFF, 0xC0,
				0xEB, 0xF4,
				0xC3,
			],
			"Ovb2dSJOAuE" =>
			[
				0x0F, 0xB6, 0x07,
				0x0F, 0xB6, 0x16,
				0x29, 0xD0,
				0x75, 0x0C,
				0x84, 0xD2,
				0x74, 0x08,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xC6,
				0xEB, 0xEA,
				0xC3,
			],
			"aesyjrHVWy4" =>
			[
				0x31, 0xC0,
				0x48, 0x85, 0xD2,
				0x74, 0x19,
				0x0F, 0xB6, 0x07,
				0x0F, 0xB6, 0x0E,
				0x29, 0xC8,
				0x75, 0x0F,
				0x84, 0xC9,
				0x74, 0x0B,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xC6,
				0x48, 0xFF, 0xCA,
				0x75, 0xE7,
				0xC3,
			],
			"AV6ipCNa4Rw" =>
			[
				0x0F, 0xB6, 0x07,
				0x0F, 0xB6, 0x16,
				0x8D, 0x48, 0xBF,
				0x83, 0xF9, 0x19,
				0x77, 0x03,
				0x83, 0xC0, 0x20,
				0x8D, 0x4A, 0xBF,
				0x83, 0xF9, 0x19,
				0x77, 0x03,
				0x83, 0xC2, 0x20,
				0x29, 0xD0,
				0x75, 0x0C,
				0x85, 0xD2,
				0x74, 0x08,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xC6,
				0xEB, 0xD4,
				0xC3,
			],
			"viiwFMaNamA" =>
			[
				0x0F, 0xB6, 0x16,
				0x84, 0xD2,
				0x74, 0x2D,
				0x0F, 0xB6, 0x07,
				0x84, 0xC0,
				0x74, 0x2A,
				0x38, 0xD0,
				0x75, 0x1D,
				0x4C, 0x8D, 0x47, 0x01,
				0x4C, 0x8D, 0x4E, 0x01,
				0x41, 0x0F, 0xB6, 0x09,
				0x84, 0xC9,
				0x74, 0x12,
				0x41, 0x38, 0x08,
				0x75, 0x08,
				0x49, 0xFF, 0xC0,
				0x49, 0xFF, 0xC1,
				0xEB, 0xEB,
				0x48, 0xFF, 0xC7,
				0xEB, 0xD3,
				0x48, 0x89, 0xF8,
				0xC3,
				0x31, 0xC0,
				0xC3,
			],
			"pNtJdE3x49E" or "fV2xHER+bKE" =>
			[
				0x0F, 0xB7, 0x07,
				0x0F, 0xB7, 0x16,
				0x29, 0xD0,
				0x75, 0x0F,
				0x66, 0x85, 0xD2,
				0x74, 0x0A,
				0x48, 0x83, 0xC7, 0x02,
				0x48, 0x83, 0xC6, 0x02,
				0xEB, 0xE7,
				0xC3,
			],
			"E8wCoUEbfzk" =>
			[
				0x31, 0xC0,
				0x48, 0x85, 0xD2,
				0x74, 0x1C,
				0x0F, 0xB7, 0x07,
				0x0F, 0xB7, 0x0E,
				0x29, 0xC8,
				0x75, 0x12,
				0x66, 0x85, 0xC9,
				0x74, 0x0D,
				0x48, 0x83, 0xC7, 0x02,
				0x48, 0x83, 0xC6, 0x02,
				0x48, 0xFF, 0xCA,
				0x75, 0xE4,
				0xC3,
			],
			"kiZSXIWd9vg" =>
			[
				0x48, 0x89, 0xF8,
				0x8A, 0x16,
				0x88, 0x17,
				0x48, 0xFF, 0xC6,
				0x48, 0xFF, 0xC7,
				0x84, 0xD2,
				0x75, 0xF2,
				0xC3,
			],
			"6sJWiWSRuqk" =>
			[
				0x48, 0x89, 0xF8,
				0x48, 0x85, 0xD2,
				0x74, 0x20,
				0x8A, 0x0E,
				0x88, 0x0F,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xCA,
				0x74, 0x14,
				0x84, 0xC9,
				0x74, 0x05,
				0x48, 0xFF, 0xC6,
				0xEB, 0xEB,
				0xC6, 0x07, 0x00,
				0x48, 0xFF, 0xC7,
				0x48, 0xFF, 0xCA,
				0x75, 0xF5,
				0xC3,
			],
			// memcpy: guarded native copy. The first "rep movsb" intrinsic had no bounds/null
			// checking and crashed with a read at -1 right after a null-dst memset recovery in
			// the NGS2 audio streaming code path, so it was temporarily pulled in favor of the
			// safe C# HLE memcpy. That detour costs a full import dispatch per call - far too
			// slow for a function this hot - so this stub keeps the native leaf path and adds
			// the same guards as memset below: it silently returns dst without copying when dst
			// or src is null/low-page (< 0x10000) or outside canonical user space, or when len
			// is 0 or absurd (> 512MB).
			"Q3VBxCXhUHs" =>
			[
				0x48, 0x89, 0xF8,                                           // mov rax, rdi (return dst)
				0x48, 0x81, 0xFF, 0x00, 0x00, 0x01, 0x00,                   // cmp rdi, 0x10000
				0x72, 0x31,                                                 // jb done
				0x49, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, // mov r8, 0x800000000000
				0x4C, 0x39, 0xC7,                                           // cmp rdi, r8
				0x73, 0x22,                                                 // jae done
				0x48, 0x81, 0xFE, 0x00, 0x00, 0x01, 0x00,                   // cmp rsi, 0x10000
				0x72, 0x19,                                                 // jb done
				0x4C, 0x39, 0xC6,                                           // cmp rsi, r8
				0x73, 0x14,                                                 // jae done
				0x48, 0x81, 0xFA, 0x00, 0x00, 0x00, 0x20,                   // cmp rdx, 0x20000000
				0x77, 0x0B,                                                 // ja done
				0x48, 0x85, 0xD2,                                           // test rdx, rdx
				0x74, 0x06,                                                 // jz done
				0x48, 0x89, 0xD1,                                           // mov rcx, rdx
				0xFC,                                                       // cld
				0xF3, 0xA4,                                                 // rep movsb
				0xC3,                                                       // done: ret
			],
			// memset: guarded native fill. An earlier unguarded version crashed with a write AV
			// at address 0 (NGS2 audio streaming init memsets a never-populated buffer field),
			// so this one mirrors the HLE guards and silently returns dst without writing when
			// dst is null/low-page (< 0x10000), dst is outside canonical user space, or len is
			// absurd (> 512MB, e.g. the 0x27060035 / sign-extended values NGS2 passes). Routing
			// memset through the HLE trampoline instead is not viable: parse/streaming loops
			// issue hundreds of thousands of small memsets back-to-back, which crawls at
			// dispatch speed and looks like a repeating-import hang to the loop guard.
			// _sigprocmask: the HLE handler (KernelRuntimeCompatExports.Sigprocmask) is a pure
			// no-op returning 0 that never writes oldset, so this is behavior-identical. The
			// game's bundled libc queries the mask (set=NULL) once per iteration in its font/
			// parse loops - hundreds of thousands of back-to-back calls that both crawl at
			// dispatch speed and read as a repeating-import hang to the loop guard.
			"6xVpy0Fdq+I" =>
			[
				0x31, 0xC0, // xor eax, eax
				0xC3,       // ret
			],
			"8zTFvBIAIN8" =>
			[
				0x48, 0x89, 0xF8,                                           // mov rax, rdi (return dst)
				0x48, 0x81, 0xFF, 0x00, 0x00, 0x01, 0x00,                   // cmp rdi, 0x10000
				0x72, 0x2B,                                                 // jb done
				0x49, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, // mov r8, 0x800000000000
				0x4C, 0x39, 0xC7,                                           // cmp rdi, r8
				0x73, 0x1C,                                                 // jae done
				0x48, 0x81, 0xFA, 0x00, 0x00, 0x00, 0x20,                   // cmp rdx, 0x20000000
				0x77, 0x13,                                                 // ja done
				0x48, 0x85, 0xD2,                                           // test rdx, rdx
				0x74, 0x0E,                                                 // jz done
				0x48, 0x89, 0xD1,                                           // mov rcx, rdx
				0x49, 0x89, 0xF9,                                           // mov r9, rdi
				0x89, 0xF0,                                                 // mov eax, esi
				0xFC,                                                       // cld
				0xF3, 0xAA,                                                 // rep stosb
				0x4C, 0x89, 0xC8,                                           // mov rax, r9
				0xC3,                                                       // done: ret
			],
			_ => default,
		};
		if (code.IsEmpty)
		{
			address = 0;
			return false;
		}

		const uint intrinsicAllocationSize = 128u;
		void* memory = (void*)_hostMemory.Allocate(0, intrinsicAllocationSize, HostPageProtection.ReadWrite);
		if (memory == null)
		{
			address = 0;
			return false;
		}

		code.CopyTo(new Span<byte>(memory, code.Length));
		if (nid == "fgxnMeTNUtY")
		{
			*(nint*)((byte*)memory + 11) = _queryPerformanceCounterAddress;
		}
		else if (nid == "1jfXLRVzisc")
		{
			*(nint*)((byte*)memory + 20) = _switchToThreadAddress;
			*(nint*)((byte*)memory + 64) = _sleepAddress;
		}
		uint oldProtect = 0;
		if (!_hostMemory.Protect((ulong)memory, intrinsicAllocationSize, HostPageProtection.ReadExecute, out oldProtect))
		{
			_hostMemory.Free((ulong)memory);
			address = 0;
			return false;
		}

		_hostMemory.FlushInstructionCache((ulong)memory, (nuint)code.Length);
		address = (nint)memory;
		_importHandlerTrampolines.Add(address);
		return true;
	}

	private bool TryResolveDirectImportTarget(string nid, out ulong targetAddress, out string resolvedSymbol)
	{
		targetAddress = 0uL;
		resolvedSymbol = string.Empty;
		if (string.IsNullOrWhiteSpace(nid) || string.Equals(nid, RuntimeStubNids.KernelDynlibDlsym, StringComparison.Ordinal))
		{
			return false;
		}
		if (IsHlePreferredNid(nid))
		{
			return false;
		}

		if (_moduleManager.TryGetExport(nid, out ExportedFunction export))
		{
			if (IsKernelLibrary(export.LibraryName))
			{
				if (_logImportSetup)
				{
					Console.Error.WriteLine($"[LOADER][DEBUG] TryResolveDirectImportTarget: {nid} ({export.LibraryName}:{export.Name}) -> HLE (kernel library)");
				}
				return false;
			}
			if (!IsLibcLibrary(export.LibraryName) || !PreferLleForLibcExport(export.Name))
			{
				return false;
			}
			if (TryResolveRuntimeSymbolAddress(nid, out var value2) && IsDirectImportTargetUsable(value2))
			{
				targetAddress = value2;
				resolvedSymbol = nid;
				return true;
			}
			foreach (string item in EnumerateRuntimeSymbolCandidates(export.Name))
			{
				if (TryResolveRuntimeSymbolAddress(item, out value2) && IsDirectImportTargetUsable(value2))
				{
					targetAddress = value2;
					resolvedSymbol = item;
					return true;
				}
			}
			return false;
		}

		if (_logImportSetup)
		{
			Console.Error.WriteLine($"[LOADER][DEBUG] TryResolveDirectImportTarget: {nid} not in HLE table, checking runtime symbols...");
		}

		if (TryResolveRuntimeSymbolAddress(nid, out var directValue) && IsDirectImportTargetUsable(directValue))
		{
			targetAddress = directValue;
			resolvedSymbol = nid;
			if (_logImportSetup)
			{
				Console.Error.WriteLine($"[LOADER][DEBUG] TryResolveDirectImportTarget: {nid} -> runtime symbol 0x{targetAddress:X16}");
			}
			return true;
		}

		if (Aerolib.Instance.TryGetByNid(nid, out var symbolByNid))
		{
			if (!PreferLleForLibcExport(symbolByNid.ExportName))
			{
				return false;
			}
			foreach (string item in EnumerateRuntimeSymbolCandidates(symbolByNid.ExportName))
			{
				if (TryResolveRuntimeSymbolAddress(item, out var value) && IsDirectImportTargetUsable(value))
				{
					targetAddress = value;
					resolvedSymbol = item;
					return true;
				}
			}
		}
		return false;
	}

	internal static bool IsHlePreferredNid(string nid)
	{
		return string.Equals(nid, "QrZZdJ8XsX0", StringComparison.Ordinal) ||
			string.Equals(nid, "Q3VBxCXhUHs", StringComparison.Ordinal);
	}

	private static bool IsLibcLibrary(string libraryName)
	{
		return !string.IsNullOrWhiteSpace(libraryName) && libraryName.IndexOf("libc", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool IsKernelLibrary(string libraryName)
	{
		if (string.IsNullOrWhiteSpace(libraryName))
		{
			return false;
		}
		return libraryName.Equals("libKernel", StringComparison.OrdinalIgnoreCase) ||
			   libraryName.Equals("libKernelExt", StringComparison.OrdinalIgnoreCase) ||
			   libraryName.IndexOf("Kernel", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private bool PreferLleForLibcExport(string exportName)
	{
		if (string.IsNullOrWhiteSpace(exportName))
		{
			return false;
		}
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_LLE_LIBC"), "1", StringComparison.Ordinal))
		{
			return false;
		}
		var value = Environment.GetEnvironmentVariable("SHARPEMU_LLE_LIBC_SAFE_ONLY");
		if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LLE_LIBC_ALL"), "1", StringComparison.Ordinal))
		{
			return true;
		}
		if (IsLibcAllocatorExport(exportName))
		{
			return CanUseLleLibcAllocatorFamily();
		}
		if (string.Equals(value, "0", StringComparison.Ordinal))
		{
			return true;
		}
		if (string.Equals(value, "1", StringComparison.Ordinal))
		{
			return IsSafeLleLibcExport(exportName);
		}
		return IsSafeLleLibcExport(exportName);
	}

	private bool CanUseLleLibcAllocatorFamily()
	{
		return HasUsableLleLibcExport("gQX+4GDQjpM", "malloc") &&
			   HasUsableLleLibcExport("tIhsqj0qsFE", "free") &&
			   HasUsableLleLibcExport("2X5agFjKxMc", "calloc") &&
			   HasUsableLleLibcExport("Y7aJ1uydPMo", "realloc") &&
			   HasUsableLleLibcExport("Ujf3KzMvRmI", "memalign") &&
			   HasUsableLleLibcExport("2Btkg8k24Zg", "aligned_alloc") &&
			   HasUsableLleLibcExport("cVSk9y8URbc", "posix_memalign");
	}

	private bool HasUsableLleLibcExport(string nid, string exportName)
	{
		if (TryResolveRuntimeSymbolAddress(nid, out var address) && IsDirectImportTargetUsable(address))
		{
			return true;
		}

		foreach (var candidate in EnumerateRuntimeSymbolCandidates(exportName))
		{
			if (TryResolveRuntimeSymbolAddress(candidate, out address) && IsDirectImportTargetUsable(address))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsLibcAllocatorExport(string exportName)
	{
		return exportName switch
		{
			"malloc" or
			"free" or
			"calloc" or
			"realloc" or
			"memalign" or
			"aligned_alloc" or
			"posix_memalign" or
			"malloc_usable_size" => true,
			_ => false,
		};
	}

	private static bool IsSafeLleLibcExport(string exportName)
	{
		return exportName switch
		{
			"memmove" or
			// memset/memcpy excluded: the raw LLE routines have no null/bounds guard and crash
			// with an access violation on bad pointers (observed hit during Quake's CL_Init,
			// where a still-unidentified upstream bug calls memcpy/memset with a null
			// destination). Both are instead served by the guarded native intrinsics in
			// TryCreateNativeImportIntrinsic, which fail safely without leaving the leaf path.
			"memcmp" or
			// _Getpctype must come from the game's own Dinkumware libc when one is bundled:
			// it returns a pointer to that CRT's ctype bitmask table, whose bit layout
			// (_DI=0x20, _SP=0x04, _BB=0x80, ...) differs from the MSVC-style table the HLE
			// fallback used to serve. Serving the wrong layout made the bundled printf engine
			// render every Sys_Error message as an empty string (isdigit misfired during
			// %-directive parsing) and made mcpp drop 'a'-'f' from identifiers ("texture" ->
			// "txtur", the 0x80 bit reads as _BB/control there). _Getptolower/_Getptoupper
			// already resolve to the bundled module because no HLE export shadows them; this
			// keeps _Getpctype consistent with them. It is a pure accessor returning a pointer
			// to a const table, so it is also the cheapest possible LLE call - important
			// because parsers hit it once per input character.
			"_Getpctype" => true,
			_ => false,
		};
	}

	private static IEnumerable<string> EnumerateRuntimeSymbolCandidates(string exportName)
	{
		if (string.IsNullOrWhiteSpace(exportName))
		{
			yield break;
		}
		yield return exportName;
		if (exportName.StartsWith("_", StringComparison.Ordinal))
		{
			if (exportName.Length > 1)
			{
				yield return exportName[1..];
			}
			yield break;
		}
		yield return "_" + exportName;
	}

	private bool IsDirectImportTargetUsable(ulong address)
	{
		if (address < 65536 || IsUnresolvedSentinel(address) ||
			_cpuContext is null || !TryGetVirtualMemory(_cpuContext, out var virtualMemory))
		{
			return false;
		}

		foreach (var region in virtualMemory.SnapshotRegions())
		{
			if ((region.Protection & ProgramHeaderFlags.Execute) != 0 &&
				ContainsAddress(region.VirtualAddress, region.MemorySize, address))
			{
				return true;
			}
		}

		return false;
	}

	private unsafe void BindTlsBase(CpuContext context)
	{
		nint num = (nint)((context.FsBase != 0L) ? context.FsBase : context.GsBase);
		if (num == 0)
		{
			num = _tlsBaseAddress;
		}
		if (!HasActiveExecutionThread && num != _tlsBaseAddress)
		{
			_tlsBaseAddress = num;
			_ownsTlsBaseAddress = _tlsBaseAddress == _ownedTlsBaseAddress;
		}
		if (num != 0)
		{
			context.FsBase = (ulong)num;
			context.GsBase = (ulong)num;
			SeedTlsLayout(num);
			_hostThreading.SetTlsValue(_guestTlsBaseTlsIndex, num);
		}
	}

	private unsafe static void SeedTlsLayout(nint tlsBase)
	{
		ulong num = (ulong)tlsBase;
		*(ulong*)tlsBase = num;
		if (*(ulong*)(tlsBase + 16) == 0)
		{
			*(ulong*)(tlsBase + 16) = num;
		}
		*(long*)(tlsBase + 40) = -4548986510476657986L;
		*(ulong*)(tlsBase + 96) = num;
	}

	private unsafe void UpdateTlsHandlerBase(nint tlsBase)
	{
		if (_tlsHandlerAddress == 0)
		{
			return;
		}

		uint oldProtect = default;
		if (!_hostMemory.Protect((ulong)(void*)_tlsHandlerAddress, 16u, HostPageProtection.ReadWrite, out oldProtect))
		{
			return;
		}

		try
		{
			*(long*)((byte*)_tlsHandlerAddress + 2) = tlsBase;
		}
		finally
		{
			_hostMemory.ProtectRaw((ulong)(void*)_tlsHandlerAddress, 16u, oldProtect, out oldProtect);
			_hostMemory.FlushInstructionCache((ulong)(void*)_tlsHandlerAddress, 16u);
		}
	}

	private unsafe bool TryPrepareGuestContextTransfer(
		GuestCpuContinuation target,
		out nint frameAddress,
		out nint transferStub,
		out string? error)
	{
		frameAddress = 0;
		transferStub = 0;
		error = null;
		if (ActiveCpuContext is not { } activeContext)
		{
			error = "guest context transfer without an active CPU context";
			return false;
		}
		if (!TryValidateGuestContextTransferTarget(activeContext.Memory, target, out error))
		{
			return false;
		}
		if (!activeContext.TryWriteUInt64(target.Rsp - sizeof(ulong), target.Rip))
		{
			error = $"guest context transfer slot is not writable at 0x{target.Rsp - sizeof(ulong):X16}";
			return false;
		}

		transferStub = GetOrCreateGuestContextTransferStub();
		if (transferStub == 0)
		{
			error = "failed to allocate guest context transfer stub";
			return false;
		}

		frameAddress = _guestContextTransferFrames.Value;
		if (frameAddress == 0)
		{
			error = "failed to allocate guest context transfer frame";
			return false;
		}

		var frame = (ulong*)frameAddress;
		frame[0] = target.Rip;
		frame[1] = target.Rsp;
		frame[2] = target.Rax;
		frame[3] = target.Rcx;
		frame[4] = target.Rdx;
		frame[5] = target.Rbx;
		frame[6] = target.Rbp;
		frame[7] = target.Rsi;
		frame[8] = target.Rdi;
		frame[9] = target.R8;
		frame[10] = target.R9;
		frame[11] = target.R12;
		frame[12] = target.R13;
		frame[13] = target.R14;
		frame[14] = target.R15;
		return true;
	}

	internal static bool TryValidateGuestContextTransferTarget(
		ICpuMemory memory,
		in GuestCpuContinuation target,
		out string? error)
	{
		if (target.Rip < 65536 || target.Rsp < sizeof(ulong))
		{
			error = $"invalid guest context transfer target rip=0x{target.Rip:X16} rsp=0x{target.Rsp:X16}";
			return false;
		}

		Span<byte> ripProbe = stackalloc byte[1];
		if (!memory.TryRead(target.Rip, ripProbe))
		{
			error =
				$"guest context transfer target rip=0x{target.Rip:X16} is not mapped guest memory " +
				$"(rsp=0x{target.Rsp:X16})";
			return false;
		}

		error = null;
		return true;
	}

	private unsafe nint GetOrCreateGuestContextTransferStub()
	{
		if (Volatile.Read(ref _guestContextTransferStub) != 0)
		{
			return _guestContextTransferStub;
		}

		lock (_guestContextTransferStubGate)
		{
			if (_guestContextTransferStub != 0)
			{
				return _guestContextTransferStub;
			}

			const uint stubSize = 128;
			var code = (byte*)_hostMemory.Allocate(0, stubSize, HostPageProtection.ReadWrite);
			if (code == null)
			{
				return 0;
			}

			var offset = 0;
			void Emit(byte value) => code[offset++] = value;
			void EmitLoadFromR11(int register, byte displacement)
			{
				Emit((byte)(0x49 | (register >= 8 ? 0x04 : 0x00)));
				Emit(0x8B);
				Emit((byte)(0x40 | ((register & 7) << 3) | 0x03));
				Emit(displacement);
			}

			Emit(0x49); Emit(0x89); Emit(0xC3); // mov r11, rax
			EmitLoadFromR11(10, 0);             // target RIP
			EmitLoadFromR11(4, 8);              // rsp
			EmitLoadFromR11(1, 24);             // rcx
			EmitLoadFromR11(2, 32);             // rdx
			EmitLoadFromR11(3, 40);             // rbx
			EmitLoadFromR11(5, 48);             // rbp
			EmitLoadFromR11(6, 56);             // rsi
			EmitLoadFromR11(7, 64);             // rdi
			EmitLoadFromR11(8, 72);             // r8
			EmitLoadFromR11(9, 80);             // r9
			EmitLoadFromR11(12, 88);            // r12
			EmitLoadFromR11(13, 96);            // r13
			EmitLoadFromR11(14, 104);           // r14
			EmitLoadFromR11(15, 112);           // r15
			EmitLoadFromR11(0, 16);             // rax
			Emit(0x41); Emit(0xFF); Emit(0xE2); // jmp r10

			uint oldProtect = 0;
			if (!_hostMemory.Protect((ulong)code, stubSize, HostPageProtection.ReadExecute, out oldProtect))
			{
				_hostMemory.Free((ulong)code);
				return 0;
			}

			_hostMemory.FlushInstructionCache((ulong)code, stubSize);
			Volatile.Write(ref _guestContextTransferStub, (nint)code);
			return (nint)code;
		}
	}

	private unsafe nint CreateImportHandlerTrampoline(int importIndex)
	{
		const uint trampolineSize = 512;
		void* ptr = (void*)_hostMemory.Allocate(0, trampolineSize, HostPageProtection.ReadWrite);
		if (ptr == null)
		{
			return 0;
		}
		_importHandlerTrampolines.Add((nint)ptr);
		try
		{
			byte* ptr2 = (byte*)ptr;
			int num = 0;
			ptr2[num++] = 65;
			ptr2[num++] = 87;
			ptr2[num++] = 65;
			ptr2[num++] = 86;
			ptr2[num++] = 65;
			ptr2[num++] = 85;
			ptr2[num++] = 65;
			ptr2[num++] = 84;
			ptr2[num++] = 85;
			ptr2[num++] = 83;
			ptr2[num++] = 65;
			ptr2[num++] = 81;
			ptr2[num++] = 65;
			ptr2[num++] = 80;
			ptr2[num++] = 81;
			ptr2[num++] = 82;
			ptr2[num++] = 86;
			ptr2[num++] = 87;
			ptr2[num++] = 0x48; ptr2[num++] = 0x81; ptr2[num++] = 0xEC;
			*(uint*)(ptr2 + num) = 0xB0;
			num += 4;
			ptr2[num++] = 0x48; ptr2[num++] = 0x89; ptr2[num++] = 0x04; ptr2[num++] = 0x24;
			ptr2[num++] = 0x4C; ptr2[num++] = 0x89; ptr2[num++] = 0x54; ptr2[num++] = 0x24; ptr2[num++] = 0x08;
			ptr2[num++] = 0x4C; ptr2[num++] = 0x89; ptr2[num++] = 0x5C; ptr2[num++] = 0x24; ptr2[num++] = 0x10;
			ptr2[num++] = 0x0F; ptr2[num++] = 0xAE; ptr2[num++] = 0x5C; ptr2[num++] = 0x24; ptr2[num++] = 0x18;
			ptr2[num++] = 0xD9; ptr2[num++] = 0x7C; ptr2[num++] = 0x24; ptr2[num++] = 0x1C;
			for (var xmm = 0; xmm < 8; xmm++)
			{
				ptr2[num++] = 0xF3;
				ptr2[num++] = 0x0F;
				ptr2[num++] = 0x7F;
				ptr2[num++] = (byte)(0x84 | (xmm << 3));
				ptr2[num++] = 0x24;
				*(uint*)(ptr2 + num) = (uint)(0x30 + (xmm * 0x10));
				num += 4;
			}
			ptr2[num++] = 0x4C; ptr2[num++] = 0x8D; ptr2[num++] = 0xA4; ptr2[num++] = 0x24;
			*(uint*)(ptr2 + num) = 0xB0;
			num += 4;
			ptr2[num++] = 72;
			ptr2[num++] = 131;
			ptr2[num++] = 236;
			ptr2[num++] = 40;
			ptr2[num++] = 185;
			*(uint*)(ptr2 + num) = _hostRspSlotTlsIndex;
			num += 4;
			ptr2[num++] = 72;
			ptr2[num++] = 184;
			*(long*)(ptr2 + num) = _tlsGetValueAddress;
			num += 8;
			ptr2[num++] = byte.MaxValue;
			ptr2[num++] = 208;
			ptr2[num++] = 72;
			ptr2[num++] = 131;
			ptr2[num++] = 196;
			ptr2[num++] = 40;
			ptr2[num++] = 0x4C; ptr2[num++] = 0x8D; ptr2[num++] = 0xA4; ptr2[num++] = 0x24;
			*(uint*)(ptr2 + num) = 0xB0;
			num += 4;
			ptr2[num++] = 73;
			ptr2[num++] = 137;
			ptr2[num++] = 195;
			ptr2[num++] = 73;
			ptr2[num++] = 139;
			ptr2[num++] = 35;
			ptr2[num++] = 72;
			ptr2[num++] = 131;
			ptr2[num++] = 236;
			ptr2[num++] = 56;
			ptr2[num++] = 0x4C; ptr2[num++] = 0x89; ptr2[num++] = 0x64; ptr2[num++] = 0x24; ptr2[num++] = 0x28;
			ptr2[num++] = 72;
			ptr2[num++] = 185;
			*(long*)(ptr2 + num) = _selfHandlePtr;
			num += 8;
			ptr2[num++] = 186;
			*(int*)(ptr2 + num) = importIndex;
			num += 4;
			ptr2[num++] = 77;
			ptr2[num++] = 137;
			ptr2[num++] = 224;
			ptr2[num++] = 72;
			ptr2[num++] = 184;
			*(long*)(ptr2 + num) = ImportGatewayPtr;
			num += 8;
			ptr2[num++] = byte.MaxValue;
			ptr2[num++] = 208;
			ptr2[num++] = 0x4C; ptr2[num++] = 0x8B; ptr2[num++] = 0x64; ptr2[num++] = 0x24; ptr2[num++] = 0x28;
			ptr2[num++] = 72;
			ptr2[num++] = 131;
			ptr2[num++] = 196;
			ptr2[num++] = 56;
			for (var xmm = 0; xmm < 2; xmm++)
			{
				ptr2[num++] = 0xF3;
				ptr2[num++] = 0x41;
				ptr2[num++] = 0x0F;
				ptr2[num++] = 0x6F;
				ptr2[num++] = (byte)(0x84 | (xmm << 3));
				ptr2[num++] = 0x24;
				*(int*)(ptr2 + num) = -0x80 + (xmm * 0x10);
				num += 4;
			}
			ptr2[num++] = 76;
			ptr2[num++] = 137;
			ptr2[num++] = 228;
			ptr2[num++] = 95;
			ptr2[num++] = 94;
			ptr2[num++] = 90;
			ptr2[num++] = 89;
			ptr2[num++] = 65;
			ptr2[num++] = 88;
			ptr2[num++] = 65;
			ptr2[num++] = 89;
			ptr2[num++] = 91;
			ptr2[num++] = 93;
			ptr2[num++] = 65;
			ptr2[num++] = 92;
			ptr2[num++] = 65;
			ptr2[num++] = 93;
			ptr2[num++] = 65;
			ptr2[num++] = 94;
			ptr2[num++] = 65;
			ptr2[num++] = 95;
			ptr2[num++] = 195;
			Debug.Assert(num <= trampolineSize, "Import handler trampoline exceeded its allocation.");
		uint num2 = default(uint);
		if (!_hostMemory.Protect((ulong)ptr, trampolineSize, HostPageProtection.ReadExecute, out num2))
		{
			Console.Error.WriteLine($"[LOADER][ERROR] VirtualProtect failed for import dispatch stub at 0x{(nint)ptr:X16}");
			return 0;
		}
		_hostMemory.FlushInstructionCache((ulong)ptr, trampolineSize);
		return (nint)ptr;
		}
		catch
		{
			return 0;
		}
	}

	private unsafe bool PatchImportStub(nint address, nint trampoline)
	{
		uint flNewProtect = default(uint);
		if (!_hostMemory.Protect((ulong)(void*)address, 16u, HostPageProtection.ReadWrite, out flNewProtect))
		{
			Console.Error.WriteLine($"[LOADER][ERROR] VirtualProtect failed for import stub at 0x{address:X16}");
			return false;
		}
		var protectionRestored = false;
		try
		{
			*(sbyte*)address = 72;
			*(sbyte*)(address + 1) = -72;
			*(long*)(address + 2) = trampoline;
			*(sbyte*)(address + 10) = -1;
			*(sbyte*)(address + 11) = -32;
			*(sbyte*)(address + 12) = -112;
			*(sbyte*)(address + 13) = -112;
			*(sbyte*)(address + 14) = -112;
			*(sbyte*)(address + 15) = -112;
		}
		finally
		{
			protectionRestored = _hostMemory.ProtectRaw(
				(ulong)(void*)address,
				16u,
				flNewProtect,
				out flNewProtect);
			_hostMemory.FlushInstructionCache((ulong)(void*)address, 16u);
		}
		if (!protectionRestored)
		{
			Console.Error.WriteLine($"[LOADER][ERROR] Failed to restore protection for import stub at 0x{address:X16}");
		}
		return protectionRestored;
	}

	private unsafe void ClearImportHandlerTrampolines()
	{
		foreach (nint importHandlerTrampoline in _importHandlerTrampolines)
		{
			if (importHandlerTrampoline != 0)
			{
				_hostMemory.Free((ulong)importHandlerTrampoline);
			}
		}
		_importHandlerTrampolines.Clear();
	}

	internal unsafe bool TryCreateTlsHandler()
	{
		_tlsLoadHelpers.Clear();
		_tlsStoreHelpers.Clear();
		_tlsImmediateStoreHelpers.Clear();
		_tlsStackCanaryHelpers.Clear();
		_tlsHandlerAddress = (nint)TryAllocateNearEntry(TlsHandlerRegionSize);
		if (_tlsHandlerAddress == 0)
		{
			_tlsHandlerAddress = (nint)_hostMemory.Allocate(0, TlsHandlerRegionSize, HostPageProtection.ReadWrite);
		}
		if (_tlsHandlerAddress == 0)
		{
			LastError = "Failed to allocate TLS handler";
			return false;
		}
		// The handler runs in place of a patched guest `mov reg, fs:[0]`,
		// which preserves every register and the flags. TlsGetValue (and the
		// Win64 ABI in general) clobbers rcx/rdx/r8-r11 and the arithmetic
		// flags, so save them all: guest code legitimately keeps live values
		// and comparison results across TLS reads, and losing them corrupted
		// deterministic computations (e.g. procedural texture generation).
		byte* tlsHandlerAddress = (byte*)_tlsHandlerAddress;
		int num = 0;
		tlsHandlerAddress[num++] = 0x9C;                    // pushfq
		tlsHandlerAddress[num++] = 0x51;                    // push rcx
		tlsHandlerAddress[num++] = 0x52;                    // push rdx
		tlsHandlerAddress[num++] = 0x41;                    // push r8
		tlsHandlerAddress[num++] = 0x50;
		tlsHandlerAddress[num++] = 0x41;                    // push r9
		tlsHandlerAddress[num++] = 0x51;
		tlsHandlerAddress[num++] = 0x41;                    // push r10
		tlsHandlerAddress[num++] = 0x52;
		tlsHandlerAddress[num++] = 0x41;                    // push r11
		tlsHandlerAddress[num++] = 0x53;
		tlsHandlerAddress[num++] = 0x48;                    // sub rsp, 0x20
		tlsHandlerAddress[num++] = 0x83;
		tlsHandlerAddress[num++] = 0xEC;
		tlsHandlerAddress[num++] = 0x20;
		tlsHandlerAddress[num++] = 0xB9;                    // mov ecx, index
		*(uint*)(tlsHandlerAddress + num) = _guestTlsBaseTlsIndex;
		num += 4;
		tlsHandlerAddress[num++] = 0x48;                    // mov rax, TlsGetValue
		tlsHandlerAddress[num++] = 0xB8;
		*(long*)(tlsHandlerAddress + num) = _tlsGetValueAddress;
		num += 8;
		tlsHandlerAddress[num++] = 0xFF;                    // call rax
		tlsHandlerAddress[num++] = 0xD0;
		tlsHandlerAddress[num++] = 0x48;                    // add rsp, 0x20
		tlsHandlerAddress[num++] = 0x83;
		tlsHandlerAddress[num++] = 0xC4;
		tlsHandlerAddress[num++] = 0x20;
		tlsHandlerAddress[num++] = 0x41;                    // pop r11
		tlsHandlerAddress[num++] = 0x5B;
		tlsHandlerAddress[num++] = 0x41;                    // pop r10
		tlsHandlerAddress[num++] = 0x5A;
		tlsHandlerAddress[num++] = 0x41;                    // pop r9
		tlsHandlerAddress[num++] = 0x59;
		tlsHandlerAddress[num++] = 0x41;                    // pop r8
		tlsHandlerAddress[num++] = 0x58;
		tlsHandlerAddress[num++] = 0x5A;                    // pop rdx
		tlsHandlerAddress[num++] = 0x59;                    // pop rcx
		tlsHandlerAddress[num++] = 0x9D;                    // popfq
		tlsHandlerAddress[num++] = 0xC3;                    // ret
		_tlsPatchStubOffset = (num + 15) & ~15;
		uint num2 = default(uint);
		if (!_hostMemory.Protect((ulong)(void*)_tlsHandlerAddress, TlsHandlerRegionSize, HostPageProtection.ReadExecute, out num2))
		{
			Console.Error.WriteLine($"[LOADER][ERROR] VirtualProtect failed for TLS handler at 0x{_tlsHandlerAddress:X16}");
			LastError = $"Failed to protect TLS handler at 0x{_tlsHandlerAddress:X16}";
			_hostMemory.Free((ulong)_tlsHandlerAddress);
			_tlsHandlerAddress = 0;
			return false;
		}
		_tlsHandlerAllocations.Add(_tlsHandlerAddress);
		_hostMemory.FlushInstructionCache((ulong)(void*)_tlsHandlerAddress, TlsHandlerRegionSize);
		Console.Error.WriteLine($"[LOADER][INFO] TLS handler at 0x{_tlsHandlerAddress:X16}");
		return true;
	}

	private unsafe nint CreateUnresolvedReturnStub()
	{
		void* ptr = (void*)_hostMemory.Allocate(0, 4096u, HostPageProtection.ReadWrite);
		if (ptr == null)
		{
			return 0;
		}
		byte* ptr2 = (byte*)ptr;
		*ptr2 = 49;
		ptr2[1] = 192;
		ptr2[2] = 195;
		for (int i = 3; i < 16; i++)
		{
			ptr2[i] = 144;
		}
		uint num = default(uint);
		if (!_hostMemory.Protect((ulong)ptr, 4096u, HostPageProtection.ReadExecute, out num))
		{
			Console.Error.WriteLine($"[LOADER][ERROR] VirtualProtect failed for unresolved return stub at 0x{(nint)ptr:X16}");
			_hostMemory.Free((ulong)ptr);
			return 0;
		}
		_hostMemory.FlushInstructionCache((ulong)ptr, 16u);
		return (nint)ptr;
	}

	private unsafe nint CreateGuestReturnStub()
	{
		const uint stubSize = 256u;
		void* ptr = (void*)_hostMemory.Allocate(0, stubSize, HostPageProtection.ReadWrite);
		if (ptr == null)
		{
			return 0;
		}

		byte* code = (byte*)ptr;
		int offset = 0;
		// TlsGetValue returns its TLS pointer in RAX. Preserve the guest return value
		// above the 32-byte Windows shadow space while keeping the call site aligned.
		EmitByte(code, ref offset, 0x48); // sub rsp, 0x30
		EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xEC);
		EmitByte(code, ref offset, 0x30);
		EmitByte(code, ref offset, 0x48); // mov [rsp+0x20], rax
		EmitByte(code, ref offset, 0x89);
		EmitByte(code, ref offset, 0x44);
		EmitByte(code, ref offset, 0x24);
		EmitByte(code, ref offset, 0x20);
		EmitByte(code, ref offset, 0xB9); // mov ecx, tlsIndex
		EmitUInt32(code, ref offset, _hostRspSlotTlsIndex);
		EmitByte(code, ref offset, 0x48); // mov rax, TlsGetValue
		EmitByte(code, ref offset, 0xB8);
		*(long*)(code + offset) = _tlsGetValueAddress;
		offset += sizeof(ulong);
		EmitByte(code, ref offset, 0xFF); // call rax
		EmitByte(code, ref offset, 0xD0);
		EmitByte(code, ref offset, 0x49); // mov r11, rax
		EmitByte(code, ref offset, 0x89);
		EmitByte(code, ref offset, 0xC3);
		EmitByte(code, ref offset, 0x48); // mov rax, [rsp+0x20]
		EmitByte(code, ref offset, 0x8B);
		EmitByte(code, ref offset, 0x44);
		EmitByte(code, ref offset, 0x24);
		EmitByte(code, ref offset, 0x20);
		EmitByte(code, ref offset, 0x48); // add rsp, 0x30
		EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xC4);
		EmitByte(code, ref offset, 0x30);
		EmitByte(code, ref offset, 0x49); // mov rsp, [r11]
		EmitByte(code, ref offset, 0x8B);
		EmitByte(code, ref offset, 0x23);
		EmitHostNonvolatileXmmRestore(code, ref offset);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5F);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5E);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5D);
		EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x5C);
		EmitByte(code, ref offset, 0x5E);
		EmitByte(code, ref offset, 0x5F);
		EmitByte(code, ref offset, 0x5D);
		EmitByte(code, ref offset, 0x5B);
		EmitByte(code, ref offset, 0xC3);

		uint oldProtect = default;
		if (!_hostMemory.Protect((ulong)ptr, stubSize, HostPageProtection.ReadExecute, out oldProtect))
		{
			Console.Error.WriteLine($"[LOADER][ERROR] VirtualProtect failed for guest return stub at 0x{(nint)ptr:X16}");
			_hostMemory.Free((ulong)ptr);
			return 0;
		}
		_hostMemory.FlushInstructionCache((ulong)ptr, (nuint)offset);
		return (nint)ptr;
	}

	private unsafe void* TryAllocateNearEntry(nuint size)
	{
		ulong entryPoint = _entryPoint;
		ulong baseAddress = entryPoint & 0xFFFFFFFFFFFF0000uL;
		for (long num = 0L; num <= 1879048192; num += 16777216)
		{
			if (TryAllocAt(baseAddress, num, size, out var memory))
			{
				return memory;
			}
			if (num != 0L && TryAllocAt(baseAddress, -num, size, out memory))
			{
				return memory;
			}
		}
		return null;
	}

	private unsafe bool TryAllocAt(ulong baseAddress, long signedDelta, nuint size, out void* memory)
	{
		memory = null;
		ulong num;
		if (signedDelta >= 0)
		{
			if (baseAddress > (ulong)(-1 - signedDelta))
			{
				return false;
			}
			num = baseAddress + (ulong)signedDelta;
		}
		else
		{
			ulong num2 = (ulong)(-signedDelta);
			if (baseAddress < num2)
			{
				return false;
			}
			num = baseAddress - num2;
		}
		void* ptr = (void*)_hostMemory.Allocate(num, size, HostPageProtection.ReadWrite);
		if (ptr == null)
		{
			return false;
		}
		memory = ptr;
		return true;
	}

	private unsafe bool PatchTlsPatterns(ICpuMemory memory)
	{
		var scanRegions = GetTlsPatchScanRegions(memory, _entryPoint);
		int num3 = 0;
		int num4 = 0;
		int num9 = 0;
		int sse4aPatchCount = 0;
		int failedPatchCount = 0;
		var recognizedPatches = new List<(nint Address, byte[] OriginalBytes)>();
		var scanSucceeded = false;
		try
		{
			for (var regionIndex = 0; regionIndex < scanRegions.Count; regionIndex++)
			{
				ulong num = scanRegions[regionIndex].Start;
				ulong num2 = scanRegions[regionIndex].End;
				while (num < num2)
				{
					if (!_hostMemory.Query(num, out var lpBuffer) || lpBuffer.RegionSize == 0)
					{
						num = AdvanceTlsScanAddress(num, num2);
						continue;
					}
					ulong num5 = Math.Max(num, lpBuffer.BaseAddress);
					ulong num6 = lpBuffer.RegionSize > ulong.MaxValue - lpBuffer.BaseAddress
						? ulong.MaxValue
						: lpBuffer.BaseAddress + lpBuffer.RegionSize;
					if (num6 > num2)
					{
						num6 = num2;
					}
					uint num7 = lpBuffer.RawProtection & 0xFF;
					bool flag = lpBuffer.State == HostRegionState.Committed && (lpBuffer.RawProtection & PAGE_GUARD) == 0 && num7 != PAGE_NOACCESS;
					bool flag2 = num7 == PAGE_EXECUTE || num7 == 32 || num7 == 64 || num7 == PAGE_EXECUTE_WRITECOPY;
					var nextScanAddress = flag && flag2
						? GetTlsScanChunkEnd(num5, num6)
						: num6;
					var scanReadEnd = nextScanAddress < num6
						? GetTlsScanLookaheadEnd(nextScanAddress, num6)
						: nextScanAddress;
					if (flag &&
						flag2 &&
						nextScanAddress > num5 &&
						scanReadEnd - num5 >= MinTlsPatchInstructionBytes)
					{
						byte* ptr = (byte*)num5;
						int scanBytes = checked((int)(scanReadEnd - num5));
						int ownedStartBytes = checked((int)(nextScanAddress - num5));
						for (var i = 0;
							i <= ownedStartBytes -
								Sse4aExtrqBlendPatch.SequenceLength;
							i++)
						{
							var source = new ReadOnlySpan<byte>(
								ptr + i,
								Sse4aExtrqBlendPatch.SequenceLength);
							if (!Sse4aExtrqBlendPatch.TryMatch(
								source,
								out _,
								out _))
							{
								continue;
							}

							var address = (nint)(ptr + i);
							recognizedPatches.Add((
								address,
								source.ToArray()));
							if (TryPatchSse4aExtrqBlend(
								address,
								ptr + i))
							{
								sse4aPatchCount++;
								i +=
									Sse4aExtrqBlendPatch.SequenceLength -
									1;
							}
							else
							{
								failedPatchCount++;
								Console.Error.WriteLine(
									$"[LOADER][ERROR] Failed to patch recognized SSE4a EXTRQ blend at 0x{address:X16}");
							}
						}

						var candidates = GetTlsPatchCandidates(
							new ReadOnlySpan<byte>(ptr, scanBytes),
							ownedStartBytes,
							out var consumedBytes);
						for (var candidateIndex = 0;
							candidateIndex < candidates.Count;
							candidateIndex++)
						{
							var candidate = candidates[candidateIndex];
							int i = candidate.Offset;
							nint address = (nint)(ptr + i);
							int remainingBytes = scanBytes - i;
							recognizedPatches.Add((
								address,
								new ReadOnlySpan<byte>(ptr + i, candidate.Instruction.Length).ToArray()));
							if (TryPatchTlsInstruction(
								address,
								ptr + i,
								remainingBytes,
								out var tlsInstructionKind))
							{
								if (tlsInstructionKind == NativeTlsInstructionKind.Load)
								{
									num3++;
								}
								else if (tlsInstructionKind is
									NativeTlsInstructionKind.StackCanaryXor or
									NativeTlsInstructionKind.StackCanarySub)
								{
									num4++;
								}
								else
								{
									num9++;
								}
							}
							else
							{
								failedPatchCount++;
								Console.Error.WriteLine(
									$"[LOADER][ERROR] Failed to patch recognized {candidate.Instruction.Kind} TLS instruction at 0x{address:X16}");
							}
						}

						var consumedEnd = num5 + (ulong)consumedBytes;
						nextScanAddress = Math.Min(
							num6,
							Math.Max(nextScanAddress, consumedEnd));
					}
					num = nextScanAddress > num ? nextScanAddress : AdvanceTlsScanAddress(num, num2);
				}
			}
			Console.Error.WriteLine(
				$"[LOADER][INFO] Patched {num3} TLS loads, {num9} TLS stores, " +
				$"{num4} stack-canary accesses, {sse4aPatchCount} SSE4a " +
				$"EXTRQ blends; failures={failedPatchCount}");
			scanSucceeded = failedPatchCount == 0;
			return scanSucceeded;
		}
		finally
		{
			if (!scanSucceeded)
			{
				RollbackTlsInstructionPatches(recognizedPatches);
			}
		}
	}

	private unsafe bool TryPatchSse4aExtrqBlend(
		nint address,
		byte* source)
	{
		var window = new ReadOnlySpan<byte>(
			source,
			Sse4aExtrqBlendPatch.SequenceLength);
		if (!Sse4aExtrqBlendPatch.TryMatch(
			window,
			out var destinationRegister,
			out var sourceRegister))
		{
			return false;
		}

		Span<byte> replacement =
			stackalloc byte[Sse4aExtrqBlendPatch.SequenceLength];
		if (!Sse4aExtrqBlendPatch.TryEncode(
			destinationRegister,
			sourceRegister,
			replacement))
		{
			return false;
		}

		var originalBytes = window.ToArray();
		if (!_hostMemory.Protect(
			(ulong)(void*)address,
			(nuint)replacement.Length,
			HostPageProtection.ReadWrite,
			out var originalProtection))
		{
			return false;
		}

		var patchComplete = false;
		var patchCommitted = false;
		try
		{
			replacement.CopyTo(
				new Span<byte>(
					(void*)address,
					replacement.Length));
			patchComplete = true;
		}
		finally
		{
			patchCommitted = FinalizeInstructionPatch(
				address,
				originalBytes,
				originalProtection,
				patchComplete);
		}

		return patchCommitted;
	}

	internal static ulong GetTlsScanChunkEnd(ulong regionStart, ulong regionEnd)
	{
		if (regionEnd <= regionStart)
		{
			return regionStart;
		}

		return regionEnd - regionStart <= MaxTlsScanChunkBytes
			? regionEnd
			: regionStart + MaxTlsScanChunkBytes;
	}

	private static ulong GetTlsScanLookaheadEnd(ulong chunkEnd, ulong regionEnd)
	{
		const ulong lookaheadBytes = MaxX86InstructionBytes - 1;
		return regionEnd - chunkEnd <= lookaheadBytes
			? regionEnd
			: chunkEnd + lookaheadBytes;
	}

	private static ulong AdvanceTlsScanAddress(ulong address, ulong scanEnd)
	{
		const ulong scanStep = 4096;
		return scanEnd - address <= scanStep
			? scanEnd
			: address + scanStep;
	}

	private unsafe void RollbackTlsInstructionPatches(
		List<(nint Address, byte[] OriginalBytes)> recognizedPatches)
	{
		for (var index = recognizedPatches.Count - 1; index >= 0; index--)
		{
			var (address, originalBytes) = recognizedPatches[index];
			if (!_hostMemory.Protect(
				(ulong)(void*)address,
				(nuint)originalBytes.Length,
				HostPageProtection.ReadWrite,
				out var oldProtection))
			{
				Console.Error.WriteLine($"[LOADER][ERROR] Failed to reopen TLS instruction at 0x{address:X16} during scan rollback");
				continue;
			}

			originalBytes.CopyTo(new Span<byte>((void*)address, originalBytes.Length));
			if (!_hostMemory.ProtectRaw(
				(ulong)(void*)address,
				(nuint)originalBytes.Length,
				oldProtection,
				out _))
			{
				Console.Error.WriteLine($"[LOADER][ERROR] Failed to restore protection for TLS instruction at 0x{address:X16} during scan rollback");
			}
			_hostMemory.FlushInstructionCache((ulong)(void*)address, (nuint)originalBytes.Length);
		}
	}

	internal static IReadOnlyList<TlsPatchScanRegion> GetTlsPatchScanRegions(
		ICpuMemory memory,
		ulong entryPoint)
	{
		ArgumentNullException.ThrowIfNull(memory);
		if (memory is IGuestImageMemory imageMemory &&
			imageMemory.TryGetImageRegions(entryPoint, out var imageRegions))
		{
			var executableRegions = new List<TlsPatchScanRegion>();
			for (var index = 0; index < imageRegions.Count; index++)
			{
				var region = imageRegions[index];
				if (region.MemorySize == 0 ||
					(region.Protection & ProgramHeaderFlags.Execute) == 0 ||
					region.VirtualAddress > ulong.MaxValue - region.MemorySize)
				{
					continue;
				}

				executableRegions.Add(new TlsPatchScanRegion(
					region.VirtualAddress,
					region.VirtualAddress + region.MemorySize));
			}

			executableRegions.Sort(static (left, right) =>
				left.Start.CompareTo(right.Start));
			if (executableRegions.Count < 2)
			{
				return executableRegions.ToArray();
			}

			var mergedRegions = new List<TlsPatchScanRegion>(
				executableRegions.Count);
			mergedRegions.Add(executableRegions[0]);
			for (var index = 1; index < executableRegions.Count; index++)
			{
				var current = executableRegions[index];
				var previous = mergedRegions[^1];
				if (current.Start <= previous.End)
				{
					mergedRegions[^1] = new TlsPatchScanRegion(
						previous.Start,
						Math.Max(previous.End, current.End));
					continue;
				}

				mergedRegions.Add(current);
			}

			return mergedRegions.ToArray();
		}

		var end = entryPoint > ulong.MaxValue - FallbackTlsScanSize
			? ulong.MaxValue
			: entryPoint + FallbackTlsScanSize;
		return [new TlsPatchScanRegion(entryPoint, end)];
	}

	internal readonly record struct TlsPatchScanRegion(
		ulong Start,
		ulong End);

	internal static IReadOnlyList<TlsPatchCandidate> GetTlsPatchCandidates(
		ReadOnlySpan<byte> bytes,
		int ownedLength,
		out int consumedLength)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(ownedLength);
		if (ownedLength > bytes.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(ownedLength));
		}

		var candidates = new List<TlsPatchCandidate>();
		var offset = 0;
		while (offset < ownedLength)
		{
			var candidateLength = Math.Min(
				MaxX86InstructionBytes,
				bytes.Length - offset);
			var instructionBytes = bytes.Slice(offset, candidateLength);
			var instructionLength =
				NativeTlsInstructionDecoder.GetInstructionLength(instructionBytes);
			if (instructionLength == 0)
			{
				offset++;
				continue;
			}

			if (NativeTlsInstructionDecoder.TryDecode(
				instructionBytes,
				out var tlsInstruction))
			{
				candidates.Add(new TlsPatchCandidate(offset, tlsInstruction));
			}

			offset += instructionLength;
		}

		consumedLength = offset;
		return candidates;
	}

	internal readonly record struct TlsPatchCandidate(
		int Offset,
		NativeTlsInstruction Instruction);

	private unsafe bool IsPatternMatch(byte* ptr, byte[] pattern)
	{
		for (int i = 0; i < pattern.Length; i++)
		{
			if (ptr[i] != pattern[i])
			{
				return false;
			}
		}
		return true;
	}

	private unsafe bool PatchStackCanaryInstruction(
		nint address,
		in NativeTlsInstruction instruction)
	{
		var helper = GetOrCreateTlsStackCanaryHelper(
			instruction.Kind,
			instruction.Register,
			instruction.Displacement,
			instruction.Is64Bit);
		return helper != 0 && PatchCallSite(address, instruction.Length, helper);
	}

	private unsafe nint GetOrCreateTlsStackCanaryHelper(
		NativeTlsInstructionKind instructionKind,
		int destinationRegister,
		int displacement,
		bool is64Bit)
	{
		var arithmeticOpcode = instructionKind switch
		{
			NativeTlsInstructionKind.StackCanaryXor => 0x31,
			NativeTlsInstructionKind.StackCanarySub => 0x29,
			_ => 0,
		};
		if (arithmeticOpcode == 0 || destinationRegister is < 0 or >= 16 or 4)
		{
			return 0;
		}

		var helperKey = (instructionKind, destinationRegister, displacement, is64Bit);
		if (_tlsStackCanaryHelpers.TryGetValue(helperKey, out var existingHelper))
		{
			return existingHelper;
		}

		const int helperSize = 128;
		var helper = AllocateTlsPatchStub(helperSize);
		if (helper == 0)
		{
			return 0;
		}

		var code = (byte*)helper;
		var offset = 0;
		EmitByte(code, ref offset, 0x9C); // pushfq; discarded after XOR establishes guest flags
		EmitByte(code, ref offset, 0x50); // push rax
		EmitByte(code, ref offset, 0x51); // push rcx
		EmitByte(code, ref offset, 0x52); // push rdx
		EmitByte(code, ref offset, 0x41); // push r8
		EmitByte(code, ref offset, 0x50);
		EmitByte(code, ref offset, 0x41); // push r9
		EmitByte(code, ref offset, 0x51);
		EmitByte(code, ref offset, 0x41); // push r10
		EmitByte(code, ref offset, 0x52);
		EmitByte(code, ref offset, 0x41); // push r11
		EmitByte(code, ref offset, 0x53);
		EmitByte(code, ref offset, 0x48); // sub rsp, 0x28
		EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xEC);
		EmitByte(code, ref offset, 0x28);
		EmitByte(code, ref offset, 0xB9); // mov ecx, TLS index
		EmitUInt32(code, ref offset, _guestTlsBaseTlsIndex);
		EmitByte(code, ref offset, 0x48); // mov rax, TlsGetValue
		EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = _tlsGetValueAddress;
		offset += sizeof(nint);
		EmitByte(code, ref offset, 0xFF); // call rax
		EmitByte(code, ref offset, 0xD0);
		if (is64Bit)
		{
			EmitByte(code, ref offset, 0x48);
		}
		EmitByte(code, ref offset, 0x8B); // mov edx/rdx, [rax+displacement]
		EmitByte(code, ref offset, 0x90);
		EmitUInt32(code, ref offset, unchecked((uint)displacement));
		EmitByte(code, ref offset, 0x48); // add rsp, 0x28
		EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xC4);
		EmitByte(code, ref offset, 0x28);

		var savedSlot = destinationRegister switch
		{
			0 => 48,
			1 => 40,
			2 => 32,
			8 => 24,
			9 => 16,
			10 => 8,
			11 => 0,
			_ => -1,
		};
		if (savedSlot >= 0)
		{
			if (is64Bit)
			{
				EmitByte(code, ref offset, 0x48);
			}
			EmitByte(code, ref offset, (byte)arithmeticOpcode); // arithmetic [rsp+savedSlot], edx/rdx
			EmitByte(code, ref offset, 0x54);
			EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, (byte)savedSlot);
		}
		else
		{
			var rex = 0x40 |
				(is64Bit ? 0x08 : 0) |
				(destinationRegister >= 8 ? 0x01 : 0);
			if (rex != 0x40)
			{
				EmitByte(code, ref offset, (byte)rex);
			}
			EmitByte(code, ref offset, (byte)arithmeticOpcode); // arithmetic destination, edx/rdx
			EmitByte(code, ref offset, (byte)(0xD0 | (destinationRegister & 7)));
		}

		EmitByte(code, ref offset, 0x41); // pop r11
		EmitByte(code, ref offset, 0x5B);
		EmitByte(code, ref offset, 0x41); // pop r10
		EmitByte(code, ref offset, 0x5A);
		EmitByte(code, ref offset, 0x41); // pop r9
		EmitByte(code, ref offset, 0x59);
		EmitByte(code, ref offset, 0x41); // pop r8
		EmitByte(code, ref offset, 0x58);
		EmitByte(code, ref offset, 0x5A); // pop rdx
		EmitByte(code, ref offset, 0x59); // pop rcx
		EmitByte(code, ref offset, 0x58); // pop rax
		EmitByte(code, ref offset, 0x48); // lea rsp, [rsp+8] (discard saved flags)
		EmitByte(code, ref offset, 0x8D);
		EmitByte(code, ref offset, 0x64);
		EmitByte(code, ref offset, 0x24);
		EmitByte(code, ref offset, 0x08);
		EmitByte(code, ref offset, 0xC3); // ret
		while (offset < helperSize)
		{
			EmitByte(code, ref offset, 0x90);
		}

		if (!_hostMemory.Protect(
			(ulong)(void*)helper,
			helperSize,
			HostPageProtection.ReadExecute,
			out _))
		{
			RollbackTlsPatchStub(helper, helperSize);
			return 0;
		}

		_hostMemory.FlushInstructionCache((ulong)(void*)helper, helperSize);
		_tlsStackCanaryHelpers[helperKey] = helper;
		return helper;
	}

	internal unsafe bool TryPatchTlsInstruction(
		nint address,
		byte* source,
		int availableLength,
		out NativeTlsInstructionKind instructionKind)
	{
		instructionKind = default;
		if (availableLength < MinTlsPatchInstructionBytes)
		{
			return false;
		}

		var candidateLength = Math.Min(MaxX86InstructionBytes, availableLength);
		if (!NativeTlsInstructionDecoder.TryDecode(
				new ReadOnlySpan<byte>(source, candidateLength),
				out var instruction))
		{
			return false;
		}

		var patched = instruction.Kind switch
		{
			NativeTlsInstructionKind.Load => PatchTlsLoadInstruction(
				address,
				instruction.Length,
				instruction.Register,
				instruction.Displacement,
				instruction.Is64Bit,
				instruction.MemorySize,
				instruction.SignExtend),
			NativeTlsInstructionKind.RegisterStore => PatchTlsRegisterStoreInstruction(
				address,
				in instruction),
			NativeTlsInstructionKind.ImmediateStore => PatchTlsImmediateStoreInstruction(
				address,
				in instruction),
			NativeTlsInstructionKind.StackCanaryXor => PatchStackCanaryInstruction(
				address,
				in instruction),
			NativeTlsInstructionKind.StackCanarySub => PatchStackCanaryInstruction(
				address,
				in instruction),
			_ => false,
		};
		if (patched)
		{
			instructionKind = instruction.Kind;
		}

		return patched;
	}

	private unsafe bool PatchTlsLoadInstruction(
		nint address,
		int instructionLength,
		int destinationRegister,
		int displacement,
		bool is64Bit,
		int memorySize,
		bool signExtend)
	{
		var helper = GetOrCreateTlsLoadHelper(
			destinationRegister,
			displacement,
			is64Bit,
			memorySize,
			signExtend);
		if (helper == 0)
		{
			return false;
		}
		long relativeTarget = helper - (address + 5);
		if (relativeTarget < int.MinValue || relativeTarget > int.MaxValue)
		{
			Console.Error.WriteLine($"[LOADER][WARNING] TLS patch out of rel32 range at 0x{address:X16}");
			return false;
		}

		var originalBytes = new ReadOnlySpan<byte>((void*)address, instructionLength).ToArray();
		uint flNewProtect = default(uint);
		if (!_hostMemory.Protect((ulong)(void*)address, (nuint)instructionLength, HostPageProtection.ReadWrite, out flNewProtect))
		{
			return false;
		}
		var patchComplete = false;
		var patchCommitted = false;
		try
		{
			*(sbyte*)address = -24;
			*(int*)(address + 1) = (int)relativeTarget;
			var offset = 5;
			while (offset < instructionLength)
			{
				*(byte*)(address + offset++) = 0x90;
			}
			patchComplete = true;
		}
		finally
		{
			patchCommitted = FinalizeInstructionPatch(
				address,
				originalBytes,
				flNewProtect,
				patchComplete);
		}
		return patchCommitted;
	}

	internal unsafe nint GetOrCreateTlsLoadHelper(
		int destinationRegister,
		int displacement,
		bool is64Bit,
		int memorySize,
		bool signExtend)
	{
		if (destinationRegister is < 0 or >= 16 or 4 ||
			!IsSupportedTlsLoad(memorySize, is64Bit, signExtend))
		{
			return 0;
		}

		var helperKey = (destinationRegister, displacement, is64Bit, memorySize, signExtend);
		if (_tlsLoadHelpers.TryGetValue(helperKey, out var existingHelper))
		{
			return existingHelper;
		}

		const int helperSize = 128;
		var helper = AllocateTlsPatchStub(helperSize);
		if (helper == 0)
		{
			return 0;
		}

		var code = (byte*)helper;
		var offset = 0;
		EmitByte(code, ref offset, 0x9C); // pushfq
		EmitByte(code, ref offset, 0x50); // push rax
		EmitByte(code, ref offset, 0x51); // push rcx
		EmitByte(code, ref offset, 0x52); // push rdx
		EmitByte(code, ref offset, 0x41); // push r8
		EmitByte(code, ref offset, 0x50);
		EmitByte(code, ref offset, 0x41); // push r9
		EmitByte(code, ref offset, 0x51);
		EmitByte(code, ref offset, 0x41); // push r10
		EmitByte(code, ref offset, 0x52);
		EmitByte(code, ref offset, 0x41); // push r11
		EmitByte(code, ref offset, 0x53);
		EmitByte(code, ref offset, 0x48); // sub rsp, 0x28
		EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xEC);
		EmitByte(code, ref offset, 0x28);
		EmitByte(code, ref offset, 0xB9); // mov ecx, TLS index
		EmitUInt32(code, ref offset, _guestTlsBaseTlsIndex);
		EmitByte(code, ref offset, 0x48); // mov rax, TlsGetValue
		EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = _tlsGetValueAddress;
		offset += sizeof(nint);
		EmitByte(code, ref offset, 0xFF); // call rax
		EmitByte(code, ref offset, 0xD0);
		EmitTlsLoadInstruction(
			code,
			ref offset,
			displacement,
			memorySize,
			is64Bit,
			signExtend);
		EmitByte(code, ref offset, 0x48); // add rsp, 0x28
		EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xC4);
		EmitByte(code, ref offset, 0x28);

		var savedSlot = destinationRegister switch
		{
			0 => 48,
			1 => 40,
			2 => 32,
			8 => 24,
			9 => 16,
			10 => 8,
			11 => 0,
			_ => -1,
		};
		if (savedSlot >= 0)
		{
			EmitByte(code, ref offset, 0x48); // mov [rsp+slot], rax
			EmitByte(code, ref offset, 0x89);
			EmitByte(code, ref offset, 0x44);
			EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, (byte)savedSlot);
		}
		else
		{
			EmitByte(
				code,
				ref offset,
				(byte)(0x48 | (destinationRegister >= 8 ? 1 : 0)));
			EmitByte(code, ref offset, 0x89); // mov destination, rax
			EmitByte(code, ref offset, (byte)(0xC0 | (destinationRegister & 7)));
		}

		EmitByte(code, ref offset, 0x41); // pop r11
		EmitByte(code, ref offset, 0x5B);
		EmitByte(code, ref offset, 0x41); // pop r10
		EmitByte(code, ref offset, 0x5A);
		EmitByte(code, ref offset, 0x41); // pop r9
		EmitByte(code, ref offset, 0x59);
		EmitByte(code, ref offset, 0x41); // pop r8
		EmitByte(code, ref offset, 0x58);
		EmitByte(code, ref offset, 0x5A); // pop rdx
		EmitByte(code, ref offset, 0x59); // pop rcx
		EmitByte(code, ref offset, 0x58); // pop rax
		EmitByte(code, ref offset, 0x9D); // popfq
		EmitByte(code, ref offset, 0xC3); // ret
		while (offset < helperSize)
		{
			EmitByte(code, ref offset, 0x90);
		}

		uint oldProtect = 0;
		if (!_hostMemory.Protect((ulong)(void*)helper, helperSize, HostPageProtection.ReadExecute, out oldProtect))
		{
			RollbackTlsPatchStub(helper, helperSize);
			return 0;
		}

		_hostMemory.FlushInstructionCache((ulong)(void*)helper, helperSize);
		_tlsLoadHelpers[helperKey] = helper;
		return helper;
	}

	private static bool IsSupportedTlsLoad(
		int memorySize,
		bool is64Bit,
		bool signExtend)
	{
		return (memorySize, is64Bit, signExtend) switch
		{
			(1, _, _) => true,
			(2, _, _) => true,
			(4, false, false) => true,
			(4, true, true) => true,
			(8, true, false) => true,
			_ => false,
		};
	}

	private unsafe static void EmitTlsLoadInstruction(
		byte* code,
		ref int offset,
		int displacement,
		int memorySize,
		bool is64Bit,
		bool signExtend)
	{
		if (is64Bit)
		{
			EmitByte(code, ref offset, 0x48);
		}

		if (memorySize is 1 or 2)
		{
			EmitByte(code, ref offset, 0x0F);
			EmitByte(
				code,
				ref offset,
				(byte)(signExtend
					? (memorySize == 1 ? 0xBE : 0xBF)
					: (memorySize == 1 ? 0xB6 : 0xB7)));
		}
		else
		{
			EmitByte(code, ref offset, signExtend ? (byte)0x63 : (byte)0x8B);
		}

		EmitByte(code, ref offset, 0x80);
		EmitUInt32(code, ref offset, unchecked((uint)displacement));
	}

	private unsafe bool PatchTlsRegisterStoreInstruction(
		nint address,
		in NativeTlsInstruction instruction)
	{
		var helper = GetOrCreateTlsStoreHelper(
			instruction.Register,
			instruction.Displacement,
			instruction.Is64Bit);
		return helper != 0 && PatchCallSite(address, instruction.Length, helper);
	}

	private unsafe nint GetOrCreateTlsStoreHelper(
		int sourceRegister,
		int displacement,
		bool is64Bit)
	{
		if (sourceRegister is < 0 or >= 16)
		{
			return 0;
		}

		var helperKey = (sourceRegister, displacement, is64Bit);
		if (_tlsStoreHelpers.TryGetValue(helperKey, out var existingHelper))
		{
			return existingHelper;
		}

		const int helperSize = 128;
		var helper = AllocateTlsPatchStub(helperSize);
		if (helper == 0)
		{
			return 0;
		}

		var code = (byte*)helper;
		var offset = 0;
		EmitByte(code, ref offset, 0x9C); // pushfq
		EmitByte(code, ref offset, 0x50); // push rax
		EmitByte(code, ref offset, 0x51); // push rcx
		EmitByte(code, ref offset, 0x52); // push rdx
		EmitByte(code, ref offset, 0x41); // push r8
		EmitByte(code, ref offset, 0x50);
		EmitByte(code, ref offset, 0x41); // push r9
		EmitByte(code, ref offset, 0x51);
		EmitByte(code, ref offset, 0x41); // push r10
		EmitByte(code, ref offset, 0x52);
		EmitByte(code, ref offset, 0x41); // push r11
		EmitByte(code, ref offset, 0x53);
		EmitByte(code, ref offset, 0x48); // sub rsp, 0x28
		EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xEC);
		EmitByte(code, ref offset, 0x28);
		EmitByte(code, ref offset, 0xB9); // mov ecx, TLS index
		EmitUInt32(code, ref offset, _guestTlsBaseTlsIndex);
		EmitByte(code, ref offset, 0x48); // mov rax, TlsGetValue
		EmitByte(code, ref offset, 0xB8);
		*(nint*)(code + offset) = _tlsGetValueAddress;
		offset += sizeof(nint);
		EmitByte(code, ref offset, 0xFF); // call rax
		EmitByte(code, ref offset, 0xD0);
		EmitByte(code, ref offset, 0x48); // add rsp, 0x28
		EmitByte(code, ref offset, 0x83);
		EmitByte(code, ref offset, 0xC4);
		EmitByte(code, ref offset, 0x28);

		var savedSlot = sourceRegister switch
		{
			0 => 48,
			1 => 40,
			2 => 32,
			8 => 24,
			9 => 16,
			10 => 8,
			11 => 0,
			_ => -1,
		};
		if (sourceRegister == 4)
		{
			EmitByte(code, ref offset, 0x48); // lea rdx, [rsp+0x48] (guest rsp before patched call)
			EmitByte(code, ref offset, 0x8D);
			EmitByte(code, ref offset, 0x54);
			EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, 0x48);
		}
		else if (savedSlot >= 0)
		{
			EmitByte(code, ref offset, 0x48); // mov rdx, [rsp+slot]
			EmitByte(code, ref offset, 0x8B);
			EmitByte(code, ref offset, 0x54);
			EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, (byte)savedSlot);
		}
		else
		{
			EmitByte(code, ref offset, (byte)(0x48 | (sourceRegister >= 8 ? 4 : 0)));
			EmitByte(code, ref offset, 0x89); // mov rdx, source
			EmitByte(code, ref offset, (byte)(0xC2 | ((sourceRegister & 7) << 3)));
		}

		if (is64Bit)
		{
			EmitByte(code, ref offset, 0x48);
		}
		// Without REX.W, store only edx while preserving the adjacent TLS bytes.
		EmitByte(code, ref offset, 0x89);
		EmitByte(code, ref offset, 0x90);
		EmitUInt32(code, ref offset, unchecked((uint)displacement));
		EmitByte(code, ref offset, 0x41); // pop r11
		EmitByte(code, ref offset, 0x5B);
		EmitByte(code, ref offset, 0x41); // pop r10
		EmitByte(code, ref offset, 0x5A);
		EmitByte(code, ref offset, 0x41); // pop r9
		EmitByte(code, ref offset, 0x59);
		EmitByte(code, ref offset, 0x41); // pop r8
		EmitByte(code, ref offset, 0x58);
		EmitByte(code, ref offset, 0x5A); // pop rdx
		EmitByte(code, ref offset, 0x59); // pop rcx
		EmitByte(code, ref offset, 0x58); // pop rax
		EmitByte(code, ref offset, 0x9D); // popfq
		EmitByte(code, ref offset, 0xC3); // ret
		while (offset < helperSize)
		{
			EmitByte(code, ref offset, 0x90);
		}

		uint oldProtect = 0;
		if (!_hostMemory.Protect((ulong)(void*)helper, helperSize, HostPageProtection.ReadExecute, out oldProtect))
		{
			RollbackTlsPatchStub(helper, helperSize);
			return 0;
		}

		_hostMemory.FlushInstructionCache((ulong)(void*)helper, helperSize);
		_tlsStoreHelpers[helperKey] = helper;
		return helper;
	}

	private unsafe bool PatchTlsImmediateStoreInstruction(
		nint address,
		in NativeTlsInstruction instruction)
	{
		var helper = GetOrCreateTlsImmediateStoreHelper(
			instruction.Displacement,
			instruction.ImmediateValue,
			instruction.Is64Bit);
		if (helper == 0)
		{
			return false;
		}

		return PatchCallSite(address, instruction.Length, helper);
	}

	private unsafe nint GetOrCreateTlsImmediateStoreHelper(
		int displacement,
		int immediateValue,
		bool is64Bit)
	{
		var helperKey = (displacement, immediateValue, is64Bit);
		if (_tlsImmediateStoreHelpers.TryGetValue(helperKey, out var existingHelper))
		{
			return existingHelper;
		}

		const int helperSize = 32;
		var helper = AllocateTlsPatchStub(helperSize);
		if (helper == 0)
		{
			return 0;
		}

		var code = (byte*)helper;
		var offset = 0;
		EmitByte(code, ref offset, 0x50); // push rax
		EmitByte(code, ref offset, 0xE8); // call TLS base handler
		var relativeHandler = _tlsHandlerAddress - (helper + offset + sizeof(int));
		if (relativeHandler < int.MinValue || relativeHandler > int.MaxValue)
		{
			Console.Error.WriteLine($"[LOADER][WARNING] TLS store helper out of rel32 range at 0x{helper:X16}");
			RollbackTlsPatchStub(helper, helperSize);
			return 0;
		}

		EmitUInt32(code, ref offset, unchecked((uint)relativeHandler));
		if (is64Bit)
		{
			EmitByte(code, ref offset, 0x48);
		}
		EmitByte(code, ref offset, 0xC7); // mov dword/qword [rax+displacement], imm32
		EmitByte(code, ref offset, 0x80);
		EmitUInt32(code, ref offset, unchecked((uint)displacement));
		EmitUInt32(code, ref offset, unchecked((uint)immediateValue));
		EmitByte(code, ref offset, 0x58); // pop rax
		EmitByte(code, ref offset, 0xC3); // ret
		while (offset < helperSize)
		{
			EmitByte(code, ref offset, 0x90);
		}

		uint oldProtect = 0;
		if (!_hostMemory.Protect((ulong)(void*)helper, helperSize, HostPageProtection.ReadExecute, out oldProtect))
		{
			Console.Error.WriteLine($"[LOADER][ERROR] VirtualProtect failed for TLS store helper at 0x{helper:X16}");
			RollbackTlsPatchStub(helper, helperSize);
			return 0;
		}

		_hostMemory.FlushInstructionCache((ulong)(void*)helper, helperSize);
		_tlsImmediateStoreHelpers[helperKey] = helper;
		return helper;
	}

	private unsafe nint AllocateTlsPatchStub(int size)
	{
		if (_tlsHandlerAddress == 0 || size <= 0)
		{
			return 0;
		}
		int num = (size + 15) & -16;
		if (_tlsPatchStubOffset + num > TlsHandlerRegionSize)
		{
			Console.Error.WriteLine("[LOADER][WARNING] TLS patch stub region exhausted.");
			return 0;
		}
		nint result = _tlsHandlerAddress + _tlsPatchStubOffset;
		uint flNewProtect = default(uint);
		if (!_hostMemory.Protect((ulong)(void*)result, (nuint)num, HostPageProtection.ReadWrite, out flNewProtect))
		{
			return 0;
		}
		_tlsPatchStubOffset += num;
		return result;
	}

	private unsafe void RollbackTlsPatchStub(nint address, int size)
	{
		var alignedSize = (size + 15) & -16;
		if (alignedSize <= 0 ||
			address + alignedSize != _tlsHandlerAddress + _tlsPatchStubOffset)
		{
			Console.Error.WriteLine($"[LOADER][WARNING] Cannot roll back non-tail TLS helper at 0x{address:X16}");
			return;
		}

		_tlsPatchStubOffset -= alignedSize;
		if (!_hostMemory.Protect(
			(ulong)(void*)address,
			(nuint)alignedSize,
			HostPageProtection.ReadExecute,
			out _))
		{
			Console.Error.WriteLine($"[LOADER][WARNING] Failed to restore protection for rolled-back TLS helper at 0x{address:X16}");
		}
	}

	private unsafe bool PatchCallSite(nint address, int instructionLength, nint target)
	{
		if (instructionLength < 5)
		{
			return false;
		}
		long relativeTarget = target - (address + 5);
		if (relativeTarget < int.MinValue || relativeTarget > int.MaxValue)
		{
			Console.Error.WriteLine($"[LOADER][WARNING] TLS patch out of rel32 range at 0x{address:X16}");
			return false;
		}
		var originalBytes = new ReadOnlySpan<byte>((void*)address, instructionLength).ToArray();
		uint flNewProtect = default(uint);
		if (!_hostMemory.Protect((ulong)(void*)address, (nuint)instructionLength, HostPageProtection.ReadWrite, out flNewProtect))
		{
			return false;
		}
		var patchComplete = false;
		var patchCommitted = false;
		try
		{
			*(byte*)address = 232;
			*(int*)(address + 1) = (int)relativeTarget;
			for (int i = 5; i < instructionLength; i++)
			{
				*(byte*)(address + i) = 144;
			}
			patchComplete = true;
		}
		finally
		{
			patchCommitted = FinalizeInstructionPatch(
				address,
				originalBytes,
				flNewProtect,
				patchComplete);
		}
		return patchCommitted;
	}

	private unsafe bool FinalizeInstructionPatch(
		nint address,
		byte[] originalBytes,
		uint originalProtection,
		bool patchComplete)
	{
		if (patchComplete && _hostMemory.ProtectRaw(
			(ulong)(void*)address,
			(nuint)originalBytes.Length,
			originalProtection,
			out _))
		{
			_hostMemory.FlushInstructionCache((ulong)(void*)address, (nuint)originalBytes.Length);
			return true;
		}

		if (patchComplete && !_hostMemory.Protect(
			(ulong)(void*)address,
			(nuint)originalBytes.Length,
			HostPageProtection.ReadWrite,
			out _))
		{
			Console.Error.WriteLine($"[LOADER][ERROR] Failed to reopen TLS patch at 0x{address:X16} for rollback");
			return false;
		}

		originalBytes.CopyTo(new Span<byte>((void*)address, originalBytes.Length));
		if (!_hostMemory.ProtectRaw(
			(ulong)(void*)address,
			(nuint)originalBytes.Length,
			originalProtection,
			out _))
		{
			Console.Error.WriteLine($"[LOADER][ERROR] Failed to restore protection for rolled-back TLS patch at 0x{address:X16}");
		}
		_hostMemory.FlushInstructionCache((ulong)(void*)address, (nuint)originalBytes.Length);
		return false;
	}

	private unsafe void TryPreReservePrtAperture(ulong baseAddress, ulong size)
	{
		if (_hostMemory.Query(baseAddress, out var lpBuffer) && lpBuffer.State != HostRegionState.Free)
		{
			Console.Error.WriteLine($"[LOADER][INFO] PRT aperture at 0x{baseAddress:X16} already in use (state=0x{lpBuffer.RawState:X}), will use lazy-commit");
			return;
		}
		ulong num = baseAddress;
		ulong num2 = baseAddress + size;
		int num3 = 0;
		int num4 = 0;
		nuint num5;
		for (; num < num2; num += num5)
		{
			ulong val = num2 - num;
			num5 = (nuint)Math.Min(2097152uL, val);
			if (_hostMemory.Reserve(num, num5, HostPageProtection.ReadWrite) != 0)
			{
				num3++;
			}
			else
			{
				num4++;
			}
		}
		if (num4 == 0)
		{
			Console.Error.WriteLine($"[LOADER][INFO] Pre-reserved PRT aperture: 0x{baseAddress:X16}-0x{num2:X16} ({num3} chunks)");
		}
		else
		{
			Console.Error.WriteLine($"[LOADER][INFO] Partial PRT aperture reserve: 0x{baseAddress:X16}-0x{num2:X16} ({num3} chunks OK, {num4} failed)");
		}
		ulong num6 = baseAddress;
		ulong num7 = baseAddress + 67108864;
		int num8 = 0;
		for (; num6 < num7; num6 += 2097152)
		{
			if (_hostMemory.Commit(num6, 2097152u, HostPageProtection.ReadWrite))
			{
				num8++;
			}
		}
		if (num8 > 0)
		{
			Console.Error.WriteLine($"[LOADER][INFO] Pre-committed PRT bootstrap: 0x{baseAddress:X16}-0x{num7:X16} ({num8 * 2}MB in {num8} chunks)");
		}
		else
		{
			Console.Error.WriteLine($"[LOADER][WARN] Failed to pre-commit any PRT bootstrap chunks at 0x{baseAddress:X16}");
		}
	}

	private void RegisterPrtLazyCommitRange(ulong baseAddress, ulong size)
	{
		if (size == 0)
		{
			return;
		}

		bool added = false;
		lock (_lazyCommitRangeGate)
		{
			if (!_prtLazyCommitRanges.Any(range => range.BaseAddress == baseAddress && range.Size == size))
			{
				_prtLazyCommitRanges.Add(new LazyCommitRange(baseAddress, size));
				added = true;
			}
		}

		if (added)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] registered PRT lazy range: base=0x{baseAddress:X16} size=0x{size:X16}");
		}
	}

	private bool IsGuestOwnedLazyCommitAddress(ulong address, out string owner)
	{
		var cpuContext = ActiveCpuContext;
		if (cpuContext != null && TryGetVirtualMemory(cpuContext, out var virtualMemory))
		{
			foreach (var region in virtualMemory.SnapshotRegions())
			{
				if (ContainsAddress(region.VirtualAddress, region.MemorySize, address))
				{
					owner = $"vmem:0x{region.VirtualAddress:X16}+0x{region.MemorySize:X}";
					return true;
				}
			}
		}

		lock (_lazyCommitRangeGate)
		{
			foreach (var range in _prtLazyCommitRanges)
			{
				if (ContainsAddress(range.BaseAddress, range.Size, address))
				{
					owner = $"prt:0x{range.BaseAddress:X16}+0x{range.Size:X}";
					return true;
				}
			}
		}

		owner = string.Empty;
		return false;
	}

	private static bool ContainsAddress(ulong baseAddress, ulong size, ulong address)
	{
		return size != 0 && address >= baseAddress && address - baseAddress < size;
	}

	public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
	{
		error = null;
		if (request.ThreadHandle == 0 || request.EntryPoint < 65536)
		{
			error = $"invalid thread start request: handle=0x{request.ThreadHandle:X16} entry=0x{request.EntryPoint:X16}";
			return false;
		}
		if (!TryCreateGuestThreadState(creatorContext, request, out var thread, out error))
		{
			return false;
		}
		using (LockGate("TryStartThread"))
		{
			_guestThreads[request.ThreadHandle] = thread;
			Volatile.Write(ref _guestThreadCount, _guestThreads.Count);
			_readyGuestThreads.Enqueue(thread);
			Interlocked.Increment(ref _readyGuestThreadCount);
		}
		Console.Error.WriteLine(
			$"[LOADER][INFO] Scheduled guest thread '{thread.Name}' handle=0x{thread.ThreadHandle:X16} " +
			$"entry=0x{thread.EntryPoint:X16} arg=0x{thread.Argument:X16} priority={thread.Priority} " +
			$"host_priority={MapGuestThreadPriority(thread.Priority)} affinity=0x{thread.AffinityMask:X}");
		Pump(creatorContext, "pthread_create");
		return true;
	}

	public bool SupportsGuestContextTransfer => true;

	public bool TryJoinThread(
		CpuContext callerContext,
		ulong threadHandle,
		out ulong returnValue,
		out string? error)
	{
		returnValue = 0;
		error = null;
		if (threadHandle == 0)
		{
			error = "thread handle is zero";
			return false;
		}

		if (threadHandle == GuestThreadExecution.CurrentGuestThreadHandle)
		{
			error = "thread cannot join itself";
			return false;
		}

		// Joins regularly park here for minutes (a game main thread joining a
		// streamer); polling at a fixed 1ms burns half a host core for the
		// whole wait, so back off toward a 10ms cadence once the join is
		// clearly long-lived.
		var joinPollMilliseconds = 1;
		while (!ActiveForcedGuestExit)
		{
			Thread? hostThread;
			using (LockGate("TryJoinThread"))
			{
				if (!_guestThreads.TryGetValue(threadHandle, out var thread))
				{
					error = $"unknown guest thread 0x{threadHandle:X16}";
					return false;
				}

				if (thread.State == GuestThreadRunState.Exited)
				{
					returnValue = thread.ExitValue;
					return true;
				}

				if (thread.State == GuestThreadRunState.Faulted)
				{
					error =
						$"guest thread 0x{threadHandle:X16} faulted: " +
						(thread.BlockReason ?? "unknown error");
					return false;
				}

				hostThread = thread.HostThread;
			}

			if (hostThread is not null &&
				!ReferenceEquals(hostThread, Thread.CurrentThread))
			{
				// The handle is published before the host thread starts.
				if ((hostThread.ThreadState & System.Threading.ThreadState.Unstarted) != 0)
				{
					Thread.Sleep(1);
					continue;
				}

				try
				{
					hostThread.Join(joinPollMilliseconds);
				}
				catch (ThreadStateException)
				{
					Thread.Sleep(joinPollMilliseconds);
				}
			}
			else
			{
				Thread.Sleep(joinPollMilliseconds);
			}

			if (joinPollMilliseconds < 10)
			{
				joinPollMilliseconds++;
			}
		}

		error = "guest execution stopped while joining thread";
		return false;
	}

	public bool TryReapThread(ulong threadHandle)
	{
		Thread? hostThread;
		using (LockGate("TryReapThread.inspect"))
		{
			if (!_guestThreads.TryGetValue(threadHandle, out var thread) ||
				thread.State != GuestThreadRunState.Exited)
			{
				return false;
			}

			hostThread = thread.HostThread;
		}

		if (hostThread is not null && hostThread.IsAlive)
		{
			if (ReferenceEquals(hostThread, Thread.CurrentThread))
			{
				return false;
			}

			hostThread.Join();
		}

		GuestContinuationRunner? continuationRunner;
		using (LockGate("TryReapThread.remove"))
		{
			if (!_guestThreads.TryGetValue(threadHandle, out var thread) ||
				thread.State != GuestThreadRunState.Exited)
			{
				return false;
			}

			_guestThreads.Remove(threadHandle);
			Volatile.Write(ref _guestThreadCount, _guestThreads.Count);
			continuationRunner = thread.ContinuationRunner;
			thread.ContinuationRunner = null;
		}

		continuationRunner?.Dispose();
		return true;
	}

	public bool RequestThreadReap(ulong threadHandle)
	{
		using (LockGate("RequestThreadReap"))
		{
			if (!_guestThreads.TryGetValue(threadHandle, out var thread) ||
				thread.State != GuestThreadRunState.Exited)
			{
				return false;
			}

			if (Volatile.Read(ref thread.HostThreadId) != 0)
			{
				thread.ReapRequested = true;
				return true;
			}
		}

		if (!TryReapThread(threadHandle))
		{
			return false;
		}

		GuestThreadExecution.NotifyGuestThreadReaped(threadHandle);
		return true;
	}

	public void Pump(CpuContext callerContext, string reason)
	{
		_ = callerContext;
		if (_guestTeardownRequested)
		{
			return;
		}
		var runSynchronously = string.Equals(reason, "entry_return", StringComparison.Ordinal);
		if (Volatile.Read(ref _readyGuestThreadCount) == 0)
		{
			return;
		}
		if (Interlocked.CompareExchange(ref _guestThreadPumpDepth, 1, 0) != 0)
		{
			return;
		}
		try
		{
			for (int i = 0; i < 8; i++)
			{
				GuestThreadState? thread = null;
				using (LockGate("Pump.dequeue"))
				{
					while (_readyGuestThreads.Count > 0)
					{
						var candidate = _readyGuestThreads.Dequeue();
						Interlocked.Decrement(ref _readyGuestThreadCount);
						if (candidate.State == GuestThreadRunState.Ready)
						{
							thread = candidate;
							thread.State = GuestThreadRunState.Running;
							break;
						}
					}
				}
				if (thread == null)
				{
					return;
				}

				if (runSynchronously)
				{
					RunGuestThread(thread, reason);
					continue;
				}

				var hostThread = new Thread(() => RunGuestThread(thread, reason))
				{
					IsBackground = true,
					Name = $"SharpEmu-{thread.Name}",
					Priority = MapGuestThreadPriority(thread.Priority),
				};
				using (LockGate("Pump.bind_host"))
				{
					thread.HostThread = hostThread;
				}
				hostThread.Start();
			}
		}
		finally
		{
			Volatile.Write(ref _guestThreadPumpDepth, 0);
		}
	}

	public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads()
	{
		using (LockGate("SnapshotThreads"))
		{
			var snapshots = new GuestThreadSnapshot[_guestThreads.Count];
			var index = 0;
			foreach (var thread in _guestThreads.Values)
			{
				snapshots[index++] = new GuestThreadSnapshot(
					thread.ThreadHandle,
					thread.Name,
					thread.State.ToString(),
					Interlocked.Read(ref thread.ImportCount),
					Volatile.Read(ref thread.LastImportNid),
					Volatile.Read(ref thread.LastReturnRip),
					thread.BlockReason);
			}

			return snapshots;
		}
	}

	private long GetTotalGuestThreadImports()
	{
		using (LockGate("GetTotalGuestThreadImports"))
		{
			long total = 0;
			foreach (var thread in _guestThreads.Values)
			{
				var imports = Interlocked.Read(ref thread.ImportCount);
				if (imports > long.MaxValue - total)
				{
					return long.MaxValue;
				}

				total += imports;
			}

			return total;
		}
	}

	private static int SaturateImportCount(long workerImports, int entryImports)
	{
		if (workerImports >= int.MaxValue || entryImports >= int.MaxValue - workerImports)
		{
			return int.MaxValue;
		}

		return (int)workerImports + entryImports;
	}

	private void PumpUntilGuestThreadsIdle(CpuContext callerContext, string reason)
	{
		var nextSnapshotTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
		while (!ActiveForcedGuestExit)
		{
			Pump(callerContext, reason);

			// Tally run states under the lock without allocating a snapshot every
			// spin (this loop can iterate rapidly); the full snapshot is only
			// materialized for the gated diagnostic dump below.
			GetGuestThreadActivity(out var threadCount, out var hasReadyThread, out var hasRunningThread, out var hasBlockedThread);
			if (threadCount == 0)
			{
				return;
			}

			if (hasReadyThread)
			{
				continue;
			}

			if (!hasRunningThread && !hasBlockedThread)
			{
				return;
			}

			if (_logGuestThreads && Stopwatch.GetTimestamp() >= nextSnapshotTimestamp)
			{
				foreach (var thread in SnapshotGuestThreads())
				{
					Console.Error.WriteLine(
						$"[LOADER][TRACE] guest_thread.idle_wait reason={reason} handle=0x{thread.ThreadHandle:X16} " +
						$"name='{thread.Name}' state={thread.State} imports={Interlocked.Read(ref thread.ImportCount)} " +
						$"nid={Volatile.Read(ref thread.LastImportNid) ?? "none"} ret=0x{Volatile.Read(ref thread.LastReturnRip):X16} " +
						$"block={thread.BlockReason ?? "none"}");
				}

				nextSnapshotTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
			}

			Thread.Sleep(1);
		}
	}

	private GuestThreadState[] SnapshotGuestThreads()
	{
		using (LockGate("SnapshotGuestThreads"))
		{
			var snapshot = new GuestThreadState[_guestThreads.Count];
			_guestThreads.Values.CopyTo(snapshot, 0);
			return snapshot;
		}
	}

	// Allocation-free run-state tally for the idle spin loop.
	private void GetGuestThreadActivity(out int count, out bool hasReady, out bool hasRunning, out bool hasBlocked)
	{
		hasReady = false;
		hasRunning = false;
		hasBlocked = false;
		using (LockGate("GetGuestThreadActivity"))
		{
			count = _guestThreads.Count;
			foreach (var thread in _guestThreads.Values)
			{
				switch (thread.State)
				{
					case GuestThreadRunState.Ready:
						hasReady = true;
						break;
					case GuestThreadRunState.Running:
						hasRunning = true;
						break;
					case GuestThreadRunState.Blocked:
						hasBlocked = true;
						break;
				}
			}
		}
	}

	public bool TryCallGuestFunction(
		CpuContext callerContext,
		ulong entryPoint,
		ulong arg0,
		ulong arg1,
		ulong stackAddress,
		ulong stackSize,
		string reason,
		out string? error) =>
		TryCallGuestFunction(
			callerContext,
			entryPoint,
			arg0,
			arg1,
			arg2: 0,
			stackAddress,
			stackSize,
			reason,
			out _,
			out error);

	public bool TryCallGuestFunction(
		CpuContext callerContext,
		ulong entryPoint,
		ulong arg0,
		ulong arg1,
		ulong arg2,
		ulong stackAddress,
		ulong stackSize,
		string reason,
		out ulong returnValue,
		out string? error)
	{
		returnValue = 0;
		error = null;
		if (entryPoint < 65536)
		{
			error = $"invalid guest callback entry=0x{entryPoint:X16}";
			return false;
		}
		if (!TryGetVirtualMemory(callerContext, out var virtualMemory))
		{
			error = "caller context memory is not backed by IVirtualMemory";
			return false;
		}

		ulong callbackStackBase;
		ulong callbackStackSize;
		if (stackAddress != 0 && stackSize >= 0x100)
		{
			callbackStackBase = stackAddress;
			callbackStackSize = stackSize;
		}
		else
		{
			if (!TryMapGuestThreadRegion(virtualMemory, GuestThreadStackBaseAddress, GuestThreadStackSize, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write, out callbackStackBase, out error))
			{
				return false;
			}
			callbackStackSize = GuestThreadStackSize;
		}
		if (virtualMemory is IGuestStackMemory stackMemory)
		{
			stackMemory.RegisterStackRange(callbackStackBase, callbackStackSize);
		}

		var trackedMemory = new TrackedCpuMemory(virtualMemory);
		var fallbackTlsBase = unchecked((ulong)_tlsBaseAddress);
		var context = new CpuContext(trackedMemory, callerContext.TargetGeneration)
		{
			Rip = entryPoint,
			Rflags = 0x202,
			FsBase = callerContext.FsBase != 0 ? callerContext.FsBase : fallbackTlsBase,
			GsBase = callerContext.GsBase != 0 ? callerContext.GsBase : fallbackTlsBase,
		};
		context[CpuRegister.Rsp] = AlignDown(callbackStackBase + callbackStackSize, 16) - sizeof(ulong);
		context[CpuRegister.Rdi] = arg0;
		context[CpuRegister.Rsi] = arg1;
		context[CpuRegister.Rdx] = arg2;
		context[CpuRegister.Rcx] = 0;
		context[CpuRegister.R8] = 0;
		context[CpuRegister.R9] = 0;
		if (!InitializeGuestThreadFrame(context))
		{
			error = "failed to initialize guest callback stack";
			return false;
		}

		var previousLastError = LastError;
		try
		{
			LastError = null;
			var exitReason = ExecuteGuestThreadEntry(context, entryPoint, reason, out var callbackReason);
			if (exitReason is GuestNativeCallExitReason.Exception or GuestNativeCallExitReason.ForcedExit)
			{
				error = callbackReason ?? LastError ?? "guest callback failed";
				return false;
			}

			returnValue = context[CpuRegister.Rax];
			return true;
		}
		finally
		{
			LastError = previousLastError;
		}
	}

	public bool TryCallGuestContinuation(
		CpuContext callerContext,
		GuestCpuContinuation continuation,
		string reason,
		out string? error)
	{
		error = null;
		if (continuation.Rip < 65536 || continuation.Rsp == 0)
		{
			error = $"invalid guest continuation rip=0x{continuation.Rip:X16} rsp=0x{continuation.Rsp:X16}";
			return false;
		}
		if (!TryGetVirtualMemory(callerContext, out var virtualMemory))
		{
			error = "caller context memory is not backed by IVirtualMemory";
			return false;
		}

		var trackedMemory = new TrackedCpuMemory(virtualMemory);
		var fallbackTlsBase = unchecked((ulong)_tlsBaseAddress);
		var context = new CpuContext(trackedMemory, callerContext.TargetGeneration)
		{
			Rip = continuation.Rip,
			Rflags = continuation.Rflags == 0 ? 0x202UL : continuation.Rflags,
			FsBase = callerContext.FsBase != 0 ? callerContext.FsBase : (continuation.FsBase != 0 ? continuation.FsBase : fallbackTlsBase),
			GsBase = callerContext.GsBase != 0 ? callerContext.GsBase : (continuation.GsBase != 0 ? continuation.GsBase : fallbackTlsBase),
		};

		context[CpuRegister.Rax] = continuation.Rax;
		context[CpuRegister.Rcx] = continuation.Rcx;
		context[CpuRegister.Rdx] = continuation.Rdx;
		context[CpuRegister.Rbx] = continuation.Rbx;
		context[CpuRegister.Rbp] = continuation.Rbp;
		context[CpuRegister.Rsi] = continuation.Rsi;
		context[CpuRegister.Rdi] = continuation.Rdi;
		context[CpuRegister.R8] = continuation.R8;
		context[CpuRegister.R9] = continuation.R9;
		context[CpuRegister.R12] = continuation.R12;
		context[CpuRegister.R13] = continuation.R13;
		context[CpuRegister.R14] = continuation.R14;
		context[CpuRegister.R15] = continuation.R15;
		context[CpuRegister.Rsp] = continuation.Rsp;

		var exitReason = GuestNativeCallExitReason.Exception;
		string? callbackReason = null;
		string? callbackLastError = null;
		Exception? callbackException = null;
		var currentGuestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
		var currentFiberAddress = GuestThreadExecution.CurrentFiberAddress;
		var currentGuestThreadState = _activeGuestThreadState;

		void RunContinuation()
		{
			var restoreGuestThread = currentGuestThreadHandle != 0 &&
				GuestThreadExecution.CurrentGuestThreadHandle != currentGuestThreadHandle;
			var previousGuestThreadHandle = restoreGuestThread
				? GuestThreadExecution.EnterGuestThread(currentGuestThreadHandle)
				: 0UL;
			var restoreFiber = currentFiberAddress != 0 &&
				GuestThreadExecution.CurrentFiberAddress != currentFiberAddress;
			var previousFiberAddress = restoreFiber
				? GuestThreadExecution.EnterFiber(currentFiberAddress)
				: 0UL;
			var previousGuestThreadState = _activeGuestThreadState;
			_activeGuestThreadState = currentGuestThreadState;
			var previousLastError = LastError;
			try
			{
				TraceGuestContext(
					$"continuation-enter reason={reason} managed={Environment.CurrentManagedThreadId} guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} fiber=0x{GuestThreadExecution.CurrentFiberAddress:X16} captured_guest=0x{currentGuestThreadHandle:X16} captured_fiber=0x{currentFiberAddress:X16} restore_guest={restoreGuestThread} restore_fiber={restoreFiber}");
				LastError = null;
				exitReason = ExecuteGuestContinuationEntry(
					context,
					continuation.Rip,
					continuation.ReturnSlotAddress,
					reason,
					out callbackReason);
				callbackLastError = LastError;
			}
			catch (Exception ex)
			{
				callbackException = ex;
				callbackReason = ex.GetType().Name + ": " + ex.Message;
				exitReason = GuestNativeCallExitReason.Exception;
			}
			finally
			{
				_activeGuestThreadState = previousGuestThreadState;
				TraceGuestContext(
					$"continuation-exit reason={reason} managed={Environment.CurrentManagedThreadId} guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} fiber=0x{GuestThreadExecution.CurrentFiberAddress:X16} exit={exitReason}");
				LastError = previousLastError;
				if (restoreFiber)
				{
					GuestThreadExecution.RestoreFiber(previousFiberAddress);
				}
				if (restoreGuestThread)
				{
					GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
				}
			}
		}

		if (currentGuestThreadHandle != 0)
		{
			GuestContinuationRunner? runner;
			using (LockGate("TryCallGuestContinuation"))
			{
				if (_guestThreads.TryGetValue(currentGuestThreadHandle, out var guestThread))
				{
					runner = guestThread.ContinuationRunner ??= new GuestContinuationRunner(
						currentGuestThreadHandle,
						MapGuestThreadPriority(guestThread.Priority));
				}
				else
				{
					runner = null;
				}
			}

			if (runner is not null && !runner.IsCurrentThread)
			{
				runner.Run(RunContinuation);
			}
			else if (runner is not null)
			{
				TraceGuestContext(
					$"continuation-inline reason={reason} managed={Environment.CurrentManagedThreadId} guest=0x{currentGuestThreadHandle:X16} fiber=0x{currentFiberAddress:X16}");
				RunContinuation();
			}
			else
			{
				RunContinuationOnTemporaryThread(currentGuestThreadHandle, RunContinuation);
			}
		}
		else
		{
			RunContinuation();
		}

		if (callbackException is not null)
		{
			error = callbackReason ?? callbackException.Message;
			return false;
		}

		if (exitReason is GuestNativeCallExitReason.Exception or GuestNativeCallExitReason.ForcedExit)
		{
			error = callbackReason ?? callbackLastError ?? "guest continuation failed";
			return false;
		}

		return true;
	}

	private void TraceGuestContext(string message)
	{
		if (_logGuestContext)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] guest_context.{message}");
		}
	}

	private static void RunContinuationOnTemporaryThread(ulong guestThreadHandle, Action continuation)
	{
		var continuationThread = new Thread(() =>
		{
			var previousGuestThreadHandle = GuestThreadExecution.EnterGuestThread(guestThreadHandle);
			try
			{
				continuation();
			}
			finally
			{
				GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
			}
		})
		{
			IsBackground = true,
			Name = $"GuestContinuationNested-{guestThreadHandle:X}",
			Priority = ThreadPriority.BelowNormal,
		};
		continuationThread.Start();
		continuationThread.Join();
	}

	/// <summary>
	/// Parks every guest worker thread before the caller starts freeing executable
	/// memory. Workers unwind to the host at their next import dispatch (see the
	/// teardown check in DispatchImport); this waits for their host threads to
	/// finish within <paramref name="timeoutMs"/>. Returns false when at least one
	/// worker is still running — the caller must then leak its executable stubs
	/// rather than free memory a live thread may still execute.
	/// </summary>
	private bool RequestGuestThreadTeardown(int timeoutMs)
	{
		_guestTeardownRequested = true;
		GuestThreadBlocking.RequestShutdown();
		Thread[] hostThreads;
		using (LockGate("RequestGuestThreadTeardown"))
		{
			_readyGuestThreads.Clear();
			Interlocked.Exchange(ref _readyGuestThreadCount, 0);
			hostThreads = _guestThreads.Values
				.Select(static thread => thread.HostThread)
				.Where(static host => host is not null && host != Thread.CurrentThread && host.IsAlive)
				.Cast<Thread>()
				.ToArray();
		}

		var deadline = Environment.TickCount64 + timeoutMs;
		var allStopped = true;
		foreach (var host in hostThreads)
		{
			var remaining = (int)Math.Max(1L, deadline - Environment.TickCount64);
			if (!host.Join(remaining) && host.IsAlive)
			{
				allStopped = false;
				Console.Error.WriteLine(
					$"[LOADER][WARN] Guest worker host thread '{host.Name}' still running after teardown wait.");
			}
		}

		return allStopped;
	}

	private void ClearGuestThreads()
	{
		GuestContinuationRunner[] runners;
		ulong[] threadHandles;
		using (LockGate("ClearGuestThreads"))
		{
			threadHandles = _guestThreads.Keys
				.Concat(_externalGuestThreads.Keys)
				.Distinct()
				.ToArray();
			runners = _guestThreads.Values
				.Select(static thread => thread.ContinuationRunner)
				.Where(static runner => runner is not null)
				.Cast<GuestContinuationRunner>()
				.ToArray();
			_readyGuestThreads.Clear();
			Interlocked.Exchange(ref _readyGuestThreadCount, 0);
			_guestThreads.Clear();
			Volatile.Write(ref _guestThreadCount, 0);
			ClearGuestExceptionState();
		}
		ResetImportLoopPattern();

		foreach (var runner in runners)
		{
			runner.Dispose();
		}

		foreach (var threadHandle in threadHandles)
		{
			GuestThreadExecution.NotifyGuestThreadReaped(threadHandle);
		}
	}

	private bool TryCreateGuestThreadState(CpuContext creatorContext, GuestThreadStartRequest request, out GuestThreadState thread, out string? error)
	{
		thread = null!;
		if (!TryGetVirtualMemory(creatorContext, out var virtualMemory))
		{
			error = "creator context memory is not backed by IVirtualMemory";
			return false;
		}
		if (!TryMapGuestThreadRegion(virtualMemory, GuestThreadStackBaseAddress, GuestThreadStackSize, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write, out var stackBase, out error))
		{
			return false;
		}
		if (virtualMemory is IGuestStackMemory stackMemory)
		{
			stackMemory.RegisterStackRange(stackBase, GuestThreadStackSize);
		}
		if (!TryMapGuestThreadTlsRegion(virtualMemory, out var tlsBase, out error))
		{
			return false;
		}

		var trackedMemory = new TrackedCpuMemory(virtualMemory);
		var context = new CpuContext(trackedMemory, creatorContext.TargetGeneration)
		{
			Rip = request.EntryPoint,
			Rflags = 0x202,
			FsBase = tlsBase,
			GsBase = tlsBase,
		};
		context[CpuRegister.Rsp] = stackBase + GuestThreadStackSize - sizeof(ulong);
		context[CpuRegister.Rdi] = request.Argument;
		context[CpuRegister.Rsi] = 0;
		context[CpuRegister.Rdx] = 0;
		context[CpuRegister.Rcx] = 0;
		context[CpuRegister.R8] = 0;
		context[CpuRegister.R9] = 0;
		if (!InitializeGuestThreadFrame(context) || !InitializeGuestThreadTls(context, tlsBase, request.ThreadHandle))
		{
			error = "failed to initialize guest thread stack/TLS";
			return false;
		}

		thread = new GuestThreadState
		{
			ThreadHandle = request.ThreadHandle,
			EntryPoint = request.EntryPoint,
			Argument = request.Argument,
			Name = string.IsNullOrWhiteSpace(request.Name) ? $"Thread-{request.ThreadHandle:X}" : request.Name,
			Priority = request.Priority,
			AffinityMask = request.AffinityMask,
			Context = context,
			State = GuestThreadRunState.Ready,
		};
		error = null;
		return true;
	}

	private static bool TryGetVirtualMemory(CpuContext context, out IVirtualMemory virtualMemory)
	{
		if (context.Memory is IVirtualMemory directMemory)
		{
			virtualMemory = directMemory;
			return true;
		}
		if (context.Memory is TrackedCpuMemory trackedMemory && trackedMemory.Inner is IVirtualMemory trackedInner)
		{
			virtualMemory = trackedInner;
			return true;
		}

		virtualMemory = null!;
		return false;
	}

	private static bool TryMapGuestThreadRegion(
		IVirtualMemory virtualMemory,
		ulong baseAddress,
		ulong size,
		ProgramHeaderFlags protection,
		out ulong mappedBase,
		out string? error)
	{
		for (int i = 0; i < GuestThreadRegionSlotCount; i++)
		{
			var candidateBase = baseAddress - ((ulong)i * GuestThreadRegionStride);
			if (!IsGuestThreadRegionFree(virtualMemory, candidateBase, size))
			{
				continue;
			}
			try
			{
				virtualMemory.Map(
					candidateBase,
					size,
					fileOffset: 0,
					fileData: ReadOnlySpan<byte>.Empty,
					protection: protection);
				mappedBase = candidateBase;
				error = null;
				return true;
			}
			catch (InvalidOperationException)
			{
			}
		}

		mappedBase = 0;
		error = $"failed to map guest thread region near 0x{baseAddress:X16}";
		return false;
	}

	private static bool TryMapGuestThreadTlsRegion(
		IVirtualMemory virtualMemory,
		out ulong tlsBase,
		out string? error)
	{
		for (int i = 0; i < GuestThreadRegionSlotCount; i++)
		{
			var candidateBase = GuestThreadTlsBaseAddress - ((ulong)i * GuestThreadRegionStride);
			var mappedBase = candidateBase - GuestThreadTlsPrefixSize;
			var mappedSize = GuestThreadTlsSize + GuestThreadTlsPrefixSize;
			if (!IsGuestThreadRegionFree(virtualMemory, mappedBase, mappedSize))
			{
				continue;
			}
			try
			{
				virtualMemory.Map(
					mappedBase,
					mappedSize,
					fileOffset: 0,
					fileData: ReadOnlySpan<byte>.Empty,
					protection: ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
				tlsBase = candidateBase;
				error = null;
				return true;
			}
			catch (InvalidOperationException)
			{
			}
		}

		tlsBase = 0;
		error = $"failed to map guest TLS region near 0x{GuestThreadTlsBaseAddress:X16}";
		return false;
	}

	private static bool IsGuestThreadRegionFree(IVirtualMemory virtualMemory, ulong candidateBase, ulong size)
	{
		var candidateEnd = candidateBase + size;
		foreach (var region in virtualMemory.SnapshotRegions())
		{
			var regionStart = region.VirtualAddress;
			var regionEnd = regionStart + region.MemorySize;
			if (candidateBase < regionEnd && regionStart < candidateEnd)
			{
				return false;
			}
		}

		return true;
	}

	private static bool InitializeGuestThreadFrame(CpuContext context)
	{
		var stackTop = context[CpuRegister.Rsp] + sizeof(ulong);
		var sentinelFrame = AlignDown(stackTop - 0x20, 16);
		var seedRsp = sentinelFrame - sizeof(ulong);
		if (!context.TryWriteUInt64(sentinelFrame, 0) ||
			!context.TryWriteUInt64(sentinelFrame + sizeof(ulong), 0) ||
			!context.TryWriteUInt64(seedRsp, 0))
		{
			return false;
		}

		context[CpuRegister.Rbp] = sentinelFrame;
		context[CpuRegister.Rsp] = seedRsp;
		return true;
	}

	private static bool InitializeGuestThreadTls(CpuContext context, ulong tlsBase, ulong threadHandle)
	{
		if (!context.TryWriteUInt64(tlsBase - 0xF0, 0) ||
			!context.TryWriteUInt64(tlsBase + 0x00, tlsBase) ||
			!context.TryWriteUInt64(tlsBase + 0x10, threadHandle) ||
			!context.TryWriteUInt64(tlsBase + 0x28, 0xC0DEC0DECAFEBABEUL) ||
			!context.TryWriteUInt64(tlsBase + 0x60, tlsBase))
		{
			return false;
		}

		GuestTlsTemplate.SeedThreadBlock(context, tlsBase);
		return true;
	}

	private static ThreadPriority MapGuestThreadPriority(int priority)
	{
		if (priority <= 478)
		{
			return ThreadPriority.Highest;
		}
		if (priority >= 733)
		{
			return ThreadPriority.Lowest;
		}

		return ThreadPriority.Normal;
	}

	private void ApplyGuestThreadAffinity(ulong guestAffinityMask)
	{
		var hostAffinityMask = MapGuestThreadAffinity(guestAffinityMask);
		if (hostAffinityMask == 0)
		{
			return;
		}

		if (!_hostThreading.TrySetCurrentThreadAffinity((nuint)hostAffinityMask) && _logGuestThreads)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Failed to set guest thread affinity guest=0x{guestAffinityMask:X} " +
				$"host=0x{hostAffinityMask:X} error={Marshal.GetLastWin32Error()}");
		}
	}

	private static ulong MapGuestThreadAffinity(ulong guestAffinityMask)
	{
		if (guestAffinityMask == 0 || guestAffinityMask == ulong.MaxValue)
		{
			return 0;
		}

		var processorCount = Math.Min(Environment.ProcessorCount, 64);
		if (processorCount == 0)
		{
			return 0;
		}

		ulong hostAffinityMask = 0;
		for (var guestCpu = 0; guestCpu < 64; guestCpu++)
		{
			if ((guestAffinityMask & (1UL << guestCpu)) == 0)
			{
				continue;
			}

			var hostCpu = processorCount < 8
				? guestCpu % processorCount
				: processorCount >= 16
					? guestCpu * 2
					: guestCpu;
			if (hostCpu < processorCount)
			{
				hostAffinityMask |= 1UL << hostCpu;
			}
		}

		return hostAffinityMask;
	}

	private void RunGuestThread(GuestThreadState thread, string reason)
	{
		var previousLastError = LastError;
		var previousGuestThreadHandle = GuestThreadExecution.EnterGuestThread(thread.ThreadHandle);
		var previousGuestThreadState = _activeGuestThreadState;
		ApplyGuestThreadAffinity(thread.AffinityMask);
		Volatile.Write(ref thread.HostThreadId, unchecked((int)_hostThreading.CurrentThreadId));
		_activeGuestThreadState = thread;
		if (LogThreadMode)
		{
			_threadModeCycleId = Interlocked.Increment(ref _threadModeCycleCounter);
			TraceThreadMode(
				$"cycle_start name='{thread.Name}' guest=0x{thread.ThreadHandle:X16} reason={reason} " +
				$"rsp_slot=0x{(ulong)_hostThreading.GetTlsValue(_hostRspSlotTlsIndex):X}");
		}
		try
		{
			LastError = null;
			if (_logGuestThreads)
			{
				Console.Error.WriteLine(
					$"[LOADER][INFO] Pumping guest thread '{thread.Name}' reason={reason} " +
					$"entry=0x{thread.EntryPoint:X16}");
			}
			var exitReason = ExecuteGuestThreadEntry(
				thread.Context,
				thread.EntryPoint,
				thread.Name,
				out var blockReason);
			var notifyThreadExited = false;
			using (LockGate("RunGuestThread.exit"))
			{
				switch (exitReason)
				{
					case GuestNativeCallExitReason.Returned:
						thread.ExitValue = thread.Context[CpuRegister.Rax];
						notifyThreadExited = true;
						break;
					case GuestNativeCallExitReason.Blocked:
						thread.State = GuestThreadRunState.Blocked;
						thread.BlockReason = blockReason;
						break;
					default:
						thread.State = GuestThreadRunState.Faulted;
						thread.BlockReason = blockReason;
						break;
				}
			}
			if (notifyThreadExited)
			{
				try
				{
					GuestThreadExecution.NotifyGuestThreadExiting(thread.ThreadHandle, thread.Context);
				}
				finally
				{
					using (LockGate("RunGuestThread.exited"))
					{
						thread.State = GuestThreadRunState.Exited;
					}
					GuestThreadExecution.NotifyGuestThreadExited(thread.ThreadHandle);
				}
			}
			if (_logGuestThreads)
			{
				Console.Error.WriteLine(
					$"[LOADER][INFO] Guest thread '{thread.Name}' state={thread.State} reason={blockReason ?? "none"}");
			}
		}
		finally
		{
			if (LogThreadMode)
			{
				TraceThreadMode(
					$"cycle_end name='{thread.Name}' state={thread.State} " +
					$"imports={Interlocked.Read(ref thread.ImportCount)} " +
					$"rsp_slot=0x{(ulong)_hostThreading.GetTlsValue(_hostRspSlotTlsIndex):X}");
			}
			_activeGuestThreadState = previousGuestThreadState;
			Volatile.Write(ref thread.HostThreadId, 0);
			GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
			LastError = previousLastError;
			CompleteDeferredThreadReap(thread);
		}
	}

	private void CompleteDeferredThreadReap(GuestThreadState thread)
	{
		GuestContinuationRunner? continuationRunner = null;
		var reaped = false;
		using (LockGate("CompleteDeferredThreadReap"))
		{
			if (thread.ReapRequested &&
				thread.State == GuestThreadRunState.Exited &&
				_guestThreads.TryGetValue(thread.ThreadHandle, out var registeredThread) &&
				ReferenceEquals(thread, registeredThread))
			{
				_guestThreads.Remove(thread.ThreadHandle);
				Volatile.Write(ref _guestThreadCount, _guestThreads.Count);
				continuationRunner = thread.ContinuationRunner;
				thread.ContinuationRunner = null;
				reaped = true;
			}
		}

		continuationRunner?.Dispose();
		if (reaped)
		{
			GuestThreadExecution.NotifyGuestThreadReaped(thread.ThreadHandle);
		}
	}

	private unsafe bool TryAllocateNativeEntryStub(out void* entryStub, out ulong hostRspSlot)
	{
		entryStub = null;
		hostRspSlot = 0;
		var entryStubAddress = _hostMemory.Allocate(
			0,
			NativeEntryStubSize,
			HostPageProtection.ReadWrite);
		if (entryStubAddress == 0)
		{
			return false;
		}

		hostRspSlot = _hostMemory.Allocate(
			0,
			HostRspSlotSize,
			HostPageProtection.ReadWrite);
		if (hostRspSlot == 0)
		{
			_hostMemory.Free(entryStubAddress);
			return false;
		}

		entryStub = (void*)entryStubAddress;
		return true;
	}

	private unsafe GuestNativeCallExitReason ExecuteGuestThreadEntry(CpuContext context, ulong entryPoint, string name, out string? reason)
	{
		reason = null;
		if (context[CpuRegister.Rsp] == 0)
		{
			reason = "guest thread stack pointer is zero";
			return GuestNativeCallExitReason.Exception;
		}
		const uint stubSize = NativeEntryStubSize;
		if (!TryAllocateNativeEntryStub(out var ptr, out var hostRspSlot))
		{
			reason = "failed to allocate executable memory for guest thread stub";
			return GuestNativeCallExitReason.Exception;
		}
		var previousActiveBackend = _activeExecutionBackend;
		var previousActiveContext = _activeCpuContext;
		var previousSentinel = _activeEntryReturnSentinelRip;
		var previousReturnSlotAddress = _activeGuestReturnSlotAddress;
		var previousForcedExit = _activeForcedGuestExit;
		var previousYieldRequested = _activeGuestThreadYieldRequested;
		var previousYieldReason = _activeGuestThreadYieldReason;
		var previousGuestStackStart = _activeGuestStackStart;
		var previousGuestStackEnd = _activeGuestStackEnd;
		var previousHardwareExceptionRip = _activeGuestHardwareExceptionRip;
		var previousHardwareExceptionCode = _activeGuestHardwareExceptionCode;
		var previousHardwareExceptionAccessType = _activeGuestHardwareExceptionAccessType;
		var previousHardwareExceptionAccessAddress = _activeGuestHardwareExceptionAccessAddress;
		var previousHardwareExceptionRegisters = _activeGuestHardwareExceptionRegisters;
		var previousHardwareExceptionThreadHandle = _activeGuestHardwareExceptionThreadHandle;
		var guestStackSlotAddress = context[CpuRegister.Rsp];
		var originalGuestStackValue = 0UL;
		var guestStackSlotPatched = false;
		var guestEntryStarted = false;
		nint previousHostRspSlotValue = _hostThreading.GetTlsValue(_hostRspSlotTlsIndex);
		if (LogThreadMode)
		{
			TraceThreadMode(
				$"entry_setup name='{name}' entry=0x{entryPoint:X16} stub=0x{(ulong)ptr:X16} " +
				$"guest_rsp=0x{context[CpuRegister.Rsp]:X16} rsp_slot_prev=0x{(ulong)previousHostRspSlotValue:X}");
		}
		try
    {
        _activeExecutionBackend = this;
        _activeCpuContext = context;
        _activeEntryReturnSentinelRip = 0;
        _activeGuestReturnSlotAddress = 0;
        _activeForcedGuestExit = false;
        _activeGuestThreadYieldRequested = false;
        _activeGuestThreadYieldReason = null;
		_activeGuestHardwareExceptionRip = 0;
		_activeGuestHardwareExceptionCode = 0;
		_activeGuestHardwareExceptionAccessType = 0;
		_activeGuestHardwareExceptionAccessAddress = 0;
		_activeGuestHardwareExceptionRegisters = null;
		_activeGuestHardwareExceptionThreadHandle = 0;
		BindActiveGuestStackRange(context);
        BindTlsBase(context);
        byte* ptr2 = (byte*)ptr;
		int offset = 0;
        ptr2[offset++] = 83;
        ptr2[offset++] = 85;
        ptr2[offset++] = 87;
        ptr2[offset++] = 86;
        ptr2[offset++] = 65;
        ptr2[offset++] = 84;
        ptr2[offset++] = 65;
        ptr2[offset++] = 85;
        ptr2[offset++] = 65;
        ptr2[offset++] = 86;
        ptr2[offset++] = 65;
        ptr2[offset++] = 87;
        EmitHostNonvolatileXmmSave(ptr2, ref offset);
        ptr2[offset++] = 73;
        ptr2[offset++] = 186;
        *(ulong*)(ptr2 + offset) = hostRspSlot;
        offset += 8;
        ptr2[offset++] = 73;
        ptr2[offset++] = 137;
        ptr2[offset++] = 34;
        ptr2[offset++] = 72;
        ptr2[offset++] = 184;
        *(ulong*)(ptr2 + offset) = context[CpuRegister.Rsp];
        offset += 8;
        ptr2[offset++] = 72;
        ptr2[offset++] = 137;
        ptr2[offset++] = 196;
        ptr2[offset++] = 72;
        ptr2[offset++] = 131;
        ptr2[offset++] = 236;
        ptr2[offset++] = 8;
        ptr2[offset++] = 72;
        ptr2[offset++] = 189;
        *(ulong*)(ptr2 + offset) = context[CpuRegister.Rbp];
        offset += 8;
        ptr2[offset++] = 72;
        ptr2[offset++] = 184;
        *(ulong*)(ptr2 + offset) = context[CpuRegister.Rdi];
        offset += 8;
        ptr2[offset++] = 72;
        ptr2[offset++] = 137;
        ptr2[offset++] = 199;
        ptr2[offset++] = 72;
        ptr2[offset++] = 184;
        *(ulong*)(ptr2 + offset) = context[CpuRegister.Rsi];
        offset += 8;
        ptr2[offset++] = 72;
        ptr2[offset++] = 137;
        ptr2[offset++] = 198;
        ptr2[offset++] = 72;
        ptr2[offset++] = 184;
        *(ulong*)(ptr2 + offset) = context[CpuRegister.Rdx];
        offset += 8;
        ptr2[offset++] = 72;
        ptr2[offset++] = 137;
        ptr2[offset++] = 194;
        ptr2[offset++] = 72;
        ptr2[offset++] = 184;
        *(ulong*)(ptr2 + offset) = context[CpuRegister.Rcx];
        offset += 8;
        ptr2[offset++] = 72;
        ptr2[offset++] = 137;
        ptr2[offset++] = 193;
        ptr2[offset++] = 73;
        ptr2[offset++] = 184;
        *(ulong*)(ptr2 + offset) = context[CpuRegister.R8];
        offset += 8;
        ptr2[offset++] = 73;
        ptr2[offset++] = 185;
        *(ulong*)(ptr2 + offset) = context[CpuRegister.R9];
        offset += 8;
        ptr2[offset++] = 72;
        ptr2[offset++] = 184;
        *(ulong*)(ptr2 + offset) = entryPoint;
        offset += 8;
        ptr2[offset++] = byte.MaxValue;
        ptr2[offset++] = 208;
        int sentinelOffset = offset + 4;
        ptr2[offset++] = 72;
        ptr2[offset++] = 131;
        ptr2[offset++] = 196;
        ptr2[offset++] = 8;
        ptr2[offset++] = 73;
        ptr2[offset++] = 186;
        *(ulong*)(ptr2 + offset) = hostRspSlot;
        offset += 8;
        ptr2[offset++] = 73;
        ptr2[offset++] = 139;
        ptr2[offset++] = 34;
        EmitHostNonvolatileXmmRestore(ptr2, ref offset);
        ptr2[offset++] = 65;
        ptr2[offset++] = 95;
        ptr2[offset++] = 65;
        ptr2[offset++] = 94;
        ptr2[offset++] = 65;
        ptr2[offset++] = 93;
        ptr2[offset++] = 65;
        ptr2[offset++] = 92;
        ptr2[offset++] = 94;
        ptr2[offset++] = 95;
        ptr2[offset++] = 93;
        ptr2[offset++] = 91;
        ptr2[offset++] = 195;
        ulong sentinel = (ulong)ptr + (ulong)sentinelOffset;
        ActiveEntryReturnSentinelRip = (ulong)_guestReturnStub;
        _activeGuestReturnSlotAddress = context[CpuRegister.Rsp] - 16uL;
		if (!context.TryReadUInt64(guestStackSlotAddress, out originalGuestStackValue) ||
			!context.TryWriteUInt64(guestStackSlotAddress, sentinel))
		{
			reason = $"failed to patch guest thread return sentinel at 0x{guestStackSlotAddress:X16}";
			return GuestNativeCallExitReason.Exception;
		}
		guestStackSlotPatched = true;
        uint oldProtect = default(uint);
		if (!_hostMemory.Protect((ulong)ptr, stubSize, HostPageProtection.ReadExecute, out oldProtect))
        {
            reason = $"VirtualProtect failed for guest thread entry stub at 0x{(nint)ptr:X16}";
            return GuestNativeCallExitReason.Exception;
        }
        _hostMemory.FlushInstructionCache((ulong)ptr, stubSize);
		ActiveGuestThreadYieldRequested = false;
		ActiveGuestThreadYieldReason = null;
		guestEntryStarted = true;
		var nativeReturn = RunGuestEntryStub(ptr, hostRspSlot);
		context[CpuRegister.Rax] = nativeReturn;
		if (ApplyActiveGuestHardwareException(context, out var hardwareExceptionDetail))
		{
			LastError = hardwareExceptionDetail;
		}
        if (ActiveGuestThreadYieldRequested)
        {
            reason = ActiveGuestThreadYieldReason ?? "guest thread blocked";
            return GuestNativeCallExitReason.Blocked;
        }
        if (ActiveForcedGuestExit)
        {
            reason = LastError ?? "guest thread forced exit";
            return GuestNativeCallExitReason.ForcedExit;
        }
        reason = $"returned 0x{nativeReturn:X16}";
        return GuestNativeCallExitReason.Returned;
    }
    catch (AccessViolationException ex)
    {
        reason = "access violation: " + ex.Message;
        return GuestNativeCallExitReason.Exception;
    }
    catch (Exception ex)
    {
        reason = ex.GetType().Name + ": " + ex.Message;
        return GuestNativeCallExitReason.Exception;
    }
    finally
    {
		if (guestStackSlotPatched && !guestEntryStarted)
		{
			context.TryWriteUInt64(guestStackSlotAddress, originalGuestStackValue);
		}
        _hostThreading.SetTlsValue(_hostRspSlotTlsIndex, previousHostRspSlotValue);
        RestoreActiveExecutionThread(
            previousActiveBackend,
            previousActiveContext,
            previousSentinel,
            previousReturnSlotAddress,
            previousForcedExit,
            previousYieldRequested,
            previousYieldReason);
		_activeGuestStackStart = previousGuestStackStart;
		_activeGuestStackEnd = previousGuestStackEnd;
		_activeGuestHardwareExceptionRip = previousHardwareExceptionRip;
		_activeGuestHardwareExceptionCode = previousHardwareExceptionCode;
		_activeGuestHardwareExceptionAccessType = previousHardwareExceptionAccessType;
		_activeGuestHardwareExceptionAccessAddress = previousHardwareExceptionAccessAddress;
		_activeGuestHardwareExceptionRegisters = previousHardwareExceptionRegisters;
		_activeGuestHardwareExceptionThreadHandle = previousHardwareExceptionThreadHandle;
		_hostMemory.Free(hostRspSlot);
		_hostMemory.Free((ulong)ptr);
	}
}

	private unsafe GuestNativeCallExitReason ExecuteGuestContinuationEntry(
		CpuContext context,
		ulong entryPoint,
		ulong returnSlotAddress,
		string name,
		out string? reason)
	{
		reason = null;
		if (context[CpuRegister.Rsp] == 0)
		{
			reason = "guest thread stack pointer is zero";
			return GuestNativeCallExitReason.Exception;
		}
		const uint stubSize = NativeEntryStubSize;
		if (!TryAllocateNativeEntryStub(out var ptr, out var hostRspSlot))
		{
			reason = "failed to allocate executable memory for guest thread stub";
			return GuestNativeCallExitReason.Exception;
		}
		var previousActiveBackend = _activeExecutionBackend;
		var previousActiveContext = _activeCpuContext;
		var previousSentinel = _activeEntryReturnSentinelRip;
		var previousReturnSlotAddress = _activeGuestReturnSlotAddress;
		var previousForcedExit = _activeForcedGuestExit;
		var previousYieldRequested = _activeGuestThreadYieldRequested;
		var previousYieldReason = _activeGuestThreadYieldReason;
		var previousGuestStackStart = _activeGuestStackStart;
		var previousGuestStackEnd = _activeGuestStackEnd;
		var previousHardwareExceptionRip = _activeGuestHardwareExceptionRip;
		var previousHardwareExceptionCode = _activeGuestHardwareExceptionCode;
		var previousHardwareExceptionAccessType = _activeGuestHardwareExceptionAccessType;
		var previousHardwareExceptionAccessAddress = _activeGuestHardwareExceptionAccessAddress;
		var previousHardwareExceptionRegisters = _activeGuestHardwareExceptionRegisters;
		var previousHardwareExceptionThreadHandle = _activeGuestHardwareExceptionThreadHandle;
		var originalReturnSlotValue = 0UL;
		var returnSlotPatched = false;
		var guestEntryStarted = false;
		nint previousHostRspSlotValue = _hostThreading.GetTlsValue(_hostRspSlotTlsIndex);
		if (LogThreadMode)
		{
			TraceThreadMode(
				$"continuation_setup name='{name}' resume=0x{entryPoint:X16} stub=0x{(ulong)ptr:X16} " +
				$"guest_rsp=0x{context[CpuRegister.Rsp]:X16} rsp_slot_prev=0x{(ulong)previousHostRspSlotValue:X}");
		}
		try
		{
			_activeExecutionBackend = this;
			_activeCpuContext = context;
			_activeEntryReturnSentinelRip = 0;
			_activeGuestReturnSlotAddress = returnSlotAddress;
			_activeForcedGuestExit = false;
			_activeGuestThreadYieldRequested = false;
			_activeGuestThreadYieldReason = null;
			_activeGuestHardwareExceptionRip = 0;
			_activeGuestHardwareExceptionCode = 0;
			_activeGuestHardwareExceptionAccessType = 0;
			_activeGuestHardwareExceptionAccessAddress = 0;
			_activeGuestHardwareExceptionRegisters = null;
			_activeGuestHardwareExceptionThreadHandle = 0;
			BindActiveGuestStackRange(context);
			BindTlsBase(context);
			byte* ptr2 = (byte*)ptr;
			var emitter = new NativeCodeEmitter(ptr2);

			emitter.Emit(0x53); // push rbx
			emitter.Emit(0x55); // push rbp
			emitter.Emit(0x57); // push rdi
			emitter.Emit(0x56); // push rsi
			emitter.Emit(0x41); emitter.Emit(0x54); // push r12
			emitter.Emit(0x41); emitter.Emit(0x55); // push r13
			emitter.Emit(0x41); emitter.Emit(0x56); // push r14
			emitter.Emit(0x41); emitter.Emit(0x57); // push r15
			EmitHostNonvolatileXmmSave(ptr2, ref emitter.Offset);
			emitter.EmitMovR64Immediate(0x49, 0xBA, hostRspSlot); // mov r10, hostRspSlot
			emitter.Emit(0x49); emitter.Emit(0x89); emitter.Emit(0x22); // mov [r10], rsp
			emitter.EmitMovR64Immediate(0x48, 0xB8, context[CpuRegister.Rsp]); // mov rax, guest rsp
			emitter.Emit(0x48); emitter.Emit(0x89); emitter.Emit(0xC4); // mov rsp, rax
			emitter.EmitMovR64Immediate(0x48, 0xBB, context[CpuRegister.Rbx]); // mov rbx, imm64
			emitter.EmitMovR64Immediate(0x48, 0xBD, context[CpuRegister.Rbp]); // mov rbp, imm64
			emitter.EmitMovR64Immediate(0x48, 0xBF, context[CpuRegister.Rdi]); // mov rdi, imm64
			emitter.EmitMovR64Immediate(0x48, 0xBE, context[CpuRegister.Rsi]); // mov rsi, imm64
			emitter.EmitMovR64Immediate(0x48, 0xBA, context[CpuRegister.Rdx]); // mov rdx, imm64
			emitter.EmitMovR64Immediate(0x48, 0xB9, context[CpuRegister.Rcx]); // mov rcx, imm64
			emitter.EmitMovR64Immediate(0x49, 0xB8, context[CpuRegister.R8]); // mov r8, imm64
			emitter.EmitMovR64Immediate(0x49, 0xB9, context[CpuRegister.R9]); // mov r9, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBC, context[CpuRegister.R12]); // mov r12, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBD, context[CpuRegister.R13]); // mov r13, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBE, context[CpuRegister.R14]); // mov r14, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBF, context[CpuRegister.R15]); // mov r15, imm64
			emitter.EmitMovR64Immediate(0x48, 0xB8, context[CpuRegister.Rax]); // mov rax, imm64
			emitter.EmitMovR64Immediate(0x49, 0xBB, entryPoint); // mov r11, entryPoint
			emitter.Emit(0x41); emitter.Emit(0xFF); emitter.Emit(0xE3); // jmp r11
			ActiveEntryReturnSentinelRip = (ulong)_guestReturnStub;
			if (returnSlotAddress == 0 ||
				!context.TryReadUInt64(returnSlotAddress, out originalReturnSlotValue) ||
				!context.TryWriteUInt64(returnSlotAddress, (ulong)_guestReturnStub))
			{
				reason = $"failed to patch guest continuation return slot at 0x{returnSlotAddress:X16}";
				return GuestNativeCallExitReason.Exception;
			}
			returnSlotPatched = true;
			uint oldProtect = default(uint);
			if (!_hostMemory.Protect((ulong)ptr, stubSize, HostPageProtection.ReadExecute, out oldProtect))
			{
				reason = $"VirtualProtect failed for guest continuation stub at 0x{(nint)ptr:X16}";
				return GuestNativeCallExitReason.Exception;
			}
			_hostMemory.FlushInstructionCache((ulong)ptr, stubSize);
			ActiveGuestThreadYieldRequested = false;
			ActiveGuestThreadYieldReason = null;

			try
			{
				guestEntryStarted = true;
				var nativeReturn = RunGuestEntryStub(ptr, hostRspSlot);
				var entryHardwareException =
					ApplyActiveGuestHardwareException(context, out var hardwareExceptionDetail);
				if (entryHardwareException)
				{
					LastError = hardwareExceptionDetail;
				}
				if (ActiveGuestThreadYieldRequested)
				{
					reason = ActiveGuestThreadYieldReason ?? "guest thread blocked";
					return GuestNativeCallExitReason.Blocked;
				}
				if (ActiveForcedGuestExit)
				{
					reason = LastError ?? "guest thread forced exit";
					return GuestNativeCallExitReason.ForcedExit;
				}
				reason = $"returned 0x{nativeReturn:X8}";
				return GuestNativeCallExitReason.Returned;
			}
			catch (AccessViolationException ex)
			{
				reason = "access violation: " + ex.Message;
				return GuestNativeCallExitReason.Exception;
			}
			catch (Exception ex)
			{
				reason = ex.GetType().Name + ": " + ex.Message;
				return GuestNativeCallExitReason.Exception;
			}
		}
		finally
		{
			if (returnSlotPatched && !guestEntryStarted)
			{
				context.TryWriteUInt64(returnSlotAddress, originalReturnSlotValue);
			}
			_hostThreading.SetTlsValue(_hostRspSlotTlsIndex, previousHostRspSlotValue);
			RestoreActiveExecutionThread(
				previousActiveBackend,
				previousActiveContext,
				previousSentinel,
				previousReturnSlotAddress,
				previousForcedExit,
				previousYieldRequested,
				previousYieldReason);
			_activeGuestStackStart = previousGuestStackStart;
			_activeGuestStackEnd = previousGuestStackEnd;
			_activeGuestHardwareExceptionRip = previousHardwareExceptionRip;
			_activeGuestHardwareExceptionCode = previousHardwareExceptionCode;
			_activeGuestHardwareExceptionAccessType = previousHardwareExceptionAccessType;
			_activeGuestHardwareExceptionAccessAddress = previousHardwareExceptionAccessAddress;
			_activeGuestHardwareExceptionRegisters = previousHardwareExceptionRegisters;
			_activeGuestHardwareExceptionThreadHandle = previousHardwareExceptionThreadHandle;
			_hostMemory.Free(hostRspSlot);
			_hostMemory.Free((ulong)ptr);
		}
	}

	// The continuation trampoline is rebuilt on every blocked-thread resume.
	// Keep its tiny writer on the stack: capturing local emit functions create a
	// managed display-class allocation on this extremely hot path.
	private unsafe ref struct NativeCodeEmitter(byte* code)
	{
		private readonly byte* _code = code;
		public int Offset;

		public void Emit(byte value)
		{
			_code[Offset++] = value;
		}

		public void Emit(ushort value)
		{
			*(ushort*)(_code + Offset) = value;
			Offset += sizeof(ushort);
		}

		public void Emit(uint value)
		{
			*(uint*)(_code + Offset) = value;
			Offset += sizeof(uint);
		}

		private void Emit(ulong value)
		{
			*(ulong*)(_code + Offset) = value;
			Offset += sizeof(ulong);
		}

		public void EmitMovR64Immediate(byte rex, byte opcode, ulong value)
		{
			Emit(rex);
			Emit(opcode);
			Emit(value);
		}
	}

	private static ulong AlignDown(ulong value, ulong alignment)
	{
		if (alignment == 0)
		{
			return value;
		}
		return value & ~(alignment - 1);
	}

	private static unsafe void EmitByte(byte* code, ref int offset, byte value)
	{
		code[offset++] = value;
	}

	private static unsafe void EmitUInt32(byte* code, ref int offset, uint value)
	{
		*(uint*)(code + offset) = value;
		offset += sizeof(uint);
	}

	private static unsafe void EmitHostNonvolatileXmmSave(byte* code, ref int offset)
	{
		EmitByte(code, ref offset, 0x48);
		EmitByte(code, ref offset, 0x81);
		EmitByte(code, ref offset, 0xEC);
		EmitUInt32(code, ref offset, HostXmmSaveAreaSize);
		for (int xmm = 6; xmm <= 15; xmm++)
		{
			EmitMovdquRspXmm(code, ref offset, store: true, xmm, (byte)((xmm - 6) * 16));
		}
	}

	private static unsafe void EmitHostNonvolatileXmmRestore(byte* code, ref int offset)
	{
		for (int xmm = 6; xmm <= 15; xmm++)
		{
			EmitMovdquRspXmm(code, ref offset, store: false, xmm, (byte)((xmm - 6) * 16));
		}
		EmitByte(code, ref offset, 0x48);
		EmitByte(code, ref offset, 0x81);
		EmitByte(code, ref offset, 0xC4);
		EmitUInt32(code, ref offset, HostXmmSaveAreaSize);
	}

	private static unsafe void EmitMovdquRspXmm(byte* code, ref int offset, bool store, int xmm, byte displacement)
	{
		EmitByte(code, ref offset, 0xF3);
		if (xmm >= 8)
		{
			EmitByte(code, ref offset, 0x44);
		}
		EmitByte(code, ref offset, 0x0F);
		EmitByte(code, ref offset, store ? (byte)0x7F : (byte)0x6F);
		if (displacement < 0x80)
		{
			EmitByte(code, ref offset, (byte)(0x44 | ((xmm & 7) << 3)));
			EmitByte(code, ref offset, 0x24);
			EmitByte(code, ref offset, displacement);
		}
		else
		{
			EmitByte(code, ref offset, (byte)(0x84 | ((xmm & 7) << 3)));
			EmitByte(code, ref offset, 0x24);
			EmitUInt32(code, ref offset, displacement);
		}
	}

	private unsafe bool ExecuteEntry(CpuContext context, ulong entryPoint, NativeEntryReturnContract returnContract, out OrbisGen2Result result)
	{
		Console.Error.WriteLine($"[LOADER][INFO] ExecuteEntry starting at 0x{entryPoint:X16}");
		Console.Error.WriteLine($"[LOADER][INFO] RSP=0x{context[CpuRegister.Rsp]:X16}, RDI=0x{context[CpuRegister.Rdi]:X16}");
		ulong num = context[CpuRegister.Rsp];
		if (num == 0)
		{
			LastError = "Guest stack pointer is zero";
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
			return false;
		}
		Console.Error.WriteLine($"[LOADER][INFO] StackTop: 0x{num:X16}");
		const uint stubSize = NativeEntryStubSize;
		if (!TryAllocateNativeEntryStub(out var ptr, out var num2))
		{
			LastError = "Failed to allocate executable memory for stub";
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
			return false;
		}
		var previousActiveBackend = _activeExecutionBackend;
		var previousActiveContext = _activeCpuContext;
		var previousSentinel = _activeEntryReturnSentinelRip;
		var previousReturnSlotAddress = _activeGuestReturnSlotAddress;
		var previousForcedExit = _activeForcedGuestExit;
		var previousYieldRequested = _activeGuestThreadYieldRequested;
		var previousYieldReason = _activeGuestThreadYieldReason;
		var previousGuestStackStart = _activeGuestStackStart;
		var previousGuestStackEnd = _activeGuestStackEnd;
		var previousHardwareExceptionRip = _activeGuestHardwareExceptionRip;
		var previousHardwareExceptionCode = _activeGuestHardwareExceptionCode;
		var previousHardwareExceptionAccessType = _activeGuestHardwareExceptionAccessType;
		var previousHardwareExceptionAccessAddress = _activeGuestHardwareExceptionAccessAddress;
		var previousHardwareExceptionRegisters = _activeGuestHardwareExceptionRegisters;
		var previousHardwareExceptionThreadHandle = _activeGuestHardwareExceptionThreadHandle;
		var guestStackSlotAddress = context[CpuRegister.Rsp];
		var originalGuestStackValue = 0UL;
		var guestStackSlotPatched = false;
		var guestEntryStarted = false;
		nint previousHostRspSlotValue = _hostThreading.GetTlsValue(_hostRspSlotTlsIndex);
		try
		{
			_activeExecutionBackend = this;
			_activeCpuContext = context;
			_activeEntryReturnSentinelRip = 0;
			_activeGuestReturnSlotAddress = 0;
			_activeForcedGuestExit = false;
			_activeGuestThreadYieldRequested = false;
			_activeGuestThreadYieldReason = null;
			_activeGuestHardwareExceptionRip = 0;
			_activeGuestHardwareExceptionCode = 0;
			_activeGuestHardwareExceptionAccessType = 0;
			_activeGuestHardwareExceptionAccessAddress = 0;
			_activeGuestHardwareExceptionRegisters = null;
			_activeGuestHardwareExceptionThreadHandle = 0;
			BindActiveGuestStackRange(context);
			BindTlsBase(context);
			byte* ptr2 = (byte*)ptr;
			int num3 = 0;
			ptr2[num3++] = 83;
			ptr2[num3++] = 85;
			ptr2[num3++] = 87;
			ptr2[num3++] = 86;
			ptr2[num3++] = 65;
			ptr2[num3++] = 84;
			ptr2[num3++] = 65;
			ptr2[num3++] = 85;
			ptr2[num3++] = 65;
			ptr2[num3++] = 86;
			ptr2[num3++] = 65;
			ptr2[num3++] = 87;
			EmitHostNonvolatileXmmSave(ptr2, ref num3);
			ptr2[num3++] = 73;
			ptr2[num3++] = 186;
			*(ulong*)(ptr2 + num3) = num2;
			num3 += 8;
			ptr2[num3++] = 73;
			ptr2[num3++] = 137;
			ptr2[num3++] = 34;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rsp];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 196;
			ptr2[num3++] = 72;
			ptr2[num3++] = 131;
			ptr2[num3++] = 236;
			ptr2[num3++] = 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 189;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rbp];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rdi];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 199;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rsi];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 198;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rdx];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 194;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rcx];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 193;
			ptr2[num3++] = 73;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.R8];
			num3 += 8;
			ptr2[num3++] = 73;
			ptr2[num3++] = 185;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.R9];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = entryPoint;
			num3 += 8;
			ptr2[num3++] = byte.MaxValue;
			ptr2[num3++] = 208;
			int num4 = num3 + 4;
			ptr2[num3++] = 72;
			ptr2[num3++] = 131;
			ptr2[num3++] = 196;
			ptr2[num3++] = 8;
			ptr2[num3++] = 73;
			ptr2[num3++] = 186;
			*(ulong*)(ptr2 + num3) = num2;
			num3 += 8;
			ptr2[num3++] = 73;
			ptr2[num3++] = 139;
			ptr2[num3++] = 34;
			EmitHostNonvolatileXmmRestore(ptr2, ref num3);
			ptr2[num3++] = 65;
			ptr2[num3++] = 95;
			ptr2[num3++] = 65;
			ptr2[num3++] = 94;
			ptr2[num3++] = 65;
			ptr2[num3++] = 93;
			ptr2[num3++] = 65;
			ptr2[num3++] = 92;
			ptr2[num3++] = 94;
			ptr2[num3++] = 95;
			ptr2[num3++] = 93;
			ptr2[num3++] = 91;
			ptr2[num3++] = 195;
			ulong value = (ulong)ptr + (ulong)num4;
			ActiveEntryReturnSentinelRip = (ulong)_guestReturnStub;
			_activeGuestReturnSlotAddress = context[CpuRegister.Rsp] - 16uL;
			if (!context.TryReadUInt64(guestStackSlotAddress, out originalGuestStackValue) ||
				!context.TryWriteUInt64(guestStackSlotAddress, value))
			{
				LastError = $"Failed to patch native return sentinel at 0x{guestStackSlotAddress:X16}";
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			guestStackSlotPatched = true;
			uint num5 = default(uint);
			if (!_hostMemory.Protect((ulong)ptr, stubSize, HostPageProtection.ReadExecute, out num5))
			{
				LastError = $"VirtualProtect failed for guest entry stub at 0x{(nint)ptr:X16}";
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			_hostMemory.FlushInstructionCache((ulong)ptr, stubSize);
			if (_hostRspSlotStorage != 0)
			{
				*(ulong*)_hostRspSlotStorage = num2;
			}
			_hostThreading.SetTlsValue(_hostRspSlotTlsIndex, (nint)num2);
			if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_SENTINEL_PROBE"), "1", StringComparison.Ordinal))
			{
				Console.Error.WriteLine("[LOADER][INFO] Running unresolved sentinel probe...");
				CallNativeEntry((void*)65534);
				Console.Error.WriteLine("[LOADER][INFO] Sentinel probe returned.");
			}
			Console.Error.WriteLine("[LOADER][INFO] Calling guest entry...");
			StartStallWatchdog();
			ulong num6 = ulong.MaxValue;
			try
			{
				guestEntryStarted = true;
				num6 = RunGuestEntryStub(ptr, num2);
				if (ApplyActiveGuestHardwareException(context, out var hardwareExceptionDetail))
				{
					LastError = hardwareExceptionDetail;
					DumpGuestDisasmDiagnostics(
						_activeGuestHardwareExceptionRip,
						rbp: 0);
					DumpGuestReferenceDiagnostics();
					DumpGuestPointerWindowDiagnostics();
				}
				else if (!ActiveForcedGuestExit)
				{
					LastEntryReturnValue = num6;
				}
				Console.Error.WriteLine($"[LOADER][INFO] Guest returned: {num6}");
				PumpUntilGuestThreadsIdle(context, "entry_return");
			}
			catch (AccessViolationException ex)
			{
				Console.Error.WriteLine("[LOADER][ERROR] Access Violation during execution: " + ex.Message);
				Console.Error.WriteLine("[LOADER][ERROR] This usually means:");
				Console.Error.WriteLine("  1. Invalid memory access in guest code");
				Console.Error.WriteLine("  2. Unpatched import/TLS call");
				Console.Error.WriteLine("  3. Stack corruption");
				num6 = ulong.MaxValue;
			}
			catch (Exception ex2)
			{
				Console.Error.WriteLine("[LOADER][ERROR] Exception during execution: " + ex2.GetType().Name + ": " + ex2.Message);
				LastError = "Exception during execution: " + ex2.GetType().Name + ": " + ex2.Message;
				num6 = ulong.MaxValue;
			}
			if (ActiveForcedGuestExit)
			{
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
				if (string.IsNullOrEmpty(LastError))
				{
					LastError = _hostShutdownRequested
						? "Host shutdown requested."
						: "Detected repeating import loop and forced guest unwind to host.";
				}
				Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
				RequestGuestThreadTeardown(3000);
				return false;
			}
			if (num6 == 0 || returnContract != NativeEntryReturnContract.RequireZero)
			{
				result = OrbisGen2Result.ORBIS_GEN2_OK;
				LastError = null;
				LastTrapInfo = null;
				return true;
			}
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
			if (string.IsNullOrEmpty(LastError))
			{
				LastError = $"Guest entry point returned non-zero: {num6}";
			}
			Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
			RequestGuestThreadTeardown(3000);
			return false;
		}
		finally
		{
			if (guestStackSlotPatched && !guestEntryStarted)
			{
				context.TryWriteUInt64(guestStackSlotAddress, originalGuestStackValue);
			}
			StopStallWatchdog();
			ActiveEntryReturnSentinelRip = 0uL;
			_hostThreading.SetTlsValue(_hostRspSlotTlsIndex, previousHostRspSlotValue);
			if (_hostRspSlotStorage != 0)
			{
				*(long*)_hostRspSlotStorage = 0L;
			}
			RestoreActiveExecutionThread(
				previousActiveBackend,
				previousActiveContext,
				previousSentinel,
				previousReturnSlotAddress,
				previousForcedExit,
				previousYieldRequested,
				previousYieldReason);
			_activeGuestStackStart = previousGuestStackStart;
			_activeGuestStackEnd = previousGuestStackEnd;
			_activeGuestHardwareExceptionRip = previousHardwareExceptionRip;
			_activeGuestHardwareExceptionCode = previousHardwareExceptionCode;
			_activeGuestHardwareExceptionAccessType = previousHardwareExceptionAccessType;
			_activeGuestHardwareExceptionAccessAddress = previousHardwareExceptionAccessAddress;
			_activeGuestHardwareExceptionRegisters = previousHardwareExceptionRegisters;
			_activeGuestHardwareExceptionThreadHandle = previousHardwareExceptionThreadHandle;
			_hostMemory.Free(num2);
			_hostMemory.Free((ulong)ptr);
		}
	}


	private void MarkExecutionProgress()
	{
		Volatile.Write(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
	}

	private static int GetStallWatchdogSeconds()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_STALL_WATCHDOG_SECONDS"), out var result))
		{
			return Math.Max(0, result);
		}
		return 20;
	}

	// SHARPEMU_PERIODIC_SNAPSHOT_SECONDS=N: dump the stall snapshot every N
	// seconds regardless of progress, for diagnosing soft stalls where imports
	// keep flowing but the game stops advancing.
	private static int GetPeriodicSnapshotSeconds()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PERIODIC_SNAPSHOT_SECONDS"), out var result))
		{
			return Math.Max(0, result);
		}
		return 0;
	}

	private void StartStallWatchdog()
	{
		int stallWatchdogSeconds = GetStallWatchdogSeconds();
		if (stallWatchdogSeconds <= 0 || _stallWatchdogThread != null)
		{
			return;
		}
		_stallWatchdogStop = false;

		// Drives woken threads when every guest thread is parked (nothing dispatches then).
		var dispatcherThread = new Thread(new ThreadStart(delegate
		{
			while (!_stallWatchdogStop)
			{
				Thread.Sleep(1);
				if (Volatile.Read(ref _readyGuestThreadCount) > 0 && _cpuContext is { } dispatchContext)
				{
					Pump(dispatchContext, "dispatcher");
				}
			}
		}))
		{
			IsBackground = true,
			Name = "SharpEmu-GuestThreadDispatcher"
		};
		dispatcherThread.Start();

		long num = (long)((double)stallWatchdogSeconds * Stopwatch.Frequency);
		var periodicSnapshotTicks = (long)((double)GetPeriodicSnapshotSeconds() * Stopwatch.Frequency);
		var lastPeriodicSnapshot = Stopwatch.GetTimestamp();
		_stallWatchdogThread = new Thread(new ThreadStart(delegate
		{
			while (!_stallWatchdogStop)
			{
				Thread.Sleep(200);
				if (_stallWatchdogStop)
				{
					break;
				}
				if (periodicSnapshotTicks > 0 &&
					Stopwatch.GetTimestamp() - lastPeriodicSnapshot >= periodicSnapshotTicks)
				{
					lastPeriodicSnapshot = Stopwatch.GetTimestamp();
					var gateOwnerSite = _gateOwnerSite;
					var gateOwnerTid = Volatile.Read(ref _gateOwnerManagedThreadId);
					var gateHeldMs = gateOwnerSite is null
						? 0.0
						: Stopwatch.GetElapsedTime(Volatile.Read(ref _gateAcquireTimestamp)).TotalMilliseconds;
					var snapshotText = new System.Text.StringBuilder();
					snapshotText.AppendLine(
						$"[LOADER][DIAG] Periodic snapshot: gate_owner={gateOwnerSite ?? "none"} " +
						$"gate_tid={gateOwnerTid} gate_held_ms={gateHeldMs:0}");
					// Never touch the gate here: the periodic snapshot must keep
					// reporting even (especially) when the gate is wedged.
					// Dump guest threads without the lock; tolerate torn reads.
					try
					{
						foreach (var thread in _guestThreads.Values)
						{
							snapshotText.AppendLine(
								$"[LOADER][DIAG] gateless guest-thread: handle=0x{thread.ThreadHandle:X16} name='{thread.Name}' " +
								$"state={thread.State} imports={Interlocked.Read(ref thread.ImportCount)} " +
								$"nid={Volatile.Read(ref thread.LastImportNid) ?? "none"} ret=0x{Volatile.Read(ref thread.LastReturnRip):X16} " +
								$"block={thread.BlockReason ?? GuestThreadBlocking.DescribeBlock(thread.ThreadHandle) ?? "none"}");
						}
					}
					catch (Exception snapshotError)
					{
						snapshotText.AppendLine($"[LOADER][DIAG] gateless snapshot failed: {snapshotError.Message}");
					}

					// Console can be wedged by whatever is being diagnosed, so
					// write to a side file when one is configured and fall back
					// to stderr otherwise.
					var snapshotPath = Environment.GetEnvironmentVariable("SHARPEMU_PERIODIC_SNAPSHOT_FILE");
					if (!string.IsNullOrWhiteSpace(snapshotPath))
					{
						try
						{
							System.IO.File.AppendAllText(snapshotPath, snapshotText.ToString());
						}
						catch
						{
						}
					}
					else
					{
						Console.Error.Write(snapshotText.ToString());
						Console.Error.Flush();
					}
				}
				long num2 = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastProgressTimestamp);
				if (num2 < num)
				{
					continue;
				}
				if (HasReadyGuestThread())
				{
					if (_cpuContext is { } watchdogContext)
					{
						Pump(watchdogContext, "watchdog");
					}
					Console.Error.WriteLine(
						$"[LOADER][WARN] No import progress for {stallWatchdogSeconds}s, but a guest thread is ready; continuing.");
					LogStallWatchdogSnapshot();
					Console.Error.Flush();
					MarkExecutionProgress();
					continue;
				}
				if (IsExpectedBlockingImportStall(out var blockingNid, out var blockingName))
				{
					Console.Error.WriteLine(
						$"[LOADER][WARN] No import progress for {stallWatchdogSeconds}s while waiting in {blockingName} ({blockingNid}); continuing.");
					LogStallWatchdogSnapshot();
					Console.Error.Flush();
					MarkExecutionProgress();
					continue;
				}
				if (Interlocked.Exchange(ref _stallWatchdogTriggered, 1) != 0)
				{
					continue;
				}
				LastError = $"Execution stalled with no import progress for {stallWatchdogSeconds}s (imports={Volatile.Read(ref _importDispatchCount)}).";
				Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
				LogStallWatchdogSnapshot();
				Console.Error.Flush();
				Environment.Exit(StallWatchdogExitCode);
			}
		}))
		{
			IsBackground = true,
			Name = "SharpEmu-StallWatchdog"
		};
		_stallWatchdogThread.Start();
	}

	private bool HasReadyGuestThread()
	{
		using (LockGate("HasReadyGuestThread"))
		{
			foreach (var thread in _guestThreads.Values)
			{
				if (thread.State is GuestThreadRunState.Ready)
				{
					return true;
				}
			}
		}

		return false;
	}

	// A thread parked in a blocking wait is idle by design, not stalled.
	private bool IsExpectedBlockingImportStall(out string nid, out string name)
	{
		nid = string.Empty;
		name = string.Empty;
		var cpuContext = _cpuContext;
		if (cpuContext is null)
		{
			return false;
		}

		var importAddress = cpuContext.Rip & 0xFFFFFFFFFFFFFFF0uL;
		foreach (var entry in _importEntries)
		{
			if (entry.Address != importAddress)
			{
				continue;
			}

			nid = entry.Nid;
			name = _moduleManager.TryGetExport(nid, out var export)
				? $"{export.LibraryName}:{export.Name}"
				: nid;
			return nid is
				"Op8TBGY5KHg" or // pthread_cond_wait
				"27bAgiJmOh0" or // pthread_cond_timedwait
				"fzyMKs9kim0";   // sceKernelWaitEqueue
		}

		return false;
	}

	private void StopStallWatchdog()
	{
		_stallWatchdogStop = true;
		Thread? stallWatchdogThread = _stallWatchdogThread;
		if (stallWatchdogThread == null)
		{
			return;
		}
		if (!ReferenceEquals(Thread.CurrentThread, stallWatchdogThread))
		{
			try
			{
				stallWatchdogThread.Join(300);
			}
			catch
			{
			}
		}
		_stallWatchdogThread = null;
	}

	private void LogStallWatchdogSnapshot()
	{
		try
		{
			var cpuContext = _cpuContext;
			if (cpuContext is null)
			{
				return;
			}
			ulong rsp = cpuContext[CpuRegister.Rsp];
			Console.Error.WriteLine($"[LOADER][ERROR] Stall snapshot: rip=0x{cpuContext.Rip:X16} rsp=0x{rsp:X16} rbp=0x{cpuContext[CpuRegister.Rbp]:X16} rax=0x{cpuContext[CpuRegister.Rax]:X16} rbx=0x{cpuContext[CpuRegister.Rbx]:X16} rcx=0x{cpuContext[CpuRegister.Rcx]:X16} rdx=0x{cpuContext[CpuRegister.Rdx]:X16} rsi=0x{cpuContext[CpuRegister.Rsi]:X16} rdi=0x{cpuContext[CpuRegister.Rdi]:X16}");
			ulong num = cpuContext.Rip & 0xFFFFFFFFFFFFFFF0uL;
			var importEntries = _importEntries;
			for (int i = 0; i < importEntries.Length; i++)
			{
				if (importEntries[i].Address != num)
				{
					continue;
				}
				string text = importEntries[i].Nid;
				if (_moduleManager.TryGetExport(text, out ExportedFunction export))
				{
					Console.Error.WriteLine($"[LOADER][ERROR] Stall import-stub: rip=0x{num:X16} nid={text} -> {export.LibraryName}:{export.Name}");
				}
				else
				{
					Console.Error.WriteLine($"[LOADER][ERROR] Stall import-stub: rip=0x{num:X16} nid={text}");
				}
				break;
			}
			Span<byte> destination = stackalloc byte[16];
			if (cpuContext.Memory.TryRead(cpuContext.Rip, destination))
			{
				Console.Error.WriteLine($"[LOADER][ERROR] Stall bytes @rip: {BitConverter.ToString(destination.ToArray()).Replace("-", " ")}");
			}
			else if (cpuContext.Memory.TryRead(num, destination))
			{
				Console.Error.WriteLine($"[LOADER][ERROR] Stall bytes @rip_align: {BitConverter.ToString(destination.ToArray()).Replace("-", " ")}");
			}
			if (rsp != 0 && cpuContext.TryReadUInt64(rsp, out var value) && cpuContext.TryReadUInt64(rsp + 8, out var value2))
			{
				Console.Error.WriteLine($"[LOADER][ERROR] Stall stack: [rsp]=0x{value:X16} [rsp+8]=0x{value2:X16}");
			}

			var threads = SnapshotGuestThreads();
			if (threads.Length != 0)
			{
				var logged = 0;
				foreach (var thread in threads)
				{
					var hostThreadId = Volatile.Read(ref thread.HostThreadId);
					var hostContextText = string.Empty;
					if (TryCaptureHostThreadContext(hostThreadId, out var hostContext))
					{
						hostContextText =
							$" host_tid={hostThreadId} host_rip=0x{hostContext.Rip:X16} host_rsp=0x{hostContext.Rsp:X16} " +
							$"host_rbp=0x{hostContext.Rbp:X16} host_rax=0x{hostContext.Rax:X16} host_rbx=0x{hostContext.Rbx:X16} " +
							$"host_rcx=0x{hostContext.Rcx:X16} host_rdx=0x{hostContext.Rdx:X16}";
					}
					else if (hostThreadId != 0)
					{
						hostContextText = $" host_tid={hostThreadId} host_ctx=unavailable";
					}

					Console.Error.WriteLine(
						$"[LOADER][ERROR] Stall guest-thread: handle=0x{thread.ThreadHandle:X16} name='{thread.Name}' " +
						$"state={thread.State} imports={Interlocked.Read(ref thread.ImportCount)} " +
						$"nid={Volatile.Read(ref thread.LastImportNid) ?? "none"} ret=0x{Volatile.Read(ref thread.LastReturnRip):X16} " +
						$"block={thread.BlockReason ?? "none"}{hostContextText}");
					logged++;
					if (logged >= 48 && threads.Length > logged)
					{
						Console.Error.WriteLine($"[LOADER][ERROR] Stall guest-thread: ... {threads.Length - logged} more");
						break;
					}
				}
			}
		}
		catch
		{
		}
	}

	private bool TryCaptureHostThreadContext(int hostThreadId, out HostThreadContextSnapshot snapshot)
	{
		snapshot = default;
		if (hostThreadId == 0 || unchecked((uint)hostThreadId) == _hostThreading.CurrentThreadId)
		{
			return false;
		}

		if (!_hostThreading.TryCaptureThreadRegisters(unchecked((uint)hostThreadId), out var registers))
		{
			return false;
		}

		snapshot = new HostThreadContextSnapshot(
			true,
			registers.Rip,
			registers.Rsp,
			registers.Rbp,
			registers.Rax,
			registers.Rbx,
			registers.Rcx,
			registers.Rdx);
		return true;
	}

	public unsafe void Dispose()
	{
		if (Volatile.Read(ref _disposeStarted) != 0)
		{
			return;
		}

		if (!RequestGuestThreadTeardown(2000))
		{
			// A guest worker is still executing native code; freeing the trampolines,
			// exception-handler stubs, or GC handles under it turns process exit into
			// an execute-AV / CLR fatal. Leak them — the process is going away anyway.
			Console.Error.WriteLine(
				"[LOADER][WARN] Skipping executable stub teardown: guest worker threads are still running.");
			return;
		}
		if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
		{
			return;
		}

		// Native guest workers park idle once every guest thread has unwound; stop
		// them before any executable stub or TLS index they reference is freed.
		DisposeNativeGuestExecutors();
		ClearGuestThreads();

		if (ReferenceEquals(_posixSignalBackend, this))
		{
			// The signal handlers stay installed (they chain to the previous
			// action when no backend is active), but must stop dispatching
			// into a disposed backend.
			_posixSignalBackend = null;
		}
		ClearImportHandlerTrampolines();
		_importEntries = Array.Empty<ImportStubEntry>();
		_runtimeSymbolsByName.Clear();
		ResetLazyDlsymStubState();
		_importNidHashCache.Clear();
		StopStallWatchdog();
		if (_exceptionHandler != 0)
		{
			_faultHandling.RemoveHandler(_exceptionHandler);
			_exceptionHandler = 0;
		}
		if (_rawExceptionHandler != 0)
		{
			_faultHandling.RemoveHandler(_rawExceptionHandler);
			_rawExceptionHandler = 0;
		}
		if (_rawExceptionHandlerStub != 0)
		{
			_faultHandling.FreeThunk(_rawExceptionHandlerStub);
			_rawExceptionHandlerStub = 0;
		}
		if (_exceptionHandlerStub != 0)
		{
			_faultHandling.FreeThunk(_exceptionHandlerStub);
			_exceptionHandlerStub = 0;
		}
		if (_unhandledFilterStub != 0)
		{
			_faultHandling.SetUnhandledFilter(0);
			_faultHandling.FreeThunk(_unhandledFilterStub);
			_unhandledFilterStub = 0;
		}
		if (_handlerHandle.IsAllocated)
		{
			_handlerHandle.Free();
		}
		if (_unhandledFilterHandle.IsAllocated)
		{
			_unhandledFilterHandle.Free();
		}
		if (_selfHandle.IsAllocated)
		{
			_selfHandle.Free();
			_selfHandlePtr = 0;
		}
		if (_ownedTlsBaseAddress != 0)
		{
			_hostMemory.Free((ulong)_ownedTlsBaseAddress);
			_ownedTlsBaseAddress = 0;
		}
		_tlsBaseAddress = 0;
		_ownsTlsBaseAddress = false;
		if (_tlsModuleBases.Count > 0)
		{
			foreach (var (_, num3) in _tlsModuleBases)
			{
				if (num3 != 0)
				{
					_hostMemory.Free((ulong)num3);
				}
			}
			_tlsModuleBases.Clear();
		}
		foreach (var tlsHandlerAddress in _tlsHandlerAllocations)
		{
			if (tlsHandlerAddress != 0)
			{
				_hostMemory.Free((ulong)tlsHandlerAddress);
			}
		}
		_tlsHandlerAllocations.Clear();
		_tlsHandlerAddress = 0;
		if (_hostRspSlotStorage != 0)
		{
			_hostMemory.Free((ulong)_hostRspSlotStorage);
			_hostRspSlotStorage = 0;
		}
		if (_guestTlsBaseTlsIndex != uint.MaxValue)
		{
			_hostThreading.FreeTlsSlot(_guestTlsBaseTlsIndex);
			_guestTlsBaseTlsIndex = uint.MaxValue;
		}
		if (_hostRspSlotTlsIndex != uint.MaxValue)
		{
			_hostThreading.FreeTlsSlot(_hostRspSlotTlsIndex);
			_hostRspSlotTlsIndex = uint.MaxValue;
		}
		if (_unresolvedReturnStub != 0)
		{
			_hostMemory.Free((ulong)_unresolvedReturnStub);
			_unresolvedReturnStub = 0;
		}
		if (_guestReturnStub != 0)
		{
			_hostMemory.Free((ulong)_guestReturnStub);
			_guestReturnStub = 0;
		}
		if (_guestContextTransferStub != 0)
		{
			_hostMemory.Free((ulong)_guestContextTransferStub);
			_guestContextTransferStub = 0;
		}
		foreach (var frame in _guestContextTransferFrames.Values)
		{
			if (frame != 0)
			{
				NativeMemory.Free((void*)frame);
			}
		}
		_guestContextTransferFrames.Dispose();
		if (_lowIndexedTableScratch != 0)
		{
			_hostMemory.Free((ulong)_lowIndexedTableScratch);
			_lowIndexedTableScratch = 0;
		}
		if (_stackGuardCompareScratch != 0)
		{
			_hostMemory.Free((ulong)_stackGuardCompareScratch);
			_stackGuardCompareScratch = 0;
		}
		if (_nullObjectStoreScratch != 0)
		{
			_hostMemory.Free((ulong)_nullObjectStoreScratch);
			_nullObjectStoreScratch = 0;
		}
		Volatile.Write(ref _globalUnresolvedReturnStub, 0uL);
	}
}
