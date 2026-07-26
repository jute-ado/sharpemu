// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection(AudioOutSessionStateCollection.Name)]
public sealed class AudioOutProgressTests : IDisposable
{
    private const ulong SamplesAddress = 0x0010_0000;
    private const int BufferFrames = 256;
    private const int StereoPcm16ByteLength = BufferFrames * 2 * sizeof(short);

    private readonly FakeGuestMemory _memory = new();
    private readonly CpuContext _context;
    private readonly List<string> _progressMessages = [];

    public AudioOutProgressTests()
    {
        AudioOutLifecycle.ResetRuntimeState();
        AudioOutExports.SetProgressLoggerForTests(_progressMessages.Add);
        _memory.AddRegion(SamplesAddress, new byte[StereoPcm16ByteLength]);
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void OutputReportsFirstNonSilentBufferOnce()
    {
        var handle = OpenPort();

        Assert.Equal(0, Submit(handle));
        Assert.Empty(_progressMessages);

        WriteSample(1234);
        Assert.Equal(0, Submit(handle));
        WriteSample(2345);
        Assert.Equal(0, Submit(handle));

        Assert.Equal(
            ["[LOADER][INFO] AudioOut non-silent samples submitted: handle=1 backend=silent"],
            _progressMessages);
    }

    public void Dispose()
    {
        AudioOutExports.SetProgressLoggerForTests(null);
        AudioOutLifecycle.ResetRuntimeState();
    }

    private int OpenPort()
    {
        _context[CpuRegister.Rdi] = 1;
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = 0;
        _context[CpuRegister.Rcx] = BufferFrames;
        _context[CpuRegister.R8] = 48_000;
        _context[CpuRegister.R9] = 1;
        return AudioOutExports.AudioOutOpen(_context);
    }

    private int Submit(int handle)
    {
        _context[CpuRegister.Rdi] = unchecked((ulong)handle);
        _context[CpuRegister.Rsi] = SamplesAddress;
        return AudioOutExports.AudioOutOutput(_context);
    }

    private void WriteSample(short value)
    {
        Span<byte> sample = stackalloc byte[sizeof(short)];
        BinaryPrimitives.WriteInt16LittleEndian(sample, value);
        Assert.True(_memory.TryWrite(SamplesAddress, sample));
    }
}
