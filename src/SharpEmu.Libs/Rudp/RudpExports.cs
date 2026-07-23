// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Rudp;

public static class RudpExports
{
    private const int ErrorNotInitialized = unchecked((int)0x80770001);
    private const int ErrorAlreadyInitialized = unchecked((int)0x80770002);
    private const int ErrorInvalidArgument = unchecked((int)0x80770004);
    private const int ErrorOutOfMemory = unchecked((int)0x80770007);
    private const int ErrorInternalIoThreadAlreadyEnabled =
        unchecked((int)0x80770010);
    private const int ErrorInvalidEventHandler = unchecked((int)0x80770022);
    private const int MinimumAllocatorStorageSize = 0xF8 + 0x2D8;
    private const uint MinimumInternalIoThreadStackSize = 0x4000;
    private const int GuestBufferProbeSize = 4096;

    private static readonly object StateGate = new();
    private static bool _initialized;
    private static ulong _retainedBufferAddress;
    private static int _retainedBufferSize;
    private static ulong _eventHandlerAddress;
    private static ulong _eventHandlerUserData;
    private static bool _internalIoThreadEnabled;
    private static uint _internalIoThreadStackSize;
    private static int _internalIoThreadPriority;

    [SysAbiExport(
        Nid = "amuBfI-AQc4",
        ExportName = "sceRudpInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int Init(CpuContext ctx)
    {
        var bufferAddress = ctx[CpuRegister.Rdi];
        var bufferSize = unchecked((int)ctx[CpuRegister.Rsi]);

        lock (StateGate)
        {
            if (_initialized)
            {
                return SetReturn(ctx, ErrorAlreadyInitialized);
            }

            ClearRetainedState();
            if (bufferAddress == 0 || bufferSize < 1)
            {
                return SetReturn(ctx, ErrorInvalidArgument);
            }

            if (bufferSize < MinimumAllocatorStorageSize ||
                !IsGuestBufferAvailable(ctx, bufferAddress, bufferSize))
            {
                return SetReturn(ctx, ErrorOutOfMemory);
            }

            // Firmware constructs its allocator and RUDP objects in this
            // caller-owned region, so retain it for the initialized lifetime.
            _retainedBufferAddress = bufferAddress;
            _retainedBufferSize = bufferSize;
            _initialized = true;
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "SUEVes8gvmw",
        ExportName = "sceRudpSetEventHandler",
        Target = Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int SetEventHandler(CpuContext ctx)
    {
        var handlerAddress = ctx[CpuRegister.Rdi];
        var userData = ctx[CpuRegister.Rsi];

        lock (StateGate)
        {
            if (!_initialized)
            {
                return SetReturn(ctx, ErrorNotInitialized);
            }

            if (handlerAddress == 0)
            {
                return SetReturn(ctx, ErrorInvalidEventHandler);
            }

            _eventHandlerAddress = handlerAddress;
            _eventHandlerUserData = userData;
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "6PBNpsgyaxw",
        ExportName = "sceRudpEnableInternalIOThread",
        Target = Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int EnableInternalIoThread(CpuContext ctx)
    {
        var requestedStackSize = unchecked((uint)ctx[CpuRegister.Rdi]);
        var priority = unchecked((int)ctx[CpuRegister.Rsi]);

        lock (StateGate)
        {
            if (!_initialized)
            {
                return SetReturn(ctx, ErrorNotInitialized);
            }

            if (_internalIoThreadEnabled)
            {
                return SetReturn(ctx, ErrorInternalIoThreadAlreadyEnabled);
            }

            // Retain the firmware worker configuration without starting an
            // idle host thread before implemented contexts can consume it.
            _internalIoThreadStackSize = Math.Max(
                requestedStackSize,
                MinimumInternalIoThreadStackSize);
            _internalIoThreadPriority = priority;
            _internalIoThreadEnabled = true;
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "3hBvwqEwqj8",
        ExportName = "sceRudpEnd",
        Target = Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int End(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return SetReturn(ctx, ErrorNotInitialized);
            }

            ClearRetainedState();
            return SetReturn(ctx, 0);
        }
    }

    internal static void ResetRuntimeState()
    {
        lock (StateGate)
        {
            ClearRetainedState();
        }
    }

    internal static (bool Initialized, ulong BufferAddress, int BufferSize)
        GetStateForTests()
    {
        lock (StateGate)
        {
            return (_initialized, _retainedBufferAddress, _retainedBufferSize);
        }
    }

    internal static (ulong HandlerAddress, ulong UserData)
        GetEventHandlerStateForTests()
    {
        lock (StateGate)
        {
            return (_eventHandlerAddress, _eventHandlerUserData);
        }
    }

    internal static (bool Enabled, uint StackSize, int Priority)
        GetInternalIoThreadStateForTests()
    {
        lock (StateGate)
        {
            return (
                _internalIoThreadEnabled,
                _internalIoThreadStackSize,
                _internalIoThreadPriority);
        }
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        // Firmware returns through EAX, zero-extending the 32-bit status.
        ctx[CpuRegister.Rax] = unchecked((uint)result);
        return result;
    }

    private static bool IsGuestBufferAvailable(
        CpuContext ctx,
        ulong bufferAddress,
        int bufferSize)
    {
        var byteCount = (ulong)bufferSize;
        if (bufferAddress > ulong.MaxValue - (byteCount - 1))
        {
            return false;
        }

        Span<byte> probe = stackalloc byte[GuestBufferProbeSize];
        for (ulong offset = 0; offset < byteCount;)
        {
            var length = (int)Math.Min(
                (ulong)GuestBufferProbeSize,
                byteCount - offset);
            var chunk = probe[..length];
            var address = bufferAddress + offset;
            if (!ctx.Memory.TryRead(address, chunk) ||
                !ctx.Memory.TryWrite(address, chunk))
            {
                return false;
            }

            offset += (ulong)length;
        }

        return true;
    }

    private static void ClearRetainedState()
    {
        _initialized = false;
        _retainedBufferAddress = 0;
        _retainedBufferSize = 0;
        _eventHandlerAddress = 0;
        _eventHandlerUserData = 0;
        _internalIoThreadEnabled = false;
        _internalIoThreadStackSize = 0;
        _internalIoThreadPriority = 0;
    }
}
