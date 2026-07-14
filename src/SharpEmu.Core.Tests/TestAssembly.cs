// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

// Core conformance tests temporarily change process-wide emulator settings.
// Serial collection execution prevents unrelated tests from observing them.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
