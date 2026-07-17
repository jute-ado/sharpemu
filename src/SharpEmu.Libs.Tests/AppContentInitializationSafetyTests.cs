// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.AppContent;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AppContentInitializationSafetyTests
{
    private const ulong InitParamAddress = 0x1000;
    private const ulong BootParamAddress = 0x2000;
    private const ulong OutputAddress = 0x3000;
    private const byte Canary = 0xA5;

    [Fact]
    public void InitializeClearsBootParameterAttributes()
    {
        var bootParameter = CreateCanaryBuffer(12);
        var memory = new FakeGuestMemory();
        memory.AddRegion(BootParamAddress, bootParameter);
        var context = CreateContext(memory, BootParamAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AppContentExports.AppContentInitialize(context));
        Assert.Equal(
            [Canary, Canary, Canary, Canary, 0, 0, 0, 0, Canary, Canary, Canary, Canary],
            bootParameter);
    }

    [Fact]
    public void InitializeRejectsWrappedBootParameterFieldWithoutWriting()
    {
        var wrappedDestination = CreateCanaryBuffer(sizeof(uint));
        var memory = new FakeGuestMemory();
        memory.AddRegion(0, wrappedDestination);
        var context = CreateContext(memory, ulong.MaxValue - 3);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AppContentExports.AppContentInitialize(context));
        Assert.All(wrappedDestination, value => Assert.Equal(Canary, value));
    }

    [Theory]
    [InlineData(0, BootParamAddress)]
    [InlineData(InitParamAddress, 0)]
    public void InitializeRejectsNullAbiObjects(
        ulong initParamAddress,
        ulong bootParamAddress)
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = initParamAddress;
        context[CpuRegister.Rsi] = bootParamAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AppContentExports.AppContentInitialize(context));
    }

    [Fact]
    public void InitializeRejectsUnmappedBootParameter()
    {
        var context = CreateContext(
            new FakeGuestMemory(),
            BootParamAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AppContentExports.AppContentInitialize(context));
    }

    [Fact]
    public void AddOnEnumerationReportsNoInstalledContent()
    {
        var hitCount = CreateCanaryBuffer(sizeof(uint));
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, hitCount);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rcx] = OutputAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AppContentExports.AppContentGetAddcontInfoList(context));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(hitCount));
    }

    [Fact]
    public void AddOnEnumerationRejectsUnmappedHitCount()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rcx] = OutputAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AppContentExports.AppContentGetAddcontInfoList(context));
    }

    [Fact]
    public void AddOnEnumerationAllowsOmittedHitCount()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AppContentExports.AppContentGetAddcontInfoList(context));
    }

    [Fact]
    public void AppParameterReportsFullSku()
    {
        var output = new byte[sizeof(int)];
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AppContentExports.AppContentAppParamGetInt(context));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(output));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void AppParameterReadsUserDefinedValueFromParamJson()
    {
        var app0Root = CreateTemporaryDirectory();
        var previousApp0Root =
            Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        try
        {
            var sceSys = Directory.CreateDirectory(
                Path.Combine(app0Root, "sce_sys"));
            File.WriteAllText(
                Path.Combine(sceSys.FullName, "param.json"),
                """{"userDefinedParam2": 712}""");
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", app0Root);

            var output = new byte[sizeof(int)];
            var memory = new FakeGuestMemory();
            memory.AddRegion(OutputAddress, output);
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = 2;
            context[CpuRegister.Rsi] = OutputAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                AppContentExports.AppContentAppParamGetInt(context));
            Assert.Equal(712, BinaryPrimitives.ReadInt32LittleEndian(output));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "SHARPEMU_APP0_DIR",
                previousApp0Root);
            Directory.Delete(app0Root, recursive: true);
        }
    }

    [Theory]
    [InlineData(5, OutputAddress)]
    [InlineData(1, 0)]
    public void AppParameterRejectsInvalidArguments(
        ulong parameterId,
        ulong outputAddress)
    {
        var output = CreateCanaryBuffer(sizeof(int));
        var memory = new FakeGuestMemory();
        memory.AddRegion(OutputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = parameterId;
        context[CpuRegister.Rsi] = outputAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AppContentExports.AppContentAppParamGetInt(context));
        Assert.All(output, value => Assert.Equal(Canary, value));
    }

    [Fact]
    public void AppParameterRejectsUnmappedOutput()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AppContentExports.AppContentAppParamGetInt(context));
    }

    [Fact]
    public void TemporaryDataMountCreatesConfiguredRootAndWritesMountPoint()
    {
        var temp0Root = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-app-content-{Guid.NewGuid():N}");
        var previousTemp0Root =
            Environment.GetEnvironmentVariable("SHARPEMU_TEMP0_DIR");
        try
        {
            Environment.SetEnvironmentVariable(
                "SHARPEMU_TEMP0_DIR",
                temp0Root);
            var output = CreateCanaryBuffer(16);
            var memory = new FakeGuestMemory();
            memory.AddRegion(OutputAddress, output);
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rsi] = OutputAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                AppContentExports.AppContentTemporaryDataMount2(context));
            Assert.True(Directory.Exists(temp0Root));
            Assert.Equal(
                "/temp0",
                Encoding.ASCII.GetString(output.AsSpan(0, 6)));
            Assert.Equal(0, output[6]);
            Assert.All(output[7..], value => Assert.Equal(Canary, value));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "SHARPEMU_TEMP0_DIR",
                previousTemp0Root);
            if (Directory.Exists(temp0Root))
            {
                Directory.Delete(temp0Root, recursive: true);
            }
        }
    }

    [Fact]
    public void TemporaryDataMountRejectsInvalidConfiguredRoot()
    {
        var previousTemp0Root =
            Environment.GetEnvironmentVariable("SHARPEMU_TEMP0_DIR");
        var filePath = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-app-content-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(filePath, "not a directory");
            Environment.SetEnvironmentVariable(
                "SHARPEMU_TEMP0_DIR",
                filePath);
            var memory = new FakeGuestMemory();
            memory.AddRegion(OutputAddress, new byte[16]);
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rsi] = OutputAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                AppContentExports.AppContentTemporaryDataMount2(context));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "SHARPEMU_TEMP0_DIR",
                previousTemp0Root);
            File.Delete(filePath);
        }
    }

    [Theory]
    [InlineData(0, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT)]
    [InlineData(
        OutputAddress,
        OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)]
    public void TemporaryDataMountRejectsInvalidGuestOutput(
        ulong outputAddress,
        OrbisGen2Result expected)
    {
        var temp0Root = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-app-content-{Guid.NewGuid():N}");
        var previousTemp0Root =
            Environment.GetEnvironmentVariable("SHARPEMU_TEMP0_DIR");
        try
        {
            Environment.SetEnvironmentVariable(
                "SHARPEMU_TEMP0_DIR",
                temp0Root);
            var context = new CpuContext(
                new FakeGuestMemory(),
                Generation.Gen5);
            context[CpuRegister.Rsi] = outputAddress;

            Assert.Equal(
                (int)expected,
                AppContentExports.AppContentTemporaryDataMount2(context));
            Assert.Equal(
                unchecked((ulong)(int)expected),
                context[CpuRegister.Rax]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "SHARPEMU_TEMP0_DIR",
                previousTemp0Root);
            if (Directory.Exists(temp0Root))
            {
                Directory.Delete(temp0Root, recursive: true);
            }
        }
    }

    private static CpuContext CreateContext(FakeGuestMemory memory, ulong bootParamAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = InitParamAddress;
        context[CpuRegister.Rsi] = bootParamAddress;
        return context;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-app-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] CreateCanaryBuffer(int length)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, Canary);
        return buffer;
    }
}
