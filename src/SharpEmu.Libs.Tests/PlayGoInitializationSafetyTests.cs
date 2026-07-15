// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.PlayGo;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PlayGoInitializationSafetyTests
{
    private const ulong InitParamsAddress = 0x1000;
    private const ulong BufferAddress = 0x2000;
    private const uint MinimumBufferSize = 0x20_0000;

    [Theory]
    [InlineData(InitParamsAddress)]
    [InlineData(ulong.MaxValue - 11)]
    public void InitializeAcceptsCompleteParameterStructure(ulong initParamsAddress)
    {
        var parameters = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(parameters, BufferAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(8), MinimumBufferSize);
        var memory = new FakeGuestMemory();
        memory.AddRegion(initParamsAddress, parameters);
        var context = CreateContext(memory, initParamsAddress);

        var result = PlayGoExports.PlayGoInitialize(context);
        try
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        }
        finally
        {
            if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
            {
                Assert.Equal(
                    (int)OrbisGen2Result.ORBIS_GEN2_OK,
                    PlayGoExports.PlayGoTerminate(context));
            }
        }
    }

    [Fact]
    public void InitializeRejectsWrappedBufferSizeField()
    {
        var bufferAddress = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bufferAddress, BufferAddress);
        var bufferSize = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bufferSize, MinimumBufferSize);
        var memory = new FakeGuestMemory();
        memory.AddRegion(ulong.MaxValue - 7, bufferAddress);
        memory.AddRegion(0, bufferSize);
        var context = CreateContext(memory, ulong.MaxValue - 7);

        var result = PlayGoExports.PlayGoInitialize(context);
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                PlayGoExports.PlayGoTerminate(context));
        }

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong initParamsAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = initParamsAddress;
        return context;
    }
}
