#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
shader_directory="${1:-${repository_root}/artifacts/shader-dump}"

shopt -s nullglob
manifests=("${shader_directory}"/*.conformance.json)
if (( ${#manifests[@]} == 0 )); then
  echo "shader dump did not produce any executable conformance manifests" >&2
  exit 1
fi

conformance_project="${repository_root}/tools/SharpEmu.Tools.GpuConformance/SharpEmu.Tools.GpuConformance.csproj"
dotnet build "${conformance_project}" --configuration Release

for manifest in "${manifests[@]}"; do
  dotnet run \
    --project "${conformance_project}" \
    --configuration Release \
    --no-build \
    -- \
    "${manifest}"
done

echo "Executed ${#manifests[@]} synthetic Vulkan conformance case(s)."
