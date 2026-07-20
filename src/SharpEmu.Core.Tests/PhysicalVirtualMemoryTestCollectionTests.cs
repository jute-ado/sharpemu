// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PhysicalVirtualMemoryTestCollection
{
    public const string Name = "PhysicalVirtualMemory";
}

public sealed class PhysicalVirtualMemoryTestCollectionTests
{
    [Fact]
    public void FixedAddressPhysicalMemorySuitesShareOneCollection()
    {
        Type[] suites =
        [
            typeof(GuestCallbackIntegrationTests),
            typeof(GuestThreadLifecycleIntegrationTests),
            typeof(HostMemoryAbstractionTests),
            typeof(PhysicalVirtualMemoryTests),
            typeof(SelfLoaderTests),
            typeof(VirtualMemoryQueryTests),
        ];

        foreach (var suite in suites)
        {
            var collection = Assert.Single(
                suite.CustomAttributes,
                attribute => attribute.AttributeType == typeof(CollectionAttribute));
            Assert.Equal(
                PhysicalVirtualMemoryTestCollection.Name,
                Assert.Single(collection.ConstructorArguments).Value);
        }
    }
}
