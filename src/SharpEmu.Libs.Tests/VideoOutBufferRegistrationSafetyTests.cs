// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class VideoOutBufferRegistrationSafetyTests
{
    private const ulong StackAddress = 0x1000;
    private const ulong BuffersAddress = 0x2000;
    private const ulong AttributeAddress = 0x3000;
    private const int BufferEntrySize = 0x20;
    private const int BufferAttribute2Size = 0x50;
    private const int OrbisVideoOutErrorInvalidValue = unchecked((int)0x80290001);

    [Fact]
    public void RegisterBuffers2AcceptsCompleteInputStructures()
    {
        var memory = CreateMemory();
        memory.AddRegion(BuffersAddress, new byte[BufferEntrySize]);
        memory.AddRegion(AttributeAddress, new byte[BufferAttribute2Size]);

        WithOpenPort(memory, (context, handle) =>
        {
            ConfigureRegisterBuffers2(context, handle, BuffersAddress, AttributeAddress);

            // The deliberately invalid set index is checked only after both structures
            // have been read, and keeps this unit test from starting a host presenter.
            Assert.Equal(
                OrbisVideoOutErrorInvalidValue,
                VideoOutExports.VideoOutRegisterBuffers2(context));
        });
    }

    [Fact]
    public void RegisterBuffers2RejectsWrappedAttributeStructure()
    {
        var memory = CreateMemory();
        memory.AddRegion(BuffersAddress, new byte[BufferEntrySize]);
        memory.AddRegion(ulong.MaxValue - 0x1F, new byte[0x20]);
        memory.AddRegion(0, new byte[0x30]);

        WithOpenPort(memory, (context, handle) =>
        {
            ConfigureRegisterBuffers2(
                context,
                handle,
                BuffersAddress,
                ulong.MaxValue - 0x1F);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                VideoOutExports.VideoOutRegisterBuffers2(context));
        });
    }

    [Fact]
    public void RegisterBuffers2RejectsWrappedBufferEntry()
    {
        var memory = CreateMemory();
        memory.AddRegion(ulong.MaxValue - 7, new byte[sizeof(ulong)]);
        memory.AddRegion(0, new byte[sizeof(ulong)]);
        memory.AddRegion(AttributeAddress, new byte[BufferAttribute2Size]);

        WithOpenPort(memory, (context, handle) =>
        {
            ConfigureRegisterBuffers2(
                context,
                handle,
                ulong.MaxValue - 7,
                AttributeAddress);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                VideoOutExports.VideoOutRegisterBuffers2(context));
        });
    }

    private static FakeGuestMemory CreateMemory()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(StackAddress, new byte[3 * sizeof(ulong)]);
        return memory;
    }

    private static void ConfigureRegisterBuffers2(
        CpuContext context,
        int handle,
        ulong buffersAddress,
        ulong attributeAddress)
    {
        context[CpuRegister.Rsp] = StackAddress;
        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        context[CpuRegister.Rsi] = 4;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = buffersAddress;
        context[CpuRegister.R8] = 1;
        context[CpuRegister.R9] = attributeAddress;
    }

    private static void WithOpenPort(
        FakeGuestMemory memory,
        Action<CpuContext, int> action)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        var handle = VideoOutExports.VideoOutOpen(context);
        Assert.True(handle > 0);

        try
        {
            action(context, handle);
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            _ = VideoOutExports.VideoOutClose(context);
        }
    }
}
