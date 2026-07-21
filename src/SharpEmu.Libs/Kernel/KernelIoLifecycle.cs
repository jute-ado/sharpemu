// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.LibcStdio;

namespace SharpEmu.Libs.Kernel;

public static class KernelIoLifecycle
{
    public static void ResetRuntimeState()
    {
        KernelSocketCompatExports.ResetRuntimeState();
        KernelMemoryCompatExports.ResetIoRuntimeState();
        LibcStdioExports.ResetRuntimeState();
    }
}
