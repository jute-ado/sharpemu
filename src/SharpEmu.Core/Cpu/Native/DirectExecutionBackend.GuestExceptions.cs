// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
    private sealed class ExternalGuestThreadState
    {
        public required CpuContext Context { get; set; }
        public ulong ExceptionStackBase { get; set; }
    }

    private readonly record struct PendingGuestException(
        ulong Handler,
        int ExceptionType,
        ulong ExceptionStackBase);

    [ThreadStatic]
    private static ulong _currentExternalGuestThreadHandle;

    private readonly Dictionary<ulong, ExternalGuestThreadState>
        _externalGuestThreads = [];
    private readonly Dictionary<ulong, PendingGuestException>
        _pendingGuestExceptions = [];
    private readonly HashSet<ulong> _activeGuestExceptionDeliveries = [];

    public void RegisterGuestThreadContext(
        ulong threadHandle,
        CpuContext context)
    {
        if (threadHandle == 0)
        {
            return;
        }

        using (LockGate("RegisterGuestThreadContext"))
        {
            _currentExternalGuestThreadHandle = threadHandle;
            if (_guestThreads.ContainsKey(threadHandle))
            {
                return;
            }

            if (_externalGuestThreads.TryGetValue(
                    threadHandle,
                    out var existing))
            {
                existing.Context = context;
                return;
            }

            _externalGuestThreads[threadHandle] =
                new ExternalGuestThreadState
                {
                    Context = context,
                };
        }
    }

    public bool TryRaiseGuestException(
        CpuContext callerContext,
        ulong threadHandle,
        ulong handler,
        int exceptionType,
        out string? error)
    {
        error = null;
        if (threadHandle == 0 ||
            handler < 65536 ||
            exceptionType is < 0 or >= 128)
        {
            error = "invalid guest exception delivery request";
            return false;
        }

        using (LockGate("TryRaiseGuestException"))
        {
            CpuContext targetContext;
            ulong exceptionStackBase;
            string targetMode;
            if (_guestThreads.TryGetValue(threadHandle, out var target))
            {
                if (target.State is
                    GuestThreadRunState.Exited or
                    GuestThreadRunState.Faulted)
                {
                    error =
                        $"guest exception target 0x{threadHandle:X16} " +
                        "is no longer running";
                    return false;
                }

                targetContext = target.Context;
                var targetExceptionStack = target.ExceptionStackBase;
                if (!TryEnsureExceptionStack(
                        targetContext,
                        ref targetExceptionStack,
                        out error))
                {
                    return false;
                }

                target.ExceptionStackBase = targetExceptionStack;
                exceptionStackBase = target.ExceptionStackBase;
                targetMode = "scheduled";
            }
            else if (_externalGuestThreads.TryGetValue(
                         threadHandle,
                         out var external))
            {
                targetContext = external.Context;
                var externalExceptionStack = external.ExceptionStackBase;
                if (!TryEnsureExceptionStack(
                        targetContext,
                        ref externalExceptionStack,
                        out error))
                {
                    return false;
                }

                external.ExceptionStackBase = externalExceptionStack;
                exceptionStackBase = external.ExceptionStackBase;
                targetMode = "external";
            }
            else
            {
                error =
                    $"unknown guest exception target 0x{threadHandle:X16}";
                return false;
            }

            // Coalesce repeated raises while one request is already queued.
            // If a handler is active, retain one follow-up request for the
            // next target-thread safe point.
            if (!_pendingGuestExceptions.ContainsKey(threadHandle))
            {
                _pendingGuestExceptions[threadHandle] =
                    new PendingGuestException(
                        handler,
                        exceptionType,
                        exceptionStackBase);
                GuestThreadBlocking.RequestInterrupt(threadHandle);
            }

            if (ShouldLogGuestExceptions())
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] guest_exception.queued " +
                    $"target=0x{threadHandle:X16} " +
                    $"type=0x{exceptionType:X2} mode={targetMode} " +
                    $"active={_activeGuestExceptionDeliveries.Contains(threadHandle)}");
            }

            return true;
        }
    }

    private bool TryEnsureExceptionStack(
        CpuContext context,
        ref ulong exceptionStackBase,
        out string? error)
    {
        if (exceptionStackBase != 0)
        {
            error = null;
            return true;
        }

        if (!TryGetVirtualMemory(context, out var virtualMemory))
        {
            error = "guest exception target has no virtual memory";
            return false;
        }

        return TryMapGuestThreadRegion(
            virtualMemory,
            GuestThreadStackBaseAddress,
            GuestThreadStackSize,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write,
            out exceptionStackBase,
            out error);
    }

    private void DeliverPendingGuestExceptionInPlaceForCurrentThread()
    {
        var threadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        CpuContext? context = null;
        using (LockGate("ResolveInPlaceGuestException"))
        {
            if (threadHandle != 0 &&
                _guestThreads.TryGetValue(threadHandle, out var thread))
            {
                context = thread.Context;
            }
            else if (_currentExternalGuestThreadHandle != 0 &&
                     _externalGuestThreads.TryGetValue(
                         _currentExternalGuestThreadHandle,
                         out var external))
            {
                context = external.Context;
            }
        }

        context ??= ActiveCpuContext;
        if (context is not null)
        {
            DeliverPendingGuestExceptionAtSafePoint(context, default);
        }
    }

    private void DeliverPendingGuestExceptionAtSafePoint(
        CpuContext currentContext,
        GuestCpuContinuation interruptedContinuation)
    {
        var threadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        if (threadHandle == 0)
        {
            threadHandle = _currentExternalGuestThreadHandle;
        }

        PendingGuestException pending;
        using (LockGate("TakePendingGuestException"))
        {
            if (threadHandle == 0 ||
                !_pendingGuestExceptions.Remove(
                    threadHandle,
                    out pending))
            {
                return;
            }

            _activeGuestExceptionDeliveries.Add(threadHandle);
        }
        GuestThreadBlocking.AcknowledgeInterrupt(threadHandle);

        const ulong exceptionContextSize = 0x500;
        const ulong callbackStackOffset = 0x1000;
        const ulong callbackStackSize = 0xF000;
        var exceptionContextAddress =
            pending.ExceptionStackBase + 0x100;
        try
        {
            if (!TryWriteGuestExceptionContext(
                    currentContext,
                    exceptionContextAddress,
                    interruptedContinuation,
                    exceptionContextSize))
            {
                Console.Error.WriteLine(
                    "[LOADER][ERROR] Guest exception context write failed: " +
                    $"target=0x{threadHandle:X16} " +
                    $"type=0x{pending.ExceptionType:X2}");
                return;
            }

            if (ShouldLogGuestExceptions())
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] guest_exception.safe_point_enter " +
                    $"target=0x{threadHandle:X16} " +
                    $"type=0x{pending.ExceptionType:X2} " +
                    $"rip=0x{interruptedContinuation.Rip:X16}");
            }

            if (!TryCallGuestFunction(
                    currentContext,
                    pending.Handler,
                    unchecked((ulong)pending.ExceptionType),
                    exceptionContextAddress,
                    pending.ExceptionStackBase + callbackStackOffset,
                    callbackStackSize,
                    $"kernel exception 0x{pending.ExceptionType:X2}",
                    out var callbackError))
            {
                Console.Error.WriteLine(
                    "[LOADER][ERROR] Guest exception delivery failed: " +
                    $"target=0x{threadHandle:X16} " +
                    $"type=0x{pending.ExceptionType:X2} " +
                    $"error={callbackError ?? "unknown"}");
            }
        }
        finally
        {
            using (LockGate("CompleteGuestException"))
            {
                _activeGuestExceptionDeliveries.Remove(threadHandle);
            }
        }
    }

    internal static bool TryWriteGuestExceptionContext(
        CpuContext context,
        ulong address,
        GuestCpuContinuation continuation,
        ulong size)
    {
        var bytes = new byte[checked((int)size)];
        void Write64(int offset, ulong value) =>
            BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(offset, sizeof(ulong)),
                value);

        var hasContinuation =
            continuation.Rip >= 65536 && continuation.Rsp != 0;
        const int mcontext = 0x40;
        Write64(
            mcontext + 0x08,
            hasContinuation
                ? continuation.Rdi
                : context[CpuRegister.Rdi]);
        Write64(
            mcontext + 0x10,
            hasContinuation
                ? continuation.Rsi
                : context[CpuRegister.Rsi]);
        Write64(
            mcontext + 0x18,
            hasContinuation
                ? continuation.Rdx
                : context[CpuRegister.Rdx]);
        Write64(
            mcontext + 0x20,
            hasContinuation
                ? continuation.Rcx
                : context[CpuRegister.Rcx]);
        Write64(
            mcontext + 0x28,
            hasContinuation
                ? continuation.R8
                : context[CpuRegister.R8]);
        Write64(
            mcontext + 0x30,
            hasContinuation
                ? continuation.R9
                : context[CpuRegister.R9]);
        Write64(
            mcontext + 0x38,
            hasContinuation
                ? continuation.Rax
                : context[CpuRegister.Rax]);
        Write64(
            mcontext + 0x40,
            hasContinuation
                ? continuation.Rbx
                : context[CpuRegister.Rbx]);
        Write64(
            mcontext + 0x48,
            hasContinuation
                ? continuation.Rbp
                : context[CpuRegister.Rbp]);
        Write64(
            mcontext + 0x50,
            hasContinuation
                ? continuation.R10
                : context[CpuRegister.R10]);
        Write64(
            mcontext + 0x58,
            hasContinuation
                ? continuation.R11
                : context[CpuRegister.R11]);
        Write64(
            mcontext + 0x60,
            hasContinuation
                ? continuation.R12
                : context[CpuRegister.R12]);
        Write64(
            mcontext + 0x68,
            hasContinuation
                ? continuation.R13
                : context[CpuRegister.R13]);
        Write64(
            mcontext + 0x70,
            hasContinuation
                ? continuation.R14
                : context[CpuRegister.R14]);
        Write64(
            mcontext + 0x78,
            hasContinuation
                ? continuation.R15
                : context[CpuRegister.R15]);
        Write64(
            mcontext + 0xA0,
            hasContinuation ? continuation.Rip : context.Rip);
        Write64(
            mcontext + 0xB0,
            hasContinuation ? continuation.Rflags : context.Rflags);
        Write64(
            mcontext + 0xB8,
            hasContinuation
                ? continuation.Rsp
                : context[CpuRegister.Rsp]);
        Write64(mcontext + 0xC8, 0x480);
        Write64(
            mcontext + 0x440,
            hasContinuation ? continuation.FsBase : context.FsBase);
        Write64(
            mcontext + 0x448,
            hasContinuation ? continuation.GsBase : context.GsBase);
        return context.Memory.TryWrite(address, bytes);
    }

    private void ClearGuestExceptionState()
    {
        _externalGuestThreads.Clear();
        _pendingGuestExceptions.Clear();
        _activeGuestExceptionDeliveries.Clear();
        _currentExternalGuestThreadHandle = 0;
    }

    private static bool ShouldLogGuestExceptions() =>
        string.Equals(
            Environment.GetEnvironmentVariable(
                "SHARPEMU_LOG_GUEST_EXCEPTIONS"),
            "1",
            StringComparison.Ordinal);
}
