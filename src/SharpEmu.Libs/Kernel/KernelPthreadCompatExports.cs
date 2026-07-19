// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelPthreadCompatExports
{
    private const int MutexTypeErrorCheck = 1;
    private const int MutexTypeRecursive = 2;
    private const int MutexTypeNormal = 3;
    private const int MutexTypeAdaptiveNp = 4;
    private const ulong StaticAdaptiveMutexInitializer = 1;
    private const int MutexObjectSize = 0x100;
    private const int MutexAttrObjectSize = 0x40;
    private const int CondObjectSize = 0x100;
    private const int PthreadOnceUninitialized = 0;
    private const int PthreadOnceInProgress = 1;
    private const int PthreadOnceDone = 2;

    private static readonly object _stateGate = new();
    private static readonly ConcurrentDictionary<ulong, PthreadMutexState> _mutexStates = new();
    private static readonly Dictionary<ulong, PthreadMutexAttrState> _mutexAttrStates = new();
    private static readonly Dictionary<ulong, PthreadCondState> _condStates = new();
    private static readonly Dictionary<ulong, object> _onceGates = new();
    private static readonly HashSet<ulong> _condAttrStates = new();
    private static readonly bool _tracePthreads =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREADS"), "1", StringComparison.Ordinal);
    private static readonly bool _tracePthreadConds =
        _tracePthreads ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREAD_CONDS"), "1", StringComparison.Ordinal);
    private static readonly HashSet<ulong>? _tracePthreadMutexFilter = ParseTraceAddressFilter(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREAD_MUTEX_FILTER"));
    private static readonly ConcurrentDictionary<ulong, long> _posixTimedWaitCounts = new();
    private static readonly ConcurrentDictionary<ulong, byte> _reportedContendedMutexes = new();

    private sealed class PthreadMutexState
    {
        public ulong OwnerThreadId { get; set; }
        public int RecursionCount { get; set; }
        public int Type { get; set; } = MutexTypeErrorCheck;
        public int Protocol { get; set; }
        public bool Destroyed { get; set; }
        public int ConditionWaiterCount { get; set; }
        public int WaiterCount { get; set; }
        public long NextWaitTicket { get; set; }
        public LinkedList<long> WaitTickets { get; } = new();
    }

    private sealed class PthreadCondState
    {
        public object SyncRoot { get; } = new();
        public bool Destroyed { get; set; }
        public ulong SignalEpoch { get; set; }
        public int Waiters { get; set; }
        public int SignalsPending { get; set; }

        // scePthread compatibility can retain an early signal for titles that signal
        // before waiting. POSIX exports deliberately never add to this latch.
        public int CompatibilitySignalCredits { get; set; }

        public bool TryConsumeCompatibilitySignal()
        {
            if (CompatibilitySignalCredits <= 0)
            {
                return false;
            }

            CompatibilitySignalCredits--;
            return true;
        }
    }

    private readonly record struct PthreadMutexAttrState(int Type, int Protocol);

    [SysAbiExport(
        Nid = "aI+OeCz8xrQ",
        ExportName = "scePthreadSelf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSelf(CpuContext ctx)
    {
        var currentThreadHandle = KernelPthreadState.GetCurrentThreadHandle();
        GuestThreadExecution.Scheduler?.RegisterGuestThreadContext(
            currentThreadHandle,
            ctx);
        ctx[CpuRegister.Rax] = currentThreadHandle;
        TracePthreadSelf(ctx, currentThreadHandle);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EotR8a3ASf4",
        ExportName = "pthread_self",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadSelf(CpuContext ctx) => PthreadSelf(ctx);

    [SysAbiExport(
        Nid = "3PtV6p3QNX4",
        ExportName = "scePthreadEqual",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadEqual(CpuContext ctx)
    {
        var left = ctx[CpuRegister.Rdi];
        var right = ctx[CpuRegister.Rsi];
        ctx[CpuRegister.Rax] = left == right ? 1UL : 0UL;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "7Xl257M4VNI",
        ExportName = "pthread_equal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixPthreadEqual(CpuContext ctx) => PthreadEqual(ctx);

    [SysAbiExport(
        Nid = "T72hz6ffq08",
        ExportName = "scePthreadYield",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadYield(CpuContext ctx)
    {
        GuestThreadExecution.Scheduler?.Pump(ctx, "scePthreadYield");
        Thread.Yield();
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "B5GmVDKwpn0",
        ExportName = "pthread_yield",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadYield(CpuContext ctx) => PthreadYield(ctx);

    [SysAbiExport(
        Nid = "GBUY7ywdULE",
        ExportName = "scePthreadRename",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadRename(CpuContext ctx)
    {
        if (_tracePthreads)
        {
            var nameAddress = ctx[CpuRegister.Rsi];
            var name = nameAddress != 0 &&
                KernelMemoryCompatExports.TryReadNullTerminatedUtf8(ctx, nameAddress, 64, out var value)
                    ? value
                    : "<unreadable>";
            Console.Error.WriteLine(
                $"[LOADER][TRACE] pthread.rename thread=0x{ctx[CpuRegister.Rdi]:X16} name=\"{name}\"");
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EI-5-jlq2dE",
        ExportName = "scePthreadGetthreadid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadGetthreadid(CpuContext ctx) => PthreadGetthreadidCore(ctx);

    [SysAbiExport(
        Nid = "3eqs37G74-s",
        ExportName = "pthread_getthreadid_np",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadGetthreadidNp(CpuContext ctx) => PthreadGetthreadidCore(ctx);

    [SysAbiExport(
        Nid = "cmo1RIYva9o",
        ExportName = "scePthreadMutexInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexInit(CpuContext ctx) => PthreadMutexInitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "2Of0f+3mhhE",
        ExportName = "scePthreadMutexDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexDestroy(CpuContext ctx) => PthreadMutexDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "9UK1vLZQft4",
        ExportName = "scePthreadMutexLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexLock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: false);

    [SysAbiExport(
        Nid = "upoVrzMHFeE",
        ExportName = "scePthreadMutexTrylock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexTrylock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: true);

    [SysAbiExport(
        Nid = "tn3VlD0hG60",
        ExportName = "scePthreadMutexUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexUnlock(CpuContext ctx) => PthreadMutexUnlockCore(ctx, ctx[CpuRegister.Rdi], requireOwner: true);

    [SysAbiExport(
        Nid = "ttHNfU+qDBU",
        ExportName = "pthread_mutex_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexInit(CpuContext ctx) => PthreadMutexInitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "ltCfaGr2JGE",
        ExportName = "pthread_mutex_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexDestroy(CpuContext ctx) => PthreadMutexDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "7H0iTOciTLo",
        ExportName = "pthread_mutex_lock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexLock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: false);

    [SysAbiExport(
        Nid = "K-jXhbt2gn4",
        ExportName = "pthread_mutex_trylock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexTrylock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: true);

    [SysAbiExport(
        Nid = "2Z+PpY6CaJg",
        ExportName = "pthread_mutex_unlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexUnlock(CpuContext ctx) => PthreadMutexUnlockCore(ctx, ctx[CpuRegister.Rdi], requireOwner: true);

    private static int PthreadGetthreadidCore(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = KernelPthreadState.GetCurrentThreadUniqueId();
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "F8bUHwAG284",
        ExportName = "scePthreadMutexattrInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrInit(CpuContext ctx) => PthreadMutexattrInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "smWEktiyyG0",
        ExportName = "scePthreadMutexattrDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrDestroy(CpuContext ctx) => PthreadMutexattrDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "iMp8QpE+XO4",
        ExportName = "scePthreadMutexattrSettype",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrSettype(CpuContext ctx) => PthreadMutexattrSettypeCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "1FGvU0i9saQ",
        ExportName = "scePthreadMutexattrSetprotocol",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrSetprotocol(CpuContext ctx) => PthreadMutexattrSetprotocolCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "dQHWEsJtoE4",
        ExportName = "pthread_mutexattr_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrInit(CpuContext ctx) => PthreadMutexattrInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "HF7lK46xzjY",
        ExportName = "pthread_mutexattr_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrDestroy(CpuContext ctx) => PthreadMutexattrDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "mDmgMOGVUqg",
        ExportName = "pthread_mutexattr_settype",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrSettype(CpuContext ctx) => PthreadMutexattrSettypeCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "5txKfcMUAok",
        ExportName = "pthread_mutexattr_setprotocol",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrSetprotocol(CpuContext ctx) => PthreadMutexattrSetprotocolCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "2Tb92quprl0",
        ExportName = "scePthreadCondInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondInit(CpuContext ctx) => PthreadCondInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "0TyVk4MSLt0",
        ExportName = "pthread_cond_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondInit(CpuContext ctx) => PthreadCondInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "g+PZd2hiacg",
        ExportName = "scePthreadCondDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondDestroy(CpuContext ctx) => PthreadCondDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "WKAXJ4XBPQ4",
        ExportName = "scePthreadCondWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondWait(CpuContext ctx) => PthreadCondWaitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], timed: false);

    [SysAbiExport(
        Nid = "BmMjYxmew1w",
        ExportName = "scePthreadCondTimedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondTimedwait(CpuContext ctx) => PthreadCondWaitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], timed: true, timeoutUsec: unchecked((uint)ctx[CpuRegister.Rdx]));

    [SysAbiExport(
        Nid = "27bAgiJmOh0",
        ExportName = "pthread_cond_timedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondTimedwait(CpuContext ctx)
    {
        var absoluteTimeAddress = ctx[CpuRegister.Rdx];
        if (absoluteTimeAddress == 0)
        {
            return ctx.SetReturn(TranslatePthreadResult(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                posixResult: true));
        }

        if (!ctx.TryReadUInt64(absoluteTimeAddress, out var secondsRaw) ||
            !ctx.TryReadUInt64(absoluteTimeAddress + sizeof(ulong), out var nanosecondsRaw))
        {
            return ctx.SetReturn(TranslatePthreadResult(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                posixResult: true));
        }

        var deadlineSeconds = unchecked((long)secondsRaw);
        var deadlineNanoseconds = unchecked((long)nanosecondsRaw);
        if (deadlineNanoseconds is < 0 or >= 1_000_000_000)
        {
            return ctx.SetReturn(TranslatePthreadResult(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                posixResult: true));
        }

        long nowSeconds;
        long nowNanoseconds;
        if (deadlineSeconds >= 1_000_000_000)
        {
            var now = DateTimeOffset.UtcNow;
            nowSeconds = now.ToUnixTimeSeconds();
            nowNanoseconds = (now.Ticks % TimeSpan.TicksPerSecond) * 100;
        }
        else
        {
            // Engine condition variables use monotonic deadlines.
            KernelRuntimeCompatExports.GetProcessMonotonicTime(out nowSeconds, out nowNanoseconds);
        }

        var remainingNanoseconds =
            ((Int128)deadlineSeconds * 1_000_000_000 + deadlineNanoseconds) -
            ((Int128)nowSeconds * 1_000_000_000 + nowNanoseconds);
        var timeoutUsec = remainingNanoseconds <= 0
            ? 0u
            : (uint)Int128.Min(uint.MaxValue, (remainingNanoseconds + 999) / 1_000);

        TracePosixTimedWait(
            ctx,
            deadlineSeconds,
            deadlineNanoseconds,
            nowSeconds,
            nowNanoseconds,
            remainingNanoseconds,
            timeoutUsec);

        return PthreadCondWaitCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            timed: true,
            timeoutUsec,
            posixResult: true);
    }

    private static void TracePosixTimedWait(
        CpuContext ctx,
        long deadlineSeconds,
        long deadlineNanoseconds,
        long nowSeconds,
        long nowNanoseconds,
        Int128 remainingNanoseconds,
        uint timeoutUsec)
    {
        var condAddress = ctx[CpuRegister.Rdi];
        var count = _posixTimedWaitCounts.AddOrUpdate(condAddress, 1, static (_, current) => current + 1);
        if (count != 1 && count % 1_000_000 != 0)
        {
            return;
        }

        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var returnRip);
        Console.Error.WriteLine(
            $"[LOADER][DIAG] pthread_cond_timedwait#{count}: thread=0x{KernelPthreadState.GetCurrentThreadHandle():X16} " +
            $"cond=0x{condAddress:X16} mutex=0x{ctx[CpuRegister.Rsi]:X16} abstime=0x{ctx[CpuRegister.Rdx]:X16} " +
            $"deadline={deadlineSeconds}.{deadlineNanoseconds:D9} now={nowSeconds}.{nowNanoseconds:D9} " +
            $"remaining_ns={remainingNanoseconds} timeout_us={timeoutUsec} ret=0x{returnRip:X16}");

        if (GuestThreadExecution.Scheduler is not { } scheduler)
        {
            return;
        }

        foreach (var snapshot in scheduler.SnapshotThreads())
        {
            Console.Error.WriteLine(
                $"[LOADER][DIAG] guest_thread handle=0x{snapshot.ThreadHandle:X16} name='{snapshot.Name}' " +
                $"state={snapshot.State} imports={snapshot.ImportCount} nid={snapshot.LastImportNid ?? "none"} " +
                $"ret=0x{snapshot.LastReturnRip:X16} block={snapshot.BlockReason ?? "none"}");
        }
    }

    [SysAbiExport(
        Nid = "kDh-NfxgMtE",
        ExportName = "scePthreadCondSignal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondSignal(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: false, latchWhenNoWaiters: true);

    [SysAbiExport(
        Nid = "JGgj7Uvrl+A",
        ExportName = "scePthreadCondBroadcast",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondBroadcast(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: true, latchWhenNoWaiters: true);

    [SysAbiExport(
        Nid = "Op8TBGY5KHg",
        ExportName = "pthread_cond_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondWait(CpuContext ctx) => PthreadCondWaitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], timed: false);

    [SysAbiExport(
        Nid = "mkx2fVhNMsg",
        ExportName = "pthread_cond_broadcast",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondBroadcast(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: true, latchWhenNoWaiters: false);

    [SysAbiExport(
        Nid = "2MOy+rUfuhQ",
        ExportName = "pthread_cond_signal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondSignal(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: false, latchWhenNoWaiters: false);

    [SysAbiExport(
        Nid = "m5-2bsNfv7s",
        ExportName = "scePthreadCondattrInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondattrInit(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            _condAttrStates.Add(attrAddress);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "waPcxYiR3WA",
        ExportName = "scePthreadCondattrDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondattrDestroy(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            _condAttrStates.Remove(attrAddress);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "14bOACANTBo",
        ExportName = "scePthreadOnce",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadOnce(CpuContext ctx)
    {
        var onceAddress = ctx[CpuRegister.Rdi];
        var initRoutine = ctx[CpuRegister.Rsi];
        if (onceAddress == 0 || initRoutine == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadInt32(onceAddress, out var onceValue))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (onceValue == PthreadOnceDone)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        var gate = GetPthreadOnceGate(onceAddress);
        var shouldCall = false;
        lock (gate)
        {
            if (!ctx.TryReadInt32(onceAddress, out onceValue))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            while (onceValue == PthreadOnceInProgress)
            {
                Monitor.Wait(gate, TimeSpan.FromMilliseconds(1));
                if (!ctx.TryReadInt32(onceAddress, out onceValue))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            if (onceValue != PthreadOnceDone)
            {
                if (!ctx.TryWriteInt32(onceAddress, PthreadOnceInProgress))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                shouldCall = true;
            }
        }

        if (shouldCall)
        {
            var scheduler = GuestThreadExecution.Scheduler;
            string? error = null;
            if (scheduler is null ||
                !scheduler.TryCallGuestFunction(ctx, initRoutine, 0, 0, 0, 0, "pthread_once", out error))
            {
                lock (gate)
                {
                    _ = ctx.TryWriteInt32(onceAddress, PthreadOnceUninitialized);
                    Monitor.PulseAll(gate);
                }

                TracePthreadOnce(onceAddress, initRoutine, "failed", error);
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN);
            }

            lock (gate)
            {
                if (!ctx.TryWriteInt32(onceAddress, PthreadOnceDone))
                {
                    _ = ctx.TryWriteInt32(onceAddress, PthreadOnceUninitialized);
                    Monitor.PulseAll(gate);
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                Monitor.PulseAll(gate);
            }
        }

        TracePthreadOnce(onceAddress, initRoutine, shouldCall ? "call" : "done", null);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int PthreadMutexInitCore(CpuContext ctx, ulong mutexAddress, ulong attrAddress)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var attr = ResolveMutexAttrState(ctx, attrAddress);
        var state = new PthreadMutexState
        {
            Type = attr.Type,
            Protocol = attr.Protocol,
        };

        if (!TryAllocateOpaqueObject(ctx, MutexObjectSize, out var handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }
        if (!InitializeMutexObject(ctx, handle, state))
        {
            FreeOpaqueObject(ctx, handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        _mutexStates[mutexAddress] = state;
        _mutexStates[handle] = state;

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, mutexAddress, handle))
        {
            _mutexStates.TryRemove(mutexAddress, out _);
            _mutexStates.TryRemove(handle, out _);

            FreeOpaqueObject(ctx, handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexDestroyCore(CpuContext ctx, ulong mutexAddress)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexHandle(ctx, mutexAddress);
        var allocatedAddress = resolvedAddress;
        if (KernelMemoryCompatExports.TryReadUInt64Compat(
                ctx,
                mutexAddress,
                out var pointedHandle) &&
            pointedHandle != 0 &&
            _mutexStates.ContainsKey(pointedHandle))
        {
            allocatedAddress = pointedHandle;
        }

        if (!_mutexStates.TryGetValue(resolvedAddress, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        lock (state)
        {
            if (state.Destroyed)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            if (state.OwnerThreadId != 0 ||
                state.RecursionCount != 0 ||
                state.ConditionWaiterCount != 0 ||
                state.WaiterCount != 0)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
            }

            state.Destroyed = true;
            _mutexStates.TryRemove(resolvedAddress, out _);
            if (resolvedAddress != mutexAddress || allocatedAddress != mutexAddress)
            {
                _mutexStates.TryRemove(mutexAddress, out _);
            }
            if (allocatedAddress != resolvedAddress)
            {
                _mutexStates.TryRemove(allocatedAddress, out _);
            }
        }

        // Remove any additional aliases that were registered for this state.
        foreach (var pair in _mutexStates)
        {
            if (ReferenceEquals(pair.Value, state))
            {
                _mutexStates.TryRemove(pair.Key, out _);
            }
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, mutexAddress, 0);
        FreeOpaqueObject(ctx, allocatedAddress);

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexLockCore(CpuContext ctx, ulong mutexAddress, bool tryOnly)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveMutexState(ctx, mutexAddress, createIfZero: true, out var resolvedAddress, out var state))
        {
            TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, null, KernelPthreadState.GetCurrentThreadHandle(), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        lock (state)
        {
            if (state.Destroyed)
            {
                TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            if (state.OwnerThreadId == currentThreadId)
            {
                if (state.Type == MutexTypeRecursive)
                {
                    state.RecursionCount++;
                    TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }

                if (state.Type is MutexTypeNormal or MutexTypeAdaptiveNp)
                {
                    if (tryOnly)
                    {
                        TracePthreadMutex(ctx, "trylock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
                    }

                    // Several Gen5 runtimes layer their own owner/count
                    // bookkeeping over these mutexes. Preserve compatibility
                    // recursion so that bookkeeping remains synchronized.
                    state.RecursionCount++;
                    TracePthreadMutex(ctx, "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }
                else
                {
                    var ownedResult = tryOnly
                        ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY
                        : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK;
                    TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, state, currentThreadId, ownedResult);
                    return ownedResult;
                }
            }

            if (state.OwnerThreadId == 0)
            {
                state.OwnerThreadId = currentThreadId;
                state.RecursionCount = 1;
                TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (tryOnly)
            {
                TracePthreadMutex(ctx, "trylock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
            }

            TracePthreadMutex(ctx, "lock-block", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            TraceContendedMutex(
                ctx,
                mutexAddress,
                resolvedAddress,
                state,
                currentThreadId);
            state.WaiterCount++;
            var waitTicket = state.NextWaitTicket++;
            var waitNode = state.WaitTickets.AddLast(waitTicket);
            GuestThreadBlocking.NoteBlocked(currentThreadId, "pthread_mutex_lock");
            try
            {
                while (state.OwnerThreadId != 0 ||
                       state.WaitTickets.First?.Value != waitTicket)
                {
                    if (state.Destroyed)
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
                    }

                    if (GuestThreadBlocking.ShutdownRequested)
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
                    }

                    GuestThreadBlocking.Checkpoint(currentThreadId, state);
                    _ = Monitor.Wait(state, GuestThreadBlocking.WaitSliceMilliseconds);
                }

                state.WaitTickets.RemoveFirst();
                state.OwnerThreadId = currentThreadId;
                state.RecursionCount = 1;
            }
            finally
            {
                if (waitNode.List is not null)
                {
                    state.WaitTickets.Remove(waitNode);
                    Monitor.PulseAll(state);
                }

                state.WaiterCount--;
                GuestThreadBlocking.NoteUnblocked(currentThreadId);
            }

            TracePthreadMutex(ctx, "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
    }

    private static void TraceContendedMutex(
        CpuContext ctx,
        ulong mutexAddress,
        ulong resolvedAddress,
        PthreadMutexState state,
        ulong currentThreadId)
    {
        if (!_reportedContendedMutexes.TryAdd(resolvedAddress, 0))
        {
            return;
        }

        ulong ownerThreadId;
        int recursionCount;
        int type;
        int waiterCount;
        lock (state)
        {
            ownerThreadId = state.OwnerThreadId;
            recursionCount = state.RecursionCount;
            type = state.Type;
            waiterCount = state.WaiterCount;
        }

        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var returnRip);
        Console.Error.WriteLine(
            $"[LOADER][DIAG] pthread_mutex_contended: waiter=0x{currentThreadId:X16} owner=0x{ownerThreadId:X16} " +
            $"mutex=0x{mutexAddress:X16} resolved=0x{resolvedAddress:X16} recursion={recursionCount} " +
            $"type={type} queued_waiters={waiterCount} ret=0x{returnRip:X16}");

        if (GuestThreadExecution.Scheduler is not { } scheduler)
        {
            return;
        }

        foreach (var snapshot in scheduler.SnapshotThreads())
        {
            if (snapshot.ThreadHandle != currentThreadId && snapshot.ThreadHandle != ownerThreadId)
            {
                continue;
            }

            Console.Error.WriteLine(
                $"[LOADER][DIAG] pthread_mutex_party handle=0x{snapshot.ThreadHandle:X16} name='{snapshot.Name}' " +
                $"state={snapshot.State} imports={snapshot.ImportCount} nid={snapshot.LastImportNid ?? "none"} " +
                $"ret=0x{snapshot.LastReturnRip:X16} block={snapshot.BlockReason ?? "none"}");
        }
    }

    private static int PthreadMutexUnlockCore(CpuContext ctx, ulong mutexAddress, bool requireOwner)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveMutexState(ctx, mutexAddress, createIfZero: true, out var resolvedAddress, out var state))
        {
            TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, null, KernelPthreadState.GetCurrentThreadHandle(), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        lock (state)
        {
            if (state.Destroyed)
            {
                TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            if (state.RecursionCount <= 0)
            {
                TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }

            if (requireOwner && state.OwnerThreadId != currentThreadId)
            {
                TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            }

            state.RecursionCount--;
            if (state.RecursionCount == 0)
            {
                state.OwnerThreadId = 0;
                if (state.WaiterCount != 0)
                {
                    Monitor.PulseAll(state);
                }
            }
        }

        TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexattrInitCore(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryAllocateOpaqueObject(ctx, MutexAttrObjectSize, out var handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var initialState = new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
        if (!WriteMutexAttrObject(ctx, handle, initialState))
        {
            FreeOpaqueObject(ctx, handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        lock (_stateGate)
        {
            _mutexAttrStates[attrAddress] = initialState;
            _mutexAttrStates[handle] = initialState;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, attrAddress, handle))
        {
            lock (_stateGate)
            {
                _mutexAttrStates.Remove(attrAddress);
                _mutexAttrStates.Remove(handle);
            }

            FreeOpaqueObject(ctx, handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexattrDestroyCore(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        lock (_stateGate)
        {
            _mutexAttrStates.Remove(resolvedAddress);
            if (resolvedAddress != attrAddress)
            {
                _mutexAttrStates.Remove(attrAddress);
            }
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, attrAddress, 0);
        FreeOpaqueObject(ctx, resolvedAddress);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexattrSettypeCore(CpuContext ctx, ulong attrAddress, int type)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        PthreadMutexAttrState updatedState;
        lock (_stateGate)
        {
            if (!_mutexAttrStates.TryGetValue(resolvedAddress, out var state))
            {
                state = new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
            }

            updatedState = state with { Type = NormalizeMutexType(type) };
            _mutexAttrStates[resolvedAddress] = updatedState;
            if (resolvedAddress != attrAddress)
            {
                _mutexAttrStates[attrAddress] = updatedState;
            }
        }

        return WriteMutexAttrObject(ctx, resolvedAddress, updatedState)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    private static int PthreadMutexattrSetprotocolCore(CpuContext ctx, ulong attrAddress, int protocol)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        PthreadMutexAttrState updatedState;
        lock (_stateGate)
        {
            if (!_mutexAttrStates.TryGetValue(resolvedAddress, out var state))
            {
                state = new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
            }

            updatedState = state with { Protocol = protocol };
            _mutexAttrStates[resolvedAddress] = updatedState;
            if (resolvedAddress != attrAddress)
            {
                _mutexAttrStates[attrAddress] = updatedState;
            }
        }

        return WriteMutexAttrObject(ctx, resolvedAddress, updatedState)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    private static ulong ResolveMutexHandle(CpuContext ctx, ulong mutexAddress)
    {
        if (mutexAddress == 0)
        {
            return 0;
        }

        if (_mutexStates.ContainsKey(mutexAddress))
        {
            return mutexAddress;
        }

        if (KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress, out var pointedHandle) && pointedHandle != 0)
        {
            if (_mutexStates.ContainsKey(pointedHandle))
            {
                return pointedHandle;
            }
        }

        return mutexAddress;
    }

    private static bool TryResolveMutexState(CpuContext ctx, ulong mutexAddress, bool createIfZero, out ulong resolvedAddress, [NotNullWhen(true)] out PthreadMutexState? state)
    {
        resolvedAddress = 0;
        state = null;
        if (mutexAddress == 0)
        {
            return false;
        }

        if (_mutexStates.TryGetValue(mutexAddress, out state))
        {
            resolvedAddress = mutexAddress;
            return true;
        }

        if (!KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress, out var pointedHandle))
        {
            return false;
        }

        if (pointedHandle == StaticAdaptiveMutexInitializer)
        {
            return CreateImplicitMutexState(ctx, mutexAddress, MutexTypeAdaptiveNp, out resolvedAddress, out state);
        }

        if (pointedHandle != 0)
        {
            if (_mutexStates.TryGetValue(pointedHandle, out state))
            {
                _mutexStates.TryAdd(mutexAddress, state);
                resolvedAddress = pointedHandle;
                return true;
            }

            resolvedAddress = pointedHandle;
            return false;
        }

        if (!createIfZero)
        {
            resolvedAddress = mutexAddress;
            return false;
        }

        return CreateImplicitMutexState(ctx, mutexAddress, MutexTypeErrorCheck, out resolvedAddress, out state);
    }

    private static ulong ResolveMutexAttrHandle(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return 0;
        }

        if (KernelMemoryCompatExports.TryReadUInt64Compat(ctx, attrAddress, out var pointedHandle) && pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_mutexAttrStates.ContainsKey(pointedHandle))
                {
                    return pointedHandle;
                }
            }
        }

        lock (_stateGate)
        {
            if (_mutexAttrStates.ContainsKey(attrAddress))
            {
                return attrAddress;
            }
        }

        return attrAddress;
    }

    private static PthreadMutexAttrState ResolveMutexAttrState(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return default;
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        lock (_stateGate)
        {
            return _mutexAttrStates.TryGetValue(resolvedAddress, out var state)
                ? state
                : new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
        }
    }

    private static ulong ResolveCondHandle(CpuContext ctx, ulong condAddress)
    {
        if (condAddress == 0)
        {
            return 0;
        }

        lock (_stateGate)
        {
            if (_condStates.ContainsKey(condAddress))
            {
                return condAddress;
            }
        }

        if (KernelMemoryCompatExports.TryReadUInt64Compat(ctx, condAddress, out var pointedHandle) && pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_condStates.ContainsKey(pointedHandle))
                {
                    return pointedHandle;
                }
            }
        }

        return condAddress;
    }

    private static bool TryResolveCondState(CpuContext? ctx, ulong condAddress, bool createIfZero, out ulong resolvedAddress, [NotNullWhen(true)] out PthreadCondState? state)
    {
        resolvedAddress = 0;
        state = null;
        if (condAddress == 0)
        {
            return false;
        }

        lock (_stateGate)
        {
            if (_condStates.TryGetValue(condAddress, out state))
            {
                resolvedAddress = condAddress;
                return true;
            }
        }

        if (ctx is null || !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, condAddress, out var pointedHandle))
        {
            return false;
        }

        if (pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_condStates.TryGetValue(pointedHandle, out state))
                {
                    _condStates[condAddress] = state;
                    resolvedAddress = pointedHandle;
                    return true;
                }
            }

            resolvedAddress = pointedHandle;
            return false;
        }

        if (!createIfZero)
        {
            resolvedAddress = condAddress;
            return false;
        }

        var createdState = new PthreadCondState();
        if (!TryAllocateOpaqueObject(ctx, CondObjectSize, out var handle))
        {
            return false;
        }

        lock (_stateGate)
        {
            if (_condStates.TryGetValue(condAddress, out var raced))
            {
                FreeOpaqueObject(ctx, handle);
                resolvedAddress = condAddress;
                state = raced;
                return true;
            }

            _condStates[condAddress] = createdState;
            _condStates[handle] = createdState;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, condAddress, handle))
        {
            lock (_stateGate)
            {
                _condStates.Remove(condAddress);
                _condStates.Remove(handle);
            }

            FreeOpaqueObject(ctx, handle);
            return false;
        }

        resolvedAddress = handle;
        state = createdState;
        return true;
    }

    private static bool TryAllocateOpaqueObject(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory((ulong)size, alignment: 0x10, out address))
        {
            return false;
        }

        Span<byte> initialData = stackalloc byte[size];
        initialData.Clear();
        if (ctx.Memory.TryWrite(address, initialData))
        {
            return true;
        }

        _ = allocator.TryFreeGuestMemory(address);
        address = 0;
        return false;
    }

    private static void FreeOpaqueObject(CpuContext ctx, ulong address)
    {
        if (address != 0 && ctx.Memory is IGuestMemoryAllocator allocator)
        {
            _ = allocator.TryFreeGuestMemory(address);
        }
    }

    private static bool InitializeMutexObject(CpuContext ctx, ulong address, PthreadMutexState state) =>
        ctx.TryWriteUInt32(address + 0x20, unchecked((uint)state.Type)) &&
        ctx.TryWriteUInt32(address + 0x3C, unchecked((uint)state.Protocol));

    private static bool WriteMutexAttrObject(CpuContext ctx, ulong address, PthreadMutexAttrState state) =>
        ctx.TryWriteUInt32(address, unchecked((uint)state.Type)) &&
        ctx.TryWriteUInt32(address + 4, unchecked((uint)state.Protocol));

    private static int PthreadCondInitCore(CpuContext ctx, ulong condAddress)
    {
        if (condAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryAllocateOpaqueObject(ctx, CondObjectSize, out var handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        lock (_stateGate)
        {
            var state = new PthreadCondState();
            _condStates[condAddress] = state;
            _condStates[handle] = state;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, condAddress, handle))
        {
            lock (_stateGate)
            {
                _condStates.Remove(condAddress);
                _condStates.Remove(handle);
            }

            FreeOpaqueObject(ctx, handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadCondDestroyCore(CpuContext ctx, ulong condAddress)
    {
        if (condAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveCondHandle(ctx, condAddress);
        var allocatedAddress = resolvedAddress;
        if (KernelMemoryCompatExports.TryReadUInt64Compat(
                ctx,
                condAddress,
                out var pointedHandle) &&
            pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_condStates.ContainsKey(pointedHandle))
                {
                    allocatedAddress = pointedHandle;
                }
            }
        }

        PthreadCondState state;
        lock (_stateGate)
        {
            if (!_condStates.TryGetValue(
                    resolvedAddress,
                    out var resolvedState))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }
            state = resolvedState;

            lock (state.SyncRoot)
            {
                if (state.Destroyed)
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
                }

                if (state.Waiters != 0)
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
                }

                state.Destroyed = true;
                _condStates.Remove(resolvedAddress);
                if (resolvedAddress != condAddress ||
                    allocatedAddress != condAddress)
                {
                    _condStates.Remove(condAddress);
                }
                if (allocatedAddress != resolvedAddress)
                {
                    _condStates.Remove(allocatedAddress);
                }
            }

            var aliases = new List<ulong>();
            foreach (var pair in _condStates)
            {
                if (ReferenceEquals(pair.Value, state))
                {
                    aliases.Add(pair.Key);
                }
            }
            foreach (var alias in aliases)
            {
                _condStates.Remove(alias);
            }
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, condAddress, 0);
        FreeOpaqueObject(ctx, allocatedAddress);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadCondWaitCore(
        CpuContext ctx,
        ulong condAddress,
        ulong mutexAddress,
        bool timed,
        uint timeoutUsec = 0,
        bool posixResult = false)
    {
        if (condAddress == 0 || mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveCondState(
                ctx,
                condAddress,
                createIfZero: true,
                out _,
                out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (!TryResolveMutexState(
                ctx,
                mutexAddress,
                createIfZero: true,
                out _,
                out var mutexState))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        var releasedRecursion = 1;
        lock (mutexState)
        {
            if (mutexState.Destroyed)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            if (mutexState.OwnerThreadId == 0 &&
                mutexState.RecursionCount == 0)
            {
                // libkernel can acquire an uncontended mutex entirely in guest
                // memory. Adopt ownership so the unlock/wait/relock cycle is balanced.
                mutexState.OwnerThreadId = currentThreadId;
                mutexState.RecursionCount = 1;
            }
            else if (mutexState.OwnerThreadId != currentThreadId ||
                     mutexState.RecursionCount <= 0)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            }

            releasedRecursion = mutexState.RecursionCount;
        }

        var signaled = false;
        var deadlineTimestamp = timed
            ? GuestThreadExecution.ComputeDeadlineTimestamp(
                GetCondWaitTimeout(timeoutUsec))
            : long.MaxValue;
        lock (state.SyncRoot)
        {
            if (state.Destroyed)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            signaled = state.TryConsumeCompatibilitySignal();
            if (!signaled)
            {
                state.Waiters++;
            }

            TracePthreadCond(
                signaled ? "wait-enter-pending" : "wait-enter",
                condAddress,
                mutexAddress,
                state,
                timed,
                (int)OrbisGen2Result.ORBIS_GEN2_OK);

            lock (mutexState)
            {
                if (mutexState.OwnerThreadId != currentThreadId ||
                    mutexState.RecursionCount != releasedRecursion)
                {
                    if (!signaled)
                    {
                        state.Waiters--;
                    }

                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
                }

                mutexState.ConditionWaiterCount++;
                mutexState.RecursionCount = 1;
            }

            var unlockResult = PthreadMutexUnlockCore(
                ctx,
                mutexAddress,
                requireOwner: true);
            if (unlockResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
            {
                lock (mutexState)
                {
                    mutexState.ConditionWaiterCount = Math.Max(
                        0,
                        mutexState.ConditionWaiterCount - 1);
                }

                if (!signaled)
                {
                    state.Waiters--;
                }

                return unlockResult;
            }

            if (!signaled)
            {
                GuestThreadBlocking.NoteBlocked(
                    currentThreadId,
                    timed ? "pthread_cond_timedwait" : "pthread_cond_wait");
                try
                {
                    while (state.SignalsPending == 0 &&
                           !state.Destroyed &&
                           !GuestThreadBlocking.ShutdownRequested)
                    {
                        var waitDuration = TimeSpan.FromMilliseconds(
                            GuestThreadBlocking.WaitSliceMilliseconds);
                        if (timed)
                        {
                            var remaining = GetRemainingTimeout(deadlineTimestamp);
                            if (remaining <= TimeSpan.Zero)
                            {
                                break;
                            }

                            if (remaining < waitDuration)
                            {
                                waitDuration = remaining;
                            }
                        }

                        GuestThreadBlocking.Checkpoint(currentThreadId, state.SyncRoot);
                        _ = Monitor.Wait(state.SyncRoot, waitDuration);
                    }
                }
                finally
                {
                    GuestThreadBlocking.NoteUnblocked(currentThreadId);
                }

                if (state.SignalsPending > 0)
                {
                    state.SignalsPending--;
                    signaled = true;
                }

                state.Waiters--;
                if (state.SignalsPending > state.Waiters)
                {
                    state.SignalsPending = state.Waiters;
                }
            }
        }

        var relockResult = PthreadMutexLockCore(
            ctx,
            mutexAddress,
            tryOnly: false);
        lock (mutexState)
        {
            mutexState.ConditionWaiterCount = Math.Max(
                0,
                mutexState.ConditionWaiterCount - 1);
            if (relockResult == (int)OrbisGen2Result.ORBIS_GEN2_OK &&
                releasedRecursion > 1)
            {
                mutexState.RecursionCount = releasedRecursion;
            }
        }

        if (relockResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return relockResult;
        }

        var waitResult = state.Destroyed
            ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED
            : signaled || !timed
                ? (int)OrbisGen2Result.ORBIS_GEN2_OK
                : CondTimedOutResult(posixResult);
        TracePthreadCond(
            signaled ? "wait-exit" : "wait-exit-timeout",
            condAddress,
            mutexAddress,
            state,
            timed,
            waitResult);
        return waitResult;
    }

    private static int PthreadCondSignalCore(
        CpuContext ctx,
        ulong condAddress,
        bool broadcast,
        bool latchWhenNoWaiters)
    {
        if (condAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveCondState(ctx, condAddress, createIfZero: true, out var resolvedCondAddress, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        lock (state.SyncRoot)
        {
            if (state.Destroyed)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            state.SignalEpoch++;
            if (state.Waiters == 0)
            {
                if (latchWhenNoWaiters)
                {
                    state.CompatibilitySignalCredits++;
                }
            }
            else if (broadcast)
            {
                state.SignalsPending = state.Waiters;
            }
            else if (state.SignalsPending < state.Waiters)
            {
                state.SignalsPending++;
            }

            if (state.Waiters != 0)
            {
                Monitor.PulseAll(state.SyncRoot);
            }

            TracePthreadCond(broadcast ? "broadcast" : "signal", condAddress, mutexAddress: 0, state, timed: false, (int)OrbisGen2Result.ORBIS_GEN2_OK);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int TranslatePthreadResult(int result, bool posixResult)
    {
        if (!posixResult || result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return result;
        }

        return result switch
        {
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED => 1, // EPERM
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND => 2,        // ENOENT
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT => 22, // EINVAL
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK => 11,        // EDEADLK
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED => 13,         // EACCES
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY => 16,            // EBUSY
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN => 35,       // EAGAIN
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT => 60,       // ETIMEDOUT
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED => 85,        // ECANCELED
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT => 14,    // EFAULT
            _ => result
        };
    }

    private static int CondTimedOutResult(bool posixResult) =>
        posixResult
            ? 60
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;

    private static TimeSpan GetRemainingTimeout(long deadlineTimestamp)
    {
        var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(
            remainingTicks / (double)Stopwatch.Frequency);
    }

    public static string? DumpMutexStateForStall(ulong mutexAddress)
    {
        if (!_mutexStates.TryGetValue(mutexAddress, out var state))
        {
            return null;
        }

        var aliases = new List<string>();
        foreach (var pair in _mutexStates)
        {
            if (ReferenceEquals(pair.Value, state))
            {
                aliases.Add($"0x{pair.Key:X}");
            }
        }

        lock (state)
        {
            return $"mutex=0x{mutexAddress:X16} owner=0x{state.OwnerThreadId:X} recursion={state.RecursionCount} " +
                   $"type={state.Type} queued_waiters={state.WaiterCount} cond_waiters={state.ConditionWaiterCount} " +
                   $"destroyed={state.Destroyed} " +
                   $"aliases=[{string.Join(",", aliases)}]";
        }
    }

    internal static bool TryGetMutexStateSnapshot(
        ulong mutexAddress,
        out ulong ownerThreadId,
        out int recursionCount,
        out int waiterCount)
    {
        if (!_mutexStates.TryGetValue(mutexAddress, out var state))
        {
            ownerThreadId = 0;
            recursionCount = 0;
            waiterCount = 0;
            return false;
        }

        lock (state)
        {
            if (state.Destroyed)
            {
                ownerThreadId = 0;
                recursionCount = 0;
                waiterCount = 0;
                return false;
            }

            ownerThreadId = state.OwnerThreadId;
            recursionCount = state.RecursionCount;
            waiterCount = state.WaiterCount;
            return true;
        }
    }

    internal static bool TryGetConditionStateSnapshot(
        ulong condAddress,
        out int waiterCount)
    {
        PthreadCondState state;
        lock (_stateGate)
        {
            if (!_condStates.TryGetValue(
                    condAddress,
                    out var resolvedState))
            {
                waiterCount = 0;
                return false;
            }
            state = resolvedState;
        }

        lock (state.SyncRoot)
        {
            if (state.Destroyed)
            {
                waiterCount = 0;
                return false;
            }

            waiterCount = state.Waiters;
            return true;
        }
    }

    private static TimeSpan GetCondWaitTimeout(uint timeoutUsec)
    {
        if (timeoutUsec == 0)
        {
            return TimeSpan.Zero;
        }

        // Round positive sub-millisecond waits to the host sleep quantum.
        return timeoutUsec < 1_000
            ? TimeSpan.FromMilliseconds(1)
            : TimeSpan.FromTicks((long)timeoutUsec * 10L);
    }

    private static int NormalizeMutexType(int type)
    {
        return type switch
        {
            0 => MutexTypeErrorCheck,
            1 => MutexTypeErrorCheck,
            2 => MutexTypeRecursive,
            3 => MutexTypeNormal,
            4 => MutexTypeAdaptiveNp,
            _ => MutexTypeErrorCheck,
        };
    }

    private static object GetPthreadOnceGate(ulong onceAddress)
    {
        lock (_stateGate)
        {
            if (!_onceGates.TryGetValue(onceAddress, out var gate))
            {
                gate = new object();
                _onceGates[onceAddress] = gate;
            }

            return gate;
        }
    }

    private static bool CreateImplicitMutexState(CpuContext ctx, ulong mutexAddress, int type, out ulong resolvedAddress, [NotNullWhen(true)] out PthreadMutexState? state)
    {
        var createdState = new PthreadMutexState
        {
            Type = type,
        };

        if (!TryAllocateOpaqueObject(ctx, MutexObjectSize, out var handle))
        {
            resolvedAddress = 0;
            state = null;
            return false;
        }
        if (!InitializeMutexObject(ctx, handle, createdState))
        {
            FreeOpaqueObject(ctx, handle);
            resolvedAddress = 0;
            state = null;
            return false;
        }

        lock (_stateGate)
        {
            if (_mutexStates.TryGetValue(mutexAddress, out state))
            {
                FreeOpaqueObject(ctx, handle);
                resolvedAddress = mutexAddress;
                return true;
            }

            if (_mutexStates.TryGetValue(handle, out state))
            {
                FreeOpaqueObject(ctx, handle);
                resolvedAddress = handle;
                return true;
            }

            _mutexStates[mutexAddress] = createdState;
            _mutexStates[handle] = createdState;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, mutexAddress, handle))
        {
            _mutexStates.TryRemove(mutexAddress, out _);
            _mutexStates.TryRemove(handle, out _);

            FreeOpaqueObject(ctx, handle);
            resolvedAddress = 0;
            state = null;
            return false;
        }

        resolvedAddress = handle;
        state = createdState;
        return true;
    }

    private static void TracePthreadSelf(CpuContext ctx, ulong currentThreadHandle)
    {
        if (!ShouldTracePthread())
        {
            return;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadUniqueId();
        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_self: stale_rdi=0x{ctx[CpuRegister.Rdi]:X16} thread=0x{currentThreadHandle:X16} tid=0x{currentThreadId:X16}");
    }

    private static void TracePthreadOnce(ulong onceAddress, ulong initRoutine, string operation, string? error)
    {
        if (!ShouldTracePthread())
        {
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(error) ? string.Empty : $" error={error}";
        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_once_{operation}: once=0x{onceAddress:X16} init=0x{initRoutine:X16}{suffix}");
    }

    private static void TracePthreadMutex(CpuContext ctx, string operation, ulong mutexAddress, ulong resolvedAddress, PthreadMutexState? state, ulong currentThreadId, int result)
    {
        if (!ShouldTracePthreadMutex(mutexAddress, resolvedAddress))
        {
            return;
        }

        _ = KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress, out var guestWord0);
        _ = KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress + 8, out var guestWord1);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_{operation}: mutex=0x{mutexAddress:X16} resolved=0x{resolvedAddress:X16} " +
            $"guest[0]=0x{guestWord0:X16} guest[8]=0x{guestWord1:X16} " +
            $"current=0x{currentThreadId:X16} owner=0x{(state?.OwnerThreadId ?? 0):X16} " +
            $"recursion={(state?.RecursionCount ?? 0)} type={(state?.Type ?? 0)} result=0x{unchecked((uint)result):X8} " +
            $"guest_thread=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} managed={Environment.CurrentManagedThreadId}");
    }

    private static void TracePthreadCond(string operation, ulong condAddress, ulong mutexAddress, PthreadCondState? state, bool timed, int result)
    {
        if (!_tracePthreadConds)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_cond_{operation}: thread=0x{KernelPthreadState.GetCurrentThreadHandle():X16} " +
            $"cond=0x{condAddress:X16} mutex=0x{mutexAddress:X16} " +
            $"waiters={(state?.Waiters ?? 0)} timed={timed} " +
            $"result=0x{unchecked((uint)result):X8}");
    }

    private static bool ShouldTracePthread()
    {
        return _tracePthreads;
    }

    private static bool ShouldTracePthreadMutex(ulong mutexAddress, ulong resolvedAddress)
    {
        if (_tracePthreadMutexFilter is null || _tracePthreadMutexFilter.Count == 0)
        {
            return _tracePthreads;
        }

        return _tracePthreadMutexFilter.Contains(mutexAddress) ||
            _tracePthreadMutexFilter.Contains(resolvedAddress);
    }

    private static HashSet<ulong>? ParseTraceAddressFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var addresses = new HashSet<ulong>();
        foreach (var token in filter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? token[2..]
                : token;
            normalized = normalized.TrimStart('0');

            if (ulong.TryParse(
                    normalized.Length == 0 ? "0" : normalized,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var address))
            {
                addresses.Add(address);
            }
        }

        return addresses.Count == 0 ? null : addresses;
    }
}
