// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu;

internal static class GuestBufferWriteback
{
    private const int PageSize = 4096;

    internal static bool TryPublishChanges(
        ICpuMemory memory,
        ulong guestAddress,
        Span<byte> shadow,
        ReadOnlySpan<byte> output,
        bool enabled,
        out ulong changedBytes)
    {
        ArgumentNullException.ThrowIfNull(memory);
        changedBytes = 0;
        if (!enabled)
        {
            return true;
        }

        if (guestAddress == 0 ||
            shadow.Length == 0 ||
            shadow.Length != output.Length ||
            guestAddress > ulong.MaxValue - (ulong)output.Length)
        {
            return false;
        }

        var livePageBuffer = GuestDataPool.Shared.Rent(
            Math.Min(PageSize, output.Length));
        var allWritesSucceeded = true;
        try
        {
            for (var pageStart = 0;
                 pageStart < output.Length;
                 pageStart += PageSize)
            {
                var pageLength = Math.Min(
                    PageSize,
                    output.Length - pageStart);
                var pageOutput = output.Slice(pageStart, pageLength);
                var pageShadow = shadow.Slice(pageStart, pageLength);
                var pageChanged = false;
                for (var index = 0; index < pageLength; index++)
                {
                    if (pageOutput[index] != pageShadow[index])
                    {
                        pageChanged = true;
                        changedBytes++;
                    }
                }

                if (!pageChanged)
                {
                    continue;
                }

                var pageAddress = guestAddress + (ulong)pageStart;
                var livePage = livePageBuffer.AsSpan(0, pageLength);
                if (memory.TryRead(pageAddress, livePage))
                {
                    for (var index = 0; index < pageLength; index++)
                    {
                        if (pageOutput[index] != pageShadow[index])
                        {
                            livePage[index] = pageOutput[index];
                        }
                    }

                    if (memory.TryWrite(pageAddress, livePage))
                    {
                        for (var index = 0; index < pageLength; index++)
                        {
                            if (pageOutput[index] != pageShadow[index])
                            {
                                pageShadow[index] = pageOutput[index];
                            }
                        }

                        continue;
                    }
                }

                allWritesSucceeded &=
                    TryPublishExactRuns(
                        memory,
                        pageAddress,
                        pageShadow,
                        pageOutput);
            }
        }
        finally
        {
            GuestDataPool.Shared.Return(livePageBuffer);
        }

        return allWritesSucceeded;
    }

    private static bool TryPublishExactRuns(
        ICpuMemory memory,
        ulong pageAddress,
        Span<byte> shadow,
        ReadOnlySpan<byte> output)
    {
        var allWritesSucceeded = true;
        for (var cursor = 0; cursor < output.Length;)
        {
            while (cursor < output.Length &&
                   output[cursor] == shadow[cursor])
            {
                cursor++;
            }

            if (cursor == output.Length)
            {
                break;
            }

            var runStart = cursor;
            while (cursor < output.Length &&
                   output[cursor] != shadow[cursor])
            {
                cursor++;
            }

            var run = output[runStart..cursor];
            if (memory.TryWrite(pageAddress + (ulong)runStart, run))
            {
                run.CopyTo(shadow[runStart..cursor]);
            }
            else
            {
                allWritesSucceeded = false;
            }
        }

        return allWritesSucceeded;
    }
}
