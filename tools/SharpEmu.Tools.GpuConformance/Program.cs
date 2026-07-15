// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Executes a SharpEmu-emitted compute shader on a real Vulkan device and
// compares its readback buffer with a versioned conformance manifest produced
// by SharpEmu.Tools.ShaderDump.
//
// Creating the compute pipeline doubles as a driver-acceptance check for the
// emitted SPIR-V; the dispatch then verifies the arithmetic numerically.
//
// Usage: SharpEmu.Tools.GpuConformance <path-to-conformance-manifest.json>

using System.Text.Json;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

var manifestPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : throw new InvalidOperationException(
        "usage: SharpEmu.Tools.GpuConformance <path-to-conformance-manifest.json>");
var manifest = JsonSerializer.Deserialize<ConformanceManifest>(
        File.ReadAllText(manifestPath))
    ?? throw new InvalidDataException("conformance manifest is empty");
var testBuffers = manifest.Validate();

var manifestDirectory = Path.GetDirectoryName(manifestPath)
    ?? throw new InvalidDataException("conformance manifest has no parent directory");
var spvPath = Path.GetFullPath(Path.Combine(manifestDirectory, manifest.Shader));
var code = File.ReadAllBytes(spvPath);
if (code.Length == 0 || code.Length % sizeof(uint) != 0)
{
    throw new InvalidDataException("SPIR-V module must contain a non-empty sequence of 32-bit words");
}

var bufferSizes = testBuffers
    .Select(buffer => checked((ulong)buffer.InitialWords.Length * sizeof(uint)))
    .ToArray();
Console.WriteLine($"case: {manifest.Name}");

unsafe
{
    var vk = Vk.GetApi();

    var appName = (byte*)SilkMarshal.StringToPtr("SharpEmuGpuConformance");
    var appInfo = new ApplicationInfo
    {
        SType = StructureType.ApplicationInfo,
        PApplicationName = appName,
        ApiVersion = Vk.Version13,
    };
    var instanceInfo = new InstanceCreateInfo
    {
        SType = StructureType.InstanceCreateInfo,
        PApplicationInfo = &appInfo,
    };
    Check(vk.CreateInstance(in instanceInfo, null, out var instance), "vkCreateInstance");

    uint deviceCount = 0;
    vk.EnumeratePhysicalDevices(instance, &deviceCount, null);
    if (deviceCount == 0)
    {
        throw new InvalidOperationException("no Vulkan devices found");
    }

    var physicalDevices = new PhysicalDevice[deviceCount];
    fixed (PhysicalDevice* pDevices = physicalDevices)
    {
        vk.EnumeratePhysicalDevices(instance, &deviceCount, pDevices);
    }

    // Prefer the first discrete GPU; fall back to the first device.
    var physical = physicalDevices[0];
    foreach (var candidate in physicalDevices)
    {
        vk.GetPhysicalDeviceProperties(candidate, out var props);
        if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
        {
            physical = candidate;
            break;
        }
    }

    vk.GetPhysicalDeviceProperties(physical, out var chosenProps);
    Console.WriteLine(
        $"executing on: {SilkMarshal.PtrToString((nint)chosenProps.DeviceName)}");
    var storageBufferLimit = Math.Min(
        chosenProps.Limits.MaxPerStageDescriptorStorageBuffers,
        chosenProps.Limits.MaxDescriptorSetStorageBuffers);
    if ((uint)testBuffers.Length > storageBufferLimit)
    {
        throw new InvalidOperationException(
            $"manifest requires {testBuffers.Length} storage buffers, but the " +
            $"selected device supports {storageBufferLimit}");
    }

    uint familyCount = 0;
    vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, null);
    var families = new QueueFamilyProperties[familyCount];
    fixed (QueueFamilyProperties* pFamilies = families)
    {
        vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, pFamilies);
    }

    uint? computeFamilyFound = null;
    for (uint index = 0; index < familyCount; index++)
    {
        if (families[index].QueueFlags.HasFlag(QueueFlags.ComputeBit))
        {
            computeFamilyFound = index;
            break;
        }
    }

    var computeFamily = computeFamilyFound
        ?? throw new InvalidOperationException("device has no compute-capable queue family");

    // The emitted SPIR-V declares the Int64 capability.
    vk.GetPhysicalDeviceFeatures(physical, out var supportedFeatures);
    if (!supportedFeatures.ShaderInt64)
    {
        throw new InvalidOperationException(
            "device does not support shaderInt64, which the emitted SPIR-V requires");
    }

    var priority = 1f;
    var queueInfo = new DeviceQueueCreateInfo
    {
        SType = StructureType.DeviceQueueCreateInfo,
        QueueFamilyIndex = computeFamily,
        QueueCount = 1,
        PQueuePriorities = &priority,
    };
    var features = new PhysicalDeviceFeatures { ShaderInt64 = true };
    var deviceInfo = new DeviceCreateInfo
    {
        SType = StructureType.DeviceCreateInfo,
        QueueCreateInfoCount = 1,
        PQueueCreateInfos = &queueInfo,
        PEnabledFeatures = &features,
    };
    Check(vk.CreateDevice(physical, in deviceInfo, null, out var device), "vkCreateDevice");
    vk.GetDeviceQueue(device, computeFamily, 0, out var queue);

    // Each guest resource gets a distinct host-visible storage buffer. Binding
    // them as one descriptor array mirrors the compiler's guestBuffers layout.
    vk.GetPhysicalDeviceMemoryProperties(physical, out var memoryProperties);
    var vulkanBuffers = new Silk.NET.Vulkan.Buffer[testBuffers.Length];
    var memories = new DeviceMemory[testBuffers.Length];
    var mappedPointers = new nint[testBuffers.Length];
    for (var bufferIndex = 0; bufferIndex < testBuffers.Length; bufferIndex++)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = bufferSizes[bufferIndex],
            Usage = BufferUsageFlags.StorageBufferBit,
            SharingMode = SharingMode.Exclusive,
        };
        Check(
            vk.CreateBuffer(device, in bufferInfo, null, out vulkanBuffers[bufferIndex]),
            $"vkCreateBuffer[{bufferIndex}]");
        vk.GetBufferMemoryRequirements(
            device,
            vulkanBuffers[bufferIndex],
            out var requirements);

        uint memoryType = uint.MaxValue;
        for (var memoryIndex = 0; memoryIndex < memoryProperties.MemoryTypeCount; memoryIndex++)
        {
            var flags = memoryProperties.MemoryTypes[memoryIndex].PropertyFlags;
            if ((requirements.MemoryTypeBits & (1u << memoryIndex)) != 0 &&
                flags.HasFlag(MemoryPropertyFlags.HostVisibleBit) &&
                flags.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            {
                memoryType = (uint)memoryIndex;
                break;
            }
        }

        if (memoryType == uint.MaxValue)
        {
            throw new InvalidOperationException(
                $"no host-visible, host-coherent memory type available for " +
                $"readback buffer {bufferIndex}");
        }

        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = memoryType,
        };
        Check(
            vk.AllocateMemory(device, in allocateInfo, null, out memories[bufferIndex]),
            $"vkAllocateMemory[{bufferIndex}]");
        Check(
            vk.BindBufferMemory(
                device,
                vulkanBuffers[bufferIndex],
                memories[bufferIndex],
                0),
            $"vkBindBufferMemory[{bufferIndex}]");

        void* mapped;
        Check(
            vk.MapMemory(
                device,
                memories[bufferIndex],
                0,
                bufferSizes[bufferIndex],
                0,
                &mapped),
            $"vkMapMemory[{bufferIndex}]");
        mappedPointers[bufferIndex] = (nint)mapped;
        var words = (uint*)mapped;
        for (var wordIndex = 0;
             wordIndex < testBuffers[bufferIndex].InitialWords.Length;
             wordIndex++)
        {
            words[wordIndex] = testBuffers[bufferIndex].InitialWords[wordIndex];
        }
    }

    // SharpEmu emits all guest buffers as one descriptor array at set 0,
    // binding 0.
    ShaderModule module;
    fixed (byte* pCode = code)
    {
        var moduleInfo = new ShaderModuleCreateInfo
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)code.Length,
            PCode = (uint*)pCode,
        };
        Check(vk.CreateShaderModule(device, in moduleInfo, null, out module), "vkCreateShaderModule");
    }

    var layoutBinding = new DescriptorSetLayoutBinding
    {
        Binding = 0,
        DescriptorType = DescriptorType.StorageBuffer,
        DescriptorCount = (uint)testBuffers.Length,
        StageFlags = ShaderStageFlags.ComputeBit,
    };
    var setLayoutInfo = new DescriptorSetLayoutCreateInfo
    {
        SType = StructureType.DescriptorSetLayoutCreateInfo,
        BindingCount = 1,
        PBindings = &layoutBinding,
    };
    Check(
        vk.CreateDescriptorSetLayout(device, in setLayoutInfo, null, out var setLayout),
        "vkCreateDescriptorSetLayout");

    var pipelineLayoutInfo = new PipelineLayoutCreateInfo
    {
        SType = StructureType.PipelineLayoutCreateInfo,
        SetLayoutCount = 1,
        PSetLayouts = &setLayout,
    };
    Check(
        vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out var pipelineLayout),
        "vkCreatePipelineLayout");

    var entryName = (byte*)SilkMarshal.StringToPtr("main");
    var pipelineInfo = new ComputePipelineCreateInfo
    {
        SType = StructureType.ComputePipelineCreateInfo,
        Stage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = module,
            PName = entryName,
        },
        Layout = pipelineLayout,
    };
    Check(
        vk.CreateComputePipelines(device, default, 1, in pipelineInfo, null, out var pipeline),
        "vkCreateComputePipelines");
    Console.WriteLine("driver accepted the SPIR-V (pipeline created)");

    var poolSize = new DescriptorPoolSize
    {
        Type = DescriptorType.StorageBuffer,
        DescriptorCount = (uint)testBuffers.Length,
    };
    var poolInfo = new DescriptorPoolCreateInfo
    {
        SType = StructureType.DescriptorPoolCreateInfo,
        MaxSets = 1,
        PoolSizeCount = 1,
        PPoolSizes = &poolSize,
    };
    Check(vk.CreateDescriptorPool(device, in poolInfo, null, out var pool), "vkCreateDescriptorPool");

    var setAllocateInfo = new DescriptorSetAllocateInfo
    {
        SType = StructureType.DescriptorSetAllocateInfo,
        DescriptorPool = pool,
        DescriptorSetCount = 1,
        PSetLayouts = &setLayout,
    };
    Check(vk.AllocateDescriptorSets(device, in setAllocateInfo, out var descriptorSet), "vkAllocateDescriptorSets");

    var descriptorBuffers = new DescriptorBufferInfo[testBuffers.Length];
    for (var bufferIndex = 0; bufferIndex < testBuffers.Length; bufferIndex++)
    {
        descriptorBuffers[bufferIndex] = new DescriptorBufferInfo
        {
            Buffer = vulkanBuffers[bufferIndex],
            Offset = 0,
            Range = bufferSizes[bufferIndex],
        };
    }

    fixed (DescriptorBufferInfo* pDescriptorBuffers = descriptorBuffers)
    {
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorCount = (uint)testBuffers.Length,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = pDescriptorBuffers,
        };
        vk.UpdateDescriptorSets(device, 1, in write, 0, null);
    }

    var commandPoolInfo = new CommandPoolCreateInfo
    {
        SType = StructureType.CommandPoolCreateInfo,
        QueueFamilyIndex = computeFamily,
    };
    Check(vk.CreateCommandPool(device, in commandPoolInfo, null, out var commandPool), "vkCreateCommandPool");

    var commandBufferInfo = new CommandBufferAllocateInfo
    {
        SType = StructureType.CommandBufferAllocateInfo,
        CommandPool = commandPool,
        Level = CommandBufferLevel.Primary,
        CommandBufferCount = 1,
    };
    Check(vk.AllocateCommandBuffers(device, in commandBufferInfo, out var commandBuffer), "vkAllocateCommandBuffers");

    var beginInfo = new CommandBufferBeginInfo
    {
        SType = StructureType.CommandBufferBeginInfo,
    };
    Check(vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");
    vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
    vk.CmdBindDescriptorSets(
        commandBuffer,
        PipelineBindPoint.Compute,
        pipelineLayout,
        0,
        1,
        in descriptorSet,
        0,
        null);
    vk.CmdDispatch(
        commandBuffer,
        manifest.GroupCountX,
        manifest.GroupCountY,
        manifest.GroupCountZ);
    var barrier = new MemoryBarrier
    {
        SType = StructureType.MemoryBarrier,
        SrcAccessMask = AccessFlags.ShaderWriteBit,
        DstAccessMask = AccessFlags.HostReadBit,
    };
    vk.CmdPipelineBarrier(
        commandBuffer,
        PipelineStageFlags.ComputeShaderBit,
        PipelineStageFlags.HostBit,
        0,
        1,
        in barrier,
        0,
        null,
        0,
        null);
    Check(vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

    var submitInfo = new SubmitInfo
    {
        SType = StructureType.SubmitInfo,
        CommandBufferCount = 1,
        PCommandBuffers = &commandBuffer,
    };
    var fenceInfo = new FenceCreateInfo
    {
        SType = StructureType.FenceCreateInfo,
    };
    Check(vk.CreateFence(device, in fenceInfo, null, out var dispatchFence), "vkCreateFence");
    Check(vk.QueueSubmit(queue, 1, in submitInfo, dispatchFence), "vkQueueSubmit");

    const ulong dispatchTimeoutNanoseconds = 30UL * 1_000_000_000UL;
    var waitResult = vk.WaitForFences(
        device,
        1,
        &dispatchFence,
        true,
        dispatchTimeoutNanoseconds);
    if (waitResult == Result.Timeout)
    {
        throw new TimeoutException(
            $"Vulkan conformance dispatch did not finish within " +
            $"{dispatchTimeoutNanoseconds / 1_000_000_000UL} seconds");
    }

    Check(waitResult, "vkWaitForFences");

    var failures = 0;
    for (var bufferIndex = 0; bufferIndex < testBuffers.Length; bufferIndex++)
    {
        var testBuffer = testBuffers[bufferIndex];
        var words = (uint*)mappedPointers[bufferIndex];
        for (var wordIndex = 0; wordIndex < testBuffer.ExpectedWords.Length; wordIndex++)
        {
            var label = testBuffer.Labels[wordIndex];
            var actual = words[wordIndex];
            var expected = testBuffer.ExpectedWords[wordIndex];
            var status = actual == expected ? "PASS" : "FAIL";
            if (actual != expected)
            {
                failures++;
            }

            Console.WriteLine(
                $"{status}  {testBuffer.Name} / {label}: " +
                $"gpu=0x{actual:X8} expected=0x{expected:X8}");
        }
    }

    Console.WriteLine(failures == 0
        ? "RESULT: all values match"
        : $"RESULT: {failures} mismatch(es)");

    vk.DestroyFence(device, dispatchFence, null);
    vk.DestroyCommandPool(device, commandPool, null);
    vk.DestroyDescriptorPool(device, pool, null);
    vk.DestroyPipeline(device, pipeline, null);
    vk.DestroyPipelineLayout(device, pipelineLayout, null);
    vk.DestroyDescriptorSetLayout(device, setLayout, null);
    vk.DestroyShaderModule(device, module, null);
    for (var bufferIndex = 0; bufferIndex < testBuffers.Length; bufferIndex++)
    {
        vk.UnmapMemory(device, memories[bufferIndex]);
        vk.DestroyBuffer(device, vulkanBuffers[bufferIndex], null);
        vk.FreeMemory(device, memories[bufferIndex], null);
    }
    vk.DestroyDevice(device, null);
    vk.DestroyInstance(instance, null);

    Environment.ExitCode = failures == 0 ? 0 : 1;

    static void Check(Result result, string what)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{what} failed: {result}");
        }
    }
}

sealed record ConformanceManifest(
    int SchemaVersion,
    string Name,
    string Shader,
    uint[]? InitialWords,
    uint[]? ExpectedWords,
    string[]? Labels,
    ConformanceBuffer[]? Buffers,
    uint LocalSizeX,
    uint LocalSizeY,
    uint LocalSizeZ,
    uint GroupCountX,
    uint GroupCountY,
    uint GroupCountZ)
{
    public ConformanceBuffer[] Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException("conformance manifest name is required");
        }

        if (string.IsNullOrWhiteSpace(Shader) ||
            Path.IsPathRooted(Shader) ||
            Shader.Contains('/') ||
            Shader.Contains('\\'))
        {
            throw new InvalidDataException(
                "conformance manifest shader must name a file beside the manifest");
        }

        var validatedBuffers = SchemaVersion switch
        {
            2 when InitialWords is not null &&
                   ExpectedWords is not null &&
                   Labels is not null =>
                [new ConformanceBuffer("guest buffer 0", InitialWords, ExpectedWords, Labels)],
            3 when Buffers is { Length: > 0 } => Buffers,
            2 => throw new InvalidDataException(
                "schema 2 conformance manifest requires initial, expected, and label arrays"),
            3 => throw new InvalidDataException(
                "schema 3 conformance manifest requires at least one buffer"),
            _ => throw new InvalidDataException(
                $"unsupported conformance manifest schema version {SchemaVersion}"),
        };

        var duplicateName = validatedBuffers
            .GroupBy(buffer => buffer.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateName is not null)
        {
            throw new InvalidDataException(
                $"conformance manifest buffer name '{duplicateName}' is duplicated");
        }

        foreach (var buffer in validatedBuffers)
        {
            buffer.Validate();
        }

        if (validatedBuffers.Length > 1024)
        {
            throw new InvalidDataException(
                "conformance manifest cannot contain more than 1024 buffers");
        }

        if (LocalSizeX == 0 || LocalSizeY == 0 || LocalSizeZ == 0)
        {
            throw new InvalidDataException(
                "conformance manifest local sizes must be positive");
        }

        var localSizeXY = (ulong)LocalSizeX * LocalSizeY;
        if (localSizeXY > 1024 || localSizeXY * LocalSizeZ > 1024)
        {
            throw new InvalidDataException(
                "conformance manifest local-size product must not exceed 1024");
        }

        if (GroupCountX == 0 || GroupCountY == 0 || GroupCountZ == 0)
        {
            throw new InvalidDataException(
                "conformance manifest group counts must be positive");
        }

        return validatedBuffers;
    }
}

sealed record ConformanceBuffer(
    string Name,
    uint[] InitialWords,
    uint[] ExpectedWords,
    string[] Labels)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException(
                "conformance manifest requires a non-empty name for every buffer");
        }

        if (InitialWords is null || InitialWords.Length == 0)
        {
            throw new InvalidDataException(
                $"conformance manifest buffer '{Name}' requires initial words");
        }

        if (ExpectedWords is null || ExpectedWords.Length != InitialWords.Length)
        {
            throw new InvalidDataException(
                $"conformance manifest buffer '{Name}' initial and expected words " +
                "must have equal lengths");
        }

        if (Labels is null || Labels.Length != InitialWords.Length ||
            Labels.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                $"conformance manifest buffer '{Name}' requires one non-empty " +
                "label per word");
        }
    }
}
