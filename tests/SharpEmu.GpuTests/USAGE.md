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

The same regression also sends its 1280x720 four-quadrant sampled texture
through the backend-neutral tiled-source seam twice at the same guest address.
Vulkan records each verified compute detile and buffer-to-image copy in the
draw command buffer, retains the transient buffers until that submission
completes, and must report two GPU-detile uploads with the second version in
the capture. This covers both initial creation and refresh of an existing
CPU-backed guest image. AGC can select that boundary for validated sampled 2D
textures, including complete 2D arrays, when `SHARPEMU_GPU_DETILE=1`; it
remains off by default, and this synthetic test continues to exercise the
backend seam directly.
