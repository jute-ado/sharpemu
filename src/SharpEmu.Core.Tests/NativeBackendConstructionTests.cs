// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class NativeBackendConstructionTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void ImportSetupTracingIsExplicitlyOptIn(string? value, bool expected)
    {
        Assert.Equal(expected, DirectExecutionBackend.IsImportSetupTracingEnabled(value));
    }

    [Fact]
    public void NativeWorkerControlCallbacksUseTransitionSafeThunks()
    {
        Assert.True(DirectExecutionBackend.NativeWorkerControlCallbacksUseDelegateThunks);
    }

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
    public void DisposeSignalsBlockedGuestThreadsBeforeReleasingBackendResources()
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

        try
        {
            Assert.False(GuestThreadBlocking.ShutdownRequested);

            backend.Dispose();

            Assert.True(GuestThreadBlocking.ShutdownRequested);
            Assert.Empty(memory.ActiveAllocations);
        }
        finally
        {
            backend.Dispose();
            GuestThreadBlocking.BeginExecution();
        }
    }

    [Fact]
    public void RegisteredPrimaryThreadAcceptsQueuedGuestException()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var hostMemory =
            new AllocatingHostMemory(failedAllocation: int.MaxValue);
        var platform = new StubHostPlatform(
            threading,
            hostMemory,
            new StubHostSymbolResolver(address: 1));
        using var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var context =
            new CpuContext(new VirtualMemory(), Generation.Gen5);
        const ulong threadHandle = 0x1234;
        backend.RegisterGuestThreadContext(threadHandle, context);

        Assert.Equal(threadHandle, GuestThreadExecution.CurrentGuestThreadHandle);
        Assert.False(GuestThreadExecution.IsGuestThread);

        Assert.True(
            backend.TryRaiseGuestException(
                context,
                threadHandle,
                handler: 0x1234_0000,
                exceptionType: 30,
                out var error));
        Assert.Null(error);

        // Duplicate raises coalesce while preserving the pending delivery.
        Assert.True(
            backend.TryRaiseGuestException(
                context,
                threadHandle,
                handler: 0x1234_0000,
                exceptionType: 30,
                out error));
        Assert.Null(error);
        Assert.False(
            backend.TryRaiseGuestException(
                context,
                threadHandle + 1,
                handler: 0x1234_0000,
                exceptionType: 30,
                out error));
        Assert.Contains("unknown", error);
    }

    [Fact]
    public async Task ExceptionStackMappingDoesNotHoldTheGuestThreadSchedulerGate()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var hostMemory =
            new AllocatingHostMemory(failedAllocation: int.MaxValue);
        var platform = new StubHostPlatform(
            threading,
            hostMemory,
            new StubHostSymbolResolver(address: 1));
        using var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        using var memory = new BlockingSnapshotVirtualMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong threadHandle = 0x1234;
        backend.RegisterGuestThreadContext(threadHandle, context);

        var raiseTask = Task.Run(
            () => backend.TryRaiseGuestException(
                context,
                threadHandle,
                handler: 0x1234_0000,
                exceptionType: 30,
                out _));

        await memory.SnapshotEntered.WaitAsync(TimeSpan.FromSeconds(2));
        var registrationTask = Task.Run(
            () => backend.RegisterGuestThreadContext(
                threadHandle + 1,
                new CpuContext(new VirtualMemory(), Generation.Gen5)));

        try
        {
            Assert.Same(
                registrationTask,
                await Task.WhenAny(
                    registrationTask,
                    Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.True(
                registrationTask.IsCompletedSuccessfully,
                "exception-stack mapping blocked the guest-thread scheduler gate");
        }
        finally
        {
            memory.ReleaseSnapshot();
        }

        Assert.True(await raiseTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ExceptionStackMappingSnapshotsGuestRegionsOnce()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var hostMemory =
            new AllocatingHostMemory(failedAllocation: int.MaxValue);
        var platform = new StubHostPlatform(
            threading,
            hostMemory,
            new StubHostSymbolResolver(address: 1));
        using var backend = new DirectExecutionBackend(
            new ModuleManager(),
            platform,
            new StubFaultHandling(succeed: true));
        var stackBase = OperatingSystem.IsWindows()
            ? 0x7FFF_E000_0000UL
            : 0x6FFF_A000_0000UL;
        const ulong regionStride = 0x0100_0000UL;
        var occupiedRegions = Enumerable.Range(0, 3)
            .Select(index => new VirtualMemoryRegion(
                stackBase - ((ulong)index * regionStride),
                memorySize: 0x0020_0000,
                fileOffset: 0,
                fileSize: 0,
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Write))
            .ToArray();
        var memory = new CountingRegionVirtualMemory(occupiedRegions);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong threadHandle = 0x1234;
        backend.RegisterGuestThreadContext(threadHandle, context);

        Assert.True(
            backend.TryRaiseGuestException(
                context,
                threadHandle,
                handler: 0x1234_0000,
                exceptionType: 30,
                out var error));

        Assert.Null(error);
        Assert.Equal(1, memory.SnapshotCount);
        Assert.Equal(
            stackBase - (3 * regionStride),
            Assert.Single(memory.MapAddresses));
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
        var memory = new AllocatingHostMemory(
            failedAllocation: int.MaxValue,
            contiguousAllocationCapacity: 0x0010_0000);
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
            Assert.True(backend.TryCreateTlsHandler());

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
    public unsafe void RepeatedImportSetupReusesExistingPatchAndTrampoline()
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
        Enumerable.Repeat((byte)0xA5, 16).ToArray()
            .CopyTo(new Span<byte>((void*)stub, 16));

        try
        {
            var imports = new Dictionary<ulong, string>
            {
                [stub] = "unknown-import",
            };
            Assert.True(backend.SetupImportStubs(imports));
            var firstPatch = new ReadOnlySpan<byte>((void*)stub, 16).ToArray();
            var activeAllocations = memory.ActiveAllocations.Count;

            Assert.True(backend.SetupImportStubs(imports));

            Assert.Equal(activeAllocations, memory.ActiveAllocations.Count);
            Assert.Equal(firstPatch, new ReadOnlySpan<byte>((void*)stub, 16).ToArray());

            Assert.False(backend.SetupImportStubs(new Dictionary<ulong, string>
            {
                [stub] = "different-import",
            }));
            Assert.Contains("already registered", backend.LastError, StringComparison.Ordinal);
            Assert.Equal(activeAllocations, memory.ActiveAllocations.Count);
            Assert.Equal(firstPatch, new ReadOnlySpan<byte>((void*)stub, 16).ToArray());
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
    public void NativeWorkerCreationFailureStopsBeforeInlineGuestExecution()
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
                entryPoint: 0x2000,
                Generation.Gen5,
                new Dictionary<ulong, string>(),
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP, result);
            Assert.Contains("native guest worker", backend.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, threading.NativeThreadCreationAttempts);
        }
        finally
        {
            backend.Dispose();
        }
    }

    [Fact]
    public void NativeWorkerEventFailureClosesCreatedEventBeforeReturning()
    {
        var threading = new RecordingHostThreading([17u, 23u]);
        var memory = new AllocatingHostMemory(failedAllocation: int.MaxValue);
        var nativeInterop = new RecordingHostNativeInterop(failedEventCreation: 2);
        var platform = new StubHostPlatform(
            threading,
            memory,
            new StubHostSymbolResolver(address: 1),
            nativeInterop);
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
                entryPoint: 0x2000,
                Generation.Gen5,
                new Dictionary<ulong, string>(),
                new Dictionary<string, ulong>(),
                default,
                NativeEntryReturnContract.RequireZero,
                out var result);

            Assert.False(executed);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP, result);
            Assert.Contains("native guest worker", backend.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, nativeInterop.EventCreationAttempts);
            Assert.Equal([1], nativeInterop.SignaledEvents);
            Assert.Equal([1], nativeInterop.ClosedEvents);
            Assert.Equal(0, threading.NativeThreadCreationAttempts);
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
        IHostSymbolResolver? symbols = null,
        IHostNativeInterop? nativeInterop = null) : IHostPlatform
    {
        public IHostMemory Memory { get; } = memory ?? new StubHostMemory();

        public IHostThreading Threading { get; } = threading;

        public IHostSymbolResolver Symbols { get; } = symbols ?? new StubHostSymbolResolver();

        public IHostNativeInterop NativeInterop { get; } = nativeInterop ?? new RecordingHostNativeInterop();

        public IHostAudioOutput Audio { get; } = new StubHostAudioOutput();

        public IHostInput Input { get; } = new StubHostInput();
    }

    private sealed class RecordingHostNativeInterop(int failedEventCreation = int.MaxValue) : IHostNativeInterop
    {
        private nint _nextEvent = 1;

        public int EventCreationAttempts { get; private set; }

        public List<nint> SignaledEvents { get; } = [];

        public List<nint> ClosedEvents { get; } = [];

        public nint AdaptGuestAbiCallback(nint hostTarget) => hostTarget;

        public nint CreateWorkerEvent()
        {
            EventCreationAttempts++;
            return EventCreationAttempts == failedEventCreation ? 0 : _nextEvent++;
        }

        public bool SignalWorkerEvent(nint handle)
        {
            SignaledEvents.Add(handle);
            return true;
        }

        public bool WaitWorkerEvent(nint handle, int timeoutMilliseconds) => true;

        public void CloseWorkerEvent(nint handle)
        {
            ClosedEvents.Add(handle);
        }
    }

    private sealed class StubHostAudioOutput : IHostAudioOutput
    {
        public string BackendName => "test";

        public IHostAudioStream OpenStereoPcm16Stream(uint sampleRate) =>
            throw new NotSupportedException("Native backend construction tests do not open audio streams.");
    }

    private sealed class StubHostInput : IHostInput
    {
        public void EnsureStarted()
        {
        }

        public int GetGamepadStates(Span<HostGamepadState> destination) => 0;

        public string? DescribeConnectedGamepad() => null;

        public void SetRumble(byte largeMotor, byte smallMotor)
        {
        }

        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
        {
        }

        public void SetLightbar(byte red, byte green, byte blue)
        {
        }

        public void ResetLightbar()
        {
        }

        public bool IsHostWindowFocused() => false;

        public bool IsKeyDown(int virtualKey) => false;
    }

    private sealed class RecordingHostThreading(IEnumerable<uint> slots) : IHostThreading
    {
        private readonly Queue<uint> _slots = new(slots);

        public List<uint> FreedSlots { get; } = [];

        public int NativeThreadCreationAttempts { get; private set; }

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

        public void RequestTimerResolution()
        {
        }

        public nint CreateNativeThread(
            nint entry,
            nint parameter,
            nuint stackReserveBytes,
            out uint threadId)
        {
            NativeThreadCreationAttempts++;
            threadId = 0;
            return 0;
        }

        public void JoinExitedThread(nint threadHandle)
        {
        }

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
        int throwingQuery = 0,
        ulong contiguousAllocationCapacity = 0) : IHostMemory
    {
        private const ulong HostPageSize = 4096;
        private int _allocationCount;
        private int _protectionCount;
        private int _rawProtectionCount;
        private int _queryCount;
        private ulong _contiguousAllocationBase;
        private ulong _contiguousAllocationOffset;

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

            ulong address;
            if (contiguousAllocationCapacity != 0)
            {
                if (_contiguousAllocationBase == 0)
                {
                    _contiguousAllocationBase = AllocatePageAligned(contiguousAllocationCapacity);
                    if (_contiguousAllocationBase == 0)
                    {
                        return 0;
                    }
                    _contiguousAllocationOffset = 0;
                }

                var alignedOffset = (_contiguousAllocationOffset + 15UL) & ~15UL;
                if (alignedOffset > contiguousAllocationCapacity ||
                    size > contiguousAllocationCapacity - alignedOffset)
                {
                    return 0;
                }

                address = _contiguousAllocationBase + alignedOffset;
                _contiguousAllocationOffset = alignedOffset + Math.Max(size, 16UL);
            }
            else
            {
                address = AllocatePageAligned(size);
            }
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

            FreedAddresses.Add(address);
            if (contiguousAllocationCapacity == 0)
            {
                NativeMemory.AlignedFree((void*)address);
            }
            else if (ActiveAllocations.Count == 0)
            {
                NativeMemory.AlignedFree((void*)_contiguousAllocationBase);
                _contiguousAllocationBase = 0;
                _contiguousAllocationOffset = 0;
            }
            return true;
        }

        private static ulong AllocatePageAligned(ulong size)
        {
            if (size == 0 ||
                size > ulong.MaxValue - (HostPageSize - 1))
            {
                return 0;
            }

            var alignedSize = (size + (HostPageSize - 1)) & ~(HostPageSize - 1);
            if (alignedSize > (ulong)nuint.MaxValue)
            {
                return 0;
            }

            var memory = NativeMemory.AlignedAlloc(
                checked((nuint)alignedSize),
                checked((nuint)HostPageSize));
            if (memory == null)
            {
                return 0;
            }

            NativeMemory.Clear(memory, checked((nuint)alignedSize));
            return (ulong)memory;
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

    private sealed class BlockingSnapshotVirtualMemory : IVirtualMemory, IDisposable
    {
        private readonly TaskCompletionSource _snapshotEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSnapshot =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SnapshotEntered => _snapshotEntered.Task;

        public void ReleaseSnapshot() => _releaseSnapshot.TrySetResult();

        public void Clear()
        {
        }

        public void Map(
            ulong virtualAddress,
            ulong memorySize,
            ulong fileOffset,
            ReadOnlySpan<byte> fileData,
            ProgramHeaderFlags protection)
        {
        }

        public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions()
        {
            _snapshotEntered.TrySetResult();
            _releaseSnapshot.Task.GetAwaiter().GetResult();
            return [];
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;

        public void Dispose()
        {
            ReleaseSnapshot();
        }
    }

    private sealed class CountingRegionVirtualMemory(
        IReadOnlyList<VirtualMemoryRegion> regions) : IVirtualMemory
    {
        public int SnapshotCount { get; private set; }

        public List<ulong> MapAddresses { get; } = [];

        public void Clear()
        {
        }

        public void Map(
            ulong virtualAddress,
            ulong memorySize,
            ulong fileOffset,
            ReadOnlySpan<byte> fileData,
            ProgramHeaderFlags protection) => MapAddresses.Add(virtualAddress);

        public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions()
        {
            SnapshotCount++;
            return regions;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
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
