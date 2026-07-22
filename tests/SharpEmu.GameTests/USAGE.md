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
is configured. Each manifest case is discovered as a separate test, so one
failure does not hide later compatibility results. A single case can be selected
by its displayed name, for example:

```text
dotnet test tests/SharpEmu.GameTests/SharpEmu.GameTests.csproj -c Release --filter "DisplayName~Example game load"
```

Standard output and error are streamed directly to their ignored artifact logs
instead of being buffered in memory. Each log is capped at 64 MiB so a repeated
guest diagnostic cannot exhaust host memory or disk. Import counts, known and
unexpected warning totals, configured output milestones, and requested image
observations continue to be analyzed after a log reaches that cap.

## External GPU and pull-request runners

An external runner can keep licensed game archives, GPU scheduling, display
capture, recordings, pull-request discovery, and result publication outside
this repository while using this project as the single compatibility assertion
engine:

1. Extract the runner-owned game archive into an isolated workspace.
2. Generate a private schema-1 manifest whose `executablePath` points into that
   workspace and whose bundle hash and expectations identify the exact game
   build.
3. Set `SHARPEMU_GAME_TEST_MANIFEST` to that manifest and provide the runner's
   GPU-backed display environment.
4. Run the normal test project with a machine-readable test logger:

   ```text
   dotnet test tests/SharpEmu.GameTests/SharpEmu.GameTests.csproj \
     -c Release \
     --logger "trx;LogFileName=game-regressions.trx" \
     --results-directory artifacts/game-tests/test-results
   ```

5. Publish the TRX plus the ignored JSON execution reports, stdout/stderr logs,
   and requested image captures from `artifactDirectory`. Screen or window
   recording remains an external concern and can run around the test process.

The external service should derive pass/fail from the test result rather than
from process survival alone. It may translate the result into its own dashboard
schema, attach recordings, and post pull-request comments without coupling
those orchestration concerns to the emulator. Game archives, private manifests,
machine paths, and captured proprietary content remain runner-local.

`requiredVideoOutFrameFingerprints` checks the guest's CPU-visible VideoOut
buffer. `requiredPresentedGuestImage` captures one explicit presented frame and
checks the image SharpEmu actually presents. Set `fingerprint` for a stable exact
image, or use `forbiddenFingerprints` for a coarse progression gate that rejects
known stale or broken frames while allowing rendering to improve.
Use `minimumNonBlackPixels` to reject empty output without pinning any exact
pixels; this is the preferred first rendering milestone while output is evolving.
Use `minimumDistinctColors` (from 2 through 65,536) to reject solid or
near-solid captures while still allowing individual pixels, lighting, and shader
output to improve. A small threshold such as 16 is a useful coarse milestone;
the diagnostic count intentionally stops at the configured maximum.
Use separate cases for separate milestones so each synchronous Vulkan readback
runs in an isolated emulator process. Presented-frame checks also write ignored
BMP and metadata files beside the JSON report so the tested output can be
inspected without rerunning the game.

`requiredGuestImageWrite` turns an intermediate capture into a repeatable local
regression. Its `selector` uses the same canonical address-or-size syntax as the
presenter (for example, `1280x720@105`). Add a pixel-shader qualifier when
several passes share that target, such as
`3840x2160,ps=0x55D4300@1`. The occurrence then counts only writes matching both
the image and shader. When address-space randomization changes shader addresses,
use a stable shader-signature prefix such as `1920x1080,sig=8988B48B@1`.
Signatures are the `ps_hash` values emitted by guest-write diagnostics. Use
`minimumNonBlackPixels` for a coarse content milestone
and `minimumDistinctColors` to reject blank or flat output without fixing exact
pixels. Use `forbiddenFingerprints` for known-bad output; reserve `fingerprint`
for output that is intentionally exact and stable. The harness configures
capture variables itself, requires a dedicated capture marker, and stores
ignored RGBA/BMP artifacts per case.
Keep one synchronous intermediate capture per case so failures identify one
pipeline milestone precisely.

## Deterministic controller replay

Execution cases may include a `padReplay` timeline to reach menus and gameplay
without relying on a focused window or physical controller. Each event is a
complete pad snapshot that becomes active at `atMilliseconds` after
`scePadInit`; an empty `buttons` array releases every button. Events must be in
strictly increasing order and occur before the case timeout.

```json
"padReplay": {
  "events": [
    {
      "atMilliseconds": 3000,
      "buttons": ["Cross"]
    },
    {
      "atMilliseconds": 3400,
      "buttons": []
    },
    {
      "atMilliseconds": 5000,
      "buttons": ["Right"],
      "leftX": 255
    }
  ]
}
```

Supported buttons are `L3`, `R3`, `Options`, the four D-pad directions, `L1`,
`L2`, `R1`, `R2`, `Triangle`, `Circle`, `Cross`, `Square`, and `TouchPad`.
Stick and trigger values use the guest's 0–255 representation; omitted sticks
are centered at 128 and omitted triggers are released. A replay fully replaces
live host input for that isolated emulator process, making repeated runs
comparable. The older `SHARPEMU_AUTO_CROSS` debugging variable remains
compatible but is translated into this same replay engine.

## Intermediate GPU captures

When a presented frame is wrong but the game is rendering, capture one
intermediate guest-image write without changing the game-test manifest:

```text
SHARPEMU_CAPTURE_GUEST_IMAGE_WRITE=0xC490000@3
SHARPEMU_GUEST_IMAGE_DUMP_DIR=C:\path\to\captures
```

The selector before `@` may instead be a structural image size, such as
`1280x720@100`. Size selectors are useful when guest addresses vary between
processes. Append `,ps=0x<address>` before `@` to restrict the match to one
pixel shader, or `,sig=<ps_hash>` for an ASLR-independent shader match. Signature
prefixes contain 8–64 hexadecimal digits. The number after `@` is the matching
write to capture. Each
capture logs a fingerprint and, for supported RGBA formats, writes both the raw
bytes and a viewable BMP to the dump directory.

Capture directories and machine-specific environment values are local
diagnostics. Do not add game binaries, captures, local paths, or local
fingerprints to the repository.

`maximumImportWarnings` limits unexpected import warnings. Put understood,
game-expected non-success results in `knownImportWarnings` as exact `nid` and
`result` pairs. Repeated known results do not consume the budget, while an
unresolved import, a different result for the same NID, or any other new warning
still fails a zero-warning contract.

## Selected draw vertex traces

When an intermediate image identifies a suspicious shader pass, trace a bounded
number of matching draws by either export- or pixel-shader address:

```text
SHARPEMU_TRACE_DRAW_SHADER=0x5662100@4
```

Each trace records render targets, primitive and index state, vertex-buffer
metadata, the first referenced vertex records as raw bytes, and decoded values
for known 32-bit float formats. This is one canonical replacement for
shape- or texture-specific vertex probes; the limit after `@` prevents an
accidental unbounded game log.
