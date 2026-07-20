// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PosixSemaphoreAbiTests
{
    private const ulong MemoryBase = 0x1_1000_0000;
    private const ulong SemaphoreAddress = MemoryBase + 0x100;
    private const ulong ValueAddress = MemoryBase + 0x200;
    private const ulong ThreadPointer = MemoryBase + 0x1000;
    private const ulong ErrnoAddress = ThreadPointer + 0x40;

    [Fact]
    public void SemInitFailureReturnsMinusOneAndSetsEinval()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = (ulong)int.MaxValue + 1;

        AssertPosixFailure(context, KernelSemaphoreCompatExports.PosixSemInit(context), errno: 22);
    }

    [Fact]
    public void SemTryWaitOnEmptySemaphoreReturnsMinusOneAndSetsEagain()
    {
        var context = CreateContext();
        InitializeSemaphore(context, initialCount: 0);
        try
        {
            context[CpuRegister.Rdi] = SemaphoreAddress;

            AssertPosixFailure(context, KernelSemaphoreCompatExports.PosixSemTryWait(context), errno: 35);
        }
        finally
        {
            DestroySemaphore(context);
        }
    }

    [Fact]
    public void SemPostAtMaximumCountReturnsMinusOneAndSetsEoverflow()
    {
        var context = CreateContext();
        InitializeSemaphore(context, initialCount: int.MaxValue);
        try
        {
            context[CpuRegister.Rdi] = SemaphoreAddress;

            AssertPosixFailure(context, KernelSemaphoreCompatExports.PosixSemPost(context), errno: 84);
        }
        finally
        {
            DestroySemaphore(context);
        }
    }

    [Theory]
    [InlineData("wait")]
    [InlineData("post")]
    [InlineData("getvalue")]
    [InlineData("destroy")]
    public void InvalidSemaphoreReturnsMinusOneAndSetsEinval(string operation)
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = ValueAddress;

        var result = operation switch
        {
            "wait" => KernelSemaphoreCompatExports.PosixSemWait(context),
            "post" => KernelSemaphoreCompatExports.PosixSemPost(context),
            "getvalue" => KernelSemaphoreCompatExports.PosixSemGetValue(context),
            "destroy" => KernelSemaphoreCompatExports.PosixSemDestroy(context),
            _ => throw new InvalidOperationException(),
        };

        AssertPosixFailure(context, result, errno: 22);
    }

    private static CpuContext CreateContext()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x2000), Generation.Gen5)
        {
            FsBase = ThreadPointer,
        };
        Assert.True(context.TryWriteInt32(ErrnoAddress, 0));
        return context;
    }

    private static void InitializeSemaphore(CpuContext context, uint initialCount)
    {
        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = initialCount;
        Assert.Equal(0, KernelSemaphoreCompatExports.PosixSemInit(context));
    }

    private static void DestroySemaphore(CpuContext context)
    {
        context[CpuRegister.Rdi] = SemaphoreAddress;
        Assert.Equal(0, KernelSemaphoreCompatExports.PosixSemDestroy(context));
    }

    private static void AssertPosixFailure(CpuContext context, int result, int errno)
    {
        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        Assert.True(context.TryReadInt32(ErrnoAddress, out var actualErrno));
        Assert.Equal(errno, actualErrno);
    }
}
