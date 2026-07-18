// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.ShaderCompiler;

internal readonly record struct GuestBufferRange(ulong Start, ulong Length)
{
    public ulong End => checked(Start + Length);
}

/// <summary>
/// Pure range rules for the persistent Vulkan guest-buffer cache. Keeping
/// these rules independent of Vulkan makes aliasing and in-flight versioning
/// deterministic and directly testable.
/// </summary>
internal static class GuestBufferRangeSet
{
    internal const ulong PortableOffsetAlignment =
        Gen5GlobalMemoryBinding.PortableDescriptorOffsetAlignment;

    public static bool TryCreate(
        ulong address,
        int length,
        out GuestBufferRange range)
    {
        range = default;
        if (address == 0 || length < 0)
        {
            return false;
        }

        var size = (ulong)Math.Max(length, sizeof(uint));
        if (address > ulong.MaxValue - size - 3)
        {
            return false;
        }

        var start = address & ~(PortableOffsetAlignment - 1);
        var end = (address + size + 3) & ~3UL;
        if (end <= start)
        {
            return false;
        }

        range = new GuestBufferRange(start, end - start);
        return true;
    }

    public static List<GuestBufferRange> Merge(
        IReadOnlyList<GuestBufferRange> ranges)
    {
        var ordered = new List<GuestBufferRange>(ranges.Count);
        foreach (var range in ranges)
        {
            if (range.Length != 0)
            {
                ordered.Add(range);
            }
        }

        ordered.Sort(static (left, right) =>
            left.Start.CompareTo(right.Start));
        var merged = new List<GuestBufferRange>(ordered.Count);
        foreach (var range in ordered)
        {
            if (merged.Count == 0 ||
                range.Start > merged[^1].End)
            {
                merged.Add(range);
                continue;
            }

            var previous = merged[^1];
            var end = Math.Max(previous.End, range.End);
            merged[^1] = new GuestBufferRange(
                previous.Start,
                end - previous.Start);
        }

        return merged;
    }

    public static bool Contains(
        GuestBufferRange allocation,
        GuestBufferRange requested) =>
        allocation.Start <= requested.Start &&
        allocation.End >= requested.End;

    public static bool Overlaps(
        GuestBufferRange left,
        GuestBufferRange right) =>
        left.Start < right.End &&
        right.Start < left.End;

    public static GuestBufferRange ExpandToOverlapClosure(
        GuestBufferRange requested,
        IReadOnlyList<GuestBufferRange> allocations)
    {
        var start = requested.Start;
        var end = requested.End;
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            var candidate =
                new GuestBufferRange(start, end - start);
            foreach (var allocation in allocations)
            {
                if (!Overlaps(candidate, allocation))
                {
                    continue;
                }

                var nextStart = Math.Min(start, allocation.Start);
                var nextEnd = Math.Max(end, allocation.End);
                if (nextStart == start && nextEnd == end)
                {
                    continue;
                }

                start = nextStart;
                end = nextEnd;
                expanded = true;
            }
        }

        return new GuestBufferRange(start, end - start);
    }

    public static bool MustVersionReadOnlyMutation(
        bool contentsChanged,
        ulong lastUseSequence,
        ulong completedSequence,
        bool referencedByOpenSubmission) =>
        contentsChanged &&
        (lastUseSequence > completedSequence ||
         referencedByOpenSubmission);

    public static bool MustWaitForPersistentAccess(
        ulong lastUseSequence,
        ulong lastWriteSequence,
        ulong completedSequence,
        bool writable) =>
        lastWriteSequence > completedSequence ||
        (writable && lastUseSequence > completedSequence);
}
