// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

// GUI tests share process-wide configuration and should not race one another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
