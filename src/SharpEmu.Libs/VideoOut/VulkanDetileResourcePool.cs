// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct VulkanDetileResourceCapacity(
    ulong TiledBytes,
    ulong XTermsBytes,
    ulong YTermsBytes,
    ulong OutputBytes)
{
    internal const ulong MinimumBucketBytes = 4 * 1024;
    internal const ulong MaximumBucketBytes = 64 * 1024 * 1024;

    public bool Fits(in VulkanDetileResourceCapacity required) =>
        TiledBytes >= required.TiledBytes &&
        XTermsBytes >= required.XTermsBytes &&
        YTermsBytes >= required.YTermsBytes &&
        OutputBytes >= required.OutputBytes;

    public bool IsPoolable =>
        TiledBytes <= MaximumBucketBytes &&
        XTermsBytes <= MaximumBucketBytes &&
        YTermsBytes <= MaximumBucketBytes &&
        OutputBytes <= MaximumBucketBytes;

    public bool TryGetTotalBytes(out ulong totalBytes)
    {
        try
        {
            totalBytes = checked(
                TiledBytes +
                XTermsBytes +
                YTermsBytes +
                OutputBytes);
            return true;
        }
        catch (OverflowException)
        {
            totalBytes = 0;
            return false;
        }
    }

    public static VulkanDetileResourceCapacity ForRequirements(
        ulong tiledBytes,
        ulong xTermsBytes,
        ulong yTermsBytes,
        ulong outputBytes) =>
        new(
            BucketSize(tiledBytes),
            BucketSize(xTermsBytes),
            BucketSize(yTermsBytes),
            BucketSize(outputBytes));

    public static ulong BucketSize(ulong requiredBytes)
    {
        if (requiredBytes > MaximumBucketBytes)
        {
            return requiredBytes;
        }

        var bucket = MinimumBucketBytes;
        while (bucket < requiredBytes)
        {
            bucket <<= 1;
        }

        return bucket;
    }
}

internal sealed class BoundedVulkanDetileResourcePool<T>(
    int maxEntries,
    ulong maxRetainedBytes,
    Action<T> destroy)
{
    private readonly List<Entry> _available = [];

    public int Count => _available.Count;

    public ulong RetainedBytes { get; private set; }

    public bool TryRent(
        in VulkanDetileResourceCapacity required,
        out T resource,
        out VulkanDetileResourceCapacity capacity)
    {
        var bestIndex = -1;
        var bestBytes = ulong.MaxValue;
        for (var index = 0; index < _available.Count; index++)
        {
            var candidate = _available[index];
            if (!candidate.Capacity.Fits(required) ||
                !candidate.Capacity.TryGetTotalBytes(out var candidateBytes) ||
                candidateBytes >= bestBytes)
            {
                continue;
            }

            bestIndex = index;
            bestBytes = candidateBytes;
        }

        if (bestIndex < 0)
        {
            resource = default!;
            capacity = default;
            return false;
        }

        var selected = _available[bestIndex];
        _available.RemoveAt(bestIndex);
        RetainedBytes -= bestBytes;
        resource = selected.Resource;
        capacity = selected.Capacity;
        return true;
    }

    public void Return(
        T resource,
        in VulkanDetileResourceCapacity capacity)
    {
        if (maxEntries <= 0 ||
            !capacity.IsPoolable ||
            !capacity.TryGetTotalBytes(out var resourceBytes) ||
            resourceBytes > maxRetainedBytes)
        {
            destroy(resource);
            return;
        }

        while (_available.Count > 0 &&
               (_available.Count >= maxEntries ||
                RetainedBytes > maxRetainedBytes - resourceBytes))
        {
            DestroyOldest();
        }

        if (_available.Count >= maxEntries ||
            RetainedBytes > maxRetainedBytes - resourceBytes)
        {
            destroy(resource);
            return;
        }

        _available.Add(new Entry(resource, capacity));
        RetainedBytes += resourceBytes;
    }

    public void Clear()
    {
        foreach (var entry in _available)
        {
            destroy(entry.Resource);
        }

        _available.Clear();
        RetainedBytes = 0;
    }

    private void DestroyOldest()
    {
        var oldest = _available[0];
        _available.RemoveAt(0);
        if (oldest.Capacity.TryGetTotalBytes(out var bytes))
        {
            RetainedBytes -= bytes;
        }
        else
        {
            RetainedBytes = 0;
        }

        destroy(oldest.Resource);
    }

    private readonly record struct Entry(
        T Resource,
        VulkanDetileResourceCapacity Capacity);
}
