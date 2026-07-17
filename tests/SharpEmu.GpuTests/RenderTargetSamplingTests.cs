// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.GpuTests;

/// <summary>
/// End-to-end regressions for resource ordering inside the canonical Vulkan
/// presenter.
/// </summary>
public sealed class RenderTargetSamplingTests
{
    private const uint SourceWidth = 32;
    private const uint SourceHeight = 18;
    private const uint DestinationWidth = 96;
    private const uint DestinationHeight = 54;
    private const uint Rgba8DataFormat = 10;
    private const uint UnormNumberType = 0;
    private const ulong SourceAddress = 0x0010_0000;
    private const ulong FirstDisplayAddress = 0x0020_0000;
    private const ulong SecondDisplayAddress = 0x0030_0000;

    /// <summary>
    /// Verifies that a render target rewritten after an earlier sample exposes
    /// the new pixels when an indexed triangle strip composes it into a reused
    /// presentation target.
    /// </summary>
    [GpuConformanceFact]
    public void RewrittenRenderTarget_IsSampledIntoReusedPresentationTarget()
    {
        var captureDirectory = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-gpu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(captureDirectory);
        Environment.SetEnvironmentVariable(
            "SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE",
            $"0x{FirstDisplayAddress:X}@2");
        Environment.SetEnvironmentVariable(
            "SHARPEMU_GUEST_IMAGE_DUMP_DIR",
            captureDirectory);

        try
        {
            VulkanVideoPresenter.EnsureStarted(
                DestinationWidth,
                DestinationHeight);
            VulkanVideoPresenter.HideSplashScreen();

            SubmitSolid(SourceAddress, red: 1f, green: 0f, blue: 0f);
            ComposeSourceTo(FirstDisplayAddress);

            SubmitSolid(SourceAddress, red: 0f, green: 1f, blue: 0f);
            Assert.True(CopySourceTo(SecondDisplayAddress));

            // Reuse both the smaller source and an alternating presentation
            // target. This catches stale contents, missing render-to-sample
            // ordering, and failures when composition changes dimensions.
            SubmitSolid(SourceAddress, red: 0.125f, green: 0.5f, blue: 0.875f);
            Assert.True(CopySourceTo(FirstDisplayAddress));

            var capturePath = WaitForCapture(captureDirectory);
            var pixels = File.ReadAllBytes(capturePath);
            Assert.Equal(
                checked((int)(DestinationWidth * DestinationHeight * 4)),
                pixels.Length);
            AssertRgbaPixels(
                pixels,
                expectedRed: 32,
                expectedGreen: 128,
                expectedBlue: 223,
                expectedAlpha: 255);
        }
        finally
        {
            VulkanVideoPresenter.RequestClose();
            Environment.SetEnvironmentVariable(
                "SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE",
                null);
            Environment.SetEnvironmentVariable(
                "SHARPEMU_GUEST_IMAGE_DUMP_DIR",
                null);
            Directory.Delete(captureDirectory, recursive: true);
        }
    }

    private static void SubmitSolid(
        ulong address,
        float red,
        float green,
        float blue)
    {
        VulkanVideoPresenter.SubmitOffscreenTranslatedDraw(
            SpirvFixedShaders.CreateSolidFragment(red, green, blue, alpha: 1f),
            [],
            [],
            attributeCount: 0,
            new GuestRenderTarget(
                address,
                SourceWidth,
                SourceHeight,
                Rgba8DataFormat,
                UnormNumberType));
    }

    private static bool CopySourceTo(ulong destinationAddress) =>
        VulkanVideoPresenter.TrySubmitGuestImageBlit(
            SourceAddress,
            SourceWidth,
            SourceHeight,
            Rgba8DataFormat,
            destinationAddress,
            DestinationWidth,
            DestinationHeight,
            Rgba8DataFormat);

    private static void ComposeSourceTo(ulong destinationAddress)
    {
        VulkanVideoPresenter.SubmitOffscreenTranslatedDraw(
            SpirvFixedShaders.CreateCopyFragment(),
            [
                new GuestDrawTexture(
                    SourceAddress,
                    SourceWidth,
                    SourceHeight,
                    Rgba8DataFormat,
                    UnormNumberType,
                    [],
                    IsFallback: false,
                    IsStorage: false),
            ],
            [],
            attributeCount: 1,
            new GuestRenderTarget(
                destinationAddress,
                DestinationWidth,
                DestinationHeight,
                Rgba8DataFormat,
                UnormNumberType),
            vertexSpirv: CreatePassthroughVertex(),
            vertexCount: 4,
            primitiveType: 6,
            indexBuffer: new GuestIndexBuffer(
                [0, 0, 1, 0, 2, 0, 3, 0],
                Is32Bit: false),
            vertexBuffers:
            [
                CreateVertexBuffer(
                    location: 0,
                    baseAddress: 0x0040_0000,
                    (-1f, 1f),
                    (1f, 1f),
                    (-1f, -1f),
                    (1f, -1f)),
                CreateVertexBuffer(
                    location: 1,
                    baseAddress: 0x0041_0000,
                    (0f, 0f),
                    (1f, 0f),
                    (0f, 1f),
                    (1f, 1f)),
            ],
            renderState: new GuestRenderState(
                [GuestBlendState.Default],
                Scissor: null,
                new GuestViewport(
                    0,
                    DestinationHeight,
                    DestinationWidth,
                    -DestinationHeight,
                    0,
                    1)));
    }

    private static GuestVertexBuffer CreateVertexBuffer(
        uint location,
        ulong baseAddress,
        params (float X, float Y)[] vertices)
    {
        const int stride = 16;
        var data = new byte[vertices.Length * stride];
        for (var index = 0; index < vertices.Length; index++)
        {
            var offset = index * stride;
            BitConverter.TryWriteBytes(data.AsSpan(offset), vertices[index].X);
            BitConverter.TryWriteBytes(data.AsSpan(offset + sizeof(float)), vertices[index].Y);
        }

        return new GuestVertexBuffer(
            location,
            ComponentCount: 2,
            DataFormat: 11,
            NumberFormat: 7,
            baseAddress,
            Stride: stride,
            OffsetBytes: 0,
            data);
    }

    private static byte[] CreatePassthroughVertex()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec2Type = module.TypeVector(floatType, 2);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputPointer = module.TypePointer(SpirvStorageClass.Input, vec2Type);
        var outputPointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);

        var positionInput = module.AddGlobalVariable(inputPointer, SpirvStorageClass.Input);
        module.AddDecoration(positionInput, SpirvDecoration.Location, 0);
        var textureInput = module.AddGlobalVariable(inputPointer, SpirvStorageClass.Input);
        module.AddDecoration(textureInput, SpirvDecoration.Location, 1);
        var positionOutput = module.AddGlobalVariable(outputPointer, SpirvStorageClass.Output);
        module.AddDecoration(
            positionOutput,
            SpirvDecoration.BuiltIn,
            (uint)SpirvBuiltIn.Position);
        var textureOutput = module.AddGlobalVariable(outputPointer, SpirvStorageClass.Output);
        module.AddDecoration(textureOutput, SpirvDecoration.Location, 0);
        module.AddDecoration(textureOutput, SpirvDecoration.NoPerspective);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var position = module.AddInstruction(SpirvOp.Load, vec2Type, positionInput);
        var texture = module.AddInstruction(SpirvOp.Load, vec2Type, textureInput);
        var positionX = module.AddInstruction(SpirvOp.CompositeExtract, floatType, position, 0);
        var positionY = module.AddInstruction(SpirvOp.CompositeExtract, floatType, position, 1);
        var textureX = module.AddInstruction(SpirvOp.CompositeExtract, floatType, texture, 0);
        var textureY = module.AddInstruction(SpirvOp.CompositeExtract, floatType, texture, 1);
        var zero = module.ConstantFloat(floatType, 0);
        var one = module.ConstantFloat(floatType, 1);
        var clipPosition = module.AddInstruction(
            SpirvOp.CompositeConstruct,
            vec4Type,
            positionX,
            positionY,
            zero,
            one);
        var coordinates = module.AddInstruction(
            SpirvOp.CompositeConstruct,
            vec4Type,
            textureX,
            textureY,
            zero,
            one);
        module.AddStatement(SpirvOp.Store, positionOutput, clipPosition);
        module.AddStatement(SpirvOp.Store, textureOutput, coordinates);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(
            SpirvExecutionModel.Vertex,
            main,
            "main",
            [positionInput, textureInput, positionOutput, textureOutput]);
        return module.Build();
    }

    private static string WaitForCapture(string captureDirectory)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            var captures = Directory.GetFiles(captureDirectory, "*.rgba");
            var previews = Directory.GetFiles(captureDirectory, "*.bmp");
            if (captures.Length != 0 && previews.Length != 0)
            {
                Assert.Single(captures);
                Assert.Single(previews);
                return captures[0];
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            "Vulkan presenter did not capture the sampled render target " +
            "within 30 seconds.");
    }

    private static void AssertRgbaPixels(
        byte[] pixels,
        byte expectedRed,
        byte expectedGreen,
        byte expectedBlue,
        byte expectedAlpha)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            Assert.InRange(pixels[offset + 0], expectedRed - 1, expectedRed + 1);
            Assert.InRange(pixels[offset + 1], expectedGreen - 1, expectedGreen + 1);
            Assert.InRange(pixels[offset + 2], expectedBlue - 1, expectedBlue + 1);
            Assert.InRange(pixels[offset + 3], expectedAlpha - 1, expectedAlpha);
        }
    }
}
