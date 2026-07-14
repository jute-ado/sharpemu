// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.LibcStdio;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcStdioFgetsTests
{
    private const ulong DestinationAddress = 0x1000;
    private const byte Canary = 0xA5;

    [Fact]
    public void ReadLineHandlesMaximumGuestCountWithSmallOutput()
    {
        var fixture = CreateFixture(16, "ok\nrest");

        AssertSuccess(
            LibcStdioExports.ReadLineToGuest(
                fixture.Context,
                fixture.Stream,
                DestinationAddress,
                int.MaxValue),
            fixture.Context);

        Assert.Equal("ok\n\0", Encoding.UTF8.GetString(fixture.Output.AsSpan(0, 4)));
        Assert.All(fixture.Output.AsSpan(4).ToArray(), value => Assert.Equal(Canary, value));
        Assert.Equal(3, fixture.Stream.Position);
    }

    [Fact]
    public void ReadLineStreamsContentLargerThanOneHostChunk()
    {
        const int lineLength = 10_000;
        var line = new string('x', lineLength) + "\nrest";
        var fixture = CreateFixture(lineLength + 16, line);

        AssertSuccess(
            LibcStdioExports.ReadLineToGuest(
                fixture.Context,
                fixture.Stream,
                DestinationAddress,
                lineLength + 2),
            fixture.Context);

        Assert.All(fixture.Output.AsSpan(0, lineLength).ToArray(), value => Assert.Equal((byte)'x', value));
        Assert.Equal((byte)'\n', fixture.Output[lineLength]);
        Assert.Equal(0, fixture.Output[lineLength + 1]);
        Assert.All(
            fixture.Output.AsSpan(lineLength + 2).ToArray(),
            value => Assert.Equal(Canary, value));
        Assert.Equal(lineLength + 1, fixture.Stream.Position);
    }

    [Fact]
    public void ReadLineTruncatesWithoutConsumingTheRemainder()
    {
        var fixture = CreateFixture(16, "abcdef\nnext\n");

        AssertSuccess(
            LibcStdioExports.ReadLineToGuest(
                fixture.Context,
                fixture.Stream,
                DestinationAddress,
                maxCount: 5),
            fixture.Context);
        Assert.Equal("abcd\0", Encoding.UTF8.GetString(fixture.Output.AsSpan(0, 5)));
        Assert.Equal(4, fixture.Stream.Position);

        Array.Fill(fixture.Output, Canary);
        AssertSuccess(
            LibcStdioExports.ReadLineToGuest(
                fixture.Context,
                fixture.Stream,
                DestinationAddress,
                maxCount: 8),
            fixture.Context);
        Assert.Equal("ef\n\0", Encoding.UTF8.GetString(fixture.Output.AsSpan(0, 4)));
        Assert.All(fixture.Output.AsSpan(4).ToArray(), value => Assert.Equal(Canary, value));
        Assert.Equal(7, fixture.Stream.Position);
    }

    [Fact]
    public void ReadLineReturnsEndOfFileWithoutTouchingDestination()
    {
        var fixture = CreateFixture(8, string.Empty);

        AssertError(
            LibcStdioExports.ReadLineToGuest(
                fixture.Context,
                fixture.Stream,
                DestinationAddress,
                maxCount: 8),
            fixture.Context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        Assert.All(fixture.Output, value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void ReadLineReportsGuestMemoryFaultWithoutThrowing()
    {
        var fixture = CreateFixture(2, "abcd\n");

        AssertError(
            LibcStdioExports.ReadLineToGuest(
                fixture.Context,
                fixture.Stream,
                DestinationAddress,
                maxCount: 8),
            fixture.Context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        Assert.All(fixture.Output, value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void ReadLineRejectsTerminatorAddressOverflow()
    {
        var memory = new FakeGuestMemory();
        var topByte = new byte[1];
        var lowByte = new[] { Canary };
        memory.AddRegion(ulong.MaxValue, topByte);
        memory.AddRegion(0, lowByte);
        var context = new CpuContext(memory, Generation.Gen5);
        using var stream = new MemoryStream([(byte)'a']);

        AssertError(
            LibcStdioExports.ReadLineToGuest(
                context,
                stream,
                ulong.MaxValue,
                maxCount: 3),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        Assert.Equal((byte)'a', topByte[0]);
        Assert.Equal(Canary, lowByte[0]);
    }

    [Fact]
    public void ReadLineConvertsHostIoFailureToGuestError()
    {
        var memory = new FakeGuestMemory();
        var output = FilledBuffer(8);
        memory.AddRegion(DestinationAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        using var stream = new ThrowingReadStream();

        AssertError(
            LibcStdioExports.ReadLineToGuest(
                context,
                stream,
                DestinationAddress,
                maxCount: 8),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        Assert.All(output, value => Assert.Equal(Canary, value));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(DestinationAddress, 0)]
    [InlineData(DestinationAddress, -1)]
    public void ReadLineRejectsInvalidArguments(ulong destination, int maxCount)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        using var stream = new MemoryStream([(byte)'a']);

        AssertError(
            LibcStdioExports.ReadLineToGuest(context, stream, destination, maxCount),
            context,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    private static Fixture CreateFixture(int outputLength, string input)
    {
        var memory = new FakeGuestMemory();
        var output = FilledBuffer(outputLength);
        memory.AddRegion(DestinationAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        return new Fixture(context, stream, output);
    }

    private static byte[] FilledBuffer(int length)
    {
        var output = new byte[length];
        Array.Fill(output, Canary);
        return output;
    }

    private static void AssertSuccess(int result, CpuContext context)
    {
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(DestinationAddress, context[CpuRegister.Rax]);
    }

    private static void AssertError(
        int result,
        CpuContext context,
        OrbisGen2Result expected)
    {
        Assert.Equal((int)expected, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private sealed record Fixture(CpuContext Context, MemoryStream Stream, byte[] Output);

    private sealed class ThrowingReadStream : MemoryStream
    {
        public override int ReadByte() => throw new IOException("simulated read failure");
    }
}
