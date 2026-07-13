// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    private const ulong MaximumSecureStringLength = 1_048_576;

    [SysAbiExport(
        Nid = "5Xa2ACNECdo",
        ExportName = "strcpy_s",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int StrcpyS(CpuContext ctx)
        => StrcpySCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx]);

    [SysAbiExport(
        Nid = "YNzNkJzYqEg",
        ExportName = "strncpy_s",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int StrncpyS(CpuContext ctx)
        => StrncpySCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rcx]);

    private static int StrcpySCore(
        CpuContext ctx,
        ulong destination,
        ulong destinationSize,
        ulong source)
    {
        if (destination == 0 || destinationSize == 0)
        {
            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (source == 0)
        {
            if (!TryZeroDestination(ctx, destination, destinationSize))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var probeLimit = Math.Min(destinationSize, MaximumSecureStringLength);
        if (!TryReadCStringBounded(ctx, source, probeLimit, out var bytes, out var terminated))
        {
            _ = TryZeroDestination(ctx, destination, destinationSize);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!terminated || (ulong)bytes.Length + 1 > destinationSize)
        {
            if (!TryZeroDestination(ctx, destination, destinationSize))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = Erange;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryWriteCStringPayload(ctx, destination, bytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int StrncpySCore(
        CpuContext ctx,
        ulong destination,
        ulong destinationSize,
        ulong source,
        ulong count)
    {
        if (destination == 0 || destinationSize == 0)
        {
            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (source == 0)
        {
            if (!TryZeroDestination(ctx, destination, destinationSize))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = Einval;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (count == 0)
        {
            if (!TryWriteTerminator(ctx, destination))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (count == ulong.MaxValue)
        {
            var copyLimit = Math.Min(destinationSize - 1, MaximumSecureStringLength);
            if (!TryReadCStringBounded(ctx, source, copyLimit, out var bytes, out var terminated))
            {
                _ = TryZeroDestination(ctx, destination, destinationSize);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (!TryWriteCStringPayload(ctx, destination, bytes))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = terminated ? 0UL : Struncate;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var boundedCount = Math.Min(count, MaximumSecureStringLength);
        if (!TryReadCStringBounded(ctx, source, boundedCount, out var copiedBytes, out var sourceTerminated))
        {
            _ = TryZeroDestination(ctx, destination, destinationSize);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var requiredSize = sourceTerminated
            ? (ulong)copiedBytes.Length + 1
            : boundedCount + 1;
        if (requiredSize > destinationSize)
        {
            if (!TryZeroDestination(ctx, destination, destinationSize))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = Erange;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryWriteCStringPayload(ctx, destination, copiedBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryReadCStringBounded(
        CpuContext ctx,
        ulong address,
        ulong maxLength,
        out byte[] bytes,
        out bool terminated)
    {
        bytes = Array.Empty<byte>();
        terminated = false;
        if (address == 0)
        {
            return false;
        }

        var limit = (int)Math.Min(maxLength, MaximumSecureStringLength);
        if (limit == 0)
        {
            Span<byte> first = stackalloc byte[1];
            if (!TryReadCompat(ctx, address, first))
            {
                return false;
            }

            terminated = first[0] == 0;
            return true;
        }

        var buffer = new List<byte>(Math.Min(limit, 256));
        Span<byte> one = stackalloc byte[1];
        for (var index = 0; index < limit; index++)
        {
            if (!TryReadCompat(ctx, address + (ulong)index, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                terminated = true;
                bytes = buffer.ToArray();
                return true;
            }

            buffer.Add(one[0]);
        }

        bytes = buffer.ToArray();
        return true;
    }

    private static bool TryWriteCStringPayload(
        CpuContext ctx,
        ulong destination,
        ReadOnlySpan<byte> bytes)
    {
        var payload = new byte[bytes.Length + 1];
        bytes.CopyTo(payload);
        return TryWriteCompat(ctx, destination, payload);
    }

    private static bool TryWriteTerminator(CpuContext ctx, ulong address)
    {
        Span<byte> terminator = stackalloc byte[1];
        terminator.Clear();
        return TryWriteCompat(ctx, address, terminator);
    }

    private static bool TryZeroDestination(
        CpuContext ctx,
        ulong destination,
        ulong destinationSize)
        => destination == 0 || destinationSize == 0 || TryWriteTerminator(ctx, destination);
}
