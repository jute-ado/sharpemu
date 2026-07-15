// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class SecureStringCopyTests
{
    private const ulong DestinationAddress = 0x1000;
    private const ulong SourceAddress = 0x2000;

    [Fact]
    public void StrcpySCopiesTerminatedString()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 6).ToArray();
        var context = CreateContext(destination, "hello");
        SetStrcpyArguments(context, destinationSize: 6, SourceAddress);

        var result = KernelMemoryCompatExports.StrcpyS(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal("hello\0", Encoding.UTF8.GetString(destination));
    }

    [Fact]
    public void StrcpySClearsDestinationWhenSourceDoesNotFit()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 4).ToArray();
        var context = CreateContext(destination, "hello");
        SetStrcpyArguments(context, destinationSize: 4, SourceAddress);

        var result = KernelMemoryCompatExports.StrcpyS(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(34UL, context[CpuRegister.Rax]);
        Assert.Equal(0, destination[0]);
        Assert.All(destination[1..], value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void StrcpySRejectsNullSourceAndClearsDestination()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 4).ToArray();
        var context = CreateContext(destination, "unused");
        SetStrcpyArguments(context, destinationSize: 4, source: 0);

        var result = KernelMemoryCompatExports.StrcpyS(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(22UL, context[CpuRegister.Rax]);
        Assert.Equal(0, destination[0]);
    }

    [Fact]
    public void StrncpySCopiesCountBytesAndTerminates()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 5).ToArray();
        var context = CreateContext(destination, "abcdef");
        SetStrncpyArguments(context, destinationSize: 5, SourceAddress, count: 3);

        var result = KernelMemoryCompatExports.StrncpyS(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(new byte[] { (byte)'a', (byte)'b', (byte)'c', 0, 0xCC }, destination);
    }

    [Fact]
    public void StrncpySTruncateModeFillsDestinationAndReportsTruncation()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 5).ToArray();
        var context = CreateContext(destination, "abcdef");
        SetStrncpyArguments(context, destinationSize: 5, SourceAddress, count: ulong.MaxValue);

        var result = KernelMemoryCompatExports.StrncpyS(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(80UL, context[CpuRegister.Rax]);
        Assert.Equal("abcd\0", Encoding.UTF8.GetString(destination));
    }

    [Fact]
    public void StrncpySTruncateModeRecognizesEmptyStringInOneByteDestination()
    {
        var destination = new byte[] { 0xCC };
        var context = CreateContext(destination, string.Empty);
        SetStrncpyArguments(context, destinationSize: 1, SourceAddress, count: ulong.MaxValue);

        var result = KernelMemoryCompatExports.StrncpyS(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, destination[0]);
    }

    [Fact]
    public void StrncpySCopiesShorterTerminatedSourceWhenCountIsLarger()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 5).ToArray();
        var context = CreateContext(destination, "abc");
        SetStrncpyArguments(context, destinationSize: 5, SourceAddress, count: 10);

        var result = KernelMemoryCompatExports.StrncpyS(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(new byte[] { (byte)'a', (byte)'b', (byte)'c', 0, 0xCC }, destination);
    }

    [Fact]
    public void StrncpySClearsDestinationWhenCountCannotFit()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 5).ToArray();
        var context = CreateContext(destination, "abcdef");
        SetStrncpyArguments(context, destinationSize: 5, SourceAddress, count: 5);

        var result = KernelMemoryCompatExports.StrncpyS(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(34UL, context[CpuRegister.Rax]);
        Assert.Equal(0, destination[0]);
        Assert.All(destination[1..], value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void StrncpySZeroCountOnlyWritesTerminator()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 4).ToArray();
        var context = CreateContext(destination, "abcdef");
        SetStrncpyArguments(context, destinationSize: 4, SourceAddress, count: 0);

        var result = KernelMemoryCompatExports.StrncpyS(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, destination[0]);
        Assert.All(destination[1..], value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void StrncpySWritesTrackedLibcDestinationUsedByGuestRuntime()
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(SourceAddress, Encoding.UTF8.GetBytes("runtime-path\0"));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x400;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.Malloc(context));
        var destination = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, destination);

        try
        {
            context[CpuRegister.Rdi] = destination;
            context[CpuRegister.Rsi] = 0x400;
            context[CpuRegister.Rdx] = SourceAddress;
            context[CpuRegister.Rcx] = 0x3FF;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.StrncpyS(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            var copied = new byte[13];
            Marshal.Copy((nint)destination, copied, 0, copied.Length);
            Assert.Equal("runtime-path\0", Encoding.UTF8.GetString(copied));
        }
        finally
        {
            context[CpuRegister.Rdi] = destination;
            _ = KernelMemoryCompatExports.Free(context);
        }
    }

    [Fact]
    public void SecureCopiesReturnMemoryFaultForUnreadableSourceOrDestination()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 8).ToArray();
        var context = CreateContext(destination, "hello");
        SetStrcpyArguments(context, destinationSize: 8, source: 0xDEAD_0000);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.StrcpyS(context));
        Assert.Equal(0, destination[0]);

        context[CpuRegister.Rdi] = 0xDEAD_0000;
        context[CpuRegister.Rsi] = 8;
        context[CpuRegister.Rdx] = SourceAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.StrcpyS(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecureCopyRejectsSourceAddressWrapAndClearsDestination(bool bounded)
    {
        var destination = Enumerable.Repeat((byte)0xCC, 4).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(DestinationAddress, destination);
        memory.AddRegion(ulong.MaxValue, [(byte)'A']);
        memory.AddRegion(0, [0]);
        var context = new CpuContext(memory, Generation.Gen5);

        if (bounded)
        {
            SetStrncpyArguments(context, destinationSize: 4, ulong.MaxValue, count: 2);
        }
        else
        {
            SetStrcpyArguments(context, destinationSize: 4, ulong.MaxValue);
        }

        var result = bounded
            ? KernelMemoryCompatExports.StrncpyS(context)
            : KernelMemoryCompatExports.StrcpyS(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(0, destination[0]);
        Assert.All(destination[1..], value => Assert.Equal(0xCC, value));
    }

    [Fact]
    public void StrncpySCanCopyFinalAddressSpaceByteWhenCountIsOne()
    {
        var destination = Enumerable.Repeat((byte)0xCC, 2).ToArray();
        var memory = new FakeGuestMemory();
        memory.AddRegion(DestinationAddress, destination);
        memory.AddRegion(ulong.MaxValue, [(byte)'A']);
        var context = new CpuContext(memory, Generation.Gen5);
        SetStrncpyArguments(context, destinationSize: 2, ulong.MaxValue, count: 1);

        Assert.Equal(0, KernelMemoryCompatExports.StrncpyS(context));
        Assert.Equal(0uL, context[CpuRegister.Rax]);
        Assert.Equal([(byte)'A', 0], destination);
    }

    [Theory]
    [InlineData(nameof(KernelMemoryCompatExports.StrcpyS), "5Xa2ACNECdo", "strcpy_s")]
    [InlineData(nameof(KernelMemoryCompatExports.StrncpyS), "YNzNkJzYqEg", "strncpy_s")]
    public void SecureCopyExportMetadataIsExact(string methodName, string nid, string exportName)
    {
        var method = typeof(KernelMemoryCompatExports).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static);
        var attribute = method?.GetCustomAttribute<SysAbiExportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(nid, attribute.Nid);
        Assert.Equal(exportName, attribute.ExportName);
        Assert.Equal("libc", attribute.LibraryName);
        Assert.Equal(Generation.Gen4 | Generation.Gen5, attribute.Target);
    }

    private static CpuContext CreateContext(byte[] destination, string source)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(DestinationAddress, destination);
        memory.AddRegion(SourceAddress, Encoding.UTF8.GetBytes(source + "\0"));
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void SetStrcpyArguments(CpuContext context, ulong destinationSize, ulong source)
    {
        context[CpuRegister.Rdi] = DestinationAddress;
        context[CpuRegister.Rsi] = destinationSize;
        context[CpuRegister.Rdx] = source;
    }

    private static void SetStrncpyArguments(
        CpuContext context,
        ulong destinationSize,
        ulong source,
        ulong count)
    {
        SetStrcpyArguments(context, destinationSize, source);
        context[CpuRegister.Rcx] = count;
    }
}
