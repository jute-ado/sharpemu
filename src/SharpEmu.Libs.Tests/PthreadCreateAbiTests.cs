// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PthreadCreateAbiTests
{
    private const ulong ThreadOutputAddress = 0x51_0000;
    private const ulong NameAddress = 0x52_0000;

    [Fact]
    public void PosixPthreadCreateIgnoresStaleFifthArgument()
    {
        var context = CreateContext("stale-register-value");

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelExports.PosixPthreadCreate(context));

        var identity = ReadCreatedThreadIdentity(context);
        Assert.StartsWith("Thread-", identity.Name, StringComparison.Ordinal);
        Assert.NotEqual("stale-register-value", identity.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NamedPthreadCreateVariantsConsumeFifthArgument(bool sceVariant)
    {
        var context = CreateContext("named-worker");

        var result = sceVariant
            ? KernelExports.PthreadCreate(context)
            : KernelExports.PosixPthreadCreateNameNp(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal("named-worker", ReadCreatedThreadIdentity(context).Name);
    }

    private static CpuContext CreateContext(string name)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ThreadOutputAddress, new byte[sizeof(ulong)]);
        var nameBuffer = new byte[256];
        Encoding.UTF8.GetBytes(name + '\0').CopyTo(nameBuffer, 0);
        memory.AddRegion(NameAddress, nameBuffer);
        return new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = ThreadOutputAddress,
            [CpuRegister.Rsi] = 0,
            [CpuRegister.Rdx] = 0,
            [CpuRegister.Rcx] = 0,
            [CpuRegister.R8] = NameAddress,
        };
    }

    private static KernelPthreadState.ThreadIdentity ReadCreatedThreadIdentity(
        CpuContext context)
    {
        Assert.True(context.TryReadUInt64(ThreadOutputAddress, out var handle));
        Assert.NotEqual(0UL, handle);
        Assert.True(KernelPthreadState.TryGetThreadIdentity(handle, out var identity));
        return identity;
    }
}
