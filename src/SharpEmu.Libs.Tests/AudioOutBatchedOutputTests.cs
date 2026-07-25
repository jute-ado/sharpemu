// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection(AudioOutSessionStateCollection.Name)]
public sealed class AudioOutBatchedOutputTests : IDisposable
{
    private const ulong ParametersAddress = 0x0010_0000;
    private const ulong FirstSamplesAddress = 0x0011_0000;
    private const ulong SecondSamplesAddress = 0x0012_0000;
    private const int BufferFrames = 256;
    private const int StereoPcm16ByteLength = BufferFrames * 2 * sizeof(short);

    private readonly FakeGuestMemory _memory = new();
    private readonly CpuContext _context;
    private readonly List<RecordingAudioStream> _streams = [];
    private readonly List<string> _progressMessages = [];

    public AudioOutBatchedOutputTests()
    {
        AudioOutLifecycle.ResetRuntimeState();
        AudioOutExports.SetStreamFactoryForTests(_ =>
        {
            var stream = new RecordingAudioStream();
            _streams.Add(stream);
            return stream;
        });
        AudioOutExports.SetProgressLoggerForTests(_progressMessages.Add);
        _memory.AddRegion(ParametersAddress, new byte[25 * 16]);
        _memory.AddRegion(FirstSamplesAddress, new byte[StereoPcm16ByteLength]);
        _memory.AddRegion(SecondSamplesAddress, new byte[StereoPcm16ByteLength]);
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void OutputsStagesEveryBufferBeforeSubmittingBatch()
    {
        var firstHandle = OpenPort();
        var secondHandle = OpenPort();
        var firstSamples = CreateSamples(1);
        var secondSamples = CreateSamples(17);
        Assert.True(_memory.TryWrite(FirstSamplesAddress, firstSamples));
        Assert.True(_memory.TryWrite(SecondSamplesAddress, secondSamples));
        WriteDescriptor(0, secondHandle, FirstSamplesAddress);
        WriteDescriptor(1, firstHandle, SecondSamplesAddress);

        Assert.Equal(BufferFrames, Submit(2));

        Assert.Equal(secondSamples, Assert.Single(_streams[0].Submissions));
        Assert.Equal(firstSamples, Assert.Single(_streams[1].Submissions));
    }

    [Fact]
    public void OutputsDoesNotPartiallySubmitWhenLaterBufferIsUnreadable()
    {
        var firstHandle = OpenPort();
        var secondHandle = OpenPort();
        Assert.True(_memory.TryWrite(FirstSamplesAddress, new byte[StereoPcm16ByteLength]));
        WriteDescriptor(0, firstHandle, FirstSamplesAddress);
        WriteDescriptor(1, secondHandle, 0xDEAD_0000);

        Assert.Equal(AudioOutExports.AudioOutErrorInvalidPointer, Submit(2));
        Assert.All(_streams, stream => Assert.Empty(stream.Submissions));
    }

    [Fact]
    public void OutputsReportsNonSilentSamplesOnlyAfterWholeBatchIsReadable()
    {
        var firstHandle = OpenPort();
        var secondHandle = OpenPort();
        Assert.True(_memory.TryWrite(FirstSamplesAddress, CreateSamples(1)));
        WriteDescriptor(0, firstHandle, FirstSamplesAddress);
        WriteDescriptor(1, secondHandle, 0xDEAD_0000);

        Assert.Equal(AudioOutExports.AudioOutErrorInvalidPointer, Submit(2));
        Assert.Empty(_progressMessages);

        Assert.True(_memory.TryWrite(
            SecondSamplesAddress,
            new byte[StereoPcm16ByteLength]));
        WriteDescriptor(1, secondHandle, SecondSamplesAddress);
        Assert.Equal(BufferFrames, Submit(2));

        Assert.Equal(
            ["[LOADER][INFO] AudioOut non-silent samples submitted: handle=1 backend=host"],
            _progressMessages);
    }

    [Fact]
    public void OutputsAcceptsNullSampleBufferAsSynchronizationOnly()
    {
        var handle = OpenPort();
        WriteDescriptor(0, handle, 0);

        Assert.Equal(BufferFrames, Submit(1));
        Assert.Empty(Assert.Single(_streams).Submissions);
    }

    [Fact]
    public void OutputsRejectsDuplicateHandlesAndMismatchedBufferLengths()
    {
        var firstHandle = OpenPort();
        WriteDescriptor(0, firstHandle, FirstSamplesAddress);
        WriteDescriptor(1, firstHandle, SecondSamplesAddress);
        Assert.Equal(AudioOutExports.AudioOutErrorInvalidPort, Submit(2));

        var secondHandle = OpenPort(bufferFrames: 512);
        WriteDescriptor(1, secondHandle, SecondSamplesAddress);
        Assert.Equal(AudioOutExports.AudioOutErrorInvalidSize, Submit(2));
        Assert.All(_streams, stream => Assert.Empty(stream.Submissions));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(26u)]
    public void OutputsRejectsInvalidCounts(uint count)
    {
        Assert.Equal(AudioOutExports.AudioOutErrorPortFull, Submit(count));
    }

    [Fact]
    public void OutputsRejectsNullOrUnreadableDescriptorArray()
    {
        Assert.Equal(
            AudioOutExports.AudioOutErrorInvalidPointer,
            Submit(1, parameterAddress: 0));
        Assert.Equal(
            AudioOutExports.AudioOutErrorInvalidPointer,
            Submit(1, parameterAddress: 0xDEAD_0000));
    }

    [Fact]
    public void OutputsRegistersForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(
                SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport("w3PdaSTSwGE", out var export));
            Assert.Equal("sceAudioOutOutputs", export.Name);
            Assert.Equal("libSceAudioOut", export.LibraryName);
        }
    }

    public void Dispose()
    {
        AudioOutExports.SetProgressLoggerForTests(null);
        AudioOutExports.SetStreamFactoryForTests(null);
        AudioOutLifecycle.ResetRuntimeState();
    }

    private int OpenPort(int bufferFrames = BufferFrames)
    {
        _context[CpuRegister.Rdi] = 1;
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = 0;
        _context[CpuRegister.Rcx] = unchecked((uint)bufferFrames);
        _context[CpuRegister.R8] = 48_000;
        _context[CpuRegister.R9] = 1;
        return AudioOutExports.AudioOutOpen(_context);
    }

    private int Submit(uint count, ulong parameterAddress = ParametersAddress)
    {
        _context[CpuRegister.Rdi] = parameterAddress;
        _context[CpuRegister.Rsi] = count;
        return AudioOutExports.AudioOutOutputs(_context);
    }

    private void WriteDescriptor(int index, int handle, ulong sourceAddress)
    {
        Span<byte> descriptor = stackalloc byte[16];
        descriptor.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(descriptor, handle);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[8..], sourceAddress);
        Assert.True(_memory.TryWrite(
            ParametersAddress + unchecked((ulong)(index * descriptor.Length)),
            descriptor));
    }

    private static byte[] CreateSamples(byte seed)
    {
        var samples = new byte[StereoPcm16ByteLength];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = unchecked((byte)(seed + i));
        }

        return samples;
    }

    private sealed class RecordingAudioStream : IHostAudioStream
    {
        public List<byte[]> Submissions { get; } = [];

        public bool Submit(ReadOnlySpan<byte> stereoPcm16)
        {
            Submissions.Add(stereoPcm16.ToArray());
            return true;
        }

        public void Dispose()
        {
        }
    }
}
