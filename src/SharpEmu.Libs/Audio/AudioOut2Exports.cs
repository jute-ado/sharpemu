// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Audio;

public static class AudioOut2Exports
{
    // Guest evidence places the caller's stack canary at param+0x60. The
    // structure is 0x40 bytes; writing the previously assumed 0x80 bytes
    // corrupted that canary and silently killed audio initialization.
    private const int AudioOut2ContextParamSize = 0x40;
    private const ulong AudioOut2ContextMemoryBaseSize = 0x10000;
    private const ulong AudioOut2QueueMemorySize = 0x590;
    private const ulong AudioOut2AttributeSize = 0x18;
    private const uint MaxAttributeCount = 1024;
    private const uint DefaultQueueDepth = 4;
    private const uint DefaultNumGrains = 512;
    private const int AudioOut2SystemStateSize = 0x40;
    private const int AudioOut2SpeakerInfoSize = 0x50;
    private const int AudioOut2SpeakerArrayDescriptorSize = 0x28;
    private const int AudioOut2SpeakerArrayFooterSize = 0x18;
    private const uint AudioOut2MaximumSpeakerCount = 0x20;
    private const int AudioOut2ErrorInvalidArgument = unchecked((int)0x80268001);
    private const int AudioOut2ErrorNotReady = unchecked((int)0x80268008);
    private static readonly ConcurrentDictionary<ulong, ContextState> Contexts = new();
    private static readonly ConcurrentDictionary<ulong, PortState> Ports = new();
    private static readonly ConcurrentDictionary<ulong, SpeakerArrayState> SpeakerArrays = new();
    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;

    internal static void ResetRuntimeState()
    {
        Contexts.Clear();
        Ports.Clear();
        SpeakerArrays.Clear();
        Interlocked.Exchange(ref _nextContextHandle, 1);
        Interlocked.Exchange(ref _nextUserHandle, 1);
        Interlocked.Exchange(ref _nextPortId, 0);
    }

    [SysAbiExport(
        Nid = "g2tViFIohHE",
        ExportName = "sceAudioOut2Initialize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2Initialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "t5YrizufpQc",
        ExportName = "sceAudioOut2ContextResetParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextResetParam(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], 256);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], 256);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], DefaultQueueDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], DefaultNumGrains);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x14..], 1);

        return ctx.Memory.TryWrite(paramAddress, param)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "pDmme7Bgm6E",
        ExportName = "sceAudioOut2ContextQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextQueryMemory(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var outMemorySizeAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || outMemorySizeAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadContextParameters(
                ctx,
                paramAddress,
                out var queueDepth,
                out _))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var requiredSize = RequiredContextMemorySize(queueDepth);
        return ctx.TryWriteUInt64(outMemorySizeAddress, requiredSize)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "0x6o1VVAYSY",
        ExportName = "sceAudioOut2ContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextCreate(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = ctx[CpuRegister.Rdx];
        var outContextAddress = ctx[CpuRegister.Rcx];
        if (paramAddress == 0 || memoryAddress == 0 ||
            memorySize == 0 || outContextAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadContextParameters(
                ctx,
                paramAddress,
                out var queueDepth,
                out var numGrains))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (memorySize < RequiredContextMemorySize(queueDepth))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextContextHandle);
        if (!ctx.TryWriteUInt64(outContextAddress, handle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Contexts[handle] = new ContextState(queueDepth, numGrains);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "on6ZH7Abo10",
        ExportName = "sceAudioOut2ContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextDestroy(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        if (contextHandle == 0 || !Contexts.TryRemove(contextHandle, out _))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        foreach (var pair in Ports)
        {
            if (pair.Value.ContextHandle == contextHandle)
            {
                Ports.TryRemove(pair.Key, out _);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "4dq2rblWlg0",
        ExportName = "sceAudioOut2ContextSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextSetAttributes(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var attributesAddress = ctx[CpuRegister.Rsi];
        var attributeCount = unchecked((uint)ctx[CpuRegister.Rdx]);
        if (handle == 0 || !Contexts.ContainsKey(handle) ||
            (attributeCount != 0 && attributesAddress == 0) ||
            attributeCount > MaxAttributeCount)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        for (var index = 0U; index < attributeCount; index++)
        {
            if (!TryReadAttribute(
                    ctx,
                    attributesAddress,
                    index,
                    out _,
                    out _,
                    out _))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "PE2zHMqLSHs",
        ExportName = "sceAudioOut2ContextAdvance",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextAdvance(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        if (contextHandle == 0 ||
            !Contexts.TryGetValue(contextHandle, out var state))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        state.Advance();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "aII9h5nli9U",
        ExportName = "sceAudioOut2ContextPush",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextPush(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        var blocking = ctx[CpuRegister.Rsi] != 0;
        if (contextHandle == 0 ||
            !Contexts.TryGetValue(contextHandle, out var state))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return state.TryPush(blocking)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(AudioOut2ErrorNotReady);
    }

    [SysAbiExport(
        Nid = "R7d0F1g2qsU",
        ExportName = "sceAudioOut2ContextGetQueueLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextGetQueueLevel(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        var queueLevelAddress = ctx[CpuRegister.Rsi];
        var availableQueuesAddress = ctx[CpuRegister.Rdx];
        if (contextHandle == 0 ||
            !Contexts.TryGetValue(contextHandle, out var state))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        state.GetQueueLevel(out var queueLevel, out var availableQueues);
        if ((queueLevelAddress != 0 &&
             !ctx.TryWriteUInt32(queueLevelAddress, queueLevel)) ||
            (availableQueuesAddress != 0 &&
             !ctx.TryWriteUInt32(availableQueuesAddress, availableQueues)))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "JK2wamZPzwM",
        ExportName = "sceAudioOut2PortCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortCreate(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortAddress = ctx[CpuRegister.Rdx];
        if (contextHandle == 0 || paramAddress == 0 || outPortAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        if (!Contexts.ContainsKey(contextHandle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        if (!TryReadPortParameters(
                ctx,
                paramAddress,
                out var portType,
                out var dataFormat,
                out var samplingFrequency))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        var handle = unchecked((ulong)(uint)Interlocked.Increment(ref _nextPortId));
        if (!ctx.TryWriteUInt64(outPortAddress, handle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Ports[handle] = new PortState(
            contextHandle,
            portType,
            dataFormat,
            samplingFrequency);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "gatEUKG+Ea4",
        ExportName = "sceAudioOut2PortGetState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortGetState(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        if (handle == 0 || stateAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        Span<byte> state = stackalloc byte[0x40];
        state.Clear();
        var output = 0x01;
        var channels = 2;
        if (Ports.TryGetValue(handle, out var port))
        {
            output = port.PortType == 2 ? 0x40 : 0x01;
            channels = GetChannelCount(port.DataFormat);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(state[0x00..], unchecked((ushort)output));
        state[0x02] = unchecked((byte)channels);
        BinaryPrimitives.WriteInt16LittleEndian(state[0x04..], 127);

        return ctx.Memory.TryWrite(stateAddress, state)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "bkBN+CMLwRc",
        ExportName = "sceAudioOut2GetSystemState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSystemState(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> state = stackalloc byte[AudioOut2SystemStateSize];
        state.Clear();
        return ctx.Memory.TryWrite(stateAddress, state)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "DImz2Ft9E2g",
        ExportName = "sceAudioOut2GetSpeakerInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // SceAudioOut2SpeakerInfo is a 0x50-byte structure:
        // byte type, padding, capability/flags words, then 16 angle pairs.
        Span<byte> info = stackalloc byte[AudioOut2SpeakerInfoSize];
        info.Clear();
        info[0x00] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], 0x03);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x08..], 0);
        BinaryPrimitives.WriteInt16LittleEndian(info[0x10..], -30);
        BinaryPrimitives.WriteInt16LittleEndian(info[0x12..], 0);
        BinaryPrimitives.WriteInt16LittleEndian(info[0x14..], 30);
        BinaryPrimitives.WriteInt16LittleEndian(info[0x16..], 0);

        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "G1YOKDJYX2Y",
        ExportName = "sceAudioOut2GetSpeakerArrayMemorySize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerArrayMemorySize(CpuContext ctx)
    {
        var speakerCount = unchecked((uint)ctx[CpuRegister.Rdi]);
        var useObjectLayout = unchecked((uint)ctx[CpuRegister.Rsi]) != 0;
        var includeCoefficients = unchecked((uint)ctx[CpuRegister.Rdx]) != 0;
        var size = GetSpeakerArrayMemorySize(
            speakerCount,
            useObjectLayout,
            includeCoefficients);
        ctx[CpuRegister.Rax] = size;
        return 0;
    }

    [SysAbiExport(
        Nid = "+k91hoTuoA8",
        ExportName = "sceAudioOut2SpeakerArrayCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2SpeakerArrayCreate(CpuContext ctx)
    {
        // Firmware 12.70 exposes (outHandle, descriptor, auxiliary). RCX is
        // provider-private and may contain stale caller state.
        var outHandleAddress = ctx[CpuRegister.Rdi];
        var descriptorAddress = ctx[CpuRegister.Rsi];
        var auxiliaryAddress = ctx[CpuRegister.Rdx];
        if (outHandleAddress == 0 || descriptorAddress == 0)
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidArgument);
        }

        Span<byte> descriptor = stackalloc byte[AudioOut2SpeakerArrayDescriptorSize];
        if (!ctx.Memory.TryRead(descriptorAddress, descriptor))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var positionsAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x00..]);
        var speakerCount = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x08..]);
        var layout = descriptor[0x0C];
        var workspaceAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x10..]);
        var workspaceSize = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x18..]);
        var mode = BinaryPrimitives.ReadInt32LittleEndian(descriptor[0x20..]);
        var modeParameter = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(descriptor[0x24..]));
        if (positionsAddress == 0 ||
            workspaceAddress == 0 ||
            workspaceSize == 0 ||
            speakerCount > AudioOut2MaximumSpeakerCount ||
            (mode == 1 &&
             (!float.IsFinite(modeParameter) || modeParameter < 0.0f)))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidArgument);
        }

        var coefficientConfiguration = uint.MaxValue;
        var coefficientFeature = false;
        if (auxiliaryAddress != 0)
        {
            if (!ctx.TryReadUInt32(
                    auxiliaryAddress,
                    out coefficientConfiguration))
            {
                return ctx.SetReturn(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (coefficientConfiguration < 2)
            {
                if (!GuestAddress.TryAdd(
                        auxiliaryAddress,
                        sizeof(uint),
                        out var featureAddress))
                {
                    return ctx.SetReturn(
                        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                Span<byte> feature = stackalloc byte[1];
                if (!ctx.Memory.TryRead(featureAddress, feature))
                {
                    return ctx.SetReturn(
                        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                coefficientFeature = feature[0] != 0;
            }
        }

        var includeCoefficients = coefficientConfiguration < 2;
        var requiredSize = GetSpeakerArrayMemorySize(
            speakerCount,
            layout != 0,
            includeCoefficients);
        if (workspaceSize < requiredSize ||
            workspaceSize < AudioOut2SpeakerArrayFooterSize ||
            workspaceAddress >
            ulong.MaxValue - (workspaceSize - AudioOut2SpeakerArrayFooterSize))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidArgument);
        }

        var positions = new byte[checked((int)(speakerCount * 3U * sizeof(float)))];
        if (positions.Length != 0 &&
            !ctx.Memory.TryRead(positionsAddress, positions))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var handle =
            workspaceAddress + workspaceSize - AudioOut2SpeakerArrayFooterSize;
        Span<byte> footer = stackalloc byte[AudioOut2SpeakerArrayFooterSize];
        footer.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(footer[0x10..], mode);
        footer[0x14] = layout;
        if (!ctx.Memory.TryWrite(handle, footer) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        SpeakerArrays[handle] = new SpeakerArrayState(
            workspaceAddress,
            workspaceSize,
            speakerCount,
            layout,
            mode,
            coefficientConfiguration,
            coefficientFeature,
            positions);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "erCWQR5eKiQ",
        ExportName = "sceAudioOut2SpeakerArrayDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2SpeakerArrayDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        return handle != 0 && SpeakerArrays.TryRemove(handle, out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(AudioOut2ErrorInvalidArgument);
    }

    [SysAbiExport(
        Nid = "cd+Rtw+D1x8",
        ExportName = "sceAudioOut2PortDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        return handle != 0 && Ports.TryRemove(handle, out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [SysAbiExport(
        Nid = "8XTArSPyWHk",
        ExportName = "sceAudioOut2PortSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortSetAttributes(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var attributesAddress = ctx[CpuRegister.Rsi];
        var attributeCount = unchecked((uint)ctx[CpuRegister.Rdx]);
        if (handle == 0 || !Ports.TryGetValue(handle, out var port) ||
            (attributeCount != 0 && attributesAddress == 0) ||
            attributeCount > MaxAttributeCount)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        for (var index = 0U; index < attributeCount; index++)
        {
            if (!TryReadAttribute(
                    ctx,
                    attributesAddress,
                    index,
                    out var attributeId,
                    out var valueAddress,
                    out var valueSize))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (attributeId == 0 && valueAddress != 0 &&
                valueSize >= sizeof(ulong))
            {
                if (!ctx.TryReadUInt64(valueAddress, out var pcmDataAddress))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                port.SetPcmData(pcmDataAddress);
            }
        }

        return ctx.SetReturn(0);
    }

    private static bool TryReadAttribute(
        CpuContext ctx,
        ulong attributesAddress,
        uint index,
        out uint attributeId,
        out ulong valueAddress,
        out ulong valueSize)
    {
        attributeId = 0;
        valueAddress = 0;
        valueSize = 0;
        if (!GuestAddress.TryAdd(
                attributesAddress,
                index * AudioOut2AttributeSize,
                out var attributeAddress))
        {
            return false;
        }

        Span<byte> attribute = stackalloc byte[(int)AudioOut2AttributeSize];
        if (!ctx.Memory.TryRead(attributeAddress, attribute))
        {
            return false;
        }

        attributeId = BinaryPrimitives.ReadUInt32LittleEndian(attribute);
        valueAddress = BinaryPrimitives.ReadUInt64LittleEndian(attribute[0x08..]);
        valueSize = BinaryPrimitives.ReadUInt64LittleEndian(attribute[0x10..]);
        return true;
    }

    [SysAbiExport(
        Nid = "IaZXJ9M79uo",
        ExportName = "sceAudioOut2UserDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserDestroy(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "xywYcRB7nbQ",
        ExportName = "sceAudioOut2UserCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserCreate(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outUserAddress = ctx[CpuRegister.Rsi];
        if ((userId != 0 && userId != 1 && userId != 1000 && userId != 255) || outUserAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextUserHandle);
        return ctx.TryWriteUInt64(outUserAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "XHl38ZNknbs",
        ExportName = "sceAudioOut2MasteringInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2MasteringInit(CpuContext ctx)
        => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "v8iOE+j8a5o",
        ExportName = "sceAudioOut2MasteringSetParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2MasteringSetParam(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        var output = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (parameterAddress == 0 || output > 2)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        if (!ctx.TryReadUInt32(parameterAddress, out _))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    private static bool TryReadContextParameters(
        CpuContext ctx,
        ulong address,
        out uint queueDepth,
        out uint numGrains)
    {
        queueDepth = 0;
        numGrains = 0;
        if (!GuestAddress.TryAdd(address, 0x0C, out var queueDepthAddress) ||
            !GuestAddress.TryAdd(address, 0x10, out var numGrainsAddress) ||
            !ctx.TryReadUInt32(queueDepthAddress, out queueDepth) ||
            !ctx.TryReadUInt32(numGrainsAddress, out numGrains))
        {
            return false;
        }

        queueDepth = queueDepth == 0 ? DefaultQueueDepth : queueDepth;
        numGrains = numGrains == 0 ? DefaultNumGrains : numGrains;
        return true;
    }

    private static bool TryReadPortParameters(
        CpuContext ctx,
        ulong address,
        out ushort portType,
        out uint dataFormat,
        out uint samplingFrequency)
    {
        portType = 0;
        dataFormat = 0;
        samplingFrequency = 0;
        return GuestAddress.TryAdd(address, 0x04, out var dataFormatAddress) &&
            GuestAddress.TryAdd(address, 0x08, out var samplingFrequencyAddress) &&
            ctx.TryReadUInt16(address, out portType) &&
            ctx.TryReadUInt32(dataFormatAddress, out dataFormat) &&
            ctx.TryReadUInt32(samplingFrequencyAddress, out samplingFrequency);
    }

    private static ulong RequiredContextMemorySize(uint queueDepth) =>
        AudioOut2ContextMemoryBaseSize + (queueDepth * AudioOut2QueueMemorySize);

    private static ulong GetSpeakerArrayMemorySize(
        uint speakerCount,
        bool useObjectLayout,
        bool includeCoefficients)
    {
        // Recovered from the firmware 12.70 shared speaker-array sizing path.
        var size = useObjectLayout
            ? GetObjectSpeakerArrayBaseSize(speakerCount) + 0xA0UL
            : GetStandardSpeakerArrayBaseSize(speakerCount) + 0x80UL;
        if (!includeCoefficients)
        {
            return size + 0x100UL;
        }

        var coefficientBytes =
            GetAmbisonicsCoefficientBytes(speakerCount, 5, 0xF0);
        return size + 0x1A0UL + AlignUp32(coefficientBytes + 0x100UL);
    }

    private static ulong GetStandardSpeakerArrayBaseSize(uint speakerCount)
    {
        var countPlusOne = (ulong)unchecked(speakerCount + 1U);
        return AlignUp32(countPlusOne * 8UL) +
               ((ulong)(unchecked(speakerCount + 8U) & ~7U) * 4UL) +
               AlignUp32(countPlusOne * 2UL) +
               AlignUp32(countPlusOne * 0x10UL);
    }

    private static ulong GetObjectSpeakerArrayBaseSize(uint speakerCount)
    {
        const uint objectCount = 3;
        const uint objectStrideSelector = 7;
        var totalCount = unchecked(speakerCount + objectCount);
        var lowCount = totalCount & 0xFFFFU;
        var expandedCount =
            lowCount < 3U ? lowCount : unchecked((lowCount * 2U) - 4U);
        var size = GetSpeakerMixWorkspaceSize(lowCount) +
                   0x60UL +
                   AlignUp32((ulong)totalCount * 0xCUL) +
                   ((ulong)(unchecked(
                       speakerCount + objectCount + 7U) & ~7U) * 4UL) +
                   AlignUp32((ulong)expandedCount * 6UL) +
                   AlignUp32((ulong)expandedCount * 0x30UL);
        size +=
            ((objectStrideSelector * 2UL) + 0x18UL) * objectCount;

        var lastIndex = unchecked(totalCount - 1U);
        if (lastIndex > 0x1FU)
        {
            size += ((lastIndex >> 3) & 0xFFFF_FFFCUL) + 4UL;
        }

        return AlignUp32(size);
    }

    private static ulong GetSpeakerMixWorkspaceSize(uint count)
    {
        var smallCount = count < 3U;
        var doubled = smallCount ? count : unchecked((count * 2U) - 4U);
        var tripled = smallCount ? count : unchecked((count * 3U) - 6U);
        return AlignUp32((ulong)count * 2UL) +
               AlignUp32((ulong)tripled * 4UL) +
               AlignUp32((ulong)doubled * 6UL);
    }

    private static ulong GetAmbisonicsCoefficientBytes(
        uint speakerCount,
        uint order,
        uint stride)
    {
        var alignedSpeakers = unchecked(speakerCount + 7U) & ~7U;
        var alignedStride = unchecked(stride + 7U) & ~7U;
        var coefficientCount = unchecked((order + 1U) * (order + 1U));
        return ((ulong)stride * alignedSpeakers +
                ((ulong)alignedStride + alignedSpeakers) *
                coefficientCount) *
               4UL;
    }

    private static ulong AlignUp32(ulong value) =>
        unchecked(value + 0x1FUL) & ~0x1FUL;

    private static byte GetChannelCount(uint dataFormat)
    {
        var channels = (dataFormat >> 8) & 0xFF;
        return unchecked((byte)(channels == 0 ? 2 : Math.Min(channels, 16)));
    }

    private sealed record SpeakerArrayState(
        ulong WorkspaceAddress,
        ulong WorkspaceSize,
        uint SpeakerCount,
        byte Layout,
        int Mode,
        uint CoefficientConfiguration,
        bool CoefficientFeature,
        byte[] Positions);

    private sealed class ContextState(uint queueDepth, uint numGrains)
    {
        private readonly object _sync = new();
        private uint _queued;

        public uint QueueDepth { get; } = queueDepth;

        public uint NumGrains { get; } = numGrains;

        public void Advance()
        {
            lock (_sync)
            {
                if (_queued != 0)
                {
                    _queued--;
                }
            }
        }

        public bool TryPush(bool blocking)
        {
            lock (_sync)
            {
                if (_queued >= QueueDepth)
                {
                    if (!blocking)
                    {
                        return false;
                    }

                    // There is no host audio sink yet. A blocking push models
                    // one grain being consumed before accepting the next one.
                    _queued--;
                }

                _queued++;
                return true;
            }
        }

        public void GetQueueLevel(
            out uint queueLevel,
            out uint availableQueues)
        {
            lock (_sync)
            {
                queueLevel = _queued;
                availableQueues = _queued < QueueDepth
                    ? QueueDepth - _queued
                    : 0;
            }
        }
    }

    private sealed class PortState(
        ulong contextHandle,
        ushort portType,
        uint dataFormat,
        uint samplingFrequency)
    {
        public ulong ContextHandle { get; } = contextHandle;

        public ushort PortType { get; } = portType;

        public uint DataFormat { get; } = dataFormat;

        public uint SamplingFrequency { get; } = samplingFrequency;

        public ulong PcmDataAddress { get; private set; }

        public void SetPcmData(ulong address)
        {
            PcmDataAddress = address;
        }
    }
}
