// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
// SMEM / MUBUF / MTBUF / DS / FLAT-GLOBAL — memory encodings
s_load_dword s0, s[4:5], 0x0
s_load_dwordx2 s[0:1], s[4:5], 0x10
s_load_dwordx4 s[0:3], s[4:5], 0x20
s_load_dwordx8 s[0:7], s[8:9], 0x0
s_load_dwordx16 s[0:15], s[16:17], 0x0
s_buffer_load_dword s0, s[4:7], 0x0
s_buffer_load_dwordx2 s[0:1], s[4:7], 0x0
s_buffer_load_dwordx4 s[0:3], s[4:7], 0x0
s_memtime s[0:1]
s_dcache_inv
buffer_load_dword v0, v1, s[4:7], 0 offen
buffer_load_dwordx2 v[0:1], v2, s[4:7], 0 offen
buffer_load_dwordx3 v[0:2], v3, s[4:7], 0 offen
buffer_load_dwordx4 v[0:3], v4, s[4:7], 0 offen
buffer_load_ubyte v0, v1, s[4:7], 0 offen
buffer_load_sbyte v0, v1, s[4:7], 0 offen
buffer_load_ushort v0, v1, s[4:7], 0 offen
buffer_load_sshort v0, v1, s[4:7], 0 offen
buffer_load_format_x v0, v1, s[4:7], 0 offen
buffer_load_format_xy v[0:1], v2, s[4:7], 0 offen
buffer_load_format_xyz v[0:2], v3, s[4:7], 0 offen
buffer_load_format_xyzw v[0:3], v4, s[4:7], 0 offen
buffer_store_dword v0, v1, s[4:7], 0 offen
buffer_store_dwordx2 v[0:1], v2, s[4:7], 0 offen
buffer_store_dwordx4 v[0:3], v4, s[4:7], 0 offen
buffer_store_byte v0, v1, s[4:7], 0 offen
buffer_store_short v0, v1, s[4:7], 0 offen
buffer_store_format_x v0, v1, s[4:7], 0 offen
buffer_store_format_xyzw v[0:3], v4, s[4:7], 0 offen
buffer_atomic_add v0, v1, s[4:7], 0 offen
buffer_atomic_sub v0, v1, s[4:7], 0 offen
buffer_atomic_smin v0, v1, s[4:7], 0 offen
buffer_atomic_umax v0, v1, s[4:7], 0 offen
buffer_atomic_and v0, v1, s[4:7], 0 offen
buffer_atomic_or v0, v1, s[4:7], 0 offen
buffer_atomic_xor v0, v1, s[4:7], 0 offen
buffer_atomic_swap v0, v1, s[4:7], 0 offen
buffer_atomic_cmpswap v[0:1], v2, s[4:7], 0 offen
tbuffer_load_format_x v0, v1, s[4:7], 0 format:[BUF_FMT_32_FLOAT] offen
tbuffer_load_format_xyzw v[0:3], v4, s[4:7], 0 format:[BUF_FMT_32_32_32_32_FLOAT] offen
tbuffer_store_format_x v0, v1, s[4:7], 0 format:[BUF_FMT_32_FLOAT] offen
tbuffer_store_format_xyzw v[0:3], v4, s[4:7], 0 format:[BUF_FMT_32_32_32_32_FLOAT] offen
ds_read_b32 v0, v1
ds_read_b64 v[0:1], v2
ds_read2_b32 v[0:1], v2 offset0:0 offset1:1
ds_read2st64_b32 v[0:1], v2 offset0:0 offset1:1
ds_read_u8 v0, v1
ds_read_i8 v0, v1
ds_read_u16 v0, v1
ds_read_i16 v0, v1
ds_write_b32 v0, v1
ds_write_b64 v0, v[1:2]
ds_write2_b32 v0, v1, v2 offset0:0 offset1:1
ds_write2st64_b32 v0, v1, v2 offset0:0 offset1:1
ds_write_b8 v0, v1
ds_write_b16 v0, v1
ds_add_u32 v0, v1
ds_sub_u32 v0, v1
ds_min_i32 v0, v1
ds_max_u32 v0, v1
ds_and_b32 v0, v1
ds_or_b32 v0, v1
ds_xor_b32 v0, v1
ds_add_rtn_u32 v0, v1, v2
ds_swizzle_b32 v0, v1 offset:0x8000
ds_bpermute_b32 v0, v1, v2
ds_permute_b32 v0, v1, v2
flat_load_dword v0, v[1:2]
flat_load_dwordx2 v[0:1], v[2:3]
flat_load_dwordx4 v[0:3], v[4:5]
flat_load_ubyte v0, v[1:2]
flat_load_ushort v0, v[1:2]
flat_store_dword v[0:1], v2
flat_store_dwordx2 v[0:1], v[2:3]
flat_store_dwordx4 v[0:1], v[2:5]
flat_atomic_add v0, v[1:2], v3 glc
flat_atomic_cmpswap v0, v[1:2], v[3:4] glc
global_load_dword v0, v[1:2], off
global_load_dwordx2 v[0:1], v[2:3], off
global_load_dwordx4 v[0:3], v[4:5], off
global_load_ubyte v0, v[1:2], off
global_store_dword v[0:1], v2, off
global_store_dwordx2 v[0:1], v[2:3], off
global_store_dwordx4 v[0:1], v[2:5], off
global_atomic_add v0, v[1:2], v3, off glc
scratch_load_dword v0, v1, off
scratch_store_dword v0, v1, off
