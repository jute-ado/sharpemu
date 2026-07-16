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
test-driven workflow. Its current emphasis includes:

- regression tests for CPU execution, memory, loading, HLE, shaders, and Vulkan
- synthetic guest programs and shader workloads that require no copyrighted
  games, firmware, or proprietary assets
- generated HLE export registration instead of runtime reflection discovery
- one canonical implementation path for each subsystem rather than parallel
  compatibility implementations
- stronger Windows, Linux, and macOS x64 build and runtime validation
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
> can run the macOS x64 build through Rosetta 2.

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

* **Demon's Souls Remake**
  * [Demon's Souls [PPSA01341]](https://github.com/sharpemu/sharpemu/issues/2)
  * Demon's Souls is now video loop. Shaders are ready to be converted to SPIR-V/Vulkan. We are continuing our work on this.
  ![DeS videoOut submit first frame](./.github/images/des-videoout-shaders.jpg)

* **Poppy Playtime Chapter 1**
  * [Poppy Playtime Chapter 1 [PPSA20591]](https://github.com/sharpemu/sharpemu/issues/3)

* **SILENT HILL: The Short Message**
  * [SILENT HILL: The Short Message [PPSA10112]](https://github.com/sharpemu/sharpemu/issues/4)

* **Dreaming Sarah**
  * [Dreaming Sarah [PPSA02929]](https://github.com/sharpemu/sharpemu/issues/9)
  * Real texture rendering for this game;
  ![Splash texture](./.github/images/dreaming-sarah.jpg)


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

## Contributing

Before opening an issue or pull request, please read our contribution guidelines:

**[CONTRIBUTING.md](./CONTRIBUTING.md)**

The guide covers:
- Coding style and formatting
- AI-assisted contributions
- Pull request expectations
- Testing guidelines
- Legal and reverse engineering policy
