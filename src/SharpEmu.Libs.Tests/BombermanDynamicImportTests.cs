// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class BombermanDynamicImportTests : IDisposable
{
    private const string CreateFilterNid = "MsaFhR+lPE4";
    private const string DeleteHandleNid = "fIATVMo4Y1w";
    private const string RegisterCallbackNid = "fY3QqeNkF8k";
    private const string SysmoduleUnwindNid = "4fU5yvOkVG4";

    [Fact]
    public void NpWebApi2PushFilterUsesRegisteredHandleLifecycle()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var initialize = Assert.Single(exports, export => export.Nid == "+o9816YQhqQ");
        var createHandle = Assert.Single(exports, export => export.Nid == "WV1GwM32NgY");
        var createUserContext = Assert.Single(exports, export => export.Nid == "sk54bi6FtYM");
        var createFilter = Assert.Single(exports, export => export.Nid == CreateFilterNid);
        var registerCallback = Assert.Single(exports, export => export.Nid == RegisterCallbackNid);
        var deleteHandle = Assert.Single(exports, export => export.Nid == DeleteHandleNid);
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 0x1000;
        var libraryContextId = initialize.Function(context);
        Assert.True(libraryContextId > 0);

        context[CpuRegister.Rdi] = unchecked((ulong)libraryContextId);
        var handleId = createHandle.Function(context);
        Assert.True(handleId > 0);

        context[CpuRegister.Rdi] = unchecked((ulong)libraryContextId);
        context[CpuRegister.Rsi] = 1;
        var userContextId = createUserContext.Function(context);
        Assert.True(userContextId > 0);

        context[CpuRegister.Rdi] = unchecked((ulong)libraryContextId);
        context[CpuRegister.Rsi] = unchecked((ulong)handleId);
        var filterId = createFilter.Function(context);
        Assert.True(filterId > 0);

        context[CpuRegister.Rdi] = unchecked((ulong)userContextId);
        context[CpuRegister.Rsi] = unchecked((ulong)filterId);
        context[CpuRegister.Rdx] = 0x1234;
        context[CpuRegister.Rcx] = 0x5678;
        Assert.Equal(0, registerCallback.Function(context));

        context[CpuRegister.Rdi] = unchecked((ulong)libraryContextId);
        context[CpuRegister.Rsi] = unchecked((ulong)handleId);
        Assert.Equal(0, deleteHandle.Function(context));

        context[CpuRegister.Rdi] = unchecked((ulong)userContextId);
        context[CpuRegister.Rsi] = unchecked((ulong)filterId);
        Assert.True(registerCallback.Function(context) < 0);

        context[CpuRegister.Rdi] = unchecked((ulong)libraryContextId);
        context[CpuRegister.Rsi] = unchecked((ulong)handleId);
        Assert.True(createFilter.Function(context) < 0);
    }

    [Fact]
    public void SysmoduleUnwindExportUsesCanonicalKernelLayout()
    {
        const ulong moduleBase = 0x8000_1000;
        const ulong outputAddress = 0x1000;
        const ulong ehFrame = moduleBase + 0x6000;
        const ulong ehFrameSize = 0x1800;
        _ = KernelModuleRegistry.RegisterModule(
            "Bomberman.prx",
            moduleBase,
            size: 0x9000,
            entryPoint: moduleBase + 0x100,
            initEntryPoint: 0,
            ehFrameHeaderAddress: moduleBase + 0x7800,
            ehFrame,
            ehFrameSize,
            isMain: false);
        var output = new byte[0x130];
        BinaryPrimitives.WriteUInt64LittleEndian(output, 0x130);
        var memory = new FakeGuestMemory();
        memory.AddRegion(outputAddress, output);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = moduleBase + 0x400;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = outputAddress;
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == SysmoduleUnwindNid);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, export.Function(context));
        Assert.Equal(
            ehFrame,
            BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0x110)));
        Assert.Equal(
            ehFrameSize,
            BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0x118)));
    }

    [Fact]
    public void Http2TemplateOptionsRequireLiveTemplate()
    {
        string[] optionNids =
        [
            "jjFahkBPCYs",
            "B37SruheQ5Y",
            "EWcwMpbr5F8",
            "BJgi0CH7al4",
            "izvHhqgDt44",
            "XPtW45xiLHk",
            "-HIO4VT87v8",
            "YrWX+DhPHQY",
        ];
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var initialize = Assert.Single(exports, export => export.Nid == "3JCe3lCbQ8A");
        var createTemplate = Assert.Single(exports, export => export.Nid == "+wCt7fCijgk");
        var deleteTemplate = Assert.Single(exports, export => export.Nid == "pDom5-078DA");
        var options = optionNids.Select(
            nid => Assert.Single(exports, export => export.Nid == nid)).ToArray();
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0x1000;
        context[CpuRegister.Rcx] = 4;
        Assert.Equal(0, initialize.Function(context));
        var contextId = unchecked((int)context[CpuRegister.Rax]);
        Assert.True(contextId > 0);

        context[CpuRegister.Rdi] = unchecked((ulong)contextId);
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = 1;
        var templateId = createTemplate.Function(context);
        Assert.True(templateId > 0);

        foreach (var option in options)
        {
            context[CpuRegister.Rdi] = unchecked((ulong)templateId);
            context[CpuRegister.Rsi] = 1;
            Assert.Equal(0, option.Function(context));
        }

        context[CpuRegister.Rdi] = unchecked((ulong)templateId);
        Assert.Equal(0, deleteTemplate.Function(context));
        foreach (var option in options)
        {
            context[CpuRegister.Rdi] = unchecked((ulong)templateId);
            Assert.True(option.Function(context) < 0);
        }
    }

    public void Dispose()
    {
        KernelModuleRegistry.Reset();
        GC.SuppressFinalize(this);
    }
}
