// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Fully validated dimensions and push-constant values for one Vulkan detile
/// dispatch. Resource allocation and command recording consume this plan so
/// they cannot derive different buffer extents or layer strides.
/// </summary>
internal readonly record struct VulkanDetileDispatch(
    uint TexelWidth,
    uint TexelHeight,
    uint ElementsWide,
    uint ElementsHigh,
    uint BlockWidth,
    uint BlockHeight,
    uint BlockElements,
    uint BlocksPerRow,
    uint XMask,
    uint YMask,
    uint SourceSliceElements,
    uint Equation,
    uint UintsPerElement,
    uint GroupCountX,
    uint GroupCountY,
    uint GroupCountZ,
    ulong TiledBytesPerLayer,
    ulong OutputBytes);

/// <summary>
/// Validation and planning shared by the Vulkan detile resource and command
/// paths. Keeping this pure makes unsafe GPU buffer sizing independently
/// testable before any Vulkan object is allocated.
/// </summary>
internal static unsafe class VulkanDetilePass
{
    private const uint LocalSize = 8;
    private const uint PushConstantBytes = 11 * sizeof(uint);

    /// <summary>
    /// The compute kernel copies whole 32-bit words and therefore supports
    /// 4-, 8-, and 16-byte elements. Sub-word elements remain on the CPU.
    /// </summary>
    public static bool Supports(in DetileParams parameters) =>
        parameters.Equation is DetileEquation.ExactXor or
            DetileEquation.BlockTable &&
        parameters.BytesPerElement is 4 or 8 or 16;

    /// <summary>
    /// Validates the backend-neutral address model and exact tiled source
    /// extent, then produces all dimensions needed by the Vulkan pass.
    /// </summary>
    public static bool TryCreateDispatch(
        int tiledByteLength,
        int texelWidth,
        int texelHeight,
        uint layers,
        in DetileParams parameters,
        out VulkanDetileDispatch dispatch)
    {
        dispatch = default;
        if (!Supports(parameters) ||
            tiledByteLength <= 0 ||
            texelWidth <= 0 ||
            texelHeight <= 0 ||
            layers == 0 ||
            !HasConsistentBlockGeometry(parameters) ||
            !HasValidEquationTables(parameters))
        {
            return false;
        }

        try
        {
            var blocksHigh = DivideRoundUp(
                checked((ulong)parameters.ElementsHigh),
                checked((ulong)parameters.BlockHeight));
            var tiledBytesPerLayer = checked(
                (ulong)parameters.BlocksPerRow *
                blocksHigh *
                (ulong)parameters.BlockBytes);
            var totalTiledBytes = checked(tiledBytesPerLayer * layers);
            if (totalTiledBytes != (ulong)tiledByteLength ||
                tiledBytesPerLayer % (uint)parameters.BytesPerElement != 0)
            {
                return false;
            }

            var sourceSliceElements =
                tiledBytesPerLayer / (uint)parameters.BytesPerElement;
            if (sourceSliceElements > uint.MaxValue)
            {
                return false;
            }

            var outputBytes = checked(
                (ulong)parameters.ElementsWide *
                (ulong)parameters.ElementsHigh *
                (uint)parameters.BytesPerElement *
                layers);
            var uintsPerElement =
                checked((uint)parameters.BytesPerElement / sizeof(uint));
            var invocationWidth = checked(
                (ulong)parameters.ElementsWide * uintsPerElement);
            var groupCountX = DivideRoundUp(invocationWidth, LocalSize);
            var groupCountY = DivideRoundUp(
                checked((ulong)parameters.ElementsHigh),
                LocalSize);
            if (groupCountX == 0 ||
                groupCountX > uint.MaxValue ||
                groupCountY == 0 ||
                groupCountY > uint.MaxValue)
            {
                return false;
            }

            dispatch = new VulkanDetileDispatch(
                checked((uint)texelWidth),
                checked((uint)texelHeight),
                checked((uint)parameters.ElementsWide),
                checked((uint)parameters.ElementsHigh),
                checked((uint)parameters.BlockWidth),
                checked((uint)parameters.BlockHeight),
                checked((uint)parameters.BlockElements),
                checked((uint)parameters.BlocksPerRow),
                checked((uint)parameters.XMask),
                checked((uint)parameters.YMask),
                checked((uint)sourceSliceElements),
                parameters.Equation == DetileEquation.BlockTable ? 1u : 0u,
                uintsPerElement,
                checked((uint)groupCountX),
                checked((uint)groupCountY),
                layers,
                tiledBytesPerLayer,
                outputBytes);
            return true;
        }
        catch (OverflowException)
        {
            dispatch = default;
            return false;
        }
    }

    private static bool HasConsistentBlockGeometry(
        in DetileParams parameters)
    {
        if (parameters.ElementsWide <= 0 ||
            parameters.ElementsHigh <= 0 ||
            parameters.BlockWidth <= 0 ||
            parameters.BlockHeight <= 0 ||
            parameters.BlockElements <= 0 ||
            parameters.BlockBytes <= 0 ||
            parameters.BlocksPerRow <= 0 ||
            parameters.XMask < 0 ||
            parameters.YMask < 0)
        {
            return false;
        }

        try
        {
            var blockElements = checked(
                parameters.BlockWidth * parameters.BlockHeight);
            var blockBytes = checked(
                parameters.BlockElements * parameters.BytesPerElement);
            var blocksPerRow = checked(
                (parameters.ElementsWide + parameters.BlockWidth - 1) /
                parameters.BlockWidth);
            return blockElements == parameters.BlockElements &&
                blockBytes == parameters.BlockBytes &&
                blocksPerRow == parameters.BlocksPerRow;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool HasValidEquationTables(
        in DetileParams parameters)
    {
        if (parameters.Equation == DetileEquation.BlockTable)
        {
            if (parameters.BlockTable is null ||
                parameters.BlockTable.Length != parameters.BlockElements)
            {
                return false;
            }

            foreach (var offset in parameters.BlockTable)
            {
                if ((uint)offset >= (uint)parameters.BlockElements)
                {
                    return false;
                }
            }

            return true;
        }

        if (parameters.XByteTerm is null ||
            parameters.YByteTerm is null ||
            parameters.XMask >= parameters.XByteTerm.Length ||
            parameters.YMask >= parameters.YByteTerm.Length ||
            !IsLowBitMask(parameters.XMask) ||
            !IsLowBitMask(parameters.YMask))
        {
            return false;
        }

        return HasValidByteTerms(
                parameters.XByteTerm,
                parameters.BytesPerElement,
                parameters.BlockBytes) &&
            HasValidByteTerms(
                parameters.YByteTerm,
                parameters.BytesPerElement,
                parameters.BlockBytes);
    }

    private static bool HasValidByteTerms(
        int[] terms,
        int bytesPerElement,
        int blockBytes)
    {
        foreach (var term in terms)
        {
            if (term < 0 ||
                term >= blockBytes ||
                term % bytesPerElement != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowBitMask(int mask) =>
        (mask & (mask + 1)) == 0;

    private static ulong DivideRoundUp(ulong value, ulong divisor) =>
        checked((value + divisor - 1) / divisor);

    /// <summary>
    /// Executes representative exact-XOR and block-table surfaces through the
    /// real Vulkan kernel and requires byte-for-byte parity with the CPU model.
    /// Intended for the opt-in conformance path, not normal presentation.
    /// </summary>
    public static int RunSelfTest(
        Vk vk,
        Device device,
        Queue queue,
        PhysicalDevice physicalDevice,
        uint queueFamilyIndex)
    {
        const int width = 64;
        const int height = 33;
        const uint layers = 2;
        (uint Mode, int BytesPerElement)[] cases =
        [
            (27, 4),
            (8, 4),
            (27, 8),
            (27, 16),
        ];

        using var context = new Context(
            vk,
            device,
            queue,
            physicalDevice,
            queueFamilyIndex);
        var imageRoundTrips = 0;
        foreach (var (mode, bytesPerElement) in cases)
        {
            var parameters = GnmTiling.GetDetileParams(
                mode,
                bytesPerElement,
                width,
                height);
            var blocksHigh =
                (parameters.ElementsHigh + parameters.BlockHeight - 1) /
                parameters.BlockHeight;
            var tiledBytesPerLayer = checked(
                parameters.BlocksPerRow *
                blocksHigh *
                parameters.BlockBytes);
            var linearBytesPerLayer = checked(
                width * height * bytesPerElement);
            var tiled = new byte[checked(tiledBytesPerLayer * (int)layers)];
            var expected = new byte[
                checked(linearBytesPerLayer * (int)layers)];

            for (var layer = 0; layer < layers; layer++)
            {
                var tiledSlice = tiled.AsSpan(
                    checked((int)layer * tiledBytesPerLayer),
                    tiledBytesPerLayer);
                for (var index = 0; index < tiledSlice.Length; index++)
                {
                    tiledSlice[index] = (byte)(
                        (index * 31 + 7 + layer * 101) & byte.MaxValue);
                }

                if (!GnmTiling.DetileWithParams(
                        parameters,
                        tiledSlice,
                        expected.AsSpan(
                            checked((int)layer * linearBytesPerLayer),
                            linearBytesPerLayer)))
                {
                    throw new InvalidOperationException(
                        $"CPU detile declined mode {mode}, " +
                        $"{bytesPerElement}-byte elements.");
                }
            }

            if (!TryCreateDispatch(
                    tiled.Length,
                    width,
                    height,
                    layers,
                    parameters,
                    out var dispatch))
            {
                throw new InvalidOperationException(
                    $"Vulkan dispatch planning declined mode {mode}, " +
                    $"{bytesPerElement}-byte elements.");
            }

            var actual = context.ExecuteImageRoundTrip(
                tiled,
                parameters,
                dispatch);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                var mismatch = actual
                    .Zip(expected)
                    .Select(static (pair, index) =>
                        (pair.First, pair.Second, Index: index))
                    .First(pair => pair.First != pair.Second);
                throw new InvalidOperationException(
                    $"Vulkan detile mismatch for mode {mode}, " +
                    $"{bytesPerElement}-byte elements at byte " +
                    $"{mismatch.Index}: GPU={mismatch.First}, " +
                    $"CPU={mismatch.Second}.");
            }

            imageRoundTrips++;
        }

        return imageRoundTrips;
    }

    internal struct TransientResources
    {
        public VkBuffer Tiled;
        public DeviceMemory TiledMemory;
        public VkBuffer XTerms;
        public DeviceMemory XTermsMemory;
        public VkBuffer YTerms;
        public DeviceMemory YTermsMemory;
        public VkBuffer Output;
        public DeviceMemory OutputMemory;
        public VkBuffer Readback;
        public DeviceMemory ReadbackMemory;
        public Image Image;
        public DeviceMemory ImageMemory;
        public DescriptorPool DescriptorPool;
        public DescriptorSet DescriptorSet;
    }

    internal sealed class Context : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly Queue _queue;
        private readonly PhysicalDevice _physicalDevice;
        private ShaderModule _shaderModule;
        private DescriptorSetLayout _descriptorSetLayout;
        private PipelineLayout _pipelineLayout;
        private Pipeline _pipeline;
        private CommandPool _commandPool;

        public Context(
            Vk vk,
            Device device,
            Queue queue,
            PhysicalDevice physicalDevice,
            uint queueFamilyIndex)
        {
            _vk = vk;
            _device = device;
            _queue = queue;
            _physicalDevice = physicalDevice;
            CreatePipeline();

            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };
            Check(
                _vk.CreateCommandPool(
                    _device,
                    &poolInfo,
                    null,
                    out _commandPool),
                "vkCreateCommandPool(detile self-test)");
        }

        public TransientResources RecordImageUpload(
            CommandBuffer commandBuffer,
            Image image,
            ReadOnlySpan<byte> tiled,
            in DetileParams parameters,
            in VulkanDetileDispatch dispatch,
            PipelineStageFlags shaderStage)
        {
            var resources = default(TransientResources);
            try
            {
                PrepareComputeResources(
                    tiled,
                    parameters,
                    dispatch,
                    ref resources);
                RecordComputeDispatch(
                    commandBuffer,
                    resources,
                    dispatch);
                RecordOutputToImage(
                    commandBuffer,
                    resources.Output,
                    image,
                    dispatch,
                    shaderStage);
                return resources;
            }
            catch
            {
                DestroyTransientResources(resources);
                throw;
            }
        }

        public byte[] ExecuteImageRoundTrip(
            ReadOnlySpan<byte> tiled,
            in DetileParams parameters,
            in VulkanDetileDispatch dispatch)
        {
            var resources = default(TransientResources);
            CommandBuffer commandBuffer = default;
            Fence fence = default;
            try
            {
                PrepareResources(
                    tiled,
                    parameters,
                    dispatch,
                    ref resources);
                commandBuffer = AllocateCommandBuffer();
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(detile self-test)");

                RecordComputeDispatch(
                    commandBuffer,
                    resources,
                    dispatch);

                RecordImageRoundTrip(
                    commandBuffer,
                    resources,
                    dispatch);
                Check(
                    _vk.EndCommandBuffer(commandBuffer),
                    "vkEndCommandBuffer(detile self-test)");

                var fenceInfo = new FenceCreateInfo
                {
                    SType = StructureType.FenceCreateInfo,
                };
                Check(
                    _vk.CreateFence(
                        _device,
                        &fenceInfo,
                        null,
                        out fence),
                    "vkCreateFence(detile self-test)");
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, fence),
                    "vkQueueSubmit(detile self-test)");
                Check(
                    _vk.WaitForFences(
                        _device,
                        1,
                        &fence,
                        true,
                        ulong.MaxValue),
                    "vkWaitForFences(detile self-test)");
                return ReadBytes(
                    resources.ReadbackMemory,
                    dispatch.OutputBytes);
            }
            finally
            {
                if (fence.Handle != 0)
                {
                    _vk.DestroyFence(_device, fence, null);
                }

                if (commandBuffer.Handle != 0)
                {
                    _vk.FreeCommandBuffers(
                        _device,
                        _commandPool,
                        1,
                        &commandBuffer);
                }

                DestroyResources(resources);
            }
        }

        private void RecordComputeDispatch(
            CommandBuffer commandBuffer,
            in TransientResources resources,
            in VulkanDetileDispatch dispatch)
        {
            var descriptorSet = resources.DescriptorSet;
            _vk.CmdBindPipeline(
                commandBuffer,
                PipelineBindPoint.Compute,
                _pipeline);
            _vk.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                0,
                1,
                &descriptorSet,
                0,
                null);

            Span<uint> pushConstants =
            [
                dispatch.ElementsWide,
                dispatch.ElementsHigh,
                dispatch.BlockWidth,
                dispatch.BlockHeight,
                dispatch.BlockElements,
                dispatch.BlocksPerRow,
                dispatch.XMask,
                dispatch.YMask,
                dispatch.SourceSliceElements,
                dispatch.Equation,
                dispatch.UintsPerElement,
            ];
            fixed (uint* push = pushConstants)
            {
                _vk.CmdPushConstants(
                    commandBuffer,
                    _pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    PushConstantBytes,
                    push);
            }

            _vk.CmdDispatch(
                commandBuffer,
                dispatch.GroupCountX,
                dispatch.GroupCountY,
                dispatch.GroupCountZ);
        }

        private void RecordImageRoundTrip(
            CommandBuffer commandBuffer,
            in TransientResources resources,
            in VulkanDetileDispatch dispatch)
        {
            RecordOutputToImage(
                commandBuffer,
                resources.Output,
                resources.Image,
                dispatch,
                PipelineStageFlags.FragmentShaderBit,
                out var copy,
                out var colorRange);

            var imageToTransferSource = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = resources.Image,
                SubresourceRange = colorRange,
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.FragmentShaderBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &imageToTransferSource);
            _vk.CmdCopyImageToBuffer(
                commandBuffer,
                resources.Image,
                ImageLayout.TransferSrcOptimal,
                resources.Readback,
                1,
                &copy);

            var readbackToHost = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.HostReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = resources.Readback,
                Offset = 0,
                Size = dispatch.OutputBytes,
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.HostBit,
                0,
                0,
                null,
                1,
                &readbackToHost,
                0,
                null);
        }

        private void RecordOutputToImage(
            CommandBuffer commandBuffer,
            VkBuffer output,
            Image image,
            in VulkanDetileDispatch dispatch,
            PipelineStageFlags shaderStage)
        {
            RecordOutputToImage(
                commandBuffer,
                output,
                image,
                dispatch,
                shaderStage,
                out _,
                out _);
        }

        private void RecordOutputToImage(
            CommandBuffer commandBuffer,
            VkBuffer output,
            Image image,
            in VulkanDetileDispatch dispatch,
            PipelineStageFlags shaderStage,
            out BufferImageCopy copy,
            out ImageSubresourceRange colorRange)
        {
            var outputToTransfer = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = output,
                Offset = 0,
                Size = dispatch.OutputBytes,
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                1,
                &outputToTransfer,
                0,
                null);

            colorRange = new ImageSubresourceRange(
                ImageAspectFlags.ColorBit,
                0,
                1,
                0,
                dispatch.GroupCountZ);
            var imageToTransferDestination = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = colorRange,
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &imageToTransferDestination);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    dispatch.GroupCountZ),
                ImageExtent = new Extent3D(
                    dispatch.TexelWidth,
                    dispatch.TexelHeight,
                    1),
            };
            _vk.CmdCopyBufferToImage(
                commandBuffer,
                output,
                image,
                ImageLayout.TransferDstOptimal,
                1,
                &copyRegion);
            copy = copyRegion;

            var imageToShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = colorRange,
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                shaderStage,
                0,
                0,
                null,
                0,
                null,
                1,
                &imageToShaderRead);
        }

        private void PrepareResources(
            ReadOnlySpan<byte> tiled,
            in DetileParams parameters,
            in VulkanDetileDispatch dispatch,
            ref TransientResources resources)
        {
            if (dispatch.TexelWidth != dispatch.ElementsWide ||
                dispatch.TexelHeight != dispatch.ElementsHigh)
            {
                throw new InvalidOperationException(
                    "Detile image self-test requires one element per texel.");
            }

            PrepareComputeResources(
                tiled,
                parameters,
                dispatch,
                ref resources);
            resources.Readback = CreateBuffer(
                dispatch.OutputBytes,
                out resources.ReadbackMemory,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit |
                MemoryPropertyFlags.HostCoherentBit);
            resources.Image = CreateImage(
                dispatch,
                out resources.ImageMemory);
        }

        private void PrepareComputeResources(
            ReadOnlySpan<byte> tiled,
            in DetileParams parameters,
            in VulkanDetileDispatch dispatch,
            ref TransientResources resources)
        {
            uint[] xTerms;
            uint[] yTerms;
            if (parameters.Equation == DetileEquation.BlockTable)
            {
                xTerms = Array.ConvertAll(
                    parameters.BlockTable,
                    static value => checked((uint)value));
                yTerms = [0];
            }
            else
            {
                var shift = BitOperations.TrailingZeroCount(
                    (uint)parameters.BytesPerElement);
                xTerms = ToElementTerms(parameters.XByteTerm, shift);
                yTerms = ToElementTerms(parameters.YByteTerm, shift);
            }

            resources.Tiled = CreateBuffer(
                (ulong)tiled.Length,
                out resources.TiledMemory);
            UploadBytes(resources.TiledMemory, tiled);
            resources.XTerms = CreateBuffer(
                checked((ulong)xTerms.Length * sizeof(uint)),
                out resources.XTermsMemory);
            UploadUInts(resources.XTermsMemory, xTerms);
            resources.YTerms = CreateBuffer(
                checked((ulong)yTerms.Length * sizeof(uint)),
                out resources.YTermsMemory);
            UploadUInts(resources.YTermsMemory, yTerms);
            resources.Output = CreateBuffer(
                dispatch.OutputBytes,
                out resources.OutputMemory,
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.DeviceLocalBit);

            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = 4,
            };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
            };
            Check(
                _vk.CreateDescriptorPool(
                    _device,
                    &poolInfo,
                    null,
                    out resources.DescriptorPool),
                "vkCreateDescriptorPool(detile self-test)");

            var setLayout = _descriptorSetLayout;
            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = resources.DescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            Check(
                _vk.AllocateDescriptorSets(
                    _device,
                    &allocateInfo,
                    out resources.DescriptorSet),
                "vkAllocateDescriptorSets(detile self-test)");
            WriteDescriptors(
                resources,
                (ulong)tiled.Length,
                checked((ulong)xTerms.Length * sizeof(uint)),
                checked((ulong)yTerms.Length * sizeof(uint)),
                dispatch.OutputBytes);
        }

        private void CreatePipeline()
        {
            var spirv = SpirvFixedShaders.CreateDetileCompute();
            fixed (byte* code = spirv)
            {
                var moduleInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)code,
                };
                Check(
                    _vk.CreateShaderModule(
                        _device,
                        &moduleInfo,
                        null,
                        out _shaderModule),
                    "vkCreateShaderModule(detile self-test)");
            }

            var bindings = stackalloc DescriptorSetLayoutBinding[4];
            for (uint index = 0; index < 4; index++)
            {
                bindings[index] = new DescriptorSetLayoutBinding
                {
                    Binding = index,
                    DescriptorType = DescriptorType.StorageBuffer,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.ComputeBit,
                };
            }

            var setLayoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 4,
                PBindings = bindings,
            };
            Check(
                _vk.CreateDescriptorSetLayout(
                    _device,
                    &setLayoutInfo,
                    null,
                    out _descriptorSetLayout),
                "vkCreateDescriptorSetLayout(detile self-test)");

            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = PushConstantBytes,
            };
            var setLayout = _descriptorSetLayout;
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange,
            };
            Check(
                _vk.CreatePipelineLayout(
                    _device,
                    &pipelineLayoutInfo,
                    null,
                    out _pipelineLayout),
                "vkCreatePipelineLayout(detile self-test)");

            ReadOnlySpan<byte> entryPoint = "main\0"u8;
            fixed (byte* entry = entryPoint)
            {
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Layout = _pipelineLayout,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.ComputeBit,
                        Module = _shaderModule,
                        PName = entry,
                    },
                };
                Check(
                    _vk.CreateComputePipelines(
                        _device,
                        default,
                        1,
                        &pipelineInfo,
                        null,
                        out _pipeline),
                    "vkCreateComputePipelines(detile self-test)");
            }
        }

        private VkBuffer CreateBuffer(
            ulong size,
            out DeviceMemory memory,
            BufferUsageFlags usage = BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags requiredMemory =
                MemoryPropertyFlags.HostVisibleBit |
                MemoryPropertyFlags.HostCoherentBit)
        {
            memory = default;
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };
            Check(
                _vk.CreateBuffer(
                    _device,
                    &bufferInfo,
                    null,
                    out var buffer),
                "vkCreateBuffer(detile self-test)");
            try
            {
                _vk.GetBufferMemoryRequirements(
                    _device,
                    buffer,
                    out var requirements);
                var allocateInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = FindMemoryType(
                        requirements.MemoryTypeBits,
                        requiredMemory),
                };
                Check(
                    _vk.AllocateMemory(
                        _device,
                        &allocateInfo,
                        null,
                        out memory),
                    "vkAllocateMemory(detile self-test)");
                Check(
                    _vk.BindBufferMemory(_device, buffer, memory, 0),
                    "vkBindBufferMemory(detile self-test)");
                return buffer;
            }
            catch
            {
                if (memory.Handle != 0)
                {
                    _vk.FreeMemory(_device, memory, null);
                    memory = default;
                }

                _vk.DestroyBuffer(_device, buffer, null);
                throw;
            }
        }

        private Image CreateImage(
            in VulkanDetileDispatch dispatch,
            out DeviceMemory memory)
        {
            var bytesPerTexel = checked(dispatch.UintsPerElement * sizeof(uint));
            var format = bytesPerTexel switch
            {
                4 => Format.R32Uint,
                8 => Format.R32G32Uint,
                16 => Format.R32G32B32A32Uint,
                _ => throw new InvalidOperationException(
                    $"Unsupported detile self-test texel width {bytesPerTexel}."),
            };
            var expectedBytes = checked(
                (ulong)dispatch.TexelWidth *
                dispatch.TexelHeight *
                bytesPerTexel *
                dispatch.GroupCountZ);
            if (expectedBytes != dispatch.OutputBytes)
            {
                throw new InvalidOperationException(
                    "Detile output extent does not match its image format.");
            }

            memory = default;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(
                    dispatch.TexelWidth,
                    dispatch.TexelHeight,
                    1),
                MipLevels = 1,
                ArrayLayers = dispatch.GroupCountZ,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(
                    _device,
                    &imageInfo,
                    null,
                    out var image),
                "vkCreateImage(detile self-test)");
            try
            {
                _vk.GetImageMemoryRequirements(
                    _device,
                    image,
                    out var requirements);
                var allocateInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = FindMemoryType(
                        requirements.MemoryTypeBits,
                        MemoryPropertyFlags.DeviceLocalBit),
                };
                Check(
                    _vk.AllocateMemory(
                        _device,
                        &allocateInfo,
                        null,
                        out memory),
                    "vkAllocateMemory(detile image)");
                Check(
                    _vk.BindImageMemory(_device, image, memory, 0),
                    "vkBindImageMemory(detile self-test)");
                return image;
            }
            catch
            {
                if (memory.Handle != 0)
                {
                    _vk.FreeMemory(_device, memory, null);
                    memory = default;
                }

                _vk.DestroyImage(_device, image, null);
                throw;
            }
        }

        private uint FindMemoryType(
            uint typeBits,
            MemoryPropertyFlags required)
        {
            _vk.GetPhysicalDeviceMemoryProperties(
                _physicalDevice,
                out var properties);
            var memoryTypes = &properties.MemoryTypes.Element0;
            for (uint index = 0; index < properties.MemoryTypeCount; index++)
            {
                if ((typeBits & (1u << (int)index)) != 0 &&
                    (memoryTypes[index].PropertyFlags & required) == required)
                {
                    return index;
                }
            }

            throw new InvalidOperationException(
                $"No Vulkan memory type with {required} for detile self-test.");
        }

        private void UploadBytes(
            DeviceMemory memory,
            ReadOnlySpan<byte> bytes)
        {
            void* mapped;
            Check(
                _vk.MapMemory(
                    _device,
                    memory,
                    0,
                    (ulong)bytes.Length,
                    0,
                    &mapped),
                "vkMapMemory(detile upload)");
            try
            {
                bytes.CopyTo(new Span<byte>(mapped, bytes.Length));
            }
            finally
            {
                _vk.UnmapMemory(_device, memory);
            }
        }

        private void UploadUInts(DeviceMemory memory, uint[] values)
        {
            void* mapped;
            var byteCount = checked((ulong)values.Length * sizeof(uint));
            Check(
                _vk.MapMemory(
                    _device,
                    memory,
                    0,
                    byteCount,
                    0,
                    &mapped),
                "vkMapMemory(detile terms)");
            try
            {
                values.AsSpan().CopyTo(
                    new Span<uint>(mapped, values.Length));
            }
            finally
            {
                _vk.UnmapMemory(_device, memory);
            }
        }

        private byte[] ReadBytes(DeviceMemory memory, ulong byteCount)
        {
            if (byteCount > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "Detile self-test output exceeds host span capacity.");
            }

            void* mapped;
            Check(
                _vk.MapMemory(
                    _device,
                    memory,
                    0,
                    byteCount,
                    0,
                    &mapped),
                "vkMapMemory(detile output)");
            try
            {
                return new ReadOnlySpan<byte>(
                    mapped,
                    checked((int)byteCount)).ToArray();
            }
            finally
            {
                _vk.UnmapMemory(_device, memory);
            }
        }

        private void WriteDescriptors(
            in TransientResources resources,
            ulong tiledBytes,
            ulong xTermBytes,
            ulong yTermBytes,
            ulong outputBytes)
        {
            var infos = stackalloc DescriptorBufferInfo[4]
            {
                new()
                {
                    Buffer = resources.Tiled,
                    Range = tiledBytes,
                },
                new()
                {
                    Buffer = resources.XTerms,
                    Range = xTermBytes,
                },
                new()
                {
                    Buffer = resources.YTerms,
                    Range = yTermBytes,
                },
                new()
                {
                    Buffer = resources.Output,
                    Range = outputBytes,
                },
            };
            var writes = stackalloc WriteDescriptorSet[4];
            for (uint index = 0; index < 4; index++)
            {
                writes[index] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = resources.DescriptorSet,
                    DstBinding = index,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &infos[index],
                };
            }

            _vk.UpdateDescriptorSets(_device, 4, writes, 0, null);
        }

        private CommandBuffer AllocateCommandBuffer()
        {
            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(
                _vk.AllocateCommandBuffers(
                    _device,
                    &allocateInfo,
                    out var commandBuffer),
                "vkAllocateCommandBuffers(detile self-test)");
            return commandBuffer;
        }

        private void DestroyResources(in TransientResources resources)
        {
            if (resources.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, resources.Image, null);
            }
            if (resources.ImageMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, resources.ImageMemory, null);
            }

            DestroyBuffer(
                resources.Readback,
                resources.ReadbackMemory);
            DestroyTransientResources(resources);
        }

        public void DestroyTransientResources(in TransientResources resources)
        {
            if (resources.DescriptorPool.Handle != 0)
            {
                _vk.DestroyDescriptorPool(
                    _device,
                    resources.DescriptorPool,
                    null);
            }

            DestroyBuffer(resources.Output, resources.OutputMemory);
            DestroyBuffer(resources.YTerms, resources.YTermsMemory);
            DestroyBuffer(resources.XTerms, resources.XTermsMemory);
            DestroyBuffer(resources.Tiled, resources.TiledMemory);
        }

        private void DestroyBuffer(
            VkBuffer buffer,
            DeviceMemory memory)
        {
            if (buffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, buffer, null);
            }

            if (memory.Handle != 0)
            {
                _vk.FreeMemory(_device, memory, null);
            }
        }

        private void Check(Result result, string operation)
        {
            if (result != Result.Success)
            {
                throw new InvalidOperationException(
                    $"{operation} failed: {result}.");
            }
        }

        public void Dispose()
        {
            if (_commandPool.Handle != 0)
            {
                _vk.DestroyCommandPool(_device, _commandPool, null);
            }

            if (_pipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, _pipeline, null);
            }

            if (_pipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(
                    _device,
                    _pipelineLayout,
                    null);
            }

            if (_descriptorSetLayout.Handle != 0)
            {
                _vk.DestroyDescriptorSetLayout(
                    _device,
                    _descriptorSetLayout,
                    null);
            }

            if (_shaderModule.Handle != 0)
            {
                _vk.DestroyShaderModule(_device, _shaderModule, null);
            }
        }

    }

    private static uint[] ToElementTerms(int[] byteTerms, int shift)
    {
        var terms = new uint[byteTerms.Length];
        for (var index = 0; index < byteTerms.Length; index++)
        {
            terms[index] = checked((uint)byteTerms[index]) >> shift;
        }

        return terms;
    }
}
