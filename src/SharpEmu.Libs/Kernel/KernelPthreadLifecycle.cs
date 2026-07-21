// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

internal static class KernelPthreadLifecycle
{
    static KernelPthreadLifecycle()
    {
        GuestThreadExecution.GuestThreadExited += ReleaseSynchronizationState;
    }

    internal static void EnsureInitialized()
    {
    }

    private static void ReleaseSynchronizationState(ulong threadHandle)
    {
        KernelPthreadCompatExports.ReleaseOwnedMutexes(threadHandle);
        KernelPthreadExtendedCompatExports.ReleaseOwnedRwlocks(threadHandle);
        KernelPthreadExtendedCompatExports.ReleaseThreadSpecificValues(threadHandle);
    }
}
