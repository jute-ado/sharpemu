// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
// SOP1 / SOP2 / SOPC / SOPK / SOPP — scalar ALU and control
s_mov_b32 s0, s1
s_mov_b32 s0, 0x1234
s_mov_b64 s[0:1], s[2:3]
s_not_b32 s0, s1
s_not_b64 s[0:1], s[2:3]
s_brev_b32 s0, s1
s_bcnt1_i32_b32 s0, s1
s_ff1_i32_b32 s0, s1
s_bitset1_b32 s0, s1
s_getpc_b64 s[0:1]
s_and_saveexec_b64 s[0:1], s[2:3]
s_or_saveexec_b64 s[0:1], s[2:3]
s_xor_saveexec_b64 s[0:1], s[2:3]
s_andn2_saveexec_b64 s[0:1], s[2:3]
s_add_u32 s0, s1, s2
s_sub_u32 s0, s1, s2
s_add_i32 s0, s1, s2
s_sub_i32 s0, s1, s2
s_addc_u32 s0, s1, s2
s_subb_u32 s0, s1, s2
s_min_i32 s0, s1, s2
s_min_u32 s0, s1, s2
s_max_i32 s0, s1, s2
s_max_u32 s0, s1, s2
s_cselect_b32 s0, s1, s2
s_cselect_b64 s[0:1], s[2:3], s[4:5]
s_and_b32 s0, s1, s2
s_and_b64 s[0:1], s[2:3], s[4:5]
s_or_b32 s0, s1, s2
s_or_b64 s[0:1], s[2:3], s[4:5]
s_xor_b32 s0, s1, s2
s_xor_b64 s[0:1], s[2:3], s[4:5]
s_andn2_b32 s0, s1, s2
s_andn2_b64 s[0:1], s[2:3], s[4:5]
s_orn2_b32 s0, s1, s2
s_nand_b32 s0, s1, s2
s_nor_b32 s0, s1, s2
s_xnor_b32 s0, s1, s2
s_lshl_b32 s0, s1, s2
s_lshl_b64 s[0:1], s[2:3], s4
s_lshr_b32 s0, s1, s2
s_lshr_b64 s[0:1], s[2:3], s4
s_ashr_i32 s0, s1, s2
s_ashr_i64 s[0:1], s[2:3], s4
s_bfm_b32 s0, s1, s2
s_mul_i32 s0, s1, s2
s_bfe_u32 s0, s1, s2
s_bfe_i32 s0, s1, s2
s_absdiff_i32 s0, s1, s2
s_mul_hi_u32 s0, s1, s2
s_mul_hi_i32 s0, s1, s2
s_cmp_eq_i32 s0, s1
s_cmp_lg_i32 s0, s1
s_cmp_gt_i32 s0, s1
s_cmp_ge_i32 s0, s1
s_cmp_lt_i32 s0, s1
s_cmp_le_i32 s0, s1
s_cmp_eq_u32 s0, s1
s_cmp_lg_u32 s0, s1
s_cmp_gt_u32 s0, s1
s_cmp_ge_u32 s0, s1
s_cmp_lt_u32 s0, s1
s_cmp_le_u32 s0, s1
s_cmp_eq_u64 s[0:1], s[2:3]
s_cmp_lg_u64 s[0:1], s[2:3]
s_bitcmp0_b32 s0, s1
s_bitcmp1_b32 s0, s1
s_movk_i32 s0, 0x1234
s_cmovk_i32 s0, 0x1234
s_cmpk_eq_i32 s0, 0x1234
s_cmpk_lg_i32 s0, 0x1234
s_cmpk_gt_i32 s0, 0x1234
s_cmpk_ge_i32 s0, 0x1234
s_cmpk_lt_i32 s0, 0x1234
s_cmpk_le_i32 s0, 0x1234
s_cmpk_eq_u32 s0, 0x1234
s_addk_i32 s0, 0x1234
s_mulk_i32 s0, 0x1234
s_nop 0
s_waitcnt 0
s_waitcnt vmcnt(0) expcnt(0) lgkmcnt(0)
s_barrier
s_wakeup
s_sethalt 0
s_sleep 1
s_clause 0x1
s_branch 0
s_cbranch_scc0 0
s_cbranch_scc1 0
s_cbranch_vccz 0
s_cbranch_vccnz 0
s_cbranch_execz 0
s_cbranch_execnz 0
