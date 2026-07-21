// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelPthreadLifecycle
{
    static KernelPthreadLifecycle()
    {
        GuestThreadExecution.GuestThreadExiting += RunThreadSpecificDestructors;
        GuestThreadExecution.GuestThreadExited += ReleaseSynchronizationState;
        GuestThreadExecution.GuestThreadReaped += ReleaseThreadResources;
    }

    internal static void EnsureInitialized()
    {
    }

    public static void ResetRuntimeState()
    {
        KernelPthreadCompatExports.ResetRuntimeState();
        KernelPthreadExtendedCompatExports.ResetRuntimeState();
    }

    private static void RunThreadSpecificDestructors(
        ulong threadHandle,
        CpuContext context)
    {
        KernelPthreadExtendedCompatExports.RunThreadSpecificDestructors(
            threadHandle,
            context);
    }

    private static void ReleaseSynchronizationState(ulong threadHandle)
    {
        KernelPthreadCompatExports.ReleaseOwnedMutexes(threadHandle);
        KernelPthreadExtendedCompatExports.ReleaseOwnedRwlocks(threadHandle);
        KernelPthreadExtendedCompatExports.ReleaseThreadSpecificValues(threadHandle);
        if (KernelPthreadExtendedCompatExports.IsThreadDetached(threadHandle) &&
            GuestThreadExecution.Scheduler is { } scheduler &&
            !scheduler.RequestThreadReap(threadHandle))
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Failed to request detached pthread reap: thread=0x{threadHandle:X16}");
        }
    }

    private static void ReleaseThreadResources(ulong threadHandle)
    {
        KernelPthreadExtendedCompatExports.ReleaseThreadState(threadHandle);
        KernelPthreadState.ReleaseThreadHandle(threadHandle);
    }
}
