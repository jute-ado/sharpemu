// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

public static class KernelEventObjectLifecycle
{
    public static void ResetRuntimeState()
    {
        KernelEventQueueCompatExports.ResetRuntimeState();
        KernelEventFlagCompatExports.ResetRuntimeState();
        KernelSemaphoreCompatExports.ResetRuntimeState();
        KernelExceptionCompatExports.ResetRuntimeState();
    }
}
