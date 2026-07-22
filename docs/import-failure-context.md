<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Import failure context

Set `SHARPEMU_TRACE_IMPORT_FAILURE_CONTEXT` to a NID, library-name fragment,
or export-name fragment to log the recent native import history when a matching
HLE call returns a negative result. Matching is case-insensitive. Normal runs
are unchanged when the variable is unset.

The diagnostic keeps 64 recent calls by default, including resolved symbols,
guest thread handles, return addresses, all six SysV register arguments, and
completed guest-visible return values. `--trace-imports=N` overrides the ring
size. At most eight matching failure dumps are emitted per execution session so
a frequently retried error cannot flood the log.

Windows PowerShell example:

```powershell
$env:SHARPEMU_TRACE_IMPORT_FAILURE_CONTEXT = "Q3VBxCXhUHs"
.\SharpEmu.exe --trace-imports=32 "C:\path\to\game\eboot.bin" 2>&1 |
  Tee-Object -FilePath "SharpEmu.log"
```

Linux and macOS example:

```bash
SHARPEMU_TRACE_IMPORT_FAILURE_CONTEXT=Q3VBxCXhUHs \
  ./SharpEmu --trace-imports=32 "/path/to/game/eboot.bin" 2>&1 |
  tee SharpEmu.log
```

This switch is intended to establish which guest calls immediately preceded a
repeatable HLE failure. It does not change the selected export's return value or
otherwise recover from the error.
