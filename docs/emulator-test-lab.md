<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Emulator Test Lab workflow

SharpEmu supports an external, local-only regression framework for exercising
privately owned games. The external framework owns orchestration, portable
scenario contracts, controller routes, evidence classification, and immutable
run reports. This repository owns SharpEmu-specific instrumentation and
synthetic regression tests.

The capability probe emitted by the build is authoritative. A test must fail
its capability gate before launch when the current build cannot provide the
requested controller, presented-frame, timing, diagnostic, configuration, or
snapshot, or audio-capture behavior.

## Repository boundaries

- SharpEmu source and synthetic tests belong in this repository.
- Portable game scenarios, controller routes, and expectation or baseline
  metadata belong in the external corpus repository.
- Games, saves, screenshots, videos, GPU traces, memory images, and machine
  maps remain outside Git in a private local store.
- Framework source changes require their own framework branch only when the
  task changes orchestration or a versioned contract.

Because the emulator repositories are public, their committed documentation
must use placeholders—never your `F:\...` paths, private Forgejo address,
credentials, game identities unnecessarily, or vault layout. Exact
machine-specific commands belong in a private local runbook.

Public regression tests must use synthetic or legally redistributable inputs.
Reduce behavior learned from a private commercial-game run to a synthetic test
whenever possible.

## Starting an emulator task

Create one emulator feature worktree and one unique run root. Use the accepted
corpus branch by default:

```text
task/
├── SharpEmu worktree on feature/<change>
├── machine-emulators.json
└── runs/<unique-run-id>/

shared, read-only
├── released emu-test executable
├── accepted corpus checkout
└── private asset vault
```

The task-local `machine-emulators.json` must point to the executable built from
the task's SharpEmu worktree and identify that same worktree as
`repositoryPath`. The runner uses it to record the exact emulator commit and,
when necessary, a dirty-content hash.

Every scenario selects an explicit console profile, render scale, and
strict-dynlib setting. The current adapter supports base PS5 profiles but does
not advertise PS5 Pro or complete emulator-state restoration; those scenarios
must fail their capability gates before launch. Each run receives an isolated
save-data root instead of mutating a developer profile.

Run an accepted suite with explicit, portable arguments:

```text
emu-test suite run <suite> <corpus-root> <machine-assets.json> \
  <machine-emulators.json> <machine-profile-or-dash> <suite-runs-root> ps5
```

Use `local.ps5-quick` while iterating and `local.ps5-regression` for the
complete pre-merge game gate. Other `local.ps5-*` suites select a focused
operation such as audio, visual, performance, settings, or GPU diagnostics.
Do not use a `cross_platform` aggregate for routine SharpEmu development.

The explicit `ps5` argument is a fail-closed platform guard. The runner checks
it against the suite's declared scope before corpus resolution, run-directory
creation, emulator-map loading, or process launch. The corpus also rejects a
PS5 suite containing any shadPS4 scenario. The command's explicit corpus root
isolates test intent; it does not search for or automatically use another
task's corpus branch.

## When game progress changes

If the emulator branch still satisfies the accepted expectation, no corpus
branch is required. Add or strengthen a synthetic SharpEmu test in this branch
and continue using corpus `master`.

Create a paired corpus worktree when reviewed test intent changes, including:

- a new compatibility or progress floor;
- a changed DualSense/controller route;
- a visual or temporal candidate;
- a performance reference;
- a private save-data or future snapshot pin;
- a guest-GPU diagnostic policy;
- an audio health policy.

The workflow is:

1. run this branch against the accepted corpus;
2. preserve and review the immutable evidence;
3. create a corpus feature worktree;
4. generate a candidate instead of overwriting the accepted object;
5. commit only portable corpus metadata;
6. run focused and regression suites against clean paired revisions;
7. cross-reference the proven emulator and corpus commits;
8. merge the emulator and corpus branches consecutively.

Matching branch names help people recognize the pair, but they do not establish
compatibility. Exact commit and content identities recorded in the run do.

## Concurrent worktrees

Another SharpEmu worktree continues using corpus `master` or its own corpus
worktree, so it cannot see these unmerged expectations.

If two tasks edit the same scenario, route, baseline, or policy, merge the
first reviewed pair and rebase the second pair. The second task must rerun
against the newly accepted state and create a fresh candidate. Never choose a
digest conflict mechanically or widen a visual/performance threshold merely
to make the branch pass.

Git cannot atomically merge two repositories. Local development therefore uses
a final clean paired run followed by consecutive merges. Cross-repository CI
coordination is a separate future concern.

## Audio capture contract

When `EMULATOR_TEST_LAB_AUDIO_PCM16` contains an absolute output path,
SharpEmu writes the normalized main AudioOut stream there as append-only
48 kHz stereo signed PCM16. Capture occurs after guest-format conversion and
before the host audio backend, so it is independent of the selected sound
device, speaker volume, and host mixer. The file is private run evidence and
must never be committed.
