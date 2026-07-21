// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(AvPlayerSessionStateCollection.Name, DisableParallelization = true)]
public sealed class AvPlayerSessionStateCollection
{
    public const string Name = "AvPlayer session state";
}

[Collection(AvPlayerSessionStateCollection.Name)]
public sealed class AvPlayerSessionResetTests
{
    private const ulong Handle = 0xA0_0000_0001;

    [Fact]
    public void ResetRuntimeStateDisposesResourcesAndInvalidatesPlayers()
    {
        var decoderOutput = new MemoryStream();
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);

        AvPlayerLifecycle.ResetRuntimeState();
        try
        {
            AvPlayerExports.RegisterPlayerForTest(
                Handle,
                width: 1280,
                height: 720,
                durationMilliseconds: 1_000,
                decoderOutput);

            context[CpuRegister.Rdi] = Handle;
            Assert.Equal(2, AvPlayerExports.AvPlayerStreamCount(context));

            AvPlayerLifecycle.ResetRuntimeState();

            Assert.Throws<ObjectDisposedException>(() => decoderOutput.WriteByte(1));
            context[CpuRegister.Rdi] = Handle;
            Assert.NotEqual(0, AvPlayerExports.AvPlayerStreamCount(context));
            Assert.NotEqual(0, AvPlayerExports.AvPlayerClose(context));
        }
        finally
        {
            AvPlayerLifecycle.ResetRuntimeState();
            decoderOutput.Dispose();
        }
    }
}
