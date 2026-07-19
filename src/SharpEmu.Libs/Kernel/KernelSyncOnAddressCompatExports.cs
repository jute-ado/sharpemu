// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Implements the PS5's futex-style wait/wake primitives. A waiter sleeps only
/// while the addressed 32-bit word equals its expected value. The per-address
/// monitor makes the compare-and-park transition atomic with respect to wake,
/// eliminating missed wakeups without polling or captured continuations.
/// </summary>
public static class KernelSyncOnAddressCompatExports
{
    private sealed class AddressWaitState
    {
        public object Gate { get; } = new();

        public int Users;

        public LinkedList<WaitRegistration> Waiters { get; } = [];
    }

    private sealed class WaitRegistration
    {
        public bool Released;

        public LinkedListNode<WaitRegistration>? Node;
    }

    private static readonly object RegistryGate = new();
    private static readonly Dictionary<ulong, AddressWaitState> States = [];

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var expected = unchecked((uint)ctx[CpuRegister.Rsi]);
        var timeoutAddress = ctx[CpuRegister.Rdx];
        if (address == 0 || (address & 3) != 0)
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint timeoutMicros = 0;
        if (timeoutAddress != 0 &&
            !KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                timeoutAddress,
                out timeoutMicros))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var deadlineTimestamp = timeoutAddress == 0
            ? long.MaxValue
            : GuestThreadExecution.ComputeDeadlineTimestamp(
                TimeSpan.FromTicks((long)timeoutMicros * 10L));
        if (!KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                address,
                out var current))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (current != expected)
        {
            return CompleteWait(
                ctx,
                timeoutAddress,
                deadlineTimestamp,
                OrbisGen2Result.ORBIS_GEN2_OK);
        }

        var state = AcquireState(address, create: true)!;
        try
        {
            lock (state.Gate)
            {
                if (!KernelMemoryCompatExports.TryReadUInt32Compat(
                        ctx,
                        address,
                        out current))
                {
                    return ctx.SetReturn(
                        OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (current != expected)
                {
                    return CompleteWait(
                        ctx,
                        timeoutAddress,
                        deadlineTimestamp,
                        OrbisGen2Result.ORBIS_GEN2_OK);
                }

                var guestThreadHandle =
                    GuestThreadExecution.CurrentGuestThreadHandle;
                var registration = new WaitRegistration();
                registration.Node = state.Waiters.AddLast(registration);
                GuestThreadBlocking.NoteBlocked(
                    guestThreadHandle,
                    $"sceKernelSyncOnAddressWait(0x{address:X16})");
                try
                {
                    while (true)
                    {
                        if (registration.Released)
                        {
                            return CompleteWait(
                                ctx,
                                timeoutAddress,
                                deadlineTimestamp,
                                OrbisGen2Result.ORBIS_GEN2_OK);
                        }

                        if (GuestThreadBlocking.ShutdownRequested)
                        {
                            return CompleteWait(
                                ctx,
                                timeoutAddress,
                                deadlineTimestamp,
                                OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
                        }

                        var waitMilliseconds =
                            GuestThreadBlocking.WaitSliceMilliseconds;
                        if (timeoutAddress != 0)
                        {
                            if (deadlineTimestamp <=
                                Stopwatch.GetTimestamp())
                            {
                                return CompleteWait(
                                    ctx,
                                    timeoutAddress,
                                    deadlineTimestamp,
                                    OrbisGen2Result
                                        .ORBIS_GEN2_ERROR_TIMED_OUT);
                            }

                            waitMilliseconds =
                                GuestThreadBlocking.GetWaitMilliseconds(
                                    deadlineTimestamp);
                        }

                        GuestThreadBlocking.Checkpoint(
                            guestThreadHandle,
                            state.Gate);
                        _ = Monitor.Wait(
                            state.Gate,
                            waitMilliseconds);

                        // A guest write is allowed to change the word without
                        // an accompanying wake. Rechecking on the bounded
                        // exception/shutdown slice prevents an indefinite
                        // host-side stall while preserving normal futex order.
                        if (!KernelMemoryCompatExports.TryReadUInt32Compat(
                                ctx,
                                address,
                                out current))
                        {
                            return ctx.SetReturn(
                                OrbisGen2Result
                                    .ORBIS_GEN2_ERROR_MEMORY_FAULT);
                        }

                        if (current != expected)
                        {
                            return CompleteWait(
                                ctx,
                                timeoutAddress,
                                deadlineTimestamp,
                                OrbisGen2Result.ORBIS_GEN2_OK);
                        }
                    }
                }
                finally
                {
                    if (registration.Node is { List: not null } node)
                    {
                        state.Waiters.Remove(node);
                    }

                    GuestThreadBlocking.NoteUnblocked(
                        guestThreadHandle);
                }
            }
        }
        finally
        {
            ReleaseState(address, state);
        }
    }

    [SysAbiExport(
        Nid = "q2y-wDIVWZA",
        ExportName = "sceKernelSyncOnAddressWake",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWake(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var requested = ctx[CpuRegister.Rsi];
        if (address == 0 || (address & 3) != 0)
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state = AcquireState(address, create: false);
        if (state is null)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        try
        {
            lock (state.Gate)
            {
                var remaining = requested == 0 ||
                    requested >= int.MaxValue
                        ? int.MaxValue
                        : (int)requested;
                var released = 0;
                for (var node = state.Waiters.First;
                     node is not null && remaining > 0;
                     node = node.Next)
                {
                    if (!node.Value.Released)
                    {
                        node.Value.Released = true;
                        remaining--;
                        released++;
                    }
                }

                if (released != 0)
                {
                    // PulseAll is deliberate: selected waiters are identified
                    // by their registration flags. Unselected threads may
                    // wake spuriously but immediately return to their wait,
                    // while every selected waiter is guaranteed to observe
                    // its release even between bounded wait slices.
                    Monitor.PulseAll(state.Gate);
                }
            }
        }
        finally
        {
            ReleaseState(address, state);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    internal static int GetWaitingCountForTests(ulong address)
    {
        AddressWaitState? state;
        lock (RegistryGate)
        {
            States.TryGetValue(address, out state);
        }

        if (state is null)
        {
            return 0;
        }

        lock (state.Gate)
        {
            return state.Waiters.Count;
        }
    }

    private static AddressWaitState? AcquireState(
        ulong address,
        bool create)
    {
        lock (RegistryGate)
        {
            if (!States.TryGetValue(address, out var state))
            {
                if (!create)
                {
                    return null;
                }

                state = new AddressWaitState();
                States.Add(address, state);
            }

            state.Users++;
            return state;
        }
    }

    private static void ReleaseState(
        ulong address,
        AddressWaitState state)
    {
        lock (RegistryGate)
        {
            state.Users--;
            if (state.Users == 0 &&
                state.Waiters.Count == 0 &&
                States.TryGetValue(address, out var registered) &&
                ReferenceEquals(registered, state))
            {
                States.Remove(address);
            }
        }
    }

    private static int CompleteWait(
        CpuContext ctx,
        ulong timeoutAddress,
        long deadlineTimestamp,
        OrbisGen2Result result)
    {
        if (timeoutAddress != 0)
        {
            var remainingTicks = Math.Max(
                0L,
                deadlineTimestamp - Stopwatch.GetTimestamp());
            var remainingMicros = (uint)Math.Min(
                uint.MaxValue,
                remainingTicks * 1_000_000d /
                    Stopwatch.Frequency);
            if (!KernelMemoryCompatExports.TryWriteUInt32Compat(
                    ctx,
                    timeoutAddress,
                    remainingMicros))
            {
                return ctx.SetReturn(
                    OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return ctx.SetReturn(result);
    }
}
