// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelSemaphoreCompatExports
{
    private const int MaxSemaphoreNameLength = 128;
    private static readonly ConcurrentDictionary<uint, KernelSemaphoreState> _semaphores = new();
    private static int _nextSemaphoreHandle = 1;

    internal static void ResetRuntimeState()
    {
        var states = _semaphores.Values
            .Distinct<KernelSemaphoreState>(ReferenceEqualityComparer.Instance)
            .ToArray();
        _semaphores.Clear();
        Interlocked.Exchange(ref _nextSemaphoreHandle, 1);

        foreach (var state in states)
        {
            lock (state.Gate)
            {
                state.Deleted = true;
                Monitor.PulseAll(state.Gate);
            }
        }
    }

    private sealed class KernelSemaphoreState
    {
        public required string Name { get; init; }
        public required int InitialCount { get; init; }
        public required int MaxCount { get; init; }
        public int Count { get; set; }
        public int WaitingThreads { get; set; }
        public int CancelEpoch { get; set; }
        public bool Deleted { get; set; }
        public object Gate { get; } = new();
    }

    [SysAbiExport(
        Nid = "188x57JYp0g",
        ExportName = "sceKernelCreateSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCreateSema(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        var attr = unchecked((uint)ctx[CpuRegister.Rdx]);
        var initialCount = unchecked((int)ctx[CpuRegister.Rcx]);
        var maxCount = unchecked((int)ctx[CpuRegister.R8]);
        var optionAddress = ctx[CpuRegister.R9];

        if (semaphoreAddress == 0 ||
            nameAddress == 0 ||
            attr > 2 ||
            initialCount < 0 ||
            maxCount <= 0 ||
            initialCount > maxCount ||
            optionAddress != 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!KernelMemoryCompatExports.TryReadCString(
                ctx,
                nameAddress,
                MaxSemaphoreNameLength,
                out var nameBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var name = Encoding.UTF8.GetString(nameBytes);
        var handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        if (handle == 0)
        {
            handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        }

        var state = new KernelSemaphoreState
        {
            Name = name,
            InitialCount = initialCount,
            MaxCount = maxCount,
            Count = initialCount,
        };
        _semaphores[handle] = state;

        if (!KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, semaphoreAddress, handle))
        {
            _semaphores.TryRemove(handle, out _);
            // Handles are sequential and guest-predictable, so a hostile guest can
            // race a WaitSema onto the handle between publication above and this
            // rollback. Strand-proof that waiter exactly like DeleteSema does.
            lock (state.Gate)
            {
                state.Deleted = true;
                Monitor.PulseAll(state.Gate);
            }

            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (_traceSema)
        {
            TraceSemaphore($"create handle=0x{handle:X8} name='{name}' attr=0x{attr:X} init={initialCount} max={maxCount}");
        }
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "Zxa0VhQVTsk",
        ExportName = "sceKernelWaitSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWaitSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var needCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var timeoutAddress = ctx[CpuRegister.Rdx];

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (needCount < 1 || needCount > semaphore.MaxCount)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint timeoutMicros = 0;
        if (timeoutAddress != 0 &&
            !KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                timeoutAddress,
                out timeoutMicros))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var deadlineTimestamp = timeoutAddress == 0
            ? long.MaxValue
            : GuestThreadExecution.ComputeDeadlineTimestamp(
                TimeSpan.FromTicks((long)timeoutMicros * 10L));

        lock (semaphore.Gate)
        {
            var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
            var cancelEpochAtBlock = semaphore.CancelEpoch;
            if (semaphore.Count < needCount)
            {
                semaphore.WaitingThreads++;
                GuestThreadBlocking.NoteBlocked(
                    guestThreadHandle,
                    FormatWaitBlockDescription(handle, semaphore.Name));
                if (_traceSema)
                {
                    var returnRip = GuestThreadExecution.TryGetCurrentImportCallFrame(out var importFrame)
                        ? importFrame.ReturnRip
                        : 0;
                    TraceSemaphore(FormatWaitBlockTrace(
                        handle,
                        semaphore.Name,
                        needCount,
                        semaphore.Count,
                        semaphore.WaitingThreads,
                        guestThreadHandle,
                        returnRip));
                }

                try
                {
                    while (semaphore.Count < needCount)
                    {
                        if (semaphore.Deleted)
                        {
                            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
                        }

                        if (semaphore.CancelEpoch != cancelEpochAtBlock ||
                            GuestThreadBlocking.ShutdownRequested)
                        {
                            if (timeoutAddress != 0 &&
                                !KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, timeoutAddress, 0))
                            {
                                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                            }

                            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
                        }

                        var waitMilliseconds = GuestThreadBlocking.WaitSliceMilliseconds;
                        if (timeoutAddress != 0)
                        {
                            var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
                            if (remainingTicks <= 0)
                            {
                                if (!KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, timeoutAddress, 0))
                                {
                                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                                }

                                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                            }

                            waitMilliseconds =
                                GuestThreadBlocking.GetWaitMilliseconds(deadlineTimestamp);
                        }

                        GuestThreadBlocking.Checkpoint(guestThreadHandle, semaphore.Gate);
                        _ = Monitor.Wait(semaphore.Gate, waitMilliseconds);
                    }
                }
                finally
                {
                    semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                    GuestThreadBlocking.NoteUnblocked(guestThreadHandle);
                }
            }

            if (timeoutAddress != 0)
            {
                var remainingTicks = Math.Max(0L, deadlineTimestamp - Stopwatch.GetTimestamp());
                var remainingMicros = (uint)Math.Min(
                    uint.MaxValue,
                    remainingTicks * 1_000_000d / Stopwatch.Frequency);
                if (!KernelMemoryCompatExports.TryWriteUInt32Compat(
                        ctx,
                        timeoutAddress,
                        remainingMicros))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            semaphore.Count -= needCount;
            if (_traceSema)
            {
                TraceSemaphore(
                    $"wait handle=0x{handle:X8} name='{semaphore.Name}' " +
                    $"need={needCount} count={semaphore.Count}");
            }

            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }
    }

    [SysAbiExport(
        Nid = "12wOHk8ywb0",
        ExportName = "sceKernelPollSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelPollSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var needCount = unchecked((int)ctx[CpuRegister.Rsi]);

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (needCount < 1 || needCount > semaphore.MaxCount)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Count < needCount)
            {
                if (_traceSema)
                {
                    TraceSemaphore($"poll-busy handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                }
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
            }

            semaphore.Count -= needCount;
            if (_traceSema)
            {
                TraceSemaphore($"poll handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
            }
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }
    }

    [SysAbiExport(
        Nid = "4czppHBiriw",
        ExportName = "sceKernelSignalSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSignalSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var signalCount = unchecked((int)ctx[CpuRegister.Rsi]);

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (signalCount <= 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Count > semaphore.MaxCount - signalCount)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            semaphore.Count += signalCount;
            Monitor.PulseAll(semaphore.Gate);
            if (_traceSema)
            {
                TraceSemaphore($"signal handle=0x{handle:X8} name='{semaphore.Name}' signal={signalCount} count={semaphore.Count} waiters={semaphore.WaitingThreads}");
            }
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "4DM06U2BNEY",
        ExportName = "sceKernelCancelSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCancelSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var setCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var waitingThreadsAddress = ctx[CpuRegister.Rdx];

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (setCount > semaphore.MaxCount)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (waitingThreadsAddress != 0 &&
                !KernelMemoryCompatExports.TryWriteUInt32Compat(
                    ctx,
                    waitingThreadsAddress,
                    unchecked((uint)semaphore.WaitingThreads)))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            semaphore.Count = setCount < 0 ? semaphore.InitialCount : setCount;
            semaphore.CancelEpoch++;
            Monitor.PulseAll(semaphore.Gate);
            if (_traceSema)
            {
                TraceSemaphore($"cancel handle=0x{handle:X8} name='{semaphore.Name}' set={setCount} count={semaphore.Count}");
            }
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "R1Jvn8bSCW8",
        ExportName = "sceKernelDeleteSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!_semaphores.TryRemove(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        // Delete succeeds even with blocked waiters; they wake with the deleted
        // result (the SCE kernel wakes waiters with the EACCES-class code).
        lock (semaphore.Gate)
        {
            semaphore.Deleted = true;
            Monitor.PulseAll(semaphore.Gate);
        }

        if (_traceSema)
        {
            TraceSemaphore($"delete handle=0x{handle:X8} name='{semaphore.Name}'");
        }
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "pDuPEf3m4fI",
        ExportName = "sem_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemInit(CpuContext ctx)
    {
        var result = InitializeAddressSemaphore(ctx);
        return PosixResult(ctx, result);
    }

    private static int InitializeAddressSemaphore(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var initialCountValue = ctx[CpuRegister.Rdx];
        if (semaphoreAddress == 0 || initialCountValue > int.MaxValue)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        if (handle == 0)
        {
            handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        }

        var initialCount = unchecked((int)initialCountValue);
        _semaphores[handle] = new KernelSemaphoreState
        {
            Name = $"posix@0x{semaphoreAddress:X16}",
            InitialCount = initialCount,
            MaxCount = int.MaxValue,
            Count = initialCount,
        };
        if (!KernelMemoryCompatExports.TryWriteUInt32Compat(
                ctx,
                semaphoreAddress,
                handle))
        {
            if (_semaphores.TryRemove(handle, out var published))
            {
                lock (published.Gate)
                {
                    published.Deleted = true;
                    Monitor.PulseAll(published.Gate);
                }
            }

            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (_traceSema)
        {
            TraceSemaphore($"posix-init address=0x{semaphoreAddress:X16} handle=0x{handle:X8} count={initialCount}");
        }
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "GEnUkDZoUwY",
        ExportName = "scePthreadSemInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemInit(CpuContext ctx)
    {
        // scePthreadSemInit(sem, flag, value, name) seems to only support private semaphores
        if (ctx[CpuRegister.Rsi] != 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return InitializeAddressSemaphore(ctx);
    }

    [SysAbiExport(
        Nid = "YCV5dGGBcCo",
        ExportName = "sem_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemWait(CpuContext ctx)
    {
        var result = WaitAddressSemaphore(ctx);
        return PosixResult(ctx, result);
    }

    private static int WaitAddressSemaphore(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        return KernelWaitSema(ctx);
    }

    [SysAbiExport(
        Nid = "C36iRE0F5sE",
        ExportName = "scePthreadSemWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemWait(CpuContext ctx) => WaitAddressSemaphore(ctx);

    [SysAbiExport(
        Nid = "WBWzsRifCEA",
        ExportName = "sem_trywait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemTryWait(CpuContext ctx)
    {
        var result = TryWaitAddressSemaphore(ctx);
        return PosixResult(ctx, result, busyErrno: Eagain);
    }

    private static int TryWaitAddressSemaphore(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        return KernelPollSema(ctx);
    }

    [SysAbiExport(
        Nid = "H2a+IN9TP0E",
        ExportName = "scePthreadSemTrywait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemTryWait(CpuContext ctx)
    {
        var result = TryWaitAddressSemaphore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN)
            : result;
    }

    [SysAbiExport(
        Nid = "w5IHyvahg-o",
        ExportName = "sem_timedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemTimedWait(CpuContext ctx)
    {
        var timeoutAddress = ctx[CpuRegister.Rsi];
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = timeoutAddress;
        return PosixResult(ctx, KernelWaitSema(ctx));
    }

    [SysAbiExport(
        Nid = "IKP8typ0QUk",
        ExportName = "sem_post",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemPost(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out _))
        {
            return PosixResult(
                ctx,
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var result = PostAddressSemaphore(ctx);
        return PosixResult(ctx, result, invalidArgumentErrno: Eoverflow);
    }

    private static int PostAddressSemaphore(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        return KernelSignalSema(ctx);
    }

    [SysAbiExport(
        Nid = "aishVAiFaYM",
        ExportName = "scePthreadSemPost",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemPost(CpuContext ctx) => PostAddressSemaphore(ctx);

    [SysAbiExport(
        Nid = "Bq+LRV-N6Hk",
        ExportName = "sem_getvalue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemGetValue(CpuContext ctx)
    {
        var result = GetAddressSemaphoreValue(ctx);
        return PosixResult(ctx, result);
    }

    private static int GetAddressSemaphoreValue(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0 ||
            !TryGetPosixSemaphoreHandle(ctx, semaphoreAddress, out var handle) ||
            !_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        int count;
        lock (semaphore.Gate)
        {
            count = semaphore.Count;
        }

        return KernelMemoryCompatExports.TryWriteUInt32Compat(
                ctx,
                valueAddress,
                unchecked((uint)count))
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cDW233RAwWo",
        ExportName = "sem_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemDestroy(CpuContext ctx)
    {
        var result = DestroyAddressSemaphore(ctx);
        return PosixResult(ctx, result);
    }

    private static int DestroyAddressSemaphore(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        if (!TryGetPosixSemaphoreHandle(ctx, semaphoreAddress, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        var result = KernelDeleteSema(ctx);
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            _ = KernelMemoryCompatExports.TryWriteUInt32Compat(
                ctx,
                semaphoreAddress,
                0);
        }

        return result;
    }

    [SysAbiExport(
        Nid = "Vwc+L05e6oE",
        ExportName = "scePthreadSemDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemDestroy(CpuContext ctx) => DestroyAddressSemaphore(ctx);

    private static bool TryGetPosixSemaphoreHandle(CpuContext ctx, ulong semaphoreAddress, out uint handle)
    {
        handle = 0;
        return semaphoreAddress != 0 &&
               KernelMemoryCompatExports.TryReadUInt32Compat(
                   ctx,
                   semaphoreAddress,
                   out handle) &&
               handle != 0;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }

    private const int Eacces = 13;
    private const int Efault = 14;
    private const int Einval = 22;
    private const int Eagain = 35;
    private const int Etimedout = 60;
    private const int Eoverflow = 84;
    private const int Ecanceled = 85;

    private static int PosixResult(
        CpuContext ctx,
        int result,
        int invalidArgumentErrno = Einval,
        int busyErrno = Eagain)
    {
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return 0;
        }

        var errno = result switch
        {
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT => invalidArgumentErrno,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT => Efault,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED => Eacces,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED => Eacces,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY => busyErrno,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN => Eagain,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT => Etimedout,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED => Ecanceled,
            _ => Einval,
        };
        _ = KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return -1;
    }

    // Call sites must check this before building the interpolated message; the trace
    // strings would otherwise be allocated on every semaphore op even with tracing off.
    private static readonly bool _traceSema =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SEMA"), "1", StringComparison.Ordinal);

    internal static string FormatWaitBlockTrace(
        uint handle,
        string name,
        int needCount,
        int count,
        int waitingThreads,
        ulong guestThreadHandle,
        ulong returnRip) =>
        $"wait-block handle=0x{handle:X8} name='{name}' " +
        $"need={needCount} count={count} waiters={waitingThreads} " +
        $"guest_thread=0x{guestThreadHandle:X16} ret=0x{returnRip:X16}";

    internal static string FormatWaitBlockDescription(uint handle, string name) =>
        $"sceKernelWaitSema(handle=0x{handle:X8} name='{name}')";

    private static void TraceSemaphore(string message)
    {
        Console.Error.WriteLine($"[LOADER][TRACE] sema.{message}");
    }
}
