// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(AudioOutSessionStateCollection.Name, DisableParallelization = true)]
public sealed class AudioOutSessionStateCollection
{
    public const string Name = "AudioOut session state";
}

[Collection(AudioOutSessionStateCollection.Name)]
public sealed class AudioOutSessionResetTests
{
    [Fact]
    public void ResetRuntimeStateReopensAfterShutdownAndRestartsPortHandles()
    {
        var context = CreateOpenContext();

        AudioOutLifecycle.ResetRuntimeState();
        try
        {
            Assert.Equal(1, AudioOutExports.AudioOutOpen(context));
            var firstHandle = context[CpuRegister.Rax];

            AudioOutExports.ShutdownAllPorts();
            context[CpuRegister.Rdi] = firstHandle;
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(0, AudioOutExports.AudioOutOutput(context));

            AudioOutLifecycle.ResetRuntimeState();

            context[CpuRegister.Rdi] = firstHandle;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                AudioOutExports.AudioOutOutput(context));

            context = CreateOpenContext();
            Assert.Equal(1, AudioOutExports.AudioOutOpen(context));
            Assert.Equal(1UL, context[CpuRegister.Rax]);
        }
        finally
        {
            AudioOutLifecycle.ResetRuntimeState();
        }
    }

    private static CpuContext CreateOpenContext()
    {
        var context = new CpuContext(new FakeGuestMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rcx] = 256;
        context[CpuRegister.R8] = 48_000;
        context[CpuRegister.R9] = 1;
        return context;
    }
}
