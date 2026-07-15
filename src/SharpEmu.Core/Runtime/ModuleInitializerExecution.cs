// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Core.Runtime;

public sealed record ModuleInitializerExecution(
    string ModulePath,
    int InitializerIndex,
    ulong EntryPoint,
    OrbisGen2Result Result);
