// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class AgcResourceRegistrationTests
{
    private const ulong SizeOutputAddress = 0x1000;
    private const ulong HandleOutputAddress = 0x1100;
    private const ulong OwnerOutputAddress = 0x1200;
    private const ulong NameAddress = 0x2000;
    private const ulong StackAddress = 0x3000;
    private const ulong RegistrationMemoryAddress = 0x4000;
    private const ulong ResourceAddress = 0x8000;
    private const ulong BytesPerResource = 0x118;
    private const ulong BytesPerOwner = 0x1E0;

    [Fact]
    public void MemoryRequirementQueryUsesCheckedResourceAndOwnerSizing()
    {
        var fixture = CreateFixture(includeSizeOutput: true, includeHandleOutput: true);
        fixture.Context[CpuRegister.Rdi] = SizeOutputAddress;
        fixture.Context[CpuRegister.Rsi] = 2;
        fixture.Context[CpuRegister.Rdx] = 3;

        Assert.Equal(0, AgcExports.DriverQueryResourceRegistrationUserMemoryRequirements(fixture.Context));
        Assert.True(fixture.Memory.TryRead(SizeOutputAddress, fixture.Scratch.AsSpan(0, sizeof(ulong))));
        Assert.Equal(
            (2 * BytesPerResource) + (3 * BytesPerOwner),
            BinaryPrimitives.ReadUInt64LittleEndian(fixture.Scratch));

        fixture.Context[CpuRegister.Rsi] = ulong.MaxValue;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverQueryResourceRegistrationUserMemoryRequirements(fixture.Context));
    }

    [Fact]
    public void InitializationRejectsMemoryTooSmallForOwnerTable()
    {
        var fixture = CreateFixture(includeSizeOutput: false, includeHandleOutput: true);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            Initialize(fixture.Context, BytesPerOwner - 1, ownerCount: 1));
    }

    [Fact]
    public void AgcInitializationBootstrapsOptionalResourceRegistration()
    {
        var fixture = CreateFixture(
            includeSizeOutput: false,
            includeHandleOutput: true);
        fixture.Memory.AddRegion(
            OwnerOutputAddress,
            new byte[sizeof(uint)]);

        Assert.Equal(0, InitializeAgc(fixture.Context));

        fixture.Context[CpuRegister.Rdi] = OwnerOutputAddress;
        fixture.Context[CpuRegister.Rsi] = NameAddress;
        Assert.Equal(0, AgcExports.DriverRegisterOwner(fixture.Context));
        Assert.True(fixture.Memory.TryRead(
            OwnerOutputAddress,
            fixture.Scratch.AsSpan(0, sizeof(uint))));
        var owner = BinaryPrimitives.ReadUInt32LittleEndian(fixture.Scratch);
        Assert.NotEqual(0U, owner);

        Assert.Equal(
            0,
            RegisterResource(
                fixture.Context,
                resourceAddress: ResourceAddress,
                owner: owner));
    }

    [Fact]
    public void AgcInitializationExposesAndAcceptsZeroDefaultOwner()
    {
        var fixture = CreateFixture(
            includeSizeOutput: false,
            includeHandleOutput: true);
        fixture.Memory.AddRegion(
            OwnerOutputAddress,
            new byte[sizeof(uint)]);

        Assert.Equal(0, InitializeAgc(fixture.Context));

        fixture.Context[CpuRegister.Rdi] = OwnerOutputAddress;
        Assert.Equal(0, AgcExports.DriverGetDefaultOwner(fixture.Context));
        Assert.True(fixture.Memory.TryRead(
            OwnerOutputAddress,
            fixture.Scratch.AsSpan(0, sizeof(uint))));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(fixture.Scratch));

        Assert.Equal(
            0,
            RegisterResource(
                fixture.Context,
                resourceAddress: ResourceAddress,
                owner: 0));
    }

    [Fact]
    public void ResourceRegistrationBootstrapsOptionalStateBeforeAgcInitialization()
    {
        var fixture = CreateFixture(
            includeSizeOutput: false,
            includeHandleOutput: true);

        Assert.Equal(
            0,
            RegisterResource(
                fixture.Context,
                resourceAddress: ResourceAddress,
                owner: 0));
        Assert.True(fixture.Memory.TryRead(
            HandleOutputAddress,
            fixture.Scratch.AsSpan(0, sizeof(uint))));
        Assert.NotEqual(0U, BinaryPrimitives.ReadUInt32LittleEndian(fixture.Scratch));
    }

    [Fact]
    public void ResourceHandlesRemainValidAcrossMemoryWrappers()
    {
        var fixture = CreateFixture(
            includeSizeOutput: false,
            includeHandleOutput: true);
        var registerContext = new CpuContext(
            new MemoryWrapper(fixture.Memory),
            Generation.Gen5);
        registerContext[CpuRegister.Rsp] = StackAddress;

        Assert.Equal(0, RegisterResource(registerContext, ResourceAddress));
        Assert.True(fixture.Memory.TryRead(
            HandleOutputAddress,
            fixture.Scratch.AsSpan(0, sizeof(uint))));
        var handle = BinaryPrimitives.ReadUInt32LittleEndian(fixture.Scratch);

        var unregisterContext = new CpuContext(
            new MemoryWrapper(fixture.Memory),
            Generation.Gen5);
        unregisterContext[CpuRegister.Rdi] = handle;
        Assert.Equal(0, AgcExports.DriverUnregisterResource(unregisterContext));
    }

    [Fact]
    public void RejectedAgcInitializationLeavesOptionalOwnerRegistrationUsable()
    {
        var fixture = CreateFixture(
            includeSizeOutput: false,
            includeHandleOutput: true);
        fixture.Memory.AddRegion(
            OwnerOutputAddress,
            new byte[sizeof(uint)]);

        fixture.Context[CpuRegister.Rdi] = 0;
        fixture.Context[CpuRegister.Rsi] = 10;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.Init(fixture.Context));

        fixture.Context[CpuRegister.Rdi] = OwnerOutputAddress;
        fixture.Context[CpuRegister.Rsi] = NameAddress;
        Assert.Equal(0, AgcExports.DriverRegisterOwner(fixture.Context));
        Assert.True(fixture.Memory.TryRead(
            OwnerOutputAddress,
            fixture.Scratch.AsSpan(0, sizeof(uint))));
        Assert.NotEqual(0U, BinaryPrimitives.ReadUInt32LittleEndian(fixture.Scratch));
    }

    [Fact]
    public void RegistrationMemoryLimitsLiveResourcesAndUnregisterReclaimsCapacity()
    {
        var fixture = CreateFixture(includeSizeOutput: false, includeHandleOutput: true);
        Assert.Equal(0, Initialize(
            fixture.Context,
            BytesPerOwner + BytesPerResource,
            ownerCount: 1));

        Assert.Equal(0, RegisterResource(fixture.Context, resourceAddress: ResourceAddress));
        Assert.True(fixture.Memory.TryRead(HandleOutputAddress, fixture.Scratch.AsSpan(0, sizeof(uint))));
        var firstHandle = BinaryPrimitives.ReadUInt32LittleEndian(fixture.Scratch);
        Assert.NotEqual(0U, firstHandle);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            RegisterResource(fixture.Context, resourceAddress: ResourceAddress + 0x1000));

        fixture.Context[CpuRegister.Rdi] = firstHandle;
        Assert.Equal(0, AgcExports.DriverUnregisterResource(fixture.Context));
        Assert.Equal(0, RegisterResource(
            fixture.Context,
            resourceAddress: ResourceAddress + 0x2000));
    }

    [Fact]
    public void FailedResourceHandleCopyoutDoesNotConsumeCapacity()
    {
        var fixture = CreateFixture(includeSizeOutput: false, includeHandleOutput: false);
        Assert.Equal(0, Initialize(
            fixture.Context,
            BytesPerOwner + BytesPerResource,
            ownerCount: 1));

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            RegisterResource(fixture.Context, ResourceAddress));

        fixture.Memory.AddRegion(HandleOutputAddress, new byte[sizeof(uint)]);
        Assert.Equal(0, RegisterResource(fixture.Context, ResourceAddress));
    }

    [Fact]
    public void OwnerRegistrationHonorsConfiguredOwnerLimit()
    {
        var fixture = CreateFixture(includeSizeOutput: false, includeHandleOutput: true);
        fixture.Memory.AddRegion(OwnerOutputAddress, new byte[sizeof(uint)]);
        Assert.Equal(0, InitializeAgc(fixture.Context));
        Assert.Equal(0, Initialize(fixture.Context, BytesPerOwner, ownerCount: 1));

        fixture.Context[CpuRegister.Rdi] = OwnerOutputAddress;
        fixture.Context[CpuRegister.Rsi] = NameAddress;
        Assert.Equal(0, AgcExports.DriverRegisterOwner(fixture.Context));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverRegisterOwner(fixture.Context));
    }

    [Fact]
    public void UnregisteringOwnerResourcesReclaimsResourceCapacityButKeepsOwner()
    {
        var fixture = CreateFixture(includeSizeOutput: false, includeHandleOutput: true);
        fixture.Memory.AddRegion(OwnerOutputAddress, new byte[sizeof(uint)]);
        Assert.Equal(0, Initialize(
            fixture.Context,
            BytesPerOwner + BytesPerResource,
            ownerCount: 1));

        var owner = RegisterOwner(fixture);
        Assert.Equal(0, RegisterResource(fixture.Context, ResourceAddress, owner));

        fixture.Context[CpuRegister.Rdi] = owner;
        Assert.Equal(0, AgcExports.DriverUnregisterAllResourcesForOwner(fixture.Context));
        Assert.Equal(0, RegisterResource(fixture.Context, ResourceAddress + 0x1000, owner));

        fixture.Context[CpuRegister.Rdi] = OwnerOutputAddress;
        fixture.Context[CpuRegister.Rsi] = NameAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverRegisterOwner(fixture.Context));
    }

    [Fact]
    public void UnregisteringOwnerAndResourcesReclaimsBothCapacities()
    {
        var fixture = CreateFixture(includeSizeOutput: false, includeHandleOutput: true);
        fixture.Memory.AddRegion(OwnerOutputAddress, new byte[sizeof(uint)]);
        Assert.Equal(0, Initialize(
            fixture.Context,
            BytesPerOwner + BytesPerResource,
            ownerCount: 1));

        var owner = RegisterOwner(fixture);
        Assert.Equal(0, RegisterResource(fixture.Context, ResourceAddress, owner));

        fixture.Context[CpuRegister.Rdi] = owner;
        Assert.Equal(0, AgcExports.DriverUnregisterOwnerAndResources(fixture.Context));

        var replacementOwner = RegisterOwner(fixture);
        Assert.Equal(0, RegisterResource(
            fixture.Context,
            ResourceAddress + 0x1000,
            replacementOwner));

        fixture.Context[CpuRegister.Rdi] = owner;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverUnregisterOwnerAndResources(fixture.Context));
    }

    private static int Initialize(CpuContext context, ulong memorySize, ulong ownerCount)
    {
        context[CpuRegister.Rdi] = RegistrationMemoryAddress;
        context[CpuRegister.Rsi] = memorySize;
        context[CpuRegister.Rdx] = ownerCount;
        return AgcExports.DriverInitResourceRegistration(context);
    }

    private static int InitializeAgc(CpuContext context)
    {
        context[CpuRegister.Rdi] = RegistrationMemoryAddress;
        context[CpuRegister.Rsi] = 10;
        return AgcExports.Init(context);
    }

    private static uint RegisterOwner(Fixture fixture)
    {
        fixture.Context[CpuRegister.Rdi] = OwnerOutputAddress;
        fixture.Context[CpuRegister.Rsi] = NameAddress;
        var result = AgcExports.DriverRegisterOwner(fixture.Context);
        Assert.Equal(0, result);
        Assert.True(fixture.Memory.TryRead(
            OwnerOutputAddress,
            fixture.Scratch.AsSpan(0, sizeof(uint))));
        return BinaryPrimitives.ReadUInt32LittleEndian(fixture.Scratch);
    }

    private static int RegisterResource(
        CpuContext context,
        ulong resourceAddress,
        uint owner = 0)
    {
        context[CpuRegister.Rdi] = HandleOutputAddress;
        context[CpuRegister.Rsi] = owner;
        context[CpuRegister.Rdx] = resourceAddress;
        context[CpuRegister.Rcx] = 0x100;
        context[CpuRegister.R8] = NameAddress;
        context[CpuRegister.R9] = 7;
        return AgcExports.DriverRegisterResource(context);
    }

    private static Fixture CreateFixture(bool includeSizeOutput, bool includeHandleOutput)
    {
        var memory = new FakeGuestMemory();
        if (includeSizeOutput)
        {
            memory.AddRegion(SizeOutputAddress, new byte[sizeof(ulong)]);
        }

        if (includeHandleOutput)
        {
            memory.AddRegion(HandleOutputAddress, new byte[sizeof(uint)]);
        }

        memory.AddRegion(NameAddress, Encoding.UTF8.GetBytes("resource-name\0"));
        var stack = new byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt32LittleEndian(stack.AsSpan(sizeof(ulong)), 0xA5A5);
        memory.AddRegion(StackAddress, stack);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsp] = StackAddress;
        return new Fixture(memory, context, new byte[sizeof(ulong)]);
    }

    private sealed record Fixture(FakeGuestMemory Memory, CpuContext Context, byte[] Scratch);

    private sealed class MemoryWrapper(ICpuMemory inner) : ICpuMemory, ICpuMemoryWrapper
    {
        public ICpuMemory Inner { get; } = inner;

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            Inner.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            Inner.TryWrite(virtualAddress, source);
    }
}
