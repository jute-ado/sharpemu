# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Builds the synthetic Gen5 decoder corpus from seed assembly files.

For every seed line and every target, assembles with llvm-mc, extracts raw
.text bytes with llvm-objcopy and a reference disassembly with llvm-objdump,
then writes one JSON object per case into
src/SharpEmu.Libs.Tests/Corpus/corpus-<target>.jsonl.

Only LLVM tooling is used: every byte of the corpus is our own compiled
artifact. No console-derived data.

Usage: python3 tools/corpus/generate.py [--llvm-bin /opt/homebrew/opt/llvm/bin]
"""

from __future__ import annotations

import argparse
import json
import pathlib
import subprocess
import sys
import tempfile

TARGETS = ["gfx1013", "gfx1030"]
TRIPLE = "amdgcn-amd-amdhsa"

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
SEED_DIR = pathlib.Path(__file__).resolve().parent / "seed"
OUT_DIR = REPO_ROOT / "src" / "SharpEmu.Libs.Tests" / "Corpus"


def run(args: list[str], **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(args, capture_output=True, text=True, **kwargs)


def assemble_case(llvm: pathlib.Path, target: str, asm: str, tmp: pathlib.Path):
    src = tmp / "case.s"
    obj = tmp / "case.o"
    binf = tmp / "case.bin"
    src.write_text(asm + "\n")

    mc = run([str(llvm / "llvm-mc"), f"-triple={TRIPLE}", f"-mcpu={target}",
              "--filetype=obj", str(src), "-o", str(obj)])
    if mc.returncode != 0:
        return None, None, mc.stderr.strip().splitlines()[0] if mc.stderr.strip() else "asm-error"

    run([str(llvm / "llvm-objcopy"), "-O", "binary", "--only-section=.text",
         str(obj), str(binf)])
    data = binf.read_bytes()

    dump = run([str(llvm / "llvm-objdump"), "-d", f"--mcpu={target}", str(obj)])
    disasm = []
    in_text = False
    for line in dump.stdout.splitlines():
        if "<.text>" in line:
            in_text = True
            continue
        if in_text and line.strip():
            text = line.split("//")[0].strip()
            if text:
                disasm.append(text)
    return data, " ; ".join(disasm), None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--llvm-bin", default="/opt/homebrew/opt/llvm/bin")
    args = parser.parse_args()
    llvm = pathlib.Path(args.llvm_bin)

    seeds: list[tuple[str, str]] = []
    for seed_file in sorted(SEED_DIR.glob("*.s")):
        for raw in seed_file.read_text().splitlines():
            line = raw.strip()
            if not line or line.startswith("//"):
                continue
            seeds.append((seed_file.stem, line))

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    totals = {}
    for target in TARGETS:
        ok = errors = 0
        out_path = OUT_DIR / f"corpus-{target}.jsonl"
        with out_path.open("w") as out, tempfile.TemporaryDirectory() as tmpdir:
            tmp = pathlib.Path(tmpdir)
            for family, asm in seeds:
                data, disasm, error = assemble_case(llvm, target, asm, tmp)
                entry = {"family": family, "asm": asm, "target": target}
                if error is not None:
                    entry["status"] = "asm-error"
                    entry["error"] = error
                    errors += 1
                else:
                    entry["status"] = "ok"
                    entry["hex"] = data.hex()
                    entry["disasm"] = disasm
                    ok += 1
                out.write(json.dumps(entry) + "\n")
        totals[target] = (ok, errors)
        print(f"{target}: {ok} ok, {errors} asm-error -> {out_path}")

    print(f"seed lines: {len(seeds)}")
    return 0 if all(ok > 0 for ok, _ in totals.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
