// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

// HLE modules intentionally model process-wide state. Run test collections in
// a deterministic sequence until each module has an explicit reset contract.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
