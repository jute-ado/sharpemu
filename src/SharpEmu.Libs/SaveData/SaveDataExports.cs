// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.SaveData;

public static class SaveDataExports
{
    private const int OrbisSaveDataErrorParameter = unchecked((int)0x809F0000);
    private const int OrbisSaveDataErrorBusy = unchecked((int)0x809F0003);
    private const int OrbisSaveDataErrorNotMounted = unchecked((int)0x809F0004);
    private const int OrbisSaveDataErrorExists = unchecked((int)0x809F0007);
    private const int OrbisSaveDataErrorNotFound = unchecked((int)0x809F0008);
    private const int OrbisSaveDataErrorInternal = unchecked((int)0x809F000B);
    private const int OrbisSaveDataErrorMemoryNotReady = unchecked((int)0x809F0012);
    private const int OrbisSaveDataErrorNoEvent = unchecked((int)0x809F0008);
    private const int SaveDataTitleIdSize = 10;
    private const int SaveDataDirNameSize = 32;
    private const int SaveDataParamSize = 0x530;
    private const int SaveDataSearchInfoSize = 0x30;
    private const int SearchCondSize = 0x20;
    private const int SearchResultSize = 0x28;
    private const ulong ResultHitNumOffset = 0x00;
    private const ulong ResultDirNamesOffset = 0x08;
    private const ulong ResultDirNamesNumOffset = 0x10;
    private const ulong ResultSetNumOffset = 0x14;
    private const ulong ResultParamsOffset = 0x18;
    private const ulong ResultInfosOffset = 0x20;
    private const uint SortKeyFreeBlocks = 5;
    private const uint SortOrderDescent = 1;
    private const uint MountModeCreate = 1u << 2;
    private const uint MountModeCreate2 = 1u << 5;
    private const uint UmountModeBackupAsync = 1u << 16;
    private const int MountParamSize = 0x2C;
    private const int MountResultSize = 0x40;
    private const int MountInfoSize = 0x30;
    private const ulong SaveDataBlockSize = 32UL * 1024;
    private const ulong DefaultSaveDataBlocks = 16384;
    // Emulator guard against corrupt or misread sizes, not a platform limit.
    private const ulong SaveDataMemoryMaxSize = 64UL * 1024 * 1024;
    private const int DeleteParamSize = 0x40;
    private const uint ParamTypeAll = 0;
    private const uint ParamTypeTitle = 1;
    private const uint ParamTypeSubtitle = 2;
    private const uint ParamTypeDetail = 3;
    private const uint ParamTypeUserParam = 4;
    private const int ParamTitleOffset = 0x00;
    private const int ParamTitleSize = 0x80;
    private const int ParamSubtitleOffset = 0x80;
    private const int ParamSubtitleSize = 0x80;
    private const int ParamDetailOffset = 0x100;
    private const int ParamDetailSize = 0x400;
    private const int ParamUserParamOffset = 0x500;
    private const int ParamUserParamSize = sizeof(uint);
    private const int ParamMtimeOffset = 0x508;
    private const string ParamMetadataDirectoryName = "sce_sys";
    private const string ParamMetadataFileName = "param.bin";
    private const string IconMetadataFileName = "icon0.png";
    private const int SaveDataIconPathMaxLength = 4096;
    // Emulator guard against corrupt descriptors, not a platform limit.
    private const ulong SaveDataIconMaxSize = 16UL * 1024 * 1024;
    private static readonly object _stateGate = new();
    private static readonly object _memoryGate = new();
    private static readonly HashSet<int> _transactionResources = [];
    private static readonly HashSet<int> _preparedTransactionResources = [];
    private static readonly Dictionary<string, MountedSave> _mountedSaves =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<SaveDataEvent> _events = new();
    private static string? _titleId;
    private static int _nextTransactionResource;
    private readonly record struct SaveDataEvent(
        uint Type,
        int ErrorCode,
        int UserId,
        string TitleId,
        string DirName);
    private readonly record struct MountedSave(
        string Path,
        ulong Blocks,
        int UserId,
        string TitleId,
        string DirName);

    public static void ConfigureApplicationInfo(string? titleId)
    {
        string[] mountedSavePoints;
        lock (_stateGate)
        {
            _titleId = string.IsNullOrWhiteSpace(titleId) ? null : SanitizePathSegment(titleId.Trim());
            _transactionResources.Clear();
            _preparedTransactionResources.Clear();
            mountedSavePoints = _mountedSaves.Keys.ToArray();
            _mountedSaves.Clear();
            _events.Clear();
            _nextTransactionResource = 0;
        }

        foreach (var mountPoint in mountedSavePoints)
        {
            _ = KernelMemoryCompatExports.TryUnregisterGuestPathMount(mountPoint);
        }
    }

    [SysAbiExport(
        Nid = "j8xKtiFj0SY",
        ExportName = "sceSaveDataGetEventResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetEventResult(CpuContext ctx)
    {
        const int eventSize = 0x68;
        var eventAddress = ctx[CpuRegister.Rsi];
        if (eventAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        lock (_stateGate)
        {
            if (_events.Count == 0)
            {
                return ctx.SetReturn(OrbisSaveDataErrorNoEvent);
            }

            var pending = _events.Peek();
            Span<byte> data = stackalloc byte[eventSize];
            data.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(data, pending.Type);
            BinaryPrimitives.WriteInt32LittleEndian(data[0x04..], pending.ErrorCode);
            BinaryPrimitives.WriteInt32LittleEndian(data[0x08..], pending.UserId);
            WriteAscii(data.Slice(0x10, SaveDataTitleIdSize), pending.TitleId);
            WriteAscii(data.Slice(0x20, SaveDataDirNameSize), pending.DirName);
            if (!ctx.Memory.TryWrite(eventAddress, data))
            {
                return ctx.SetReturn(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            _events.Dequeue();
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "ieP6jP138Qo",
        ExportName = "sceSaveDataIsMounted",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataIsMounted(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        uint mounted;
        lock (_stateGate)
        {
            mounted = _mountedSaves.Count == 0 ? 0u : 1u;
        }

        return ctx.TryWriteUInt32(outputAddress, mounted)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "TywrFKCoLGY",
        ExportName = "sceSaveDataInitialize3",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataInitialize3(CpuContext ctx)
    {
        try
        {
            Directory.CreateDirectory(ResolveSaveDataRoot());
            return ctx.SetReturn(0);
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "yKDy8S5yLA0",
        ExportName = "sceSaveDataTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataTerminate(CpuContext ctx)
    {
        lock (_stateGate)
        {
            if (_mountedSaves.Count != 0)
            {
                return ctx.SetReturn(OrbisSaveDataErrorBusy);
            }

            _transactionResources.Clear();
            _preparedTransactionResources.Clear();
            _events.Clear();
            _nextTransactionResource = 0;
        }

        TraceSaveData("terminate");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "dyIhnXq-0SM",
        ExportName = "sceSaveDataDirNameSearch",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataDirNameSearch(CpuContext ctx)
    {
        var condAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (condAddress == 0 || resultAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadSearchCond(ctx, condAddress, out var cond) ||
            !TryReadSearchResult(ctx, resultAddress, out var result))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (cond.UserId < 0 || cond.SortKey > SortKeyFreeBlocks || cond.SortOrder > SortOrderDescent)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        try
        {
            string titleId;
            if (cond.TitleIdAddress == 0)
            {
                titleId = ResolveConfiguredTitleId();
            }
            else if (!TryReadFixedAscii(ctx, cond.TitleIdAddress, SaveDataTitleIdSize, out titleId))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var root = ResolveTitleSaveRoot(cond.UserId, titleId);
            var entries = Directory.Exists(root)
                ? EnumerateSaveDirectories(root, cond.Pattern)
                : [];

            entries = SortEntries(entries, cond.SortKey, cond.SortOrder);
            var setNum = result.DirNamesNum == 0
                ? 0
                : Math.Min(result.DirNamesNum, entries.Count);
            if (setNum != 0)
            {
                if (result.DirNamesAddress == 0)
                {
                    return ctx.SetReturn(OrbisSaveDataErrorParameter);
                }

                var outputCount = checked((ulong)setNum);
                if (!IsOutputArrayRangeValid(
                        result.DirNamesAddress,
                        outputCount,
                        SaveDataDirNameSize) ||
                    (result.ParamsAddress != 0 &&
                     !IsOutputArrayRangeValid(
                         result.ParamsAddress,
                         outputCount,
                         SaveDataParamSize)) ||
                    (result.InfosAddress != 0 &&
                     !IsOutputArrayRangeValid(
                         result.InfosAddress,
                         outputCount,
                         SaveDataSearchInfoSize)))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            if (!ctx.TryWriteUInt32(resultAddress + ResultHitNumOffset, checked((uint)entries.Count)) ||
                !ctx.TryWriteUInt32(resultAddress + ResultSetNumOffset, checked((uint)setNum)))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (setNum == 0)
            {
                TraceSaveData($"dir_name_search user={cond.UserId} title={titleId} hits={entries.Count} set=0 root='{root}'");
                return ctx.SetReturn(0);
            }

            for (var i = 0; i < setNum; i++)
            {
                var entry = entries[i];
                if (!TryWriteFixedAscii(
                        ctx,
                        result.DirNamesAddress + ((ulong)i * SaveDataDirNameSize),
                        SaveDataDirNameSize,
                        entry.Name) ||
                    (result.ParamsAddress != 0 &&
                     !TryWriteParam(ctx, result.ParamsAddress + ((ulong)i * SaveDataParamSize), entry)) ||
                    (result.InfosAddress != 0 &&
                     !TryWriteSearchInfo(ctx, result.InfosAddress + ((ulong)i * SaveDataSearchInfoSize), entry)))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            TraceSaveData($"dir_name_search user={cond.UserId} title={titleId} hits={entries.Count} set={setNum} root='{root}'");
            return ctx.SetReturn(0);
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "ZP4e7rlzOUk",
        ExportName = "sceSaveDataMount3",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataMount3(CpuContext ctx)
    {
        var mountAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (mountAddress == 0 || resultAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!GuestAddress.IsRangeValid(mountAddress, MountParamSize) ||
            !ctx.TryReadInt32(mountAddress, out var userId) ||
            !ctx.TryReadUInt64(mountAddress + 0x08, out var dirNameAddress) ||
            !ctx.TryReadUInt64(mountAddress + 0x10, out var blocks) ||
            !ctx.TryReadUInt64(mountAddress + 0x18, out var systemBlocks) ||
            !ctx.TryReadUInt32(mountAddress + 0x20, out var mountMode) ||
            !ctx.TryReadUInt32(mountAddress + 0x24, out var resource) ||
            !ctx.TryReadUInt32(mountAddress + 0x28, out var mode) ||
            dirNameAddress == 0 ||
            !TryReadFixedAscii(ctx, dirNameAddress, SaveDataDirNameSize, out var dirName))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0 || string.IsNullOrWhiteSpace(dirName))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        try
        {
            var titleId = ResolveConfiguredTitleId();
            var titleRoot = ResolveTitleSaveRoot(userId, titleId);
            if (!TryResolveSaveDirectoryPath(titleRoot, dirName, out var savePath))
            {
                return ctx.SetReturn(OrbisSaveDataErrorParameter);
            }

            var existed = Directory.Exists(savePath);
            var create = (mountMode & MountModeCreate) != 0;
            var createIfMissing = (mountMode & MountModeCreate2) != 0;

            if (!existed && !create && !createIfMissing)
            {
                return ctx.SetReturn(OrbisSaveDataErrorNotFound);
            }

            if (existed && create)
            {
                return ctx.SetReturn(OrbisSaveDataErrorExists);
            }

            if (!existed)
            {
                Directory.CreateDirectory(savePath);
            }

            const string mountPoint = "/savedata0";
            Span<byte> result = stackalloc byte[MountResultSize];
            result.Clear();
            WriteAscii(result[..16], mountPoint);
            BinaryPrimitives.WriteUInt32LittleEndian(result[0x1C..], createIfMissing && !existed ? 1u : 0u);
            if (!ctx.Memory.TryWrite(resultAddress, result))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, savePath);
            lock (_stateGate)
            {
                _mountedSaves[mountPoint] = new MountedSave(
                    savePath,
                    blocks == 0 ? DefaultSaveDataBlocks : blocks,
                    userId,
                    titleId,
                    dirName);
            }

            TraceSaveData(
                $"mount3 user={userId} title={titleId} dir={dirName} blocks={blocks} " +
                $"system_blocks={systemBlocks} mount_mode=0x{mountMode:X} resource={resource} mode={mode} " +
                $"mount_point={mountPoint} created={!existed} root='{savePath}'");
            return ctx.SetReturn(0);
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (ArgumentException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }
    }

    [SysAbiExport(
        Nid = "65VH0Qaaz6s",
        ExportName = "sceSaveDataGetMountInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetMountInfo(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var infoAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0 || infoAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryGetMountedSave(mountPoint, out var mountedSave) ||
            !Directory.Exists(mountedSave.Path))
        {
            return ctx.SetReturn(OrbisSaveDataErrorNotMounted);
        }

        try
        {
            var usedBytes = unchecked((ulong)GetDirectorySize(mountedSave.Path));
            var usedBlocks = usedBytes / SaveDataBlockSize;
            if (usedBytes % SaveDataBlockSize != 0)
            {
                usedBlocks++;
            }

            Span<byte> info = stackalloc byte[MountInfoSize];
            info.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(info, mountedSave.Blocks);
            BinaryPrimitives.WriteUInt64LittleEndian(
                info[0x08..],
                mountedSave.Blocks > usedBlocks ? mountedSave.Blocks - usedBlocks : 0);
            if (!ctx.Memory.TryWrite(infoAddress, info))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceSaveData(
                $"get_mount_info mount_point={mountPoint} blocks={mountedSave.Blocks} " +
                $"used_blocks={usedBlocks} root='{mountedSave.Path}'");
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "85zul--eGXs",
        ExportName = "sceSaveDataSetParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetParam(CpuContext ctx) =>
        TransferParam(ctx, write: true);

    [SysAbiExport(
        Nid = "XgvSuIdnMlw",
        ExportName = "sceSaveDataGetParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetParam(CpuContext ctx) =>
        TransferParam(ctx, write: false);

    private static int TransferParam(CpuContext ctx, bool write)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var rawParamType = ctx[CpuRegister.Rsi];
        var paramBufferAddress = ctx[CpuRegister.Rdx];
        var paramBufferSize = ctx[CpuRegister.Rcx];
        if (mountPointAddress == 0 ||
            paramBufferAddress == 0 ||
            rawParamType > ParamTypeUserParam)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        var paramType = unchecked((uint)rawParamType);
        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        var (fieldOffset, fieldSize) = paramType switch
        {
            ParamTypeAll => (0, SaveDataParamSize),
            ParamTypeTitle => (ParamTitleOffset, ParamTitleSize),
            ParamTypeSubtitle => (ParamSubtitleOffset, ParamSubtitleSize),
            ParamTypeDetail => (ParamDetailOffset, ParamDetailSize),
            ParamTypeUserParam => (ParamUserParamOffset, ParamUserParamSize),
            _ => throw new InvalidOperationException($"Unsupported save-data parameter type {paramType}."),
        };
        if (paramBufferSize < unchecked((ulong)fieldSize) ||
            !GuestAddress.IsRangeValid(paramBufferAddress, unchecked((ulong)fieldSize)))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryGetMountedSavePath(mountPoint, out var savePath))
        {
            return ctx.SetReturn(OrbisSaveDataErrorNotMounted);
        }

        try
        {
            if (!Directory.Exists(savePath))
            {
                return ctx.SetReturn(OrbisSaveDataErrorNotMounted);
            }

            var param = TryReadStoredParam(savePath, out var storedParam)
                ? storedParam
                : CreateDefaultParam(Path.GetFileName(savePath), Directory.GetLastWriteTimeUtc(savePath));
            var field = param.AsSpan(fieldOffset, fieldSize);
            var transferred = write
                ? ctx.Memory.TryRead(paramBufferAddress, field)
                : ctx.Memory.TryWrite(paramBufferAddress, field);
            if (!transferred)
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (write)
            {
                WriteStoredParam(savePath, param);
            }
            TraceSaveData(
                $"{(write ? "set" : "get")}_param mount_point={mountPoint} " +
                $"type={paramType} size=0x{paramBufferSize:X} root='{savePath}'");
            return ctx.SetReturn(0);
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "c88Yy54Mx0w",
        ExportName = "sceSaveDataSaveIcon",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSaveIcon(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var iconAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0 || iconAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint) ||
            !ctx.TryReadUInt64(iconAddress, out var bufferAddress) ||
            !ctx.TryReadUInt64(iconAddress + 0x08, out var bufferSize) ||
            !ctx.TryReadUInt64(iconAddress + 0x10, out var dataSize))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var writeSize = Math.Min(bufferSize, dataSize);
        if (string.IsNullOrWhiteSpace(mountPoint) ||
            bufferAddress == 0 ||
            writeSize > SaveDataIconMaxSize ||
            !GuestAddress.IsRangeValid(bufferAddress, writeSize))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryGetMountedSavePath(mountPoint, out var savePath))
        {
            return ctx.SetReturn(OrbisSaveDataErrorNotMounted);
        }

        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, checked((int)writeSize)));
        try
        {
            var payload = rented.AsSpan(0, checked((int)writeSize));
            if (!ctx.Memory.TryRead(bufferAddress, payload))
            {
                return ctx.SetReturn(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            WriteIconAtomically(savePath, payload);
            TraceSaveData(
                $"save_icon mount_point={mountPoint} size=0x{writeSize:X} root='{savePath}'");
            return ctx.SetReturn(0);
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    [SysAbiExport(
        Nid = "cGjO3wM3V28",
        ExportName = "sceSaveDataLoadIcon",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataLoadIcon(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var iconAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0 || iconAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint) ||
            !ctx.TryReadUInt64(iconAddress, out var bufferAddress) ||
            !ctx.TryReadUInt64(iconAddress + 0x08, out var bufferSize))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint) ||
            bufferAddress == 0 ||
            bufferSize > SaveDataIconMaxSize)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryGetMountedSavePath(mountPoint, out var savePath))
        {
            return ctx.SetReturn(OrbisSaveDataErrorNotMounted);
        }

        var iconPath = Path.Combine(
            savePath,
            ParamMetadataDirectoryName,
            IconMetadataFileName);
        if (!File.Exists(iconPath))
        {
            return ctx.SetReturn(OrbisSaveDataErrorNotFound);
        }

        try
        {
            if ((ulong)new FileInfo(iconPath).Length > SaveDataIconMaxSize)
            {
                return ctx.SetReturn(OrbisSaveDataErrorInternal);
            }

            var payload = File.ReadAllBytes(iconPath);
            if ((ulong)payload.Length > SaveDataIconMaxSize)
            {
                return ctx.SetReturn(OrbisSaveDataErrorInternal);
            }

            var copySize = Math.Min(bufferSize, (ulong)payload.Length);
            if (!GuestAddress.IsRangeValid(bufferAddress, copySize) ||
                !ctx.Memory.TryWrite(
                    bufferAddress,
                    payload.AsSpan(0, checked((int)copySize))) ||
                !ctx.TryWriteUInt64(iconAddress + 0x10, (ulong)payload.Length))
            {
                return ctx.SetReturn(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceSaveData(
                $"load_icon mount_point={mountPoint} copied=0x{copySize:X} " +
                $"size=0x{payload.Length:X} root='{savePath}'");
            return ctx.SetReturn(0);
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "Z7z6HXWORJY",
        ExportName = "sceSaveDataSaveIconByPath",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSaveIconByPath(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var sourcePathAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0 || sourcePathAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint) ||
            !KernelMemoryCompatExports.TryReadNullTerminatedUtf8(
                ctx,
                sourcePathAddress,
                SaveDataIconPathMaxLength,
                out var sourceGuestPath))
        {
            return ctx.SetReturn(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint) ||
            string.IsNullOrWhiteSpace(sourceGuestPath))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryGetMountedSavePath(mountPoint, out var savePath))
        {
            return ctx.SetReturn(OrbisSaveDataErrorNotMounted);
        }

        if (!KernelMemoryCompatExports.TryResolveGuestPath(
                sourceGuestPath,
                out var sourceHostPath))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!File.Exists(sourceHostPath))
        {
            return ctx.SetReturn(OrbisSaveDataErrorNotFound);
        }

        try
        {
            if ((ulong)new FileInfo(sourceHostPath).Length > SaveDataIconMaxSize)
            {
                return ctx.SetReturn(OrbisSaveDataErrorParameter);
            }

            var payload = File.ReadAllBytes(sourceHostPath);
            if ((ulong)payload.Length > SaveDataIconMaxSize)
            {
                return ctx.SetReturn(OrbisSaveDataErrorParameter);
            }

            WriteIconAtomically(savePath, payload);
            TraceSaveData(
                $"save_icon_by_path mount_point={mountPoint} size=0x{payload.Length:X} " +
                $"source='{sourceGuestPath}' root='{savePath}'");
            return ctx.SetReturn(0);
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
    }

    private static void WriteIconAtomically(string savePath, ReadOnlySpan<byte> payload)
    {
        var metadataPath = Path.Combine(savePath, ParamMetadataDirectoryName);
        Directory.CreateDirectory(metadataPath);
        var iconPath = Path.Combine(metadataPath, IconMetadataFileName);
        var temporaryPath = Path.Combine(
            metadataPath,
            $".{IconMetadataFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, payload);
            File.Move(temporaryPath, iconPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    [SysAbiExport(
        Nid = "S1GkePI17zQ",
        ExportName = "sceSaveDataDelete",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataDelete(CpuContext ctx)
    {
        var deleteAddress = ctx[CpuRegister.Rdi];
        if (deleteAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!GuestAddress.IsRangeValid(deleteAddress, DeleteParamSize) ||
            !ctx.TryReadInt32(deleteAddress, out var userId) ||
            !ctx.TryReadUInt64(deleteAddress + 0x08, out var titleIdAddress) ||
            !ctx.TryReadUInt64(deleteAddress + 0x10, out var dirNameAddress))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0 || dirNameAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, dirNameAddress, SaveDataDirNameSize, out var dirName))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        string titleId;
        if (titleIdAddress == 0)
        {
            titleId = ResolveConfiguredTitleId();
        }
        else if (!TryReadFixedAscii(ctx, titleIdAddress, SaveDataTitleIdSize, out titleId))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(titleId) || string.IsNullOrWhiteSpace(dirName))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        try
        {
            var titleRoot = ResolveTitleSaveRoot(userId, titleId);
            if (!TryResolveSaveDirectoryPath(titleRoot, dirName, out var savePath))
            {
                return ctx.SetReturn(OrbisSaveDataErrorParameter);
            }

            if (IsSavePathMounted(savePath))
            {
                return ctx.SetReturn(OrbisSaveDataErrorBusy);
            }

            if (Directory.Exists(savePath))
            {
                Directory.Delete(savePath, recursive: true);
            }

            TraceSaveData(
                $"delete user={userId} title={titleId} dir={dirName} root='{savePath}'");
            return ctx.SetReturn(0);
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (ArgumentException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }
    }

    [SysAbiExport(
        Nid = "gjRZNnw0JPE",
        ExportName = "sceSaveDataCreateTransactionResource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataCreateTransactionResource(CpuContext ctx)
    {
        var memorySize = ctx[CpuRegister.Rdi];
        int resource;
        lock (_stateGate)
        {
            resource = ++_nextTransactionResource;
            _transactionResources.Add(resource);
        }

        TraceSaveData(
            $"create_transaction_resource memory_size=0x{memorySize:X} " +
            $"resource={resource}");
        return ctx.SetReturn(resource);
    }

    [SysAbiExport(
        Nid = "lJUQuaKqoKY",
        ExportName = "sceSaveDataDeleteTransactionResource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataDeleteTransactionResource(CpuContext ctx)
    {
        var resource = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_stateGate)
        {
            _transactionResources.Remove(resource);
            _preparedTransactionResources.Remove(resource);
        }

        TraceSaveData($"delete_transaction_resource resource={resource}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "sDCBrmc61XU",
        ExportName = "sceSaveDataPrepare",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataPrepare(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var resource = unchecked((int)ctx[CpuRegister.Rdx]);
        if (mountPointAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        lock (_stateGate)
        {
            if (resource != 0)
            {
                _preparedTransactionResources.Add(resource);
            }
        }

        TraceSaveData($"prepare mount_point={mountPoint} resource={resource}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "ie7qhZ4X0Cc",
        ExportName = "sceSaveDataCommit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataCommit(CpuContext ctx)
    {
        var commitAddress = ctx[CpuRegister.Rdi];
        if (commitAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        lock (_stateGate)
        {
            _preparedTransactionResources.Clear();
        }

        TraceSaveData($"commit commit=0x{commitAddress:X16}");
        return ctx.SetReturn(0);
    }

    // Save data memory is a small per-user blob that does not require a mount.
    [SysAbiExport(
        Nid = "oQySEUfgXRA",
        ExportName = "sceSaveDataSetupSaveDataMemory2",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetupSaveDataMemory2(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!ctx.TryReadInt32(paramAddress + 0x04, out var userId) ||
            !ctx.TryReadUInt64(paramAddress + 0x08, out var memorySize))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0 || memorySize == 0 || memorySize > SaveDataMemoryMaxSize)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        try
        {
            var path = ResolveSaveDataMemoryPath(userId);
            lock (_memoryGate)
            {
                var backing = new FileInfo(path);
                var existingSize = backing.Exists ? (ulong)backing.Length : 0;

                // Validate the guest result before mutating persistent state.
                if (resultAddress != 0 && !ctx.TryWriteUInt64(resultAddress, existingSize))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (existingSize < memorySize)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                    stream.SetLength((long)memorySize);
                }

                TraceSaveData($"memory-setup2 user={userId} size=0x{memorySize:X} existed=0x{existingSize:X}");
            }

            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "QwOO7vegnV8",
        ExportName = "sceSaveDataGetSaveDataMemory2",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataMemory2(CpuContext ctx) =>
        TransferSaveDataMemory(ctx, write: false);

    [SysAbiExport(
        Nid = "cduy9v4YmT4",
        ExportName = "sceSaveDataSetSaveDataMemory2",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetSaveDataMemory2(CpuContext ctx) =>
        TransferSaveDataMemory(ctx, write: true);

    [SysAbiExport(
        Nid = "wiT9jeC7xPw",
        ExportName = "sceSaveDataSyncSaveDataMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSyncSaveDataMemory(CpuContext ctx)
    {
        var syncAddress = ctx[CpuRegister.Rdi];
        if (syncAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!ctx.TryReadInt32(syncAddress, out var userId))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!File.Exists(ResolveSaveDataMemoryPath(userId)))
        {
            return ctx.SetReturn(OrbisSaveDataErrorMemoryNotReady);
        }

        var titleId = ResolveConfiguredTitleId();
        lock (_stateGate)
        {
            _events.Enqueue(new SaveDataEvent(
                Type: 3,
                ErrorCode: 0,
                UserId: userId,
                TitleId: titleId,
                DirName: string.Empty));
        }

        return ctx.SetReturn(0);
    }

    private static int TransferSaveDataMemory(CpuContext ctx, bool write)
    {
        var requestAddress = ctx[CpuRegister.Rdi];
        if (requestAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!ctx.TryReadInt32(requestAddress, out var userId) ||
            !ctx.TryReadUInt64(requestAddress + 0x08, out var dataAddress))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        try
        {
            var path = ResolveSaveDataMemoryPath(userId);
            lock (_memoryGate)
            {
                if (!File.Exists(path))
                {
                    return ctx.SetReturn(OrbisSaveDataErrorMemoryNotReady);
                }

                if (dataAddress == 0)
                {
                    return ctx.SetReturn(0);
                }

                if (!TryReadMemoryData(ctx, dataAddress, out var bufferAddress, out var bufferSize, out var offset))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                using var stream = new FileStream(
                    path, FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read);
                var length = (ulong)stream.Length;
                if (bufferAddress == 0 || bufferSize > length || offset > length - bufferSize)
                {
                    return ctx.SetReturn(OrbisSaveDataErrorParameter);
                }

                var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(bufferSize, 1));
                try
                {
                    var span = buffer.AsSpan(0, (int)bufferSize);
                    stream.Seek((long)offset, SeekOrigin.Begin);
                    if (write)
                    {
                        // Read all guest bytes first so a fault cannot partially modify the save.
                        if (!ctx.Memory.TryRead(bufferAddress, span))
                        {
                            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                        }

                        stream.Write(span);
                    }
                    else
                    {
                        stream.ReadExactly(span);
                        if (!ctx.Memory.TryWrite(bufferAddress, span))
                        {
                            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                TraceSaveData(
                    $"memory-{(write ? "set2" : "get2")} user={userId} offset=0x{offset:X} size=0x{bufferSize:X}");
                return ctx.SetReturn(0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "uW4vfTwMQVo",
        ExportName = "sceSaveDataUmount2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataUmount2(CpuContext ctx)
    {
        var mode = unchecked((uint)ctx[CpuRegister.Rdi]);
        var mountPointAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        MountedSave mountedSave;
        lock (_stateGate)
        {
            if (!_mountedSaves.Remove(mountPoint, out mountedSave))
            {
                return ctx.SetReturn(OrbisSaveDataErrorNotFound);
            }

            if ((mode & UmountModeBackupAsync) != 0)
            {
                _events.Enqueue(new SaveDataEvent(
                    Type: 1,
                    ErrorCode: 0,
                    UserId: mountedSave.UserId,
                    TitleId: mountedSave.TitleId,
                    DirName: mountedSave.DirName));
            }
        }

        var unregistered = KernelMemoryCompatExports.TryUnregisterGuestPathMount(mountPoint);
        TraceSaveData(
            $"umount2 mode=0x{mode:X8} mount_point={mountPoint} " +
            $"backup_async={(mode & UmountModeBackupAsync) != 0} unregistered={unregistered}");
        return ctx.SetReturn(0);
    }

    private static bool TryReadSearchCond(CpuContext ctx, ulong address, out SearchCond cond)
    {
        cond = default;
        if (!GuestAddress.IsRangeValid(address, SearchCondSize) ||
            !ctx.TryReadInt32(address, out var userId) ||
            !ctx.TryReadUInt64(address + 0x08, out var titleIdAddress) ||
            !ctx.TryReadUInt64(address + 0x10, out var dirNameAddress) ||
            !ctx.TryReadUInt32(address + 0x18, out var sortKey) ||
            !ctx.TryReadUInt32(address + 0x1C, out var sortOrder))
        {
            return false;
        }

        string pattern;
        if (dirNameAddress == 0)
        {
            pattern = string.Empty;
        }
        else if (!TryReadFixedAscii(ctx, dirNameAddress, SaveDataDirNameSize, out pattern))
        {
            return false;
        }

        cond = new SearchCond(userId, titleIdAddress, pattern, sortKey, sortOrder);
        return true;
    }

    private static bool TryReadSearchResult(CpuContext ctx, ulong address, out SearchResult result)
    {
        result = default;
        if (!GuestAddress.IsRangeValid(address, SearchResultSize) ||
            !ctx.TryReadUInt64(address + ResultDirNamesOffset, out var dirNamesAddress) ||
            !ctx.TryReadUInt32(address + ResultDirNamesNumOffset, out var dirNamesNum) ||
            !ctx.TryReadUInt64(address + ResultParamsOffset, out var paramsAddress) ||
            !ctx.TryReadUInt64(address + ResultInfosOffset, out var infosAddress))
        {
            return false;
        }

        result = new SearchResult(dirNamesAddress, dirNamesNum, paramsAddress, infosAddress);
        return true;
    }

    private static List<SaveEntry> EnumerateSaveDirectories(string root, string pattern)
    {
        var entries = new List<SaveEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) ||
                name.StartsWith("sce_", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(pattern) && !MatchPattern(name, pattern)))
            {
                continue;
            }

            var info = new DirectoryInfo(directory);
            entries.Add(new SaveEntry(name, directory, info.LastWriteTimeUtc));
        }

        return entries;
    }

    private static List<SaveEntry> SortEntries(List<SaveEntry> entries, uint sortKey, uint sortOrder)
    {
        IOrderedEnumerable<SaveEntry> sorted = sortKey switch
        {
            3 => entries.OrderBy(entry => entry.LastWriteUtc),
            _ => entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };

        var list = sorted.ToList();
        if (sortOrder == SortOrderDescent)
        {
            list.Reverse();
        }

        return list;
    }

    private static bool TryWriteParam(CpuContext ctx, ulong address, SaveEntry entry)
    {
        var param = TryReadStoredParam(entry.Path, out var storedParam)
            ? storedParam
            : CreateDefaultParam(entry.Name, entry.LastWriteUtc);
        return ctx.Memory.TryWrite(address, param);
    }

    private static byte[] CreateDefaultParam(string name, DateTime lastWriteUtc)
    {
        var param = new byte[SaveDataParamSize];
        WriteAscii(param.AsSpan(ParamTitleOffset, ParamTitleSize), "Saved Data");
        WriteAscii(param.AsSpan(ParamDetailOffset, ParamDetailSize), name);
        BinaryPrimitives.WriteInt64LittleEndian(
            param.AsSpan(ParamMtimeOffset, sizeof(long)),
            new DateTimeOffset(lastWriteUtc).ToUnixTimeSeconds());
        return param;
    }

    private static bool TryReadStoredParam(string savePath, out byte[] param)
    {
        param = [];
        var metadataPath = GetParamMetadataPath(savePath);
        if (!File.Exists(metadataPath) ||
            new FileInfo(metadataPath).Length != SaveDataParamSize)
        {
            return false;
        }

        param = File.ReadAllBytes(metadataPath);
        return param.Length == SaveDataParamSize;
    }

    private static void WriteStoredParam(string savePath, byte[] param)
    {
        if (param.Length != SaveDataParamSize)
        {
            throw new ArgumentException(
                $"Save-data parameter metadata must be exactly 0x{SaveDataParamSize:X} bytes.",
                nameof(param));
        }

        var metadataPath = GetParamMetadataPath(savePath);
        var metadataDirectory = Path.GetDirectoryName(metadataPath)
            ?? throw new InvalidOperationException("Save-data metadata path has no parent directory.");
        Directory.CreateDirectory(metadataDirectory);
        var temporaryPath = string.Concat(metadataPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, param);
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetParamMetadataPath(string savePath) =>
        Path.Combine(savePath, ParamMetadataDirectoryName, ParamMetadataFileName);

    private static bool TryWriteSearchInfo(CpuContext ctx, ulong address, SaveEntry entry)
    {
        var size = GetDirectorySize(entry.Path);
        var usedBlocks = checked((ulong)((size + 32767) / 32768));
        var blocks = Math.Max(96UL, usedBlocks);
        Span<byte> info = stackalloc byte[SaveDataSearchInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x00..], blocks);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x08..], blocks - usedBlocks);
        return ctx.Memory.TryWrite(address, info);
    }

    private static bool IsOutputArrayRangeValid(ulong address, ulong count, int elementSize)
    {
        var unsignedElementSize = checked((ulong)elementSize);
        return count <= ulong.MaxValue / unsignedElementSize &&
               GuestAddress.IsRangeValid(address, count * unsignedElementSize);
    }

    private static long GetDirectorySize(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    private static bool MatchPattern(string value, string pattern) =>
        MatchPattern(value.AsSpan(), pattern.AsSpan());

    private static bool MatchPattern(ReadOnlySpan<char> value, ReadOnlySpan<char> pattern)
    {
        if (pattern.IsEmpty)
        {
            return value.IsEmpty;
        }

        if (pattern[0] == '%')
        {
            for (var i = 0; i <= value.Length; i++)
            {
                if (MatchPattern(value[i..], pattern[1..]))
                {
                    return true;
                }
            }

            return false;
        }

        if (value.IsEmpty)
        {
            return false;
        }

        if (pattern[0] == '_' ||
            char.ToUpperInvariant(pattern[0]) == char.ToUpperInvariant(value[0]))
        {
            return MatchPattern(value[1..], pattern[1..]);
        }

        return false;
    }

    private static string ResolveTitleSaveRoot(int userId, string titleId) =>
        Path.Combine(ResolveSaveDataRoot(), userId.ToString(), SanitizePathSegment(titleId));

    private static string ResolveSaveDataMemoryPath(int userId) =>
        Path.Combine(ResolveTitleSaveRoot(userId, ResolveConfiguredTitleId()), "sce_sdmemory", "memory.dat");

    private static bool TryReadMemoryData(
        CpuContext ctx, ulong address, out ulong buffer, out ulong size, out ulong offset)
    {
        size = 0;
        offset = 0;
        return ctx.TryReadUInt64(address, out buffer) &&
            ctx.TryReadUInt64(address + 0x08, out size) &&
            ctx.TryReadUInt64(address + 0x10, out offset);
    }

    private static bool TryResolveSaveDirectoryPath(
        string titleRoot,
        string dirName,
        out string savePath)
    {
        savePath = string.Empty;
        if (string.IsNullOrWhiteSpace(dirName) ||
            dirName is "." or "..")
        {
            return false;
        }

        var root = Path.GetFullPath(titleRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, SanitizePathSegment(dirName)));
        var rootPrefix = string.Concat(Path.TrimEndingDirectorySeparator(root), Path.DirectorySeparatorChar);
        if (!candidate.StartsWith(rootPrefix, PathComparison))
        {
            return false;
        }

        savePath = candidate;
        return true;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);

    private static bool TryGetMountedSavePath(string mountPoint, out string savePath)
    {
        if (TryGetMountedSave(mountPoint, out var mountedSave))
        {
            savePath = mountedSave.Path;
            return true;
        }

        savePath = string.Empty;
        return false;
    }

    private static bool TryGetMountedSave(string mountPoint, out MountedSave mountedSave)
    {
        lock (_stateGate)
        {
            return _mountedSaves.TryGetValue(mountPoint, out mountedSave);
        }
    }

    private static bool IsSavePathMounted(string savePath)
    {
        lock (_stateGate)
        {
            return _mountedSaves.Values.Any(mountedSave =>
                PathsEqual(mountedSave.Path, savePath));
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string ResolveSaveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "user", "savedata")
            : configured;
        return Path.GetFullPath(root);
    }

    private static string ResolveConfiguredTitleId()
    {
        lock (_stateGate)
        {
            if (!string.IsNullOrWhiteSpace(_titleId))
            {
                return _titleId;
            }
        }

        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        var app0Name = string.IsNullOrWhiteSpace(app0Root)
            ? null
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(app0Root));
        if (!string.IsNullOrWhiteSpace(app0Name))
        {
            var candidate = app0Name.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return SanitizePathSegment(candidate);
            }
        }

        return "default";
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private static bool TryReadFixedAscii(CpuContext ctx, ulong address, int length, out string value)
    {
        value = string.Empty;
        Span<byte> buffer = stackalloc byte[length];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            return false;
        }

        var stringLength = buffer.IndexOf((byte)0);
        if (stringLength < 0)
        {
            stringLength = buffer.Length;
        }

        value = Encoding.ASCII.GetString(buffer[..stringLength]);
        return true;
    }

    private static bool TryWriteFixedAscii(CpuContext ctx, ulong address, int length, string value)
    {
        Span<byte> buffer = stackalloc byte[length];
        buffer.Clear();
        WriteAscii(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        var count = Math.Min(value.Length, Math.Max(0, destination.Length - 1));
        for (var i = 0; i < count; i++)
        {
            var ch = value[i];
            destination[i] = ch <= 0x7F ? (byte)ch : (byte)'?';
        }
    }

    private static void TraceSaveData(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SAVEDATA"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] savedata.{message}");
        }
    }

    private readonly record struct SearchCond(
        int UserId,
        ulong TitleIdAddress,
        string Pattern,
        uint SortKey,
        uint SortOrder);

    private readonly record struct SearchResult(
        ulong DirNamesAddress,
        uint DirNamesNum,
        ulong ParamsAddress,
        ulong InfosAddress);

    private readonly record struct SaveEntry(string Name, string Path, DateTime LastWriteUtc);
}
