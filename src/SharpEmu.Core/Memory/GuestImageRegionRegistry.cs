// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Memory;

internal sealed class GuestImageRegionRegistry
{
    private readonly List<RegisteredImage> _images = [];

    public void Register(IReadOnlyList<VirtualMemoryRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
        {
            return;
        }

        var copiedRegions = new List<VirtualMemoryRegion>(regions.Count);
        var imageStart = ulong.MaxValue;
        var imageEnd = 0UL;
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            if (region.MemorySize == 0)
            {
                continue;
            }

            var regionEnd = checked(
                region.VirtualAddress + region.MemorySize);
            copiedRegions.Add(region);
            imageStart = Math.Min(imageStart, region.VirtualAddress);
            imageEnd = Math.Max(imageEnd, regionEnd);
        }

        if (copiedRegions.Count == 0)
        {
            return;
        }

        var snapshot = copiedRegions.ToArray();
        for (var index = 0; index < _images.Count; index++)
        {
            if (_images[index].Start == imageStart &&
                _images[index].End == imageEnd)
            {
                _images[index] = new RegisteredImage(
                    imageStart,
                    imageEnd,
                    snapshot);
                return;
            }
        }

        _images.Add(new RegisteredImage(imageStart, imageEnd, snapshot));
    }

    public bool TryGet(
        ulong address,
        out IReadOnlyList<VirtualMemoryRegion> regions)
    {
        RegisteredImage? bestMatch = null;
        for (var index = 0; index < _images.Count; index++)
        {
            var image = _images[index];
            if (address < image.Start || address >= image.End)
            {
                continue;
            }

            var belongsToImage = false;
            for (var regionIndex = 0;
                regionIndex < image.Regions.Length;
                regionIndex++)
            {
                var region = image.Regions[regionIndex];
                if (address >= region.VirtualAddress &&
                    address - region.VirtualAddress < region.MemorySize)
                {
                    belongsToImage = true;
                    break;
                }
            }

            if (!belongsToImage)
            {
                continue;
            }

            if (bestMatch is null ||
                image.End - image.Start <
                bestMatch.Value.End - bestMatch.Value.Start)
            {
                bestMatch = image;
            }
        }

        if (bestMatch is { } match)
        {
            regions = match.Regions;
            return true;
        }

        regions = Array.Empty<VirtualMemoryRegion>();
        return false;
    }

    public void Clear() => _images.Clear();

    private readonly record struct RegisteredImage(
        ulong Start,
        ulong End,
        VirtualMemoryRegion[] Regions);
}
