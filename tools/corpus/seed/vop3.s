// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
// VOP3A / VOP3B — 64-bit vector ALU encodings
v_mad_u32_u24 v0, v1, v2, v3
v_mad_i32_i24 v0, v1, v2, v3
v_cubeid_f32 v0, v1, v2, v3
v_cubesc_f32 v0, v1, v2, v3
v_cubetc_f32 v0, v1, v2, v3
v_cubema_f32 v0, v1, v2, v3
v_bfe_u32 v0, v1, v2, v3
v_bfe_i32 v0, v1, v2, v3
v_bfi_b32 v0, v1, v2, v3
v_fma_f32 v0, v1, v2, v3
v_alignbit_b32 v0, v1, v2, v3
v_alignbyte_b32 v0, v1, v2, v3
v_min3_f32 v0, v1, v2, v3
v_min3_i32 v0, v1, v2, v3
v_min3_u32 v0, v1, v2, v3
v_max3_f32 v0, v1, v2, v3
v_max3_i32 v0, v1, v2, v3
v_max3_u32 v0, v1, v2, v3
v_med3_f32 v0, v1, v2, v3
v_med3_i32 v0, v1, v2, v3
v_med3_u32 v0, v1, v2, v3
v_sad_u8 v0, v1, v2, v3
v_sad_hi_u8 v0, v1, v2, v3
v_sad_u16 v0, v1, v2, v3
v_sad_u32 v0, v1, v2, v3
v_cvt_pk_u8_f32 v0, v1, v2, v3
v_lerp_u8 v0, v1, v2, v3
v_perm_b32 v0, v1, v2, v3
v_xad_u32 v0, v1, v2, v3
v_lshl_add_u32 v0, v1, v2, v3
v_add_lshl_u32 v0, v1, v2, v3
v_add3_u32 v0, v1, v2, v3
v_lshl_or_b32 v0, v1, v2, v3
v_and_or_b32 v0, v1, v2, v3
v_or3_b32 v0, v1, v2, v3
v_mad_u16 v0, v1, v2, v3
v_mad_i16 v0, v1, v2, v3
v_fma_f16 v0, v1, v2, v3
v_div_fixup_f32 v0, v1, v2, v3
v_div_fmas_f32 v0, v1, v2, v3
v_msad_u8 v0, v1, v2, v3
v_mullit_f32 v0, v1, v2, v3
v_add_f64 v[0:1], v[2:3], v[4:5]
v_mul_f64 v[0:1], v[2:3], v[4:5]
v_min_f64 v[0:1], v[2:3], v[4:5]
v_max_f64 v[0:1], v[2:3], v[4:5]
v_fma_f64 v[0:1], v[2:3], v[4:5], v[6:7]
v_mul_lo_u32 v0, v1, v2
v_mul_hi_u32 v0, v1, v2
v_mul_hi_i32 v0, v1, v2
v_add_co_u32 v0, vcc_lo, v1, v2
v_sub_co_u32 v0, vcc_lo, v1, v2
v_subrev_co_u32 v0, vcc_lo, v1, v2
v_div_scale_f32 v0, vcc_lo, v1, v2, v3
v_mad_u64_u32 v[0:1], vcc_lo, v2, v3, v[4:5]
v_mad_i64_i32 v[0:1], vcc_lo, v2, v3, v[4:5]
v_readlane_b32 s0, v1, s2
v_writelane_b32 v0, s1, s2
v_bcnt_u32_b32 v0, v1, v2
v_mbcnt_lo_u32_b32 v0, v1, v2
v_mbcnt_hi_u32_b32 v0, v1, v2
v_cvt_pknorm_i16_f32 v0, v1, v2
v_cvt_pknorm_u16_f32 v0, v1, v2
v_cvt_pk_u16_u32 v0, v1, v2
v_cvt_pk_i16_i32 v0, v1, v2
v_add_f32_e64 v0, -v1, |v2|
v_mul_f32_e64 v0, v1, v2 clamp
v_max_f32_e64 v0, v1, v2 mul:2
v_permlane16_b32 v0, v1, s2, s3
v_permlanex16_b32 v0, v1, s2, s3
