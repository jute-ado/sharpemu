// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Resolves the stable guest-memory identity behind transient decorators.
/// Subsystem state keyed by memory must use this identity so CPU dispatch
/// wrappers for the same address space share one emulated process state.
/// </summary>
public static class CpuMemoryIdentity
{
    private const int MaximumWrapperDepth = 8;

    public static ICpuMemory Resolve(ICpuMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);

        var current = memory;
        for (var depth = 0; depth < MaximumWrapperDepth; depth++)
        {
            if (current is not ICpuMemoryWrapper wrapper ||
                wrapper.Inner is null ||
                ReferenceEquals(wrapper.Inner, current))
            {
                break;
            }

            current = wrapper.Inner;
        }

        return current;
    }
}
