// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelEventFlagCompatExports
{
    private const int MaxEventFlagNameLength = 31;
    private const uint AttrThreadFifo = 0x01;
    private const uint AttrThreadPriority = 0x02;
    private const uint AttrSingle = 0x10;
    private const uint AttrMulti = 0x20;
    private const uint WaitAnd = 0x01;
    private const uint WaitOr = 0x02;
    private const uint ClearAll = 0x10;
    private const uint ClearPattern = 0x20;

    private static readonly ConcurrentDictionary<ulong, EventFlagState> _eventFlags = new();
    private static long _nextEventFlagHandle = 1;

    // Cached once: gating every call site avoids building the interpolated
    // trace string (and FormatFrameChain/FormatGuestWaitObject) when disabled.
    private static readonly bool _traceEventFlag = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_EVENT_FLAG"), "1", StringComparison.Ordinal);

    private sealed class EventFlagState
    {
        public required string Name { get; init; }
        public required uint Attributes { get; init; }
        public ulong Bits { get; set; }
        public int WaitingThreads { get; set; }
        public int CancelEpoch { get; set; }
        public bool Deleted { get; set; }
        public object Gate { get; } = new();
    }

    [SysAbiExport(
        Nid = "BpFoboUJoZU",
        ExportName = "sceKernelCreateEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCreateEventFlag(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        var attributes = unchecked((uint)ctx[CpuRegister.Rdx]);
        var initialPattern = ctx[CpuRegister.Rcx];
        var optionAddress = ctx[CpuRegister.R8];

        if (outAddress == 0 ||
            nameAddress == 0 ||
            optionAddress != 0 ||
            !IsValidAttributes(attributes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!KernelMemoryCompatExports.TryReadCString(
                ctx,
                nameAddress,
                MaxEventFlagNameLength + 1,
                out var nameBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var name = Encoding.UTF8.GetString(nameBytes);
        if (Encoding.UTF8.GetByteCount(name) > MaxEventFlagNameLength)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextEventFlagHandle));
        _eventFlags[handle] = new EventFlagState
        {
            Name = name,
            Attributes = attributes,
            Bits = initialPattern,
        };

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outAddress, handle))
        {
            _eventFlags.TryRemove(handle, out _);
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (_traceEventFlag)
        {
            TraceEventFlag($"create handle=0x{handle:X16} name='{name}' attr=0x{attributes:X2} bits=0x{initialPattern:X16}");
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "8mql9OcQnd4",
        ExportName = "sceKernelDeleteEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (!_eventFlags.TryRemove(handle, out var state))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        lock (state.Gate)
        {
            state.Deleted = true;
            Monitor.PulseAll(state.Gate);
        }

        if (_traceEventFlag)
        {
            TraceEventFlag($"delete handle=0x{handle:X16} name='{state.Name}'");
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "IOnSvHzqu6A",
        ExportName = "sceKernelSetEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        var returnRip = GetCurrentReturnRip();
        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        lock (state.Gate)
        {
            if (state.Deleted)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
            }

            state.Bits |= pattern;
            Monitor.PulseAll(state.Gate);
            if (_traceEventFlag) TraceEventFlag($"set handle=0x{handle:X16} pattern=0x{pattern:X16} bits=0x{state.Bits:X16} ret=0x{returnRip:X16}");
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "7uhBFWRAS60",
        ExportName = "sceKernelClearEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelClearEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        lock (state.Gate)
        {
            if (state.Deleted)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
            }

            state.Bits &= pattern;
            if (_traceEventFlag) TraceEventFlag($"clear handle=0x{handle:X16} mask=0x{pattern:X16} bits=0x{state.Bits:X16}");
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "9lvj5DjHZiA",
        ExportName = "sceKernelPollEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelPollEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        var waitMode = unchecked((uint)ctx[CpuRegister.Rdx]);
        var resultAddress = ctx[CpuRegister.Rcx];

        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (pattern == 0 || !IsValidWaitMode(waitMode))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (state.Gate)
        {
            if (state.Deleted)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
            }

            if (!TryWriteResultPattern(ctx, resultAddress, state.Bits))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (!IsSatisfied(state.Bits, pattern, waitMode))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
            }

            ApplyClearMode(state, pattern, waitMode);
            if (_traceEventFlag)
            {
                TraceEventFlag($"poll handle=0x{handle:X16} pattern=0x{pattern:X16} mode=0x{waitMode:X2} bits=0x{state.Bits:X16}");
            }

            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }
    }

    [SysAbiExport(
        Nid = "JTvBflhYazQ",
        ExportName = "sceKernelWaitEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWaitEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        var waitMode = unchecked((uint)ctx[CpuRegister.Rdx]);
        var resultAddress = ctx[CpuRegister.Rcx];
        var timeoutAddress = ctx[CpuRegister.R8];
        var returnRip = GetCurrentReturnRip();

        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (pattern == 0 || !IsValidWaitMode(waitMode))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint timeoutUsec = 0;
        if (timeoutAddress != 0 &&
            !KernelMemoryCompatExports.TryReadUInt32Compat(ctx, timeoutAddress, out timeoutUsec))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var deadlineTimestamp = timeoutAddress == 0
            ? long.MaxValue
            : GuestThreadExecution.ComputeDeadlineTimestamp(
                TimeSpan.FromTicks((long)timeoutUsec * 10L));

        lock (state.Gate)
        {
            if (TryCompleteSatisfiedWait(ctx, state, pattern, waitMode, resultAddress, out var immediateWaitResult))
            {
                return ctx.SetReturn(immediateWaitResult);
            }

            if (timeoutAddress != 0 && timeoutUsec == 0)
            {
                if (!KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, timeoutAddress, 0))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (!TryWriteResultPattern(ctx, resultAddress, state.Bits))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (_traceEventFlag)
                {
                    TraceEventFlag($"wait-timeout handle=0x{handle:X16} pattern=0x{pattern:X16} timeout={timeoutUsec} ret=0x{returnRip:X16}");
                }

                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
            }

            var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
            var cancelEpochAtBlock = state.CancelEpoch;
            state.WaitingThreads++;
            GuestThreadBlocking.NoteBlocked(guestThreadHandle, "sceKernelWaitEventFlag");
            if (_traceEventFlag)
            {
                TraceEventFlag(
                    $"wait-block handle=0x{handle:X16} pattern=0x{pattern:X16} " +
                    $"waiters={state.WaitingThreads} guest_thread=0x{guestThreadHandle:X16} " +
                    $"ret=0x{returnRip:X16}");
            }

            try
            {
                while (true)
                {
                    if (state.Deleted)
                    {
                        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
                    }

                    if (state.CancelEpoch != cancelEpochAtBlock ||
                        GuestThreadBlocking.ShutdownRequested)
                    {
                        if (timeoutAddress != 0 &&
                            !KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, timeoutAddress, 0))
                        {
                            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                        }

                        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
                    }

                    if (IsSatisfied(state.Bits, pattern, waitMode))
                    {
                        if (timeoutAddress != 0)
                        {
                            var remainingMicroseconds = GetRemainingMicroseconds(deadlineTimestamp);
                            if (!KernelMemoryCompatExports.TryWriteUInt32Compat(
                                    ctx,
                                    timeoutAddress,
                                    remainingMicroseconds))
                            {
                                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                            }
                        }

                        if (!TryWriteResultPattern(ctx, resultAddress, state.Bits))
                        {
                            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                        }

                        ApplyClearMode(state, pattern, waitMode);
                        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
                    }

                    var waitMilliseconds = GuestThreadBlocking.WaitSliceMilliseconds;
                    if (timeoutAddress != 0)
                    {
                        var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
                        if (remainingTicks <= 0)
                        {
                            if (!KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, timeoutAddress, 0) ||
                                !TryWriteResultPattern(ctx, resultAddress, state.Bits))
                            {
                                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                            }

                            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                        }

                        waitMilliseconds =
                            GuestThreadBlocking.GetWaitMilliseconds(deadlineTimestamp);
                    }

                    GuestThreadBlocking.Checkpoint(guestThreadHandle, state.Gate);
                    _ = Monitor.Wait(state.Gate, waitMilliseconds);
                }
            }
            finally
            {
                state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
                GuestThreadBlocking.NoteUnblocked(guestThreadHandle);
            }
        }
    }

    [SysAbiExport(
        Nid = "PZku4ZrXJqg",
        ExportName = "sceKernelCancelEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCancelEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var setPattern = ctx[CpuRegister.Rsi];
        var waiterCountAddress = ctx[CpuRegister.Rdx];
        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        lock (state.Gate)
        {
            if (state.Deleted)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
            }

            if (waiterCountAddress != 0 &&
                !KernelMemoryCompatExports.TryWriteUInt32Compat(
                    ctx,
                    waiterCountAddress,
                    unchecked((uint)state.WaitingThreads)))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            state.Bits = setPattern;
            state.CancelEpoch++;
            Monitor.PulseAll(state.Gate);
            if (_traceEventFlag)
            {
                TraceEventFlag(
                    $"cancel handle=0x{handle:X16} bits=0x{setPattern:X16} " +
                    $"guest_thread=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} ret=0x{GetCurrentReturnRip():X16}");
            }
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static bool IsValidAttributes(uint attributes)
    {
        var queueMode = attributes & 0x0F;
        var threadMode = attributes & 0xF0;
        return (queueMode is 0 or AttrThreadFifo or AttrThreadPriority) &&
            (threadMode is 0 or AttrSingle or AttrMulti) &&
            (attributes & ~0x33u) == 0;
    }

    private static bool IsValidWaitMode(uint waitMode)
    {
        var condition = waitMode & 0x0F;
        var clearMode = waitMode & 0xF0;
        return condition is WaitAnd or WaitOr &&
            clearMode is 0 or ClearAll or ClearPattern &&
            (waitMode & ~0x33u) == 0;
    }

    private static bool IsSatisfied(ulong bits, ulong pattern, uint waitMode) =>
        (waitMode & 0x0F) == WaitAnd
            ? (bits & pattern) == pattern
            : (bits & pattern) != 0;

    private static void ApplyClearMode(EventFlagState state, ulong pattern, uint waitMode)
    {
        switch (waitMode & 0xF0)
        {
            case ClearAll:
                state.Bits = 0;
                break;
            case ClearPattern:
                state.Bits &= ~pattern;
                break;
        }
    }

    private static bool TryCompleteSatisfiedWait(
    CpuContext ctx,
    EventFlagState state,
    ulong pattern,
    uint waitMode,
    ulong resultAddress,
    out OrbisGen2Result result)
    {
        result = OrbisGen2Result.ORBIS_GEN2_OK;

        if (state.Deleted)
        {
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
            return true;
        }

        if (!IsSatisfied(state.Bits, pattern, waitMode))
        {
            return false;
        }

        if (!TryWriteResultPattern(ctx, resultAddress, state.Bits))
        {
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            return true;
        }

        ApplyClearMode(state, pattern, waitMode);
        return true;
    }

    private static uint GetRemainingMicroseconds(long deadlineTimestamp)
    {
        var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
        return remainingTicks <= 0
            ? 0u
            : (uint)Math.Min(
                uint.MaxValue,
                remainingTicks * 1_000_000d / Stopwatch.Frequency);
    }

    private static bool TryWriteResultPattern(CpuContext ctx, ulong address, ulong bits) =>
        address == 0 || KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, address, bits);

    private static void TraceEventFlag(string message)
    {
        if (_traceEventFlag)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] event_flag.{message}");
        }
    }

    private static ulong GetCurrentReturnRip() =>
        GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame)
            ? frame.ReturnRip
            : 0UL;

    private static string FormatFrameChain(CpuContext ctx)
    {
        Span<ulong> returns = stackalloc ulong[4];
        var count = 0;
        var frame = ctx[CpuRegister.Rbp];
        for (var index = 0; index < returns.Length && frame != 0; index++)
        {
            if (!ctx.TryReadUInt64(frame, out var nextFrame) ||
                !ctx.TryReadUInt64(frame + sizeof(ulong), out var returnAddress))
            {
                break;
            }

            returns[count++] = returnAddress;
            if (nextFrame <= frame)
            {
                break;
            }

            frame = nextFrame;
        }

        return count switch
        {
            0 => "none",
            1 => $"0x{returns[0]:X16}",
            2 => $"0x{returns[0]:X16},0x{returns[1]:X16}",
            3 => $"0x{returns[0]:X16},0x{returns[1]:X16},0x{returns[2]:X16}",
            _ => $"0x{returns[0]:X16},0x{returns[1]:X16},0x{returns[2]:X16},0x{returns[3]:X16}",
        };
    }

    private static string FormatGuestWaitObject(CpuContext ctx)
    {
        var r12 = ctx[CpuRegister.R12];
        var r13 = ctx[CpuRegister.R13];
        var objectAddress = r12 != 0
            ? r12
            : r13 >= 0xA8
                ? r13 - 0xA8
                : 0;

        var builder = new StringBuilder(256);
        builder.Append($"r12=0x{r12:X16} r13=0x{r13:X16}");
        if (objectAddress == 0)
        {
            return builder.ToString();
        }

        builder.Append($" obj=0x{objectAddress:X16}");
        AppendUInt32(builder, ctx, objectAddress + 0x58, "o58");
        AppendUInt32(builder, ctx, objectAddress + 0x5C, "o5C");
        AppendUInt64(builder, ctx, objectAddress + 0x60, "o60");
        AppendByte(builder, ctx, objectAddress + 0x6C, "state6C");
        AppendByte(builder, ctx, objectAddress + 0x6D, "o6D");
        AppendByte(builder, ctx, objectAddress + 0xA0, "waitA0");
        AppendByte(builder, ctx, objectAddress + 0xA1, "stateA1");
        AppendByte(builder, ctx, objectAddress + 0xA2, "oA2");
        AppendUInt64(builder, ctx, objectAddress + 0xA8, "eventA8");
        if (r13 != 0)
        {
            AppendUInt64(builder, ctx, r13, "r13_0");
            AppendUInt64(builder, ctx, r13 + 8, "r13_8");
        }

        return builder.ToString();
    }

    private static void AppendByte(StringBuilder builder, CpuContext ctx, ulong address, string name)
    {
        if (ctx.TryReadByte(address, out var value))
        {
            builder.Append($" {name}=0x{value:X2}");
        }
    }

    private static void AppendUInt32(StringBuilder builder, CpuContext ctx, ulong address, string name)
    {
        if (ctx.TryReadUInt32(address, out var value))
        {
            builder.Append($" {name}=0x{value:X8}");
        }
    }

    private static void AppendUInt64(StringBuilder builder, CpuContext ctx, ulong address, string name)
    {
        if (ctx.TryReadUInt64(address, out var value))
        {
            builder.Append($" {name}=0x{value:X16}");
        }
    }
}
