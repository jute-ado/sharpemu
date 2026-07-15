// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class NativeBackendConstructionTests
{
    [Fact]
    public void ConstructorReleasesFirstTlsSlotWhenSecondAllocationFails()
    {
        var threading = new RecordingHostThreading([17u, uint.MaxValue]);
        var platform = new StubHostPlatform(threading);

        var exception = Assert.Throws<OutOfMemoryException>(() =>
            new DirectExecutionBackend(
                new ModuleManager(),
                platform,
                new StubFaultHandling()));

        Assert.Equal("Failed to allocate native TLS slots", exception.Message);
        Assert.Equal([17u], threading.FreedSlots);
    }

    [Fact]
    public void ConstructorReleasesBothTlsSlotsWhenRequiredSymbolIsMissing()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var platform = new StubHostPlatform(threading);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new DirectExecutionBackend(
                new ModuleManager(),
                platform,
                new StubFaultHandling()));

        Assert.Equal("Failed to resolve kernel32!TlsGetValue", exception.Message);
        Assert.Equal([17u, 23u], threading.FreedSlots);
    }

    [Fact]
    public void ConstructorReleasesTlsBaseWhenHostStackStorageAllocationFails()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(failedAllocation: 2);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));

        var exception = Assert.Throws<OutOfMemoryException>(() =>
            new DirectExecutionBackend(
                new ModuleManager(),
                platform,
                new StubFaultHandling()));

        Assert.Equal("Failed to allocate host stack slot storage", exception.Message);
        Assert.Equal([17u, 23u], threading.FreedSlots);
        Assert.Single(memory.FreedAddresses);
        Assert.Empty(memory.ActiveAllocations);
    }

    [Fact]
    public void DisposeCanBeCalledTwiceAfterSuccessfulConstruction()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(failedAllocation: int.MaxValue);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));

        backend.Dispose();
        var secondDisposeException = Record.Exception(backend.Dispose);

        Assert.Null(secondDisposeException);
        Assert.Equal([17u, 23u], threading.FreedSlots);
        Assert.Empty(memory.ActiveAllocations);
    }

    [Fact]
    public void ConstructorCreatesReturnStubsWritableThenFinalizesExecutable()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(failedAllocation: int.MaxValue);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        using var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));

        Assert.Collection(
            memory.AllocationCalls.Skip(2),
            allocation =>
            {
                Assert.Equal(4096UL, allocation.Size);
                Assert.Equal(HostPageProtection.ReadWrite, allocation.Protection);
            },
            allocation =>
            {
                Assert.Equal(256UL, allocation.Size);
                Assert.Equal(HostPageProtection.ReadWrite, allocation.Protection);
            });
        Assert.All(
            memory.ProtectionCalls,
            protection => Assert.Equal(HostPageProtection.ReadExecute, protection.Protection));
    }

    [Theory]
    [InlineData(1, "Failed to install raw exception handler", 1, 0)]
    [InlineData(2, "Failed to install exception handler", 2, 1)]
    public void ConstructorRejectsFailedExceptionHandlerInstallationAndCleansUp(
        int failedInstallation,
        string expectedMessage,
        int expectedFreedThunks,
        int expectedRemovedHandlers)
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(failedAllocation: int.MaxValue);
        var faultHandling = new StubFaultHandling(
            succeed: true,
            failedHandlerInstallation: failedInstallation);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        DirectExecutionBackend? backend = null;

        var constructionException = Record.Exception(() =>
            backend = new DirectExecutionBackend(
                new ModuleManager(),
                platform,
                faultHandling));
        backend?.Dispose();

        var invalidOperation = Assert.IsType<InvalidOperationException>(constructionException);
        Assert.Equal(expectedMessage, invalidOperation.Message);
        Assert.Equal([17u, 23u], threading.FreedSlots);
        Assert.Empty(memory.ActiveAllocations);
        Assert.Equal(expectedFreedThunks, faultHandling.FreedThunks.Count);
        Assert.Equal(expectedRemovedHandlers, faultHandling.RemovedHandlers.Count);
    }

    [Theory]
    [InlineData(3, 0, "Failed to create unresolved return stub", 2)]
    [InlineData(int.MaxValue, 1, "Failed to create unresolved return stub", 3)]
    [InlineData(int.MaxValue, 2, "Failed to create guest return stub", 4)]
    public void ConstructorRejectsRequiredReturnStubFailureWithoutLeakingHostMemory(
        int failedAllocation,
        int failedProtection,
        string expectedMessage,
        int expectedFreedAllocations)
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(failedAllocation, failedProtection);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        DirectExecutionBackend? backend = null;

        var constructionException = Record.Exception(() =>
            backend = new DirectExecutionBackend(
                new ModuleManager(),
                platform,
                new StubFaultHandling(succeed: true)));
        backend?.Dispose();

        var outOfMemory = Assert.IsType<OutOfMemoryException>(constructionException);
        Assert.Equal(expectedMessage, outOfMemory.Message);
        Assert.Equal([17u, 23u], threading.FreedSlots);
        Assert.Equal(expectedFreedAllocations, memory.FreedAddresses.Count);
        Assert.Empty(memory.ActiveAllocations);
    }

    [Fact]
    public void FailedTlsHandlerProtectionDoesNotRetainUnusableAllocation()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 3);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var activeAllocationsBefore = memory.ActiveAllocations.Count;
        try
        {
            Assert.False(backend.TryCreateTlsHandler());

            Assert.Equal(activeAllocationsBefore, memory.ActiveAllocations.Count);
        }
        finally
        {
            backend.Dispose();
        }
    }

    [Fact]
    public unsafe void PatchesDecodedTlsInstructionWithLeadingLegacyPrefix()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(failedAllocation: int.MaxValue);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        byte[] instruction =
        [
            0xF3, 0x64, 0x4C, 0x33, 0x3C, 0x25, 0x28, 0x00, 0x00, 0x00,
        ];
        var instructionAddress = memory.Allocate(
            0,
            (ulong)instruction.Length,
            HostPageProtection.ReadWriteExecute);
        instruction.CopyTo(new Span<byte>((void*)instructionAddress, instruction.Length));

        try
        {
            var patched = backend.TryPatchTlsInstruction(
                (nint)instructionAddress,
                (byte*)instructionAddress,
                instruction.Length,
                out var instructionKind);

            Assert.True(patched);
            Assert.Equal(NativeTlsInstructionKind.StackCanaryXor, instructionKind);
            Assert.NotEqual(
                instruction,
                new ReadOnlySpan<byte>((void*)instructionAddress, instruction.Length).ToArray());
            Assert.Equal(
                HostPageProtection.ReadWrite,
                memory.ProtectionCalls.Last(call => call.Address == instructionAddress).Protection);
        }
        finally
        {
            backend.Dispose();
            memory.Free(instructionAddress);
        }
    }

    [Fact]
    public void SuccessfulTlsHandlersRemainOwnedUntilBackendDisposal()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(failedAllocation: int.MaxValue);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var activeAllocationsBefore = memory.ActiveAllocations.Count;

        Assert.True(backend.TryCreateTlsHandler());
        Assert.True(backend.TryCreateTlsHandler());
        Assert.Equal(activeAllocationsBefore + 2, memory.ActiveAllocations.Count);

        backend.Dispose();

        Assert.Empty(memory.ActiveAllocations);
    }

    [Fact]
    public void FailedTlsHandlerReplacementRestoresPreviousHandlerState()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 6);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        const ulong stackAddress = 0x1000;
        var context = new CpuContext(
            new StackSlotMemory(stackAddress, 0x1122_3344_5566_7788),
            Generation.Gen5)
        {
            [CpuRegister.Rsp] = stackAddress,
        };

        try
        {
            Assert.True(backend.TryCreateTlsHandler());
            var previousHelper = backend.GetOrCreateTlsLoadHelper(
                destinationRegister: 0,
                displacement: 0,
                is64Bit: true,
                memorySize: 8,
                signExtend: false);
            Assert.NotEqual(0, previousHelper);
            var activeAllocationsBefore = memory.ActiveAllocations.Count;

            var executed = backend.TryExecute(
                context,
                entryPoint: 0,
                Generation.Gen5,
                new Dictionary<ulong, string>(),
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
            Assert.Equal(activeAllocationsBefore, memory.ActiveAllocations.Count);
            Assert.Equal(
                previousHelper,
                backend.GetOrCreateTlsLoadHelper(
                    destinationRegister: 0,
                    displacement: 0,
                    is64Bit: true,
                    memorySize: 8,
                    signExtend: false));
        }
        finally
        {
            backend.Dispose();
        }
    }

    [Fact]
    public void FailedTlsLoadHelperFinalizationReusesArenaReservationOnRetry()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 5);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));

        try
        {
            Assert.True(backend.TryCreateTlsHandler());
            var protectionCallStart = memory.ProtectionCalls.Count;

            var failedHelper = backend.GetOrCreateTlsLoadHelper(
                destinationRegister: 0,
                displacement: 0,
                is64Bit: true,
                memorySize: 8,
                signExtend: false);
            var retriedHelper = backend.GetOrCreateTlsLoadHelper(
                destinationRegister: 0,
                displacement: 0,
                is64Bit: true,
                memorySize: 8,
                signExtend: false);

            Assert.Equal(0, failedHelper);
            Assert.NotEqual(0, retriedHelper);
            var writableReservations = memory.ProtectionCalls
                .Skip(protectionCallStart)
                .Where(call => call.Protection == HostPageProtection.ReadWrite)
                .Select(call => call.Address)
                .ToArray();
            Assert.Equal(2, writableReservations.Length);
            Assert.Equal(writableReservations[0], writableReservations[1]);
        }
        finally
        {
            backend.Dispose();
        }
    }

    [Fact]
    public unsafe void FailedTlsPatchProtectionRestoreRollsBackInstructionBytes()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedRawProtection: 1);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var instructionAddress = memory.Allocate(
            0,
            16,
            HostPageProtection.ReadWriteExecute);
        byte[] original =
        [
            0x64, 0x4C, 0x33, 0x3C, 0x25, 0x28, 0x00, 0x00, 0x00,
            0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        ];
        original.CopyTo(new Span<byte>((void*)instructionAddress, original.Length));

        try
        {
            var patched = backend.TryPatchTlsInstruction(
                (nint)instructionAddress,
                (byte*)instructionAddress,
                original.Length,
                out _);

            Assert.False(patched);
            Assert.Equal(
                original,
                new ReadOnlySpan<byte>((void*)instructionAddress, original.Length).ToArray());
        }
        finally
        {
            backend.Dispose();
            memory.Free(instructionAddress);
        }
    }

    [Fact]
    public unsafe void RecognizedTlsPatchFailureStopsExecutionPreparation()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 6,
            failedRawProtection: 1);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var instructionAddress = memory.Allocate(
            0,
            16,
            HostPageProtection.ReadWriteExecute);
        byte[] instruction =
        [
            0x64, 0x4C, 0x33, 0x3C, 0x25, 0x28, 0x00, 0x00, 0x00,
            0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
        ];
        instruction.CopyTo(new Span<byte>((void*)instructionAddress, instruction.Length));
        memory.ExecutableRegionAddress = instructionAddress;
        memory.ExecutableRegionSize = (ulong)instruction.Length;
        const ulong stackAddress = 0x1000;
        var context = new CpuContext(
            new StackSlotMemory(stackAddress, 0x1122_3344_5566_7788),
            Generation.Gen5)
        {
            [CpuRegister.Rsp] = stackAddress,
        };

        try
        {
            var executed = backend.TryExecute(
                context,
                instructionAddress,
                Generation.Gen5,
                new Dictionary<ulong, string>(),
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
            Assert.Contains("TLS patch", backend.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                instruction,
                new ReadOnlySpan<byte>((void*)instructionAddress, instruction.Length).ToArray());
        }
        finally
        {
            backend.Dispose();
            memory.Free(instructionAddress);
        }
    }

    [Fact]
    public unsafe void MinimumLengthExecutableRegionIsScannedForTlsInstructions()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 4);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        byte[] instruction =
        [
            0x64, 0x89, 0x04, 0x25, 0x50, 0x00, 0x00, 0x00,
        ];
        var instructionAddress = memory.Allocate(
            0,
            (ulong)instruction.Length,
            HostPageProtection.ReadWriteExecute);
        instruction.CopyTo(new Span<byte>((void*)instructionAddress, instruction.Length));
        memory.ExecutableRegionAddress = instructionAddress;
        memory.ExecutableRegionSize = (ulong)instruction.Length;
        const ulong stackAddress = 0x1000;
        var context = new CpuContext(
            new StackSlotMemory(stackAddress, 0x1122_3344_5566_7788),
            Generation.Gen5)
        {
            [CpuRegister.Rsp] = stackAddress,
        };

        try
        {
            var executed = backend.TryExecute(
                context,
                instructionAddress,
                Generation.Gen5,
                new Dictionary<ulong, string>(),
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
            Assert.Contains("TLS patch", backend.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                instruction,
                new ReadOnlySpan<byte>((void*)instructionAddress, instruction.Length).ToArray());
        }
        finally
        {
            backend.Dispose();
            memory.Free(instructionAddress);
        }
    }

    [Fact]
    public void TlsScanStopsAtGuestAddressSpaceCeiling()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 4,
            throwingQuery: 2);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        const ulong stackAddress = 0x1000;
        var context = new CpuContext(
            new StackSlotMemory(stackAddress, 0x1122_3344_5566_7788),
            Generation.Gen5)
        {
            [CpuRegister.Rsp] = stackAddress,
        };

        try
        {
            var executed = backend.TryExecute(
                context,
                entryPoint: ulong.MaxValue - 1,
                Generation.Gen5,
                new Dictionary<ulong, string>(),
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
            Assert.Contains("guest entry stub", backend.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal([ulong.MaxValue - 1], memory.QueryAddresses);
        }
        finally
        {
            backend.Dispose();
        }
    }

    [Fact]
    public unsafe void TlsInstructionCrossingScanChunkBoundaryIsRecognized()
    {
        const int chunkSize = 0x0100_0000;
        byte[] instruction =
        [
            0x64, 0x4C, 0x33, 0x3C, 0x25, 0x28, 0x00, 0x00, 0x00,
        ];
        var allocationSize = chunkSize + instruction.Length;
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 4);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var codeAddress = memory.Allocate(
            0,
            (ulong)allocationSize,
            HostPageProtection.ReadWriteExecute);
        var instructionAddress = codeAddress + chunkSize - 4u;
        instruction.CopyTo(new Span<byte>((void*)instructionAddress, instruction.Length));
        memory.ExecutableRegionAddress = codeAddress;
        memory.ExecutableRegionSize = (ulong)allocationSize;
        const ulong stackAddress = 0x1000;
        var context = new CpuContext(
            new StackSlotMemory(stackAddress, 0x1122_3344_5566_7788),
            Generation.Gen5)
        {
            [CpuRegister.Rsp] = stackAddress,
        };

        try
        {
            var executed = backend.TryExecute(
                context,
                codeAddress,
                Generation.Gen5,
                new Dictionary<ulong, string>(),
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
            Assert.Contains("TLS patch", backend.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                instruction,
                new ReadOnlySpan<byte>((void*)instructionAddress, instruction.Length).ToArray());
        }
        finally
        {
            backend.Dispose();
            memory.Free(codeAddress);
        }
    }

    [Fact]
    public unsafe void LaterRecognizedTlsPatchFailureRollsBackEarlierPatches()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedRawProtection: 2);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        byte[] instructions =
        [
            0x64, 0x4C, 0x33, 0x3C, 0x25, 0x28, 0x00, 0x00, 0x00,
            0x64, 0x4C, 0x33, 0x3C, 0x25, 0x28, 0x00, 0x00, 0x00,
        ];
        var instructionAddress = memory.Allocate(
            0,
            (ulong)instructions.Length,
            HostPageProtection.ReadWriteExecute);
        instructions.CopyTo(new Span<byte>((void*)instructionAddress, instructions.Length));
        memory.ExecutableRegionAddress = instructionAddress;
        memory.ExecutableRegionSize = (ulong)instructions.Length;
        var activeAllocationsBefore = memory.ActiveAllocations.Count;
        const ulong stackAddress = 0x1000;
        var context = new CpuContext(
            new StackSlotMemory(stackAddress, 0x1122_3344_5566_7788),
            Generation.Gen5)
        {
            [CpuRegister.Rsp] = stackAddress,
        };

        try
        {
            var executed = backend.TryExecute(
                context,
                instructionAddress,
                Generation.Gen5,
                new Dictionary<ulong, string>(),
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
            Assert.Contains("TLS patch", backend.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                instructions,
                new ReadOnlySpan<byte>((void*)instructionAddress, instructions.Length).ToArray());
            Assert.Equal(activeAllocationsBefore, memory.ActiveAllocations.Count);
        }
        finally
        {
            backend.Dispose();
            memory.Free(instructionAddress);
        }
    }

    [Fact]
    public unsafe void FailedImportSetupRestoresPatchedSlotsAndAttemptAllocations()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 6);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var firstStub = memory.Allocate(0, 16, HostPageProtection.ReadWriteExecute);
        var secondStub = memory.Allocate(0, 16, HostPageProtection.ReadWriteExecute);
        var firstOriginal = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        var secondOriginal = Enumerable.Repeat((byte)0x5A, 16).ToArray();
        firstOriginal.CopyTo(new Span<byte>((void*)firstStub, firstOriginal.Length));
        secondOriginal.CopyTo(new Span<byte>((void*)secondStub, secondOriginal.Length));
        var activeAllocationsBefore = memory.ActiveAllocations.Count;
        try
        {
            var setupResult = backend.SetupImportStubs(new Dictionary<ulong, string>
            {
                [firstStub] = "unknown-import-a",
                [secondStub] = "unknown-import-b",
            });

            Assert.False(setupResult);
            Assert.Equal(firstOriginal, new ReadOnlySpan<byte>((void*)firstStub, 16).ToArray());
            Assert.Equal(secondOriginal, new ReadOnlySpan<byte>((void*)secondStub, 16).ToArray());
            Assert.Equal(activeAllocationsBefore, memory.ActiveAllocations.Count);
        }
        finally
        {
            backend.Dispose();
            memory.Free(firstStub);
            memory.Free(secondStub);
        }
    }

    [Fact]
    public unsafe void ImportSetupMutatesGuestStubWithoutWriteExecuteProtection()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(failedAllocation: int.MaxValue);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var stub = memory.Allocate(0, 16, HostPageProtection.ReadWriteExecute);
        var original = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        original.CopyTo(new Span<byte>((void*)stub, original.Length));

        try
        {
            Assert.True(backend.SetupImportStubs(new Dictionary<ulong, string>
            {
                [stub] = "unknown-import",
            }));

            Assert.Equal(
                HostPageProtection.ReadWrite,
                memory.ProtectionCalls.Last(call => call.Address == stub).Protection);
        }
        finally
        {
            backend.Dispose();
            memory.Free(stub);
        }
    }

    [Fact]
    public unsafe void FailedTlsPreparationRollsBackCompletedImportSetup()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 5);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var stub = memory.Allocate(0, 16, HostPageProtection.ReadWriteExecute);
        var original = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        original.CopyTo(new Span<byte>((void*)stub, original.Length));
        var activeAllocationsBefore = memory.ActiveAllocations.Count;
        const ulong stackAddress = 0x1000;
        var context = new CpuContext(
            new StackSlotMemory(stackAddress, 0x1122_3344_5566_7788),
            Generation.Gen5)
        {
            [CpuRegister.Rsp] = stackAddress,
        };

        try
        {
            var executed = backend.TryExecute(
                context,
                entryPoint: 0,
                Generation.Gen5,
                new Dictionary<ulong, string>
                {
                    [stub] = "unknown-import",
                },
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
            Assert.Contains("TLS handler", backend.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(original, new ReadOnlySpan<byte>((void*)stub, original.Length).ToArray());
            Assert.Equal(activeAllocationsBefore, memory.ActiveAllocations.Count);
        }
        finally
        {
            backend.Dispose();
            memory.Free(stub);
        }
    }

    [Fact]
    public unsafe void FailedLaterImportSetupPreservesEarlierModuleDispatchState()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 8);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var earlierStub = memory.Allocate(0, 16, HostPageProtection.ReadWriteExecute);
        var failedStubA = memory.Allocate(0, 16, HostPageProtection.ReadWriteExecute);
        var failedStubB = memory.Allocate(0, 16, HostPageProtection.ReadWriteExecute);
        var original = Enumerable.Repeat((byte)0xCC, 16).ToArray();
        original.CopyTo(new Span<byte>((void*)earlierStub, original.Length));
        original.CopyTo(new Span<byte>((void*)failedStubA, original.Length));
        original.CopyTo(new Span<byte>((void*)failedStubB, original.Length));

        try
        {
            Assert.True(backend.SetupImportStubs(new Dictionary<ulong, string>
            {
                [earlierStub] = "earlier-module-import",
            }));
            var earlierPatchedBytes = new ReadOnlySpan<byte>((void*)earlierStub, 16).ToArray();
            var activeAllocationsBeforeFailedAttempt = memory.ActiveAllocations.Count;

            Assert.False(backend.SetupImportStubs(new Dictionary<ulong, string>
            {
                [failedStubA] = "failed-module-import-a",
                [failedStubB] = "failed-module-import-b",
            }));

            Assert.Equal(earlierPatchedBytes, new ReadOnlySpan<byte>((void*)earlierStub, 16).ToArray());
            Assert.Equal(original, new ReadOnlySpan<byte>((void*)failedStubA, 16).ToArray());
            Assert.Equal(original, new ReadOnlySpan<byte>((void*)failedStubB, 16).ToArray());
            Assert.Equal(activeAllocationsBeforeFailedAttempt, memory.ActiveAllocations.Count);
        }
        finally
        {
            backend.Dispose();
            memory.Free(earlierStub);
            memory.Free(failedStubA);
            memory.Free(failedStubB);
        }
    }

    [Fact]
    public unsafe void FailedImportProtectionRestoreRollsBackGuestSlotAndTrampoline()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedRawProtection: 1);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var stub = memory.Allocate(0, 16, HostPageProtection.ReadWriteExecute);
        var original = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        original.CopyTo(new Span<byte>((void*)stub, original.Length));
        var activeAllocationsBefore = memory.ActiveAllocations.Count;

        try
        {
            var setupResult = backend.SetupImportStubs(new Dictionary<ulong, string>
            {
                [stub] = "unknown-import",
            });

            Assert.False(setupResult);
            Assert.Equal(original, new ReadOnlySpan<byte>((void*)stub, original.Length).ToArray());
            Assert.Equal(activeAllocationsBefore, memory.ActiveAllocations.Count);
        }
        finally
        {
            backend.Dispose();
            memory.Free(stub);
        }
    }

    [Fact]
    public void FailedEntryStubProtectionRestoresGuestStackSlot()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            failedProtection: 4);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        const ulong stackAddress = 0x1000;
        const ulong originalStackValue = 0x1122_3344_5566_7788;
        var guestMemory = new StackSlotMemory(stackAddress, originalStackValue);
        var context = new CpuContext(guestMemory, Generation.Gen5)
        {
            [CpuRegister.Rsp] = stackAddress,
        };
        var activeAllocationsBefore = memory.ActiveAllocations.Count;
        var allocationCallStart = memory.AllocationCalls.Count;

        try
        {
            var executed = backend.TryExecute(
                context,
                entryPoint: 0x2000,
                Generation.Gen5,
                new Dictionary<ulong, string>(),
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
            Assert.Equal(originalStackValue, guestMemory.Value);
            Assert.Equal(activeAllocationsBefore + 1, memory.ActiveAllocations.Count);
            var entryAllocations = memory.AllocationCalls.Skip(allocationCallStart).ToArray();
            Assert.Collection(
                entryAllocations[^2..],
                allocation =>
                {
                    Assert.Equal(512UL, allocation.Size);
                    Assert.Equal(HostPageProtection.ReadWrite, allocation.Protection);
                },
                allocation =>
                {
                    Assert.Equal(8UL, allocation.Size);
                    Assert.Equal(HostPageProtection.ReadWrite, allocation.Protection);
                });
            Assert.Equal(
                HostPageProtection.ReadExecute,
                memory.ProtectionCalls[^1].Protection);
        }
        finally
        {
            backend.Dispose();
        }
    }

    [Fact]
    public void FailedEntryStubHostRspSlotAllocationReleasesCodeStub()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(failedAllocation: 7);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1));
        var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        const ulong stackAddress = 0x1000;
        const ulong originalStackValue = 0x1122_3344_5566_7788;
        var guestMemory = new StackSlotMemory(stackAddress, originalStackValue);
        var context = new CpuContext(guestMemory, Generation.Gen5)
        {
            [CpuRegister.Rsp] = stackAddress,
        };
        var activeAllocationsBefore = memory.ActiveAllocations.Count;

        try
        {
            var executed = backend.TryExecute(
                context,
                entryPoint: 0x2000,
                Generation.Gen5,
                new Dictionary<ulong, string>(),
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
            Assert.Contains("allocate executable memory", backend.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(originalStackValue, guestMemory.Value);
            Assert.Equal(activeAllocationsBefore + 1, memory.ActiveAllocations.Count);
            var codeAllocation = memory.AllocationCalls[^1];
            Assert.Equal(512UL, codeAllocation.Size);
            Assert.Equal(HostPageProtection.ReadWrite, codeAllocation.Protection);
            Assert.Contains(codeAllocation.Address, memory.FreedAddresses);
        }
        finally
        {
            backend.Dispose();
        }
    }

    private sealed class StubHostPlatform(
        IHostThreading threading,
        IHostMemory? memory = null,
        IHostSymbolResolver? symbols = null) : IHostPlatform
    {
        public IHostMemory Memory { get; } = memory ?? new StubHostMemory();

        public IHostThreading Threading { get; } = threading;

        public IHostSymbolResolver Symbols { get; } = symbols ?? new StubHostSymbolResolver();
    }

    private sealed class RecordingHostThreading(IEnumerable<uint> slots) : IHostThreading
    {
        private readonly Queue<uint> _slots = new(slots);

        public List<uint> FreedSlots { get; } = [];

        public uint AllocateTlsSlot() => _slots.Dequeue();

        public bool FreeTlsSlot(uint slot)
        {
            FreedSlots.Add(slot);
            return true;
        }

        public bool SetTlsValue(uint slot, nint value) => true;

        public nint GetTlsValue(uint slot) => 0;

        public uint CurrentThreadId => 1;

        public bool TrySetCurrentThreadAffinity(nuint affinityMask) => true;

        public nint CreateNativeThread(
            nint entry,
            nint parameter,
            nuint stackReserveBytes,
            out uint threadId)
        {
            threadId = 0;
            return 0;
        }

        public bool WaitForThreadExit(nint threadHandle, uint timeoutMilliseconds) => true;

        public void CloseThreadHandle(nint threadHandle)
        {
        }

        public bool TryCaptureThreadRegisters(
            uint threadId,
            out HostCapturedRegisters registers)
        {
            registers = default;
            return false;
        }
    }

    private sealed class StubHostMemory : IHostMemory
    {
        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => false;

        public bool Free(ulong address) => true;

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return false;
        }

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return false;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }
    }

    private sealed class StackSlotMemory(ulong address, ulong initialValue) : ICpuMemory
    {
        private readonly byte[] _bytes = BitConverter.GetBytes(initialValue);

        public ulong Value => BinaryPrimitives.ReadUInt64LittleEndian(_bytes);

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (virtualAddress != address || destination.Length != _bytes.Length)
            {
                return false;
            }

            _bytes.CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (virtualAddress != address || source.Length != _bytes.Length)
            {
                return false;
            }

            source.CopyTo(_bytes);
            return true;
        }
    }

    private sealed unsafe class AllocatingHostMemory(
        int failedAllocation,
        int failedProtection = 0,
        int failedRawProtection = 0,
        int throwingQuery = 0) : IHostMemory
    {
        private int _allocationCount;
        private int _protectionCount;
        private int _rawProtectionCount;
        private int _queryCount;

        public HashSet<ulong> ActiveAllocations { get; } = [];

        public List<ulong> FreedAddresses { get; } = [];

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> AllocationCalls { get; } = [];

        public List<(ulong Address, HostPageProtection Protection)> ProtectionCalls { get; } = [];

        public List<ulong> QueryAddresses { get; } = [];

        public ulong ExecutableRegionAddress { get; set; }

        public ulong ExecutableRegionSize { get; set; }

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            if (++_allocationCount == failedAllocation)
            {
                return 0;
            }

            var address = (ulong)NativeMemory.AllocZeroed((nuint)size);
            ActiveAllocations.Add(address);
            AllocationCalls.Add((address, size, protection));
            return address;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => false;

        public bool Free(ulong address)
        {
            if (!ActiveAllocations.Remove(address))
            {
                return false;
            }

            NativeMemory.Free((void*)address);
            FreedAddresses.Add(address);
            return true;
        }

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            ProtectionCalls.Add((address, protection));
            return ++_protectionCount != failedProtection;
        }

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return ++_rawProtectionCount != failedRawProtection;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            QueryAddresses.Add(address);
            if (++_queryCount == throwingQuery)
            {
                throw new InvalidOperationException("Injected host query failure.");
            }

            if (ExecutableRegionSize != 0 &&
                address >= ExecutableRegionAddress &&
                address - ExecutableRegionAddress < ExecutableRegionSize)
            {
                info = new HostRegionInfo(
                    ExecutableRegionAddress,
                    ExecutableRegionAddress,
                    ExecutableRegionSize,
                    HostRegionState.Committed,
                    RawState: 0x1000,
                    HostPageProtection.ReadExecute,
                    RawProtection: 0x20,
                    RawAllocationProtection: 0x20);
                return true;
            }

            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }
    }

    private sealed class StubHostSymbolResolver(nint address = 0) : IHostSymbolResolver
    {
        public nint GetAddress(HostRuntimeFunction function) => address;
    }

    private sealed class StubFaultHandling(
        bool succeed = false,
        int failedHandlerInstallation = 0) : IHostFaultHandling
    {
        private nint _nextThunk = 100;
        private int _handlerInstallationCount;

        public List<nint> FreedThunks { get; } = [];

        public List<nint> RemovedHandlers { get; } = [];

        public nint CreateHandlerThunk(
            nint managedCallback,
            uint hostRspSwitchTlsSlot,
            nint tlsGetValueAddress) => succeed ? _nextThunk++ : 0;

        public void FreeThunk(nint thunk)
        {
            FreedThunks.Add(thunk);
        }

        public nint AddFirstChanceHandler(nint thunk)
        {
            if (!succeed || ++_handlerInstallationCount == failedHandlerInstallation)
            {
                return 0;
            }

            return thunk + 1000;
        }

        public void RemoveHandler(nint handle)
        {
            RemovedHandlers.Add(handle);
        }

        public void SetUnhandledFilter(nint thunk)
        {
        }
    }
}
