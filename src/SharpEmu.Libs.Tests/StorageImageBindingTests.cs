// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class StorageImageBindingTests
{
    [Fact]
    public void ImageLoadRemainsReadOnlyWithoutMatchingWriter()
    {
        var load = CreateBinding("ImageLoad", [1u, 2u, 3u]);

        Assert.False(Gen5ShaderTranslator.RequiresStorageImage(load, [load]));
    }

    [Fact]
    public void ImageLoadUsesStorageRepresentationWithMatchingWriter()
    {
        var load = CreateBinding("ImageLoad", [1u, 2u, 3u]);
        var store = CreateBinding("ImageStore", [1u, 2u, 3u]);

        Assert.True(Gen5ShaderTranslator.RequiresStorageImage(load, [load, store]));
        Assert.True(Gen5ShaderTranslator.RequiresStorageImage(store, [load, store]));
    }

    [Fact]
    public void ImageLoadDoesNotAliasDifferentDescriptor()
    {
        var load = CreateBinding("ImageLoad", [1u, 2u, 3u]);
        var store = CreateBinding("ImageStore", [1u, 2u, 4u]);

        Assert.False(Gen5ShaderTranslator.RequiresStorageImage(load, [load, store]));
    }

    [Theory]
    [InlineData("ImageSample")]
    [InlineData("ImageSampleL")]
    [InlineData("ImageGather4")]
    [InlineData("ImageLoad")]
    [InlineData("ImageLoadMip")]
    public void ArrayReadOperationsUseArrayedImageBindings(string opcode)
    {
        Assert.True(Gen5ShaderTranslator.IsArrayedImageBinding(
            CreateBinding(opcode, [1u, 2u, 3u], isArray: true)));
    }

    [Theory]
    [InlineData("ImageStore", true)]
    [InlineData("ImageLoad", false)]
    [InlineData("ImageSample", false)]
    [InlineData("ImageGather4", false)]
    public void NonArrayReadsAndArrayStoresRemainNonArrayed(string opcode, bool isArray)
    {
        Assert.False(Gen5ShaderTranslator.IsArrayedImageBinding(
            CreateBinding(opcode, [1u, 2u, 3u], isArray)));
    }

    private static Gen5ImageBinding CreateBinding(
        string opcode,
        IReadOnlyList<uint> descriptor,
        bool isArray = false) =>
        new(
            Pc: 0,
            opcode,
            new Gen5ImageControl(
                Dmask: 1,
                VectorAddress: 0,
                AddressRegisters: [],
                VectorData: 0,
                ScalarResource: 0,
                ScalarSampler: 0,
                Dimension: 2,
                IsArray: isArray,
                Glc: false,
                Slc: false),
            descriptor,
            SamplerDescriptor: [],
            MipLevel: null);
}
