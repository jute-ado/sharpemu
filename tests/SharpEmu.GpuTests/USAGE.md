<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Synthetic GPU regressions

This project exercises SharpEmu's canonical Vulkan presenter with synthetic
shaders and render targets. It does not use or require game files.

The tests are opt-in because they require a Vulkan device and a working
windowing environment:

```text
SHARPEMU_RUN_GPU_TESTS=1 dotnet test tests/SharpEmu.GpuTests/SharpEmu.GpuTests.csproj
```

Normal solution test runs discover these tests but skip their execution.
Linux CI runs them under Xvfb with Mesa's software Vulkan implementation so
rendering and synchronization regressions do not depend on a physical GPU.

The presenter regression also enables the Vulkan detile self-test. Its four
cases cover exact-XOR and block-table addressing, 4-, 8-, and 16-byte texels,
and two array layers. Each result must survive a device-local
compute-buffer-to-sampled-image-to-readback round-trip before it is compared
byte-for-byte with the CPU detiler.
