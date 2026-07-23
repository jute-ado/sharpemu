// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Owns process-scoped kernel memory state that must not cross guest sessions.
/// </summary>
public static class KernelMemoryLifecycle
{
    public static void ConfigureFlexibleMemorySize(ulong size)
    {
        KernelMemoryCompatExports.ConfigureFlexibleMemorySize(size);
    }

    public static bool TryReserveStartupFlexibleMemory(ulong size)
    {
        return KernelMemoryCompatExports.TryReserveStartupFlexibleMemory(size);
    }

    public static void ResetRuntimeState()
    {
        KernelMemoryCompatExports.ResetMemoryRuntimeState();
    }
}
