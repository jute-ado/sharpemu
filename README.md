<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu — Jute research fork

<p align="center">
  <img src="./assets/images/logo.png" width=30% height=30% />
</p>

<p align="center">
  An experimental PlayStation 5 emulator for Windows, Linux and macOS.
</p>

> [!IMPORTANT]
> This repository is an independent research fork of the
> [original SharpEmu project](https://github.com/par274/sharpemu). SharpEmu and
> its contributors created the foundation this work builds on; their ambitious
> work is deeply appreciated. This fork is not an official upstream release.

## About This Fork

This fork exists purely for research and education. Much of its development is
AI-assisted and exploratory, with a strong TDD approach to catch regressions,
validate behavior, and keep generated code grounded in observable results. AI
assistance is a development tool here, not evidence that a change is correct.

The fork is kept synchronized with upstream while experimenting with a more
test-driven workflow. Major downstream differences include:

- broader regression coverage for CPU execution, memory, loading, HLE, shaders,
  Vulkan, and compatibility milestones
- synthetic guest programs and executable shader-conformance workloads that
  require no copyrighted games, firmware, or proprietary assets
- generated HLE export registration instead of runtime reflection discovery
- a guest-thread scheduler with callback, synchronization, and lifecycle
  behavior exercised through native guest-code tests, including distinct
  POSIX `-1`/`errno` and SCE kernel semaphore contracts, distinct POSIX errno
  and encoded SCE error contracts for mutex and read/write-lock operations,
  rollback of failed read/write-lock initialization, input-only equeue timeout
  handling that avoids mutating guest polling intervals, bounded pthread-key
  destructor callbacks and thread-specific value cleanup when guest threads
  exit before they become joinable, and post-join scheduler, metadata, and
  unmanaged-handle reaping with deferred and late-detach cleanup; backend
  teardown also reaps unjoined workers and registered primary threads, with
  stale primary-thread identities recreated safely and process-scoped pthread
  keys and synchronization state reset for the next session; session reports
  retain native import progress and exact per-session unique-NID counts
  across guest workers and distinguish
  normal process exit codes from CPU traps, preserve structured native trap
  details and the faulting guest pthread identity across native workers instead
  of generic or threadless backend failures, include faulting-instruction
  decoding, cross-platform general-register snapshots, guest frame chains
  constrained to registered stack ranges with fault and return-site code
  windows, mapped-image-relative code locations, bounded 4 KiB fault-time guest
  stack windows with 128 bytes of pre-RSP context, and bounded readable-memory
  windows for pointer-valued registers plus their first-level references, with
  executable code-pointer candidates, extended context, and bounded decoded instruction
  paths with mapped-image-relative branch/data targets and exact-boundary
  prefix-preserving preceding-call hints plus bounded direct-callee context for
  frameless code,
  faulting guest-thread identity, and bounded cross-thread native
  import traces with resolved library/export identities, chronological combined
  history, all six SysV register arguments, guest-visible return values, and a
  reserved fault-thread slice in execution reports; schema-v5 JSON also exposes
  these entries structurally with module-relative return locations and retains
  exact preceding-call evidence on every walked frame,
  and immediate teardown signaling for interruptible guest waits
- current guest pthread attribute queries report the registered low stack
  address and size derived from the executing stack pointer, so conservative
  guest garbage collectors scan live roots without attributing that stack to
  other threads
- guest virtual- and direct-memory query contracts with exact argument
  validation, registered stack classification, terminal direct-memory ranges,
  Prospero-compatible gap errors, subrange direct-memory release, and
  idempotent fixed-range reservations; process reset also releases tracked libc
  heap allocations, discards stale mappings and names, and restarts direct and
  flexible-memory accounting for the next title
- sandboxed guest filesystem handling with virtual `/dev/random` and
  `/dev/urandom` descriptors that support entropy-read, stat, and close
  lifecycles, plus a consistent virtual `/devlog` container for stat, directory
  enumeration, open, and fstat without exposing host device paths; session
  reset disposes kernel and libc stdio host files plus sockets, clears guest
  mounts and I/O caches, and restarts file-descriptor, stdio-handle, and AIO
  allocation
- distinct four-argument POSIX and named five-argument Sony pthread creation
  ABIs, so unused registers cannot become guest thread names, with transactional
  output validation and rollback when guest scheduling fails
- process-scoped network teardown that disposes `libSceNet` sockets, releases
  per-thread errno storage, clears pool, resolver, SSL, HTTP/2, template, and
  NetCtl callback registries, and restarts guest-visible IDs between titles
- audio session teardown that disposes host AudioOut streams, clears the
  presentation-shutdown state, resets AudioOut2, ACM, NGS2, and FMOD compatibility
  registries, and restarts guest-visible audio handles between titles
- media-player session teardown that terminates retained FFmpeg decoder processes,
  closes their streams, and invalidates stale AvPlayer handles between titles
- media-codec session teardown that clears AJM contexts plus audio and video
  decoder registries and restarts their guest-visible IDs between titles
- C++ ABI session teardown that releases abandoned static-initializer guards so
  later titles cannot skip initialization or spin on stale owner threads
- application-service session teardown that resets PlayGo initialization and
  clears JSON guest objects and callback pointers between titles
- process-scoped event queues, event flags, semaphores, and exception handlers
  reset between sessions with blocked waiters woken through normal deleted-object
  semantics and deterministic handle allocation for the next process
- application plugin discovery and symbol resolution, including deferred module
  initializers that start once and can be retried after a guest failure, with
  sysmodule loaded state kept in the canonical per-session module registry
- persistent save-data mutations, including quota-aware mount information,
  retry-safe and mode-aware lifecycle events, bounded atomic icon writes, and
  size-reporting icon loads through mounted guest paths, with canonical
  size-only transaction-resource creation that never probes stale registers
- NP telemetry compatibility with guest-memory-validated event construction,
  bounded JSON serialization, and exact required-size reporting
- offline user-service defaults with the size-aware two-argument PS5 game-preset
  ABI and guest-memory-validated accessibility preference queries used by
  commercial titles
- a bounded, local-only game regression harness with redacted manifests suitable
  for source control
- Windows, Linux, and macOS x64 build, test, packaging, and release validation
- small, focused branches whose changes are merged only after relevant tests
  and hosted CI pass

The goal is to learn, improve emulator foundations, and—where a change is
general, understandable, independently tested, and useful upstream—eventually
contribute suitable work back to the original project. Not every experiment in
this fork will be appropriate for upstream, and any proposed contribution must
follow the original project’s review and contribution requirements.

The list above describes the fork’s maintained direction rather than every
individual commit. See the
[current upstream comparison](https://github.com/par274/sharpemu/compare/main...jute-ado:sharpemu:main)
for the exact code difference.

<p align="center">
  <a href="https://discord.gg/6GejPEDqpc">
    <img src="https://img.shields.io/badge/Discord-Upstream%20Community-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Join the upstream SharpEmu Discord">
  </a>
</p>

> [!NOTE]
> SharpEmu supports Windows x64, Linux x64, and macOS x64. Apple Silicon Macs
> can run the macOS x64 build through Rosetta 2, and Windows on ARM devices
> can run the Windows x64 build through Windows' built-in x64 emulation.

> [!WARNING]
> SharpEmu is an experimental PS5 emulator written in C#. This fork’s current
> focus is accuracy, tests, and infrastructure rather than game-specific hacks.

## Info

SharpEmu is an early-stage emulator developed for research and educational
purposes. It focuses exclusively on PlayStation 5 software; PS4 emulation is
outside the project's scope.

## Status

The emulator can load `eboot.bin`, ELF, SELF, PRX, and system-module images,
execute native x86-64 guest code, dispatch a growing HLE surface, translate AGC
shaders to SPIR-V/Vulkan, and present video for some games.

Current capabilities include:

- ELF/SELF loading, relocation, imports, TLS, and module initialization
- Native CPU execution with structured trap and execution diagnostics
- Application metadata and content-bundle inspection
- Partial kernel, libc, Fiber, AMPR, PlayGo, audio, input, and savedata support
- Vulkan video output on Windows and Linux and MoltenVK output on macOS
- Windows, Linux, and macOS x64 release archives

Platform support remains experimental. Compatibility and performance vary by
game, operating system, and GPU driver.

## Using

Download the release archive for your operating system, extract it, and launch
SharpEmu with the path to a legally obtained game's `eboot.bin`.

Windows PowerShell:

```powershell
.\SharpEmu.exe "C:\path\to\game\eboot.bin" 2>&1 |
  Tee-Object -FilePath "SharpEmu.log"
```

Linux and macOS:

```bash
chmod +x ./SharpEmu
./SharpEmu "/path/to/game/eboot.bin" 2>&1 | tee SharpEmu.log
```

A Vulkan-capable GPU and current graphics driver are required. The macOS
release includes the MoltenVK Vulkan implementation.

## Games Tested

The following table records recent results from this fork's local regression
harness. A timeout means execution remained alive for the configured test
window; it does not imply the game is playable.

| Game | Title ID | Current observed progress |
| --- | --- | --- |
| Dreaming Sarah | PPSA02929 | Loads and sustains execution for a 90-second automated input run. Vulkan presents a non-black, multi-color guest frame; gameplay is not yet validated. |
| Jusant | PPSA10264 | Loads seven modules and sustains a 90-second UE5 execution run without a CPU trap. No gameplay or rendered frame is validated yet. |
| Poppy Playtime: Chapter 1 | PPSA20591 | Loads seven modules and sustains the execution-survival window without a CPU trap. No gameplay is validated yet. |
| SILENT HILL: The Short Message | PPSA10112 | Loads six modules and sustains the execution-survival window without a CPU trap. No gameplay is validated yet. |
| SUPER BOMBERMAN R 2 | PPSA07190 | Loads thirteen modules and presents a 1920×1080 guest frame. Correct current-pthread stack attributes retain IL2CPP roots, nested callback stacks are registered, and deferred module starts receive their guest argument blocks. PSNCore consequently accepts Unity's runtime contract instead of faulting. Partial wave64 compute uses Vulkan's safe native-subgroup path instead of a barrier bridge that reset the host GPU, image bindings in scalar-skipped blocks no longer drop whole post-processing shaders, and live SCC scalar sources now evaluate and compile instead of rejecting repeated atmosphere-LUT passes. Execution survives 120 seconds and reaches later post-processing and 3D compute without a CPU trap or device loss. Captured display buffers are still black and gameplay is not yet validated. |
| Demon's Souls | PPSA01342 | Loads the main image and one module, presents a 3840×2160 splash, and reaches more than 670,000 import dispatches after canonical transaction-resource handling removes the earlier address-`0x9` fault; execution currently ends at an `int 41h` trap in `eboot.bin+0x1F403A3`. |

These results are observations, not compatibility promises. Exact progress can
change with the game revision, operating system, GPU driver, and test duration.


> [!IMPORTANT]
> This project does **not** support or condone piracy.
> Games used for private development and testing must be legally obtained and
> dumped from hardware owned by the tester. Game images are never committed to
> this repository.

## Build

1. Install the .NET SDK version specified in [`global.json`](./global.json).
2. Clone this fork: `git clone https://github.com/jute-ado/sharpemu.git`
3. Open the solution file (`SharpEmu.slnx`) in **VSCode**.
4. Build the project: `dotnet build` or `dotnet publish`
5. Build artifacts will be located in the `artifacts` directory.

## Disclaimer

SharpEmu is an experimental emulator intended for research and educational purposes.

This project does not contain any copyrighted system firmware, game data, or proprietary PlayStation assets.

## Special Thanks

The following projects were extremely helpful during development:

* **[ShadPS4](https://github.com/shadps4-emu/shadPS4)**
Helped with understanding the basic architecture of the PlayStation 4.

* **[Kyty](https://github.com/InoriRus/Kyty)**
One of the few PS5 emulator projects available and very useful for studying native code execution.

* **Ryujinx**
Provided valuable references for filesystem handling and low-level C# implementation patterns.

# License

- [**GPL-2.0 license**](./LICENSE)
