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
                GuestThreadBlocking.NoteBlocked(guestThreadHandle, "sceKernelWaitSema");
                if (_traceSema)
                {
                    TraceSemaphore(
                        $"wait-block handle=0x{handle:X8} name='{semaphore.Name}' " +
                        $"need={needCount} count={semaphore.Count} waiters={semaphore.WaitingThreads}");
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
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Call sites must check this before building the interpolated message; the trace
    // strings would otherwise be allocated on every semaphore op even with tracing off.
    private static readonly bool _traceSema =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SEMA"), "1", StringComparison.Ordinal);

    private static void TraceSemaphore(string message)
    {
        Console.Error.WriteLine($"[LOADER][TRACE] sema.{message}");
    }
}
