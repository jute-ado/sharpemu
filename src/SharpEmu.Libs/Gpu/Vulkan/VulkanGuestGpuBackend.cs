// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

/// <summary>
/// Vulkan backend for the guest-GPU seam: SPIR-V codegen via
/// SharpEmu.ShaderCompiler.Vulkan, rendering via a thin adapter over the existing
/// VulkanVideoPresenter statics (folding the presenter into an instance type is
/// follow-up work, not a seam concern).
/// </summary>
internal sealed class VulkanGuestGpuBackend : IGuestGpuBackend
{
    public bool TryCompileVertexShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        out IGuestCompiledShader? shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1)
    {
        shader = null;
        if (!Gen5SpirvTranslator.TryCompileVertexShader(
                state,
                evaluation,
                out var compiled,
                out error,
                globalBufferBase,
                totalGlobalBufferCount,
                imageBindingBase,
                scalarRegisterBufferIndex))
        {
            return false;
        }

        shader = new VulkanCompiledGuestShader(compiled.Spirv);
        return true;
    }

    public bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        IReadOnlyList<Gen5PixelOutputBinding> outputs,
        out IGuestCompiledShader? shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1)
    {
        shader = null;
        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                outputs,
                out var compiled,
                out error,
                globalBufferBase,
                totalGlobalBufferCount,
                imageBindingBase,
                scalarRegisterBufferIndex))
        {
            return false;
        }

        shader = new VulkanCompiledGuestShader(compiled.Spirv);
        return true;
    }

    public bool TryCompileComputeShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out IGuestCompiledShader? shader,
        out string error,
        uint waveLaneCount = 32,
        int totalGlobalBufferCount = -1,
        int scalarRegisterBufferIndex = -1)
    {
        shader = null;
        if (!Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var compiled,
                out error,
                waveLaneCount,
                totalGlobalBufferCount,
                scalarRegisterBufferIndex))
        {
            return false;
        }

        shader = new VulkanCompiledGuestShader(compiled.Spirv);
        return true;
    }

    public void EnsureStarted(uint width, uint height) =>
        VulkanVideoPresenter.EnsureStarted(width, height);

    public void HideSplashScreen() =>
        VulkanVideoPresenter.HideSplashScreen();

    public void Submit(byte[] bgraFrame, uint width, uint height) =>
        VulkanVideoPresenter.Submit(bgraFrame, width, height);

    public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height) =>
        VulkanVideoPresenter.SubmitGuestDraw(drawKind, width, height);

    public void SubmitTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint width,
        uint height,
        uint attributeCount,
        IGuestCompiledShader? vertexShader = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null,
        GuestShaderIdentity shaderIdentity = default,
        uint firstVertex = 0,
        int vertexOffset = 0,
        uint firstInstance = 0) =>
        VulkanVideoPresenter.SubmitTranslatedDraw(
            Spirv(pixelShader),
            textures,
            globalMemoryBuffers,
            width,
            height,
            attributeCount,
            vertexShader is null ? null : Spirv(vertexShader),
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState,
            shaderIdentity,
            firstVertex,
            vertexOffset,
            firstInstance);

    public void SubmitOffscreenTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        IReadOnlyList<GuestRenderTarget> targets,
        IGuestCompiledShader? vertexShader = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null,
        GuestDepthTarget? depthTarget = null,
        GuestShaderIdentity shaderIdentity = default,
        uint firstVertex = 0,
        int vertexOffset = 0,
        uint firstInstance = 0) =>
        VulkanVideoPresenter.SubmitOffscreenTranslatedDraw(
            Spirv(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            targets,
            vertexShader is null ? null : Spirv(vertexShader),
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState,
            depthTarget,
            shaderIdentity,
            firstVertex,
            vertexOffset,
            firstInstance);

    public void SubmitStorageTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height) =>
        VulkanVideoPresenter.SubmitStorageTranslatedDraw(
            Spirv(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            width,
            height);

    public long SubmitComputeDispatch(
        ulong shaderAddress,
        IGuestCompiledShader computeShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint baseGroupX = 0,
        uint baseGroupY = 0,
        uint baseGroupZ = 0,
        uint threadCountX = uint.MaxValue,
        uint threadCountY = uint.MaxValue,
        uint threadCountZ = uint.MaxValue) =>
        VulkanVideoPresenter.SubmitComputeDispatch(
            shaderAddress,
            Spirv(computeShader),
            textures,
            globalMemoryBuffers,
            groupCountX,
            groupCountY,
            groupCountZ,
            baseGroupX,
            baseGroupY,
            baseGroupZ,
            threadCountX,
            threadCountY,
            threadCountZ);

    public bool WaitForGuestWork(long workSequence, TimeSpan timeout) =>
        VulkanVideoPresenter.WaitForGuestWork(workSequence, timeout);

    public bool TrySubmitGuestImage(
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel) =>
        VulkanVideoPresenter.TrySubmitGuestImage(address, width, height, pitchInPixel);

    public void RegisterKnownDisplayBuffer(GuestDisplayBuffer buffer) =>
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(buffer);

    public void UnregisterKnownDisplayBuffer(ulong address) =>
        VulkanVideoPresenter.UnregisterKnownDisplayBuffer(address);

    public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) =>
        VulkanVideoPresenter.IsGpuGuestImageAvailable(address, format, numberType);

    public bool TrySubmitGuestImageBlit(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        uint sourceFormat,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        uint destinationFormat) =>
        VulkanVideoPresenter.TrySubmitGuestImageBlit(
            sourceAddress,
            sourceWidth,
            sourceHeight,
            sourceFormat,
            destinationAddress,
            destinationWidth,
            destinationHeight,
            destinationFormat);

    public bool TryGetRenderTargetOutputKind(uint dataFormat, uint numberType, out Gen5PixelOutputKind outputKind)
    {
        if (VulkanVideoPresenter.TryDecodeRenderTargetFormat(dataFormat, numberType, out var format))
        {
            outputKind = format.OutputKind;
            return true;
        }

        outputKind = default;
        return false;
    }

    private static byte[] Spirv(IGuestCompiledShader shader) =>
        shader is VulkanCompiledGuestShader vulkanShader
            ? vulkanShader.Spirv
            : throw new InvalidOperationException(
                $"shader handle of type {shader.GetType().Name} was not compiled by the Vulkan backend");
}
