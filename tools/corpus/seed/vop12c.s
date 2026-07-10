// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
// VOP1 / VOP2 / VOPC — vector ALU 32-bit encodings
v_mov_b32 v0, v1
v_mov_b32 v0, s1
v_mov_b32 v0, 0x3f800000
v_cvt_f32_i32 v0, v1
v_cvt_f32_u32 v0, v1
v_cvt_i32_f32 v0, v1
v_cvt_u32_f32 v0, v1
v_cvt_f16_f32 v0, v1
v_cvt_f32_f16 v0, v1
v_cvt_f32_ubyte0 v0, v1
v_cvt_f32_ubyte1 v0, v1
v_cvt_f32_ubyte2 v0, v1
v_cvt_f32_ubyte3 v0, v1
v_fract_f32 v0, v1
v_trunc_f32 v0, v1
v_ceil_f32 v0, v1
v_rndne_f32 v0, v1
v_floor_f32 v0, v1
v_exp_f32 v0, v1
v_log_f32 v0, v1
v_rcp_f32 v0, v1
v_rcp_iflag_f32 v0, v1
v_rsq_f32 v0, v1
v_sqrt_f32 v0, v1
v_sin_f32 v0, v1
v_cos_f32 v0, v1
v_not_b32 v0, v1
v_bfrev_b32 v0, v1
v_ffbh_u32 v0, v1
v_ffbl_b32 v0, v1
v_ffbh_i32 v0, v1
v_frexp_exp_i32_f32 v0, v1
v_frexp_mant_f32 v0, v1
v_movreld_b32 v0, v1
v_movrels_b32 v0, v1
v_add_f32 v0, v1, v2
v_sub_f32 v0, v1, v2
v_subrev_f32 v0, v1, v2
v_mul_legacy_f32 v0, v1, v2
v_mul_f32 v0, v1, v2
v_mul_i32_i24 v0, v1, v2
v_mul_hi_i32_i24 v0, v1, v2
v_mul_u32_u24 v0, v1, v2
v_mul_hi_u32_u24 v0, v1, v2
v_min_f32 v0, v1, v2
v_max_f32 v0, v1, v2
v_min_i32 v0, v1, v2
v_max_i32 v0, v1, v2
v_min_u32 v0, v1, v2
v_max_u32 v0, v1, v2
v_lshrrev_b32 v0, v1, v2
v_ashrrev_i32 v0, v1, v2
v_lshlrev_b32 v0, v1, v2
v_and_b32 v0, v1, v2
v_or_b32 v0, v1, v2
v_xor_b32 v0, v1, v2
v_xnor_b32 v0, v1, v2
v_mac_f32 v0, v1, v2
v_madmk_f32 v0, v1, 0x3f800000, v2
v_madak_f32 v0, v1, v2, 0x3f800000
v_add_nc_u32 v0, v1, v2
v_sub_nc_u32 v0, v1, v2
v_subrev_nc_u32 v0, v1, v2
v_fmac_f32 v0, v1, v2
v_fmamk_f32 v0, v1, 0x3f800000, v2
v_fmaak_f32 v0, v1, v2, 0x3f800000
v_cvt_pkrtz_f16_f32 v0, v1, v2
v_cndmask_b32 v0, v1, v2, vcc_lo
v_add_co_ci_u32 v0, vcc_lo, v1, v2, vcc_lo
v_sub_co_ci_u32 v0, vcc_lo, v1, v2, vcc_lo
v_cmp_f_f32 vcc_lo, v1, v2
v_cmp_lt_f32 vcc_lo, v1, v2
v_cmp_eq_f32 vcc_lo, v1, v2
v_cmp_le_f32 vcc_lo, v1, v2
v_cmp_gt_f32 vcc_lo, v1, v2
v_cmp_lg_f32 vcc_lo, v1, v2
v_cmp_ge_f32 vcc_lo, v1, v2
v_cmp_o_f32 vcc_lo, v1, v2
v_cmp_u_f32 vcc_lo, v1, v2
v_cmp_nge_f32 vcc_lo, v1, v2
v_cmp_nlg_f32 vcc_lo, v1, v2
v_cmp_ngt_f32 vcc_lo, v1, v2
v_cmp_nle_f32 vcc_lo, v1, v2
v_cmp_neq_f32 vcc_lo, v1, v2
v_cmp_nlt_f32 vcc_lo, v1, v2
v_cmp_tru_f32 vcc_lo, v1, v2
v_cmp_lt_i32 vcc_lo, v1, v2
v_cmp_eq_i32 vcc_lo, v1, v2
v_cmp_le_i32 vcc_lo, v1, v2
v_cmp_gt_i32 vcc_lo, v1, v2
v_cmp_ne_i32 vcc_lo, v1, v2
v_cmp_ge_i32 vcc_lo, v1, v2
v_cmp_lt_u32 vcc_lo, v1, v2
v_cmp_eq_u32 vcc_lo, v1, v2
v_cmp_le_u32 vcc_lo, v1, v2
v_cmp_gt_u32 vcc_lo, v1, v2
v_cmp_ne_u32 vcc_lo, v1, v2
v_cmp_ge_u32 vcc_lo, v1, v2
v_cmp_class_f32 vcc_lo, v1, v2
