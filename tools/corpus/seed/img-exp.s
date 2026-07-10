// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
// MIMG / EXP / VINTRP — image sampling, exports, interpolation
image_sample v[0:3], v[4:5], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_2D
image_sample_l v[0:3], v[4:6], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_2D
image_sample_lz v[0:3], v[4:5], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_2D
image_sample_b v[0:3], v[4:6], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_2D
image_sample_c v[0:3], v[4:6], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_2D
image_sample_c_lz v[0:3], v[4:6], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_2D
image_sample_d v[0:3], v[4:9], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_2D
image_sample_o v[0:3], v[4:6], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_2D
image_gather4 v[0:3], v[4:5], s[8:15], s[16:19] dmask:0x1 dim:SQ_RSRC_IMG_2D
image_gather4_c v[0:3], v[4:6], s[8:15], s[16:19] dmask:0x1 dim:SQ_RSRC_IMG_2D
image_gather4_lz v[0:3], v[4:5], s[8:15], s[16:19] dmask:0x1 dim:SQ_RSRC_IMG_2D
image_load v[0:3], v[4:5], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
image_load_mip v[0:3], v[4:6], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
image_store v[0:3], v[4:5], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
image_store_mip v[0:3], v[4:6], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
image_get_resinfo v[0:3], v4, s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
image_sample v[0:3], v[4:6], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_3D
image_sample v[0:3], v[4:6], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_CUBE
image_sample v[0:3], v[4:6], s[8:15], s[16:19] dmask:0xf dim:SQ_RSRC_IMG_2D_ARRAY
exp mrt0 v0, v1, v2, v3 done vm
exp mrt0 v0, v1, off, off compr done vm
exp mrt1 v0, v1, v2, v3
exp pos0 v0, v1, v2, v3 done
exp param0 v0, v1, v2, v3
exp param1 v0, v1, v2, v3
v_interp_p1_f32 v0, v1, attr0.x
v_interp_p2_f32 v0, v1, attr0.x
v_interp_mov_f32 v0, p0, attr0.x
v_interp_p1_f32 v0, v1, attr1.y
v_interp_p2_f32 v0, v1, attr1.w
