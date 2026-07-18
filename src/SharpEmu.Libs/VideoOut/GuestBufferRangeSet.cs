// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.ShaderCompiler;

internal readonly record struct GuestBufferRange(ulong Start, ulong Length)
{
    public ulong End => checked(Start + Length);
}

internal readonly record struct GuestBufferRangeRequest(
    GuestBufferRange Range,
    bool Writable);

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

    public static List<GuestBufferRangeRequest> MergeRequests(
        IReadOnlyList<GuestBufferRangeRequest> requests)
    {
        var ordered = new List<GuestBufferRangeRequest>(requests.Count);
        foreach (var request in requests)
        {
            if (request.Range.Length != 0)
            {
                ordered.Add(request);
            }
        }

        ordered.Sort(static (left, right) =>
            left.Range.Start.CompareTo(right.Range.Start));
        var merged = new List<GuestBufferRangeRequest>(ordered.Count);
        foreach (var request in ordered)
        {
            if (merged.Count == 0 ||
                request.Range.Start > merged[^1].Range.End)
            {
                merged.Add(request);
                continue;
            }

            var previous = merged[^1];
            var end = Math.Max(previous.Range.End, request.Range.End);
            merged[^1] = new GuestBufferRangeRequest(
                new GuestBufferRange(
                    previous.Range.Start,
                    end - previous.Range.Start),
                previous.Writable || request.Writable);
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
}
