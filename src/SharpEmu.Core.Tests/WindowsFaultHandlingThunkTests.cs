// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native.Windows;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class WindowsFaultHandlingThunkTests
{
    private delegate int ExceptionCallback(nint exceptionPointers);
    private static int _callbackCalls;

    [WindowsX64Theory]
    [InlineData(WindowsFaultCodes.AccessViolation)]
    [InlineData(WindowsFaultCodes.Breakpoint)]
    [InlineData(WindowsFaultCodes.IllegalInstruction)]
    [InlineData(WindowsFaultCodes.ClrManagedException)]
    [InlineData(0xE06D7363u)]
    [InlineData(WindowsFaultCodes.FastFail)]
    [InlineData(WindowsFaultCodes.StackOverflow)]
    public unsafe void EmittedThunkPassesHostStackFaultToNextHandler(
        uint exceptionCode)
    {
        var callback = new ExceptionCallback(RecordCallback);
        var faultHandling = new WindowsFaultHandling(TestHostMemory.Create());
        var thunk = faultHandling.CreateHandlerThunk(
            Marshal.GetFunctionPointerForDelegate(callback),
            hostRspSwitchTlsSlot: 0,
            tlsGetValueAddress: 0);
        Assert.NotEqual(0, thunk);
        try
        {
            _callbackCalls = 0;
            nint* exceptionRecord = stackalloc nint[2];
            *(uint*)exceptionRecord = exceptionCode;
            nint* exceptionPointers = stackalloc nint[2];
            exceptionPointers[0] = (nint)exceptionRecord;
            exceptionPointers[1] = 0;

            var result = ((delegate* unmanaged<nint, int>)thunk)(
                (nint)exceptionPointers);

            Assert.Equal(0, result);
            Assert.Equal(0, _callbackCalls);
        }
        finally
        {
            faultHandling.FreeThunk(thunk);
            GC.KeepAlive(callback);
        }
    }

    private static int RecordCallback(nint exceptionPointers)
    {
        _ = exceptionPointers;
        Interlocked.Increment(ref _callbackCalls);
        return -1;
    }
}
