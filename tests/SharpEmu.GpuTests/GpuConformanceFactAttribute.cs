// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.GpuTests;

internal sealed class GpuConformanceFactAttribute : FactAttribute
{
    public GpuConformanceFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_RUN_GPU_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip =
                "Set SHARPEMU_RUN_GPU_TESTS=1 to run tests that require a " +
                "Vulkan device and windowing environment.";
        }
    }
}
