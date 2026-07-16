<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Local game regressions

This opt-in test project runs SharpEmu against locally owned game dumps without
placing game content or machine-specific paths in the repository.

1. Copy `games.example.json` to `games.local.json`.
2. Point each case at a local `eboot.bin`, replace `expectedBundleSha256` with
   the bundle fingerprint from a load-only report, and define the compatibility
   result that should remain stable. The fingerprint is checked after loading
   but before module initializers or guest code execute.
3. Build and run:

   ```text
   dotnet test tests/SharpEmu.GameTests/SharpEmu.GameTests.csproj -c Release
   ```

`games.local.json` is ignored by Git. The manifest may instead live anywhere
outside the repository when `SHARPEMU_GAME_TEST_MANIFEST` contains its absolute
path. Reports and captured output default to `artifacts/game-tests`, which is
also ignored.

The harness launches the CLI in a child process so execution cases have a hard
timeout and a crashed or stalled guest cannot take down the test runner. CI
builds and validates the harness but skips local game execution when no manifest
is configured. `requiredVideoOutFrameFingerprints` checks the guest's CPU-visible
VideoOut buffer. `requiredPresentedGuestImageFingerprints` performs one opt-in
Vulkan readback and checks the image SharpEmu actually presents; use the latter
when GPU rendering does not write the final pixels back into guest RAM.
