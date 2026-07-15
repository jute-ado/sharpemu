#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_directory="${1:-${repository_root}/artifacts/shader-dump}"

if ! command -v spirv-val >/dev/null 2>&1; then
  echo "spirv-val is required to validate synthetic shaders" >&2
  exit 2
fi

mkdir -p "${output_directory}"
dotnet run \
  --project "${repository_root}/tools/SharpEmu.Tools.ShaderDump/SharpEmu.Tools.ShaderDump.csproj" \
  --configuration Release \
  -- \
  "${output_directory}"

validated=0
while IFS= read -r -d '' shader; do
  spirv-val --target-env vulkan1.3 "${shader}"
  validated=$((validated + 1))
done < <(find "${output_directory}" -maxdepth 1 -type f -name '*.spv' -print0)

if (( validated == 0 )); then
  echo "shader dump did not produce any SPIR-V modules" >&2
  exit 1
fi

echo "Validated ${validated} synthetic SPIR-V modules."
