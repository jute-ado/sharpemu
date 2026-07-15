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
                .Where(call => call.Protection == HostPageProtection.ReadWriteExecute)
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
        int failedRawProtection = 0) : IHostMemory
    {
        private int _allocationCount;
        private int _protectionCount;
        private int _rawProtectionCount;

        public HashSet<ulong> ActiveAllocations { get; } = [];

        public List<ulong> FreedAddresses { get; } = [];

        public List<(ulong Address, HostPageProtection Protection)> ProtectionCalls { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            if (++_allocationCount == failedAllocation)
            {
                return 0;
            }

            var address = (ulong)NativeMemory.AllocZeroed((nuint)size);
            ActiveAllocations.Add(address);
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
