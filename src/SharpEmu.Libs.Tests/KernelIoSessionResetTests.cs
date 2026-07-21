// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(
    KernelIoSessionStateCollection.Name,
    DisableParallelization = true)]
public sealed class KernelIoSessionStateCollection
{
    public const string Name = "Kernel I/O session state";
}

[Collection(KernelIoSessionStateCollection.Name)]
public sealed class KernelIoSessionResetTests
{
    private const ulong PathAddress = 0x1_0000_1000;

    [Fact]
    public void ResetRuntimeStateClosesFilesClearsMountsAndRestartsDescriptors()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpEmu.Tests",
            $"io-session-{Guid.NewGuid():N}");
        var mount = $"/io-session-{Guid.NewGuid():N}";
        var hostPath = Path.Combine(root, "data.bin");
        Directory.CreateDirectory(root);
        File.WriteAllText(hostPath, "session data");

        var memory = new FakeGuestMemory();
        memory.AddRegion(PathAddress, new byte[0x1000]);
        var context = new CpuContext(memory, Generation.Gen5);

        KernelIoLifecycle.ResetRuntimeState();
        KernelMemoryCompatExports.RegisterGuestPathMount(mount, root);
        try
        {
            WritePath(memory, $"{mount}/data.bin");
            context[CpuRegister.Rdi] = PathAddress;
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelExports.KernelOpen(context));
            var firstDescriptor = unchecked((int)context[CpuRegister.Rax]);
            Assert.Equal(3, firstDescriptor);

            KernelIoLifecycle.ResetRuntimeState();

            Assert.False(KernelMemoryCompatExports.TryUnregisterGuestPathMount(mount));
            File.Delete(hostPath);
            Assert.False(File.Exists(hostPath));

            context[CpuRegister.Rdi] = unchecked((ulong)firstDescriptor);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                KernelMemoryCompatExports.KernelClose(context));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSocketCompatExports.Socket(context));
            var socketDescriptor = unchecked((int)context[CpuRegister.Rax]);
            Assert.Equal(3, socketDescriptor);

            KernelIoLifecycle.ResetRuntimeState();

            context[CpuRegister.Rdi] = unchecked((ulong)socketDescriptor);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                KernelMemoryCompatExports.KernelClose(context));

            WritePath(memory, "/dev/urandom");
            context[CpuRegister.Rdi] = PathAddress;
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.KernelOpenUnderscore(context));
            Assert.Equal(3, unchecked((int)context[CpuRegister.Rax]));
        }
        finally
        {
            KernelIoLifecycle.ResetRuntimeState();
            _ = KernelMemoryCompatExports.TryUnregisterGuestPathMount(mount);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WritePath(FakeGuestMemory memory, string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path + '\0');
        Assert.True(memory.TryWrite(PathAddress, bytes));
    }
}
