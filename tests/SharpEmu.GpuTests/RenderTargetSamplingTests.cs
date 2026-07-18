// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
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
    private const uint Rgba8TextureDataFormat = 56;
    private const uint UnormNumberType = 0;
    private const ulong SourceAddress = 0x0010_0000;
    private const ulong FirstDisplayAddress = 0x0020_0000;
    private const ulong SecondDisplayAddress = 0x0030_0000;
    private const ulong CpuTextureAddress = 0x0042_0000;
    private const ulong ShaderAddress = 0x0050_0000;
    private const ulong ConstantsAddress = 0x0060_0000;
    private const ulong ComputeBufferAddress = 0x0070_0000;
    private const ulong DepthAddress = 0x0080_0000;

    /// <summary>
    /// Verifies both GPU render-target reuse and a large CPU-backed texture
    /// upload. The final target preserves the translated triangle-strip result
    /// on its left half and receives four distinct texture quadrants through an
    /// indexed triangle list on its right half.
    /// </summary>
    [GpuConformanceFact]
    public void RewrittenRenderTarget_AndCpuTexture_AreSampledIntoReusedPresentationTarget()
    {
        var captureDirectory = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-gpu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(captureDirectory);
        Environment.SetEnvironmentVariable(
            "SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE",
            $"0x{FirstDisplayAddress:X}@6");
        Environment.SetEnvironmentVariable(
            "SHARPEMU_GUEST_IMAGE_DUMP_DIR",
            captureDirectory);

        try
        {
            VulkanVideoPresenter.EnsureStarted(
                DestinationWidth,
                DestinationHeight);
            VulkanVideoPresenter.HideSplashScreen();
            AssertComputeBufferWriteback();

            SubmitSolid(SourceAddress, red: 1f, green: 0f, blue: 0f);
            ComposeSourceTo(FirstDisplayAddress);

            SubmitSolid(SourceAddress, red: 0f, green: 1f, blue: 0f);
            Assert.True(CopySourceTo(SecondDisplayAddress));

            // Reuse both the smaller source and an alternating presentation
            // target. This catches stale contents, missing render-to-sample
            // ordering, and failures when composition changes dimensions.
            SubmitSolid(SourceAddress, red: 0.125f, green: 0.5f, blue: 0.875f);
            Assert.True(CopySourceTo(FirstDisplayAddress));

            // Finish through generated vertex and fragment shaders. The
            // fragment uses global buffers and an aliased sampled image, which
            // protects their distinct descriptor bindings as part of the same
            // render-to-sample regression.
            var fragment = CreateTranslatedColorTransformFragment(
                out var globalMemoryBuffers);
            ComposeSourceTo(
                FirstDisplayAddress,
                fragment,
                CreateTranslatedPassthroughVertex(),
                globalMemoryBuffers,
                Rgba8TextureDataFormat);
            // Cross the presenter's in-flight submission limit before the
            // CPU-backed composition, exercising fence retirement and pooled
            // vertex/index/global buffer reuse under sustained draw traffic.
            for (var index = 0; index < 16; index++)
            {
                ComposeSourceTo(
                    SecondDisplayAddress,
                    fragment,
                    CreateTranslatedPassthroughVertex(),
                    globalMemoryBuffers,
                    Rgba8TextureDataFormat);
            }
            AssertDepthCompareAndClear(FirstDisplayAddress);
            ComposeCpuTextureToRightHalf(
                FirstDisplayAddress,
                fragment,
                CreateTranslatedPassthroughVertex(),
                globalMemoryBuffers);

            var capturePath = WaitForCapture(captureDirectory);
            var pixels = File.ReadAllBytes(capturePath);
            Assert.Equal(
                checked((int)(DestinationWidth * DestinationHeight * 4)),
                pixels.Length);
            AssertRgbaRegion(
                pixels,
                xStart: 0,
                xEnd: DestinationWidth / 8,
                expectedRed: 255,
                expectedGreen: 0,
                expectedBlue: 0,
                expectedAlpha: 255);
            AssertRgbaRegion(
                pixels,
                xStart: DestinationWidth / 8,
                xEnd: DestinationWidth / 2,
                expectedRed: 32,
                expectedGreen: 128,
                expectedBlue: 223,
                expectedAlpha: 255);
            AssertCpuTextureQuadrants(pixels);
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

    private static void AssertComputeBufferWriteback()
    {
        uint[] words =
        [
            0x7E080200,             // v_mov_b32 v4, s0 (workgroup X)
            0x7E0A0201,             // v_mov_b32 v5, s1 (threadgroup size)
            0xD5690006, 0x00020B04, // v_mul_lo_u32 v6, v4, v5
            0xD77F0007, 0x00020D00, // v_add_nc_i32 v7, v0, v6
            0x34100E82,             // v_lshlrev_b32 v8, 2, v7
            0xE0701000, 0x80020708, // buffer_store_dword v7, v8, s[8:11], 0 offen
            0xBF810000,             // s_endpgm
        ];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BitConverter.TryWriteBytes(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        var context = new CpuContext(
            new ArrayCpuMemory(ShaderAddress, bytes),
            Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out var program,
                out var decodeError),
            decodeError);
        Gen5ShaderInstruction? store = null;
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Opcode == "BufferStoreDword")
            {
                store = instruction;
                break;
            }
        }

        Assert.NotNull(store);
        var scalarRegisters = new uint[256];
        var bindingData = new byte[4 * sizeof(uint)];
        var binding = new Gen5GlobalMemoryBinding(
            ScalarAddress: 8,
            ComputeBufferAddress,
            [store.Pc],
            bindingData)
        {
            Writable = true,
        };
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            new Dictionary<uint, IReadOnlyList<uint>>(),
            [],
            [binding],
            ComputeSystemRegisters: new Gen5ComputeSystemRegisters(
                WorkGroupXRegister: 0,
                WorkGroupYRegister: null,
                WorkGroupZRegister: null,
                ThreadGroupSizeRegister: 1));
        var state = new Gen5ShaderState(program, [], Metadata: null);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                localSizeX: 4,
                localSizeY: 1,
                localSizeZ: 1,
                out var shader,
                out var compileError),
            compileError);

        var guestMemory = new ArrayCpuMemory(
            ComputeBufferAddress,
            bindingData.ToArray());
        var workSequence = VulkanVideoPresenter.SubmitComputeDispatch(
            ShaderAddress,
            shader.Spirv,
            [],
            [
                new GuestMemoryBuffer(
                    ComputeBufferAddress,
                    bindingData,
                    Writable: true,
                    guestMemory),
            ],
            groupCountX: 1,
            groupCountY: 1,
            groupCountZ: 1);

        Assert.True(
            VulkanVideoPresenter.WaitForGuestWork(
                workSequence,
                TimeSpan.FromSeconds(10)),
            $"Compute dispatch {workSequence} did not reach CPU visibility.");
        var output = new byte[bindingData.Length];
        Assert.True(guestMemory.TryRead(ComputeBufferAddress, output));
        Assert.Equal(0u, BitConverter.ToUInt32(output, 0));
        Assert.Equal(1u, BitConverter.ToUInt32(output, 4));
        Assert.Equal(2u, BitConverter.ToUInt32(output, 8));
        Assert.Equal(3u, BitConverter.ToUInt32(output, 12));
    }

    private static void AssertDepthCompareAndClear(ulong destinationAddress)
    {
        SubmitSolidWithDepth(
            SourceAddress,
            red: 1f,
            green: 0f,
            blue: 0f,
            clearDepth: true);
        SubmitSolidWithDepth(
            SourceAddress,
            red: 0f,
            green: 1f,
            blue: 0f,
            clearDepth: false);
        Assert.True(CopySourceTo(SecondDisplayAddress));

        SubmitSolidWithDepth(
            SourceAddress,
            red: 0.125f,
            green: 0.5f,
            blue: 0.875f,
            clearDepth: true);

        ComposeTextureTo(
            SecondDisplayAddress,
            DestinationWidth,
            DestinationHeight,
            destinationAddress,
            SpirvFixedShaders.CreateCopyFragment(),
            CreatePassthroughVertex(),
            destinationRegion: new GuestRect(
                X: 0,
                Y: 0,
                Width: DestinationWidth / 8,
                Height: DestinationHeight));
        ComposeTextureTo(
            SourceAddress,
            SourceWidth,
            SourceHeight,
            destinationAddress,
            SpirvFixedShaders.CreateCopyFragment(),
            CreatePassthroughVertex(),
            destinationRegion: new GuestRect(
                X: checked((int)(DestinationWidth / 8)),
                Y: 0,
                Width: DestinationWidth / 8,
                Height: DestinationHeight));
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

    private static void SubmitSolidWithDepth(
        ulong address,
        float red,
        float green,
        float blue,
        bool clearDepth)
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
                UnormNumberType),
            renderState: new GuestRenderState(
                [GuestBlendState.Default],
                Scissor: null,
                Viewport: null)
            {
                Depth = new GuestDepthState(
                    TestEnable: true,
                    WriteEnable: true,
                    CompareOp: 1,
                    ClearEnable: clearDepth),
            },
            depthTarget: new GuestDepthTarget(
                ReadAddress: DepthAddress,
                WriteAddress: DepthAddress,
                Width: SourceWidth,
                Height: SourceHeight,
                GuestFormat: 3,
                SwizzleMode: 0,
                ClearDepth: 1f,
                ReadOnly: false));
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
        => ComposeSourceTo(
            destinationAddress,
            SpirvFixedShaders.CreateCopyFragment());

    private static void ComposeSourceTo(
        ulong destinationAddress,
        byte[] fragmentSpirv)
        => ComposeSourceTo(
            destinationAddress,
            fragmentSpirv,
            CreatePassthroughVertex());

    private static void ComposeSourceTo(
        ulong destinationAddress,
        byte[] fragmentSpirv,
        byte[] vertexSpirv,
        IReadOnlyList<GuestMemoryBuffer>? globalMemoryBuffers = null,
        uint textureDataFormat = Rgba8DataFormat)
        => ComposeTextureTo(
            SourceAddress,
            SourceWidth,
            SourceHeight,
            destinationAddress,
            fragmentSpirv,
            vertexSpirv,
            globalMemoryBuffers,
            textureDataFormat);

    private static void ComposeTextureTo(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        ulong destinationAddress,
        byte[] fragmentSpirv,
        byte[] vertexSpirv,
        IReadOnlyList<GuestMemoryBuffer>? globalMemoryBuffers = null,
        uint textureDataFormat = Rgba8DataFormat,
        GuestRect? destinationRegion = null)
    {
        var region = destinationRegion ?? new GuestRect(
            X: 0,
            Y: 0,
            Width: DestinationWidth,
            Height: DestinationHeight);
        VulkanVideoPresenter.SubmitOffscreenTranslatedDraw(
            fragmentSpirv,
            [
                new GuestDrawTexture(
                    sourceAddress,
                    sourceWidth,
                    sourceHeight,
                    textureDataFormat,
                    UnormNumberType,
                    [],
                    IsFallback: false,
                    IsStorage: false),
            ],
            globalMemoryBuffers ?? [],
            attributeCount: 1,
            new GuestRenderTarget(
                destinationAddress,
                DestinationWidth,
                DestinationHeight,
                Rgba8DataFormat,
                UnormNumberType),
            vertexSpirv,
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
                Scissor: region,
                new GuestViewport(
                    region.X,
                    region.Y + region.Height,
                    region.Width,
                    -region.Height,
                    0,
                    1)));
    }

    private static void ComposeCpuTextureToRightHalf(
        ulong destinationAddress,
        byte[] fragmentSpirv,
        byte[] vertexSpirv,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers)
    {
        const uint textureWidth = 1280;
        const uint textureHeight = 720;
        var texturePixels = new byte[textureWidth * textureHeight * 4];
        for (var y = 0; y < textureHeight; y++)
        {
            for (var x = 0; x < textureWidth; x++)
            {
                var offset = checked((int)((y * textureWidth + x) * 4));
                var right = x >= textureWidth / 2;
                var bottom = y >= textureHeight / 2;
                texturePixels[offset + 0] = right == bottom ? (byte)255 : (byte)0;
                texturePixels[offset + 1] = right ? (byte)255 : (byte)0;
                texturePixels[offset + 2] = bottom && !right ? (byte)255 : (byte)0;
                texturePixels[offset + 3] = 255;
            }
        }

        VulkanVideoPresenter.SubmitOffscreenTranslatedDraw(
            fragmentSpirv,
            [
                new GuestDrawTexture(
                    CpuTextureAddress,
                    textureWidth,
                    textureHeight,
                    Rgba8TextureDataFormat,
                    UnormNumberType,
                    texturePixels,
                    IsFallback: false,
                    IsStorage: false,
                    Pitch: textureWidth,
                    Sampler: new GuestSampler(
                        Word0: 0,
                        Word1: 0x00FF_F000,
                        Word2: 0x0900_0000,
                        Word3: 0)),
            ],
            globalMemoryBuffers,
            attributeCount: 1,
            new GuestRenderTarget(
                destinationAddress,
                DestinationWidth,
                DestinationHeight,
                Rgba8DataFormat,
                UnormNumberType),
            vertexSpirv,
            vertexCount: 6,
            primitiveType: 4,
            indexBuffer: new GuestIndexBuffer(
                [
                    0, 0, 0, 0,
                    1, 0, 0, 0,
                    2, 0, 0, 0,
                    1, 0, 0, 0,
                    2, 0, 0, 0,
                    3, 0, 0, 0,
                ],
                Is32Bit: true),
            vertexBuffers:
            [
                CreateVertexBuffer(
                    location: 0,
                    baseAddress: 0x0043_0000,
                    (0f, 1f),
                    (1f, 1f),
                    (0f, -1f),
                    (1f, -1f)),
                CreateVertexBuffer(
                    location: 1,
                    baseAddress: 0x0044_0000,
                    (0f, 0f),
                    (1f, 0f),
                    (0f, 1f),
                    (1f, 1f)),
            ],
            renderState: new GuestRenderState(
                [
                    new GuestBlendState(
                        Enable: true,
                        ColorSrcFactor: 4,
                        ColorDstFactor: 5,
                        ColorFunc: 0,
                        AlphaSrcFactor: 4,
                        AlphaDstFactor: 5,
                        AlphaFunc: 0,
                        SeparateAlphaBlend: true,
                        WriteMask: 0xF),
                ],
                Scissor: null,
                new GuestViewport(
                    0,
                    DestinationHeight,
                    DestinationWidth,
                    -DestinationHeight,
                    0,
                    1)));
    }

    private static byte[] CreateTranslatedColorTransformFragment(
        out IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers)
    {
        uint[] words =
        [
            0xF42C0406, 0xFA000000, // s_buffer_load_dwordx8 s[16:23], s[12:15], 0
            0xC8100000,             // v_interp_p1_f32 v4, v0, attr0.x
            0xC8140100,             // v_interp_p1_f32 v5, v0, attr0.y
            0xC8110001,             // v_interp_p2_f32 v4, v1, attr0.x
            0xC8150101,             // v_interp_p2_f32 v5, v1, attr0.y
            0xF0800F08, 0x00400004, // image_sample v[0:3], v[4:5], s[0:7], s[8:11]
            0xBF8C0070,             // s_waitcnt
            0xD5410000, 0x00520010, // v_mad_f32 v0, s16, v0, s20
            0xD5410001, 0x00542301, // v_mad_f32 v1, v1, s17, s21
            0xD5410003, 0x005C2703, // v_mad_f32 v3, v3, s19, s23
            0xD5410002, 0x00582502, // v_mad_f32 v2, v2, s18, s22
            0x5E000300,             // v_cvt_pkrtz_f16_f32 v0, v0, v1
            0x5E020702,             // v_cvt_pkrtz_f16_f32 v1, v2, v3
            0xF8001C0F, 0x00000100, // exp mrt0 v0, v1 compr done vm
            0xBF810000,             // s_endpgm
        ];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BitConverter.TryWriteBytes(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        var context = new CpuContext(
            new ArrayCpuMemory(ShaderAddress, bytes),
            Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out var program,
                out var decodeError),
            decodeError);

        Gen5ShaderInstruction? imageInstruction = null;
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Control is Gen5ImageControl)
            {
                imageInstruction = instruction;
                break;
            }
        }

        Assert.NotNull(imageInstruction);
        var imageControl = Assert.IsType<Gen5ImageControl>(
            imageInstruction.Control);
        var constants = new byte[8 * sizeof(float)];
        for (var index = 0; index < 4; index++)
        {
            BitConverter.TryWriteBytes(
                constants.AsSpan(index * sizeof(float)),
                1f);
        }

        var scalarRegisters = new uint[256];
        var binding = new Gen5GlobalMemoryBinding(
            ScalarAddress: 12,
            ConstantsAddress,
            InstructionPcs: [0],
            constants);
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            new Dictionary<uint, IReadOnlyList<uint>>(),
            [
                new Gen5ImageBinding(
                    imageInstruction.Pc,
                    imageInstruction.Opcode,
                    imageControl,
                    new uint[8],
                    new uint[4],
                    MipLevel: null),
            ],
            [binding]);
        var state = new Gen5ShaderState(program, [], Metadata: null);
        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var shader,
                out var compileError,
                totalGlobalBufferCount: 5,
                scalarRegisterBufferIndex: 3),
            compileError);
        globalMemoryBuffers =
        [
            new GuestMemoryBuffer(ConstantsAddress, constants),
            new GuestMemoryBuffer(0, new byte[256 * sizeof(uint)]),
            new GuestMemoryBuffer(0, new byte[256 * sizeof(uint)]),
            new GuestMemoryBuffer(0, CreateScalarRegisterSnapshot(
                evaluation.InitialScalarRegisters)),
            new GuestMemoryBuffer(0, new byte[256 * sizeof(uint)]),
        ];
        return shader.Spirv;
    }

    private static byte[] CreateScalarRegisterSnapshot(
        IReadOnlyList<uint> registers)
    {
        var data = new byte[256 * sizeof(uint)];
        var count = Math.Min(registers.Count, 256);
        for (var index = 0; index < count; index++)
        {
            BitConverter.TryWriteBytes(
                data.AsSpan(index * sizeof(uint)),
                registers[index]);
        }

        return data;
    }

    private static byte[] CreateTranslatedPassthroughVertex()
    {
        uint[] words =
        [
            0xE0042000, 0x0A000000, // buffer_load_format_xy v[0:1], v0, s[0:3]
            0xE0042000, 0x0E000202, // buffer_load_format_xy v[2:3], v2, s[0:3]
            0x7E080280,             // v_mov_b32 v4, 0
            0x7E0C02F2,             // v_mov_b32 v6, 1.0
            0xF80008CF, 0x06040100, // exp pos0 v0, v1, v4, v6 done
            0x7E0E0280,             // v_mov_b32 v7, 0
            0xF800020F, 0x07070302, // exp param0 v2, v3, v7, v7
            0xBF810000,             // s_endpgm
        ];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BitConverter.TryWriteBytes(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        var context = new CpuContext(
            new ArrayCpuMemory(ShaderAddress, bytes),
            Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out var program,
                out var decodeError),
            decodeError);

        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            new Dictionary<uint, IReadOnlyList<uint>>(),
            [],
            [],
            VertexInputs:
            [
                new Gen5VertexInputBinding(
                    Pc: 0,
                    Location: 0,
                    ComponentCount: 2,
                    DataFormat: 11,
                    NumberFormat: 7,
                    BaseAddress: 0,
                    Stride: 16,
                    OffsetBytes: 0,
                    Data: []),
                new Gen5VertexInputBinding(
                    Pc: 8,
                    Location: 1,
                    ComponentCount: 2,
                    DataFormat: 11,
                    NumberFormat: 7,
                    BaseAddress: 0,
                    Stride: 16,
                    OffsetBytes: 0,
                    Data: []),
            ]);
        var state = new Gen5ShaderState(program, [], Metadata: null);
        Assert.True(
            Gen5SpirvTranslator.TryCompileVertexShader(
                state,
                evaluation,
                out var shader,
                out var compileError,
                globalBufferBase: 1,
                totalGlobalBufferCount: 5,
                scalarRegisterBufferIndex: 4),
            compileError);
        return shader.Spirv;
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

    private static void AssertRgbaRegion(
        byte[] pixels,
        uint xStart,
        uint xEnd,
        byte expectedRed,
        byte expectedGreen,
        byte expectedBlue,
        byte expectedAlpha)
    {
        for (var y = 0u; y < DestinationHeight; y++)
        {
            for (var x = xStart; x < xEnd; x++)
            {
                var offset = checked((int)((y * DestinationWidth + x) * 4));
                Assert.InRange(pixels[offset + 0], expectedRed - 1, expectedRed + 1);
                Assert.InRange(pixels[offset + 1], expectedGreen - 1, expectedGreen + 1);
                Assert.InRange(pixels[offset + 2], expectedBlue - 1, expectedBlue + 1);
                Assert.InRange(pixels[offset + 3], expectedAlpha - 1, expectedAlpha);
            }
        }
    }

    private static void AssertCpuTextureQuadrants(byte[] pixels)
    {
        var colors = new HashSet<(byte Red, byte Green, byte Blue)>
        {
            ReadRgb(pixels, DestinationWidth * 5 / 8, DestinationHeight / 4),
            ReadRgb(pixels, DestinationWidth * 7 / 8, DestinationHeight / 4),
            ReadRgb(pixels, DestinationWidth * 5 / 8, DestinationHeight * 3 / 4),
            ReadRgb(pixels, DestinationWidth * 7 / 8, DestinationHeight * 3 / 4),
        };

        Assert.Contains(((byte)255, (byte)0, (byte)0), colors);
        Assert.Contains(((byte)255, (byte)255, (byte)0), colors);
        Assert.Contains(((byte)0, (byte)0, (byte)255), colors);
        Assert.Contains(((byte)0, (byte)255, (byte)0), colors);
    }

    private static (byte Red, byte Green, byte Blue) ReadRgb(
        byte[] pixels,
        uint x,
        uint y)
    {
        var offset = checked((int)((y * DestinationWidth + x) * 4));
        return (
            pixels[offset + 0],
            pixels[offset + 1],
            pixels[offset + 2]);
    }

    private sealed class ArrayCpuMemory(
        ulong baseAddress,
        byte[] data) : ICpuMemory
    {
        public bool TryRead(
            ulong virtualAddress,
            Span<byte> destination)
        {
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var offset = virtualAddress - baseAddress;
            if (offset > (ulong)data.Length ||
                (ulong)destination.Length > (ulong)data.Length - offset)
            {
                return false;
            }

            data.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(
            ulong virtualAddress,
            ReadOnlySpan<byte> source)
        {
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var offset = virtualAddress - baseAddress;
            if (offset > (ulong)data.Length ||
                (ulong)source.Length > (ulong)data.Length - offset)
            {
                return false;
            }

            source.CopyTo(data.AsSpan((int)offset, source.Length));
            return true;
        }
    }
}
