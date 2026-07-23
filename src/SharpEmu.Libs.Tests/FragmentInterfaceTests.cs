// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class FragmentInterfaceTests
{
    [Theory]
    [InlineData(Gen5SpirvStage.Pixel, true, true)]
    [InlineData(Gen5SpirvStage.Pixel, false, false)]
    [InlineData(Gen5SpirvStage.Vertex, true, false)]
    [InlineData(Gen5SpirvStage.Compute, true, false)]
    public void IntegerFragmentInputsRequireFlatDecoration(
        Gen5SpirvStage stage,
        bool integerType,
        bool expected) =>
        Assert.Equal(
            expected,
            Gen5SpirvTranslator.RequiresFlatInput(stage, integerType));
}
