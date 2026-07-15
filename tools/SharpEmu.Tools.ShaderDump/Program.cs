// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Synthetic-shader conformance dumper.
//
// Feeds hand-assembled Gen5 (gfx10) instruction words through the real
// decode -> SPIR-V pipeline (SharpEmu.ShaderCompiler + SharpEmu.ShaderCompiler.Vulkan)
// and writes the resulting vertex, pixel, and compute SPIR-V blobs to disk. The blobs
// can then be checked with spirv-val / spirv-dis.
//
// Programs that contain buffer memory operations automatically get a single
// global-memory binding covering every load/store, which the emitter exposes as
// guestBuffers[0] (descriptor set 0, binding 0).
//
// Each program carries an expectation: ExpectTranslate=true programs must
// decode and emit the requested stages; ExpectTranslate=false programs pin a decode
// failure that must stay loud. Any unexpected outcome makes the tool exit
// non-zero, so it can gate scripts/CI.
//
// Usage: SharpEmu.Tools.ShaderDump [output-directory]

using System.Buffers.Binary;
using System.Text.Json;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;

const ulong ProgramAddress = 0x100000;

(string Name, bool ExpectTranslate, uint[] Words)[] testPrograms =
[
    ("fmac", true, [
        0x560A0501,             // v_fmac_f32 v5, v1, v2
        0x580A0501, 0x42280000, // v_fmamk_f32 v5, v1, 42.0, v2
        0x5A0A0501, 0x42280000, // v_fmaak_f32 v5, v1, v2, 42.0
        0xD52B0005, 0x00020501, // v_fmac_f32_e64 v5, v1, v2
        0xBF810000,             // s_endpgm
    ]),
    ("muls", true, [
        0xD5690005, 0x00020501, // v_mul_lo_u32 v5, v1, v2
        0xD56A0005, 0x00020501, // v_mul_hi_u32 v5, v1, v2
        0xD56B0005, 0x00020501, // v_mul_lo_i32 v5, v1, v2
        0xD56C0005, 0x00020501, // v_mul_hi_i32 v5, v1, v2
        0xBF810000,             // s_endpgm
    ]),
    ("mrt", true, [
        0x7E0002FF, 0x3F800000, // v_mov_b32 v0, 1.0f
        0x7E0202FF, 0x00000000, // v_mov_b32 v1, 0.0f
        0x7E0402FF, 0x00000000, // v_mov_b32 v2, 0.0f
        0x7E0602FF, 0x3F800000, // v_mov_b32 v3, 1.0f
        0x7E0802FF, 0x00000001, // v_mov_b32 v4, 1u
        0x7E0A02FF, 0x00000002, // v_mov_b32 v5, 2u
        0x7E0C02FF, 0x00000003, // v_mov_b32 v6, 3u
        0x7E0E02FF, 0x00000004, // v_mov_b32 v7, 4u
        0x7E1002FF, 0xFFFFFFFF, // v_mov_b32 v8, -1
        0x7E1202FF, 0x00000002, // v_mov_b32 v9, 2
        0x7E1402FF, 0xFFFFFFFD, // v_mov_b32 v10, -3
        0x7E1602FF, 0x00000004, // v_mov_b32 v11, 4
        0xF800000F, 0x03020100, // exp mrt0 v0, v1, v2, v3
        0xF800003F, 0x07060504, // exp mrt3 v4, v5, v6, v7
        0xF800086F, 0x0B0A0908, // exp mrt6 v8, v9, v10, v11 done
        0xBF810000,             // s_endpgm
    ]),
    ("mrt-float2", true, [
        0x7E0002FF, 0x3F800000, // v_mov_b32 v0, 1.0f
        0x7E0202FF, 0x3E800000, // v_mov_b32 v1, 0.25f
        0x7E0402FF, 0x3E800000, // v_mov_b32 v2, 0.25f
        0x7E0602FF, 0x3F000000, // v_mov_b32 v3, 0.5f
        0xF800000F, 0x03020100, // exp mrt0 v0, v1, v2, v3
        0xF800081F, 0x03020100, // exp mrt1 v0, v1, v2, v3 done
        0xBF810000,             // s_endpgm
    ]),
    ("mrt8", true, [
        0x7E0002FF, 0x3F800000, // v_mov_b32 v0, 1.0f
        0x7E0202FF, 0x00000000, // v_mov_b32 v1, 0.0f
        0x7E0402FF, 0x00000000, // v_mov_b32 v2, 0.0f
        0x7E0602FF, 0x3F800000, // v_mov_b32 v3, 1.0f
        0xF800000F, 0x03020100, // exp mrt0 v0, v1, v2, v3
        0xF800001F, 0x03020100, // exp mrt1 v0, v1, v2, v3
        0xF800002F, 0x03020100, // exp mrt2 v0, v1, v2, v3
        0xF800003F, 0x03020100, // exp mrt3 v0, v1, v2, v3
        0xF800004F, 0x03020100, // exp mrt4 v0, v1, v2, v3
        0xF800005F, 0x03020100, // exp mrt5 v0, v1, v2, v3
        0xF800006F, 0x03020100, // exp mrt6 v0, v1, v2, v3
        0xF800087F, 0x03020100, // exp mrt7 v0, v1, v2, v3 done
        0xBF810000,             // s_endpgm
    ]),
    ("mrt-partial", true, [
        0x7E0002FF, 0x3F4CCCCD, // v_mov_b32 v0, 0.8f
        0x7E0202FF, 0x3F333333, // v_mov_b32 v1, 0.7f
        0xF8000803, 0x03020100, // exp mrt0 v0, v1, off, off done
        0xBF810000,             // s_endpgm
    ]),
    ("mrt-partial-merge", true, [
        0x7E0002FF, 0x3DCCCCCD, // v_mov_b32 v0, 0.1f
        0x7E0202FF, 0x3E4CCCCD, // v_mov_b32 v1, 0.2f
        0x7E0C02FF, 0x3E99999A, // v_mov_b32 v6, 0.3f
        0x7E0E02FF, 0x3ECCCCCD, // v_mov_b32 v7, 0.4f
        0xF8000003, 0x03020100, // exp mrt0 v0, v1, off, off
        0xF800080C, 0x07060504, // exp mrt0 off, off, v6, v7 done
        0xBF810000,             // s_endpgm
    ]),
    ("sopp-hints", true, [
        0xBFA10001,             // s_clause 0x1
        0xBFA30000,             // s_waitcnt_depctr 0x0
        0xBF810000,             // s_endpgm
    ]),
    // s_round_mode / s_denorm_mode write the FP MODE state and must keep
    // failing decode loudly until their semantics are modeled (see #108);
    // this program pins that behavior.
    ("sopp-mode", false, [
        0xBFA40000,             // s_round_mode 0x0
        0xBFA50000,             // s_denorm_mode 0x0
        0xBF810000,             // s_endpgm
    ]),
    // Executable end-to-end test: compute with real ALU instructions, then
    // buffer_store_dword results to guestBuffers[0] at offsets 0/4/8, prove
    // that a store with EXEC=0 does not land (offset 12 stays sentinel), and
    // that stores work again after EXEC is restored (offset 16). Bitwise VOP2
    // results at offsets 20/24/28 extend the driver-executed ALU coverage.
    ("exec", true, [
        0xBFA10001,             // s_clause 0x1 (hint no-op in an executed program, needs #108)
        0x7E0002FF, 0x3FC00000, // v_mov_b32 v0, 1.5f
        0x7E0202FF, 0x40100000, // v_mov_b32 v1, 2.25f
        0x7E0402FF, 0x41200000, // v_mov_b32 v2, 10.0f
        0x56040300,             // v_fmac_f32 v2, v0, v1      -> v2 = fma(1.5, 2.25, 10.0)
        0x7E0602FF, 0x7FFFFFFF, // v_mov_b32 v3, 0x7FFFFFFF
        0x7E0802FF, 0x00010003, // v_mov_b32 v4, 0x00010003
        0xD56C0005, 0x00020903, // v_mul_hi_i32 v5, v3, v4
        0xD56B0006, 0x00020903, // v_mul_lo_i32 v6, v3, v4
        0xE0700000, 0x80020200, // buffer_store_dword v2, off, s[8:11], 0
        0xE0700004, 0x80020500, // buffer_store_dword v5, off, s[8:11], 0 offset:4
        0xE0700008, 0x80020600, // buffer_store_dword v6, off, s[8:11], 0 offset:8
        0xBEFE0380,             // s_mov_b32 exec_lo, 0       -> lane inactive
        0xE070000C, 0x80020200, // buffer_store_dword v2, off, s[8:11], 0 offset:12 (masked, must not land)
        0xBEFE03C1,             // s_mov_b32 exec_lo, -1      -> lane active again
        0xE0700010, 0x80020000, // buffer_store_dword v0, off, s[8:11], 0 offset:16
        0x360E0903,             // v_and_b32 v7, v3, v4
        0x38100903,             // v_or_b32 v8, v3, v4
        0x3A120903,             // v_xor_b32 v9, v3, v4
        0xE0700014, 0x80020700, // buffer_store_dword v7, off, s[8:11], 0 offset:20
        0xE0700018, 0x80020800, // buffer_store_dword v8, off, s[8:11], 0 offset:24
        0xE070001C, 0x80020900, // buffer_store_dword v9, off, s[8:11], 0 offset:28
        0xBF810000,             // s_endpgm
    ]),
    // A second independently manifested executable program proves that the
    // corpus runner discovers every case. It also covers the integer shift
    // direction/sign rules and the non-carrying add/subtract VOP3 encodings.
    ("exec-shifts", true, [
        0x7E0202FF, 0x00000004, // v_mov_b32 v1, 4 (shift count/addend)
        0x7E0402FF, 0x80000010, // v_mov_b32 v2, 0x80000010
        0x2C0A0501,             // v_lshrrev_b32 v5, v1, v2
        0x300C0501,             // v_ashrrev_i32 v6, v1, v2
        0x340E0501,             // v_lshlrev_b32 v7, v1, v2
        0xD77F0008, 0x00020302, // v_add_nc_i32 v8, v2, v1
        0xD7760009, 0x00020302, // v_sub_nc_i32 v9, v2, v1
        0xE0700000, 0x80020500, // buffer_store_dword v5, off, s[8:11], 0
        0xE0700004, 0x80020600, // buffer_store_dword v6, off, s[8:11], 0 offset:4
        0xE0700008, 0x80020700, // buffer_store_dword v7, off, s[8:11], 0 offset:8
        0xE070000C, 0x80020800, // buffer_store_dword v8, off, s[8:11], 0 offset:12
        0xE0700010, 0x80020900, // buffer_store_dword v9, off, s[8:11], 0 offset:16
        0xBF810000,             // s_endpgm
    ]),
    // Uniform scalar control flow is lowered into the translator's structured
    // program-counter loop. Exercise both outcomes of s_cbranch_scc1 plus an
    // unconditional branch and observe exactly which stores reach the buffer.
    ("exec-control-flow", true, [
        0x7E0002FF, 0x11111111, // v_mov_b32 v0, 0x11111111
        0x7E0202FF, 0x22222222, // v_mov_b32 v1, 0x22222222
        0xBF068000,             // s_cmp_eq_u32 s0, 0 -> SCC=1 (s0 starts at zero)
        0xBF850002,             // s_cbranch_scc1 +2 -> skip next two-dword store
        0xE0700000, 0x80020000, // buffer_store_dword v0, off, s[8:11], 0 (skipped)
        0xE0700004, 0x80020100, // buffer_store_dword v1, off, s[8:11], 0 offset:4
        0xBF078000,             // s_cmp_lg_u32 s0, 0 -> SCC=0
        0xBF850002,             // s_cbranch_scc1 +2 -> not taken
        0xE0700008, 0x80020000, // buffer_store_dword v0, off, s[8:11], 0 offset:8
        0xE070000C, 0x80020100, // buffer_store_dword v1, off, s[8:11], 0 offset:12
        0xBF820002,             // s_branch +2 -> skip next two-dword store
        0xE0700010, 0x80020000, // buffer_store_dword v0, off, s[8:11], 0 offset:16 (skipped)
        0xE0700014, 0x80020100, // buffer_store_dword v1, off, s[8:11], 0 offset:20
        0xBF810000,             // s_endpgm
    ]),
    // Read initialized descriptor memory, feed it through an ALU operation,
    // and copy a two-dword vector load elsewhere in the same buffer.
    ("exec-buffer-load", true, [
        0x7E0202FF, 0x00000004, // v_mov_b32 v1, 4
        0xE0300000, 0x80020200, // buffer_load_dword v2, off, s[8:11], 0
        0xD77F0003, 0x00020302, // v_add_nc_i32 v3, v2, v1
        0xE0700004, 0x80020300, // buffer_store_dword v3, off, s[8:11], 0 offset:4
        0xE0340008, 0x80020400, // buffer_load_dwordx2 v[4:5], off, s[8:11], 0 offset:8
        0xE0700010, 0x80020400, // buffer_store_dword v4, off, s[8:11], 0 offset:16
        0xE0700014, 0x80020500, // buffer_store_dword v5, off, s[8:11], 0 offset:20
        0xBF810000,             // s_endpgm
    ]),
    // Load through two scalar resource descriptors, then cross-copy the
    // values. Correct results require distinct descriptor-array elements and
    // correct instruction-PC-to-resource routing in the compiler.
    ("exec-buffer-bindings", true, [
        0xE0300000, 0x80020400, // buffer_load_dword v4, off, s[8:11], 0
        0xE0300000, 0x80030500, // buffer_load_dword v5, off, s[12:15], 0
        0xE0700004, 0x80030400, // buffer_store_dword v4, off, s[12:15], 0 offset:4
        0xE0700004, 0x80020500, // buffer_store_dword v5, off, s[8:11], 0 offset:4
        0xBF810000,             // s_endpgm
    ]),
    // A finite backward branch exercises the structured program-counter loop,
    // dynamic SCC updates, and scalar-to-vector transfer. The dispatch runner
    // has a fence deadline, so a broken termination condition fails boundedly.
    ("exec-scalar-loop", true, [
        0xBE800380,             // s_mov_b32 s0, 0
        0x80008100,             // s_add_u32 s0, s0, 1
        0xBF0A8400,             // s_cmp_lt_u32 s0, 4
        0xBF85FFFD,             // s_cbranch_scc1 -3 -> s_add_u32
        0x7E000200,             // v_mov_b32 v0, s0
        0xE0700000, 0x80020000, // buffer_store_dword v0, off, s[8:11], 0
        0xBF810000,             // s_endpgm
    ]),
    // Exercise sub-dword signedness and accesses that straddle two dwords.
    // Loaded values are widened to dwords and copied to non-overlapping oracle
    // slots before byte and short stores modify the trailing sentinel words.
    ("exec-subword-memory", true, [
        0xE0200000, 0x80020200, // buffer_load_ubyte v2, off, s[8:11], 0 offset:0
        0xE0240002, 0x80020300, // buffer_load_sbyte v3, off, s[8:11], 0 offset:2
        0xE0280003, 0x80020400, // buffer_load_ushort v4, off, s[8:11], 0 offset:3
        0xE02C0003, 0x80020500, // buffer_load_sshort v5, off, s[8:11], 0 offset:3
        0xE0700008, 0x80020200, // buffer_store_dword v2, off, s[8:11], 0 offset:8
        0xE070000C, 0x80020300, // buffer_store_dword v3, off, s[8:11], 0 offset:12
        0xE0700010, 0x80020400, // buffer_store_dword v4, off, s[8:11], 0 offset:16
        0xE0700014, 0x80020500, // buffer_store_dword v5, off, s[8:11], 0 offset:20
        0x7E0C02FF, 0xA1B2C3D4, // v_mov_b32 v6, 0xA1B2C3D4
        0xE0600018, 0x80020600, // buffer_store_byte v6, off, s[8:11], 0 offset:24
        0xE068001B, 0x80020600, // buffer_store_short v6, off, s[8:11], 0 offset:27
        0xBF810000,             // s_endpgm
    ]),
    // Two workgroups of four invocations synthesize a global X index from the
    // local invocation ID (v0), workgroup X (s0), and threadgroup size (s1).
    // An offen store lets every invocation write its own deterministic slot.
    ("exec-dispatch-topology", true, [
        0x7E080200,             // v_mov_b32 v4, s0 (workgroup X)
        0x7E0A0201,             // v_mov_b32 v5, s1 (threadgroup size)
        0xD5690006, 0x00020B04, // v_mul_lo_u32 v6, v4, v5
        0xD77F0007, 0x00020D00, // v_add_nc_i32 v7, v0, v6
        0x34100E82,             // v_lshlrev_b32 v8, 2, v7
        0xE0701000, 0x80020708, // buffer_store_dword v7, v8, s[8:11], 0 offen
        0xBF810000,             // s_endpgm
    ]),
    // A 2x2 local size dispatched over a 2x2 workgroup grid combines local
    // invocation X/Y (v0/v1) with workgroup X/Y (s0/s1) into a 4x4 index.
    ("exec-dispatch-2d", true, [
        0x7E080200,             // v_mov_b32 v4, s0 (workgroup X)
        0x7E0A0201,             // v_mov_b32 v5, s1 (workgroup Y)
        0x340E0881,             // v_lshlrev_b32 v7, 1, v4
        0xD77F0007, 0x00020107, // v_add_nc_i32 v7, v7, v0 (global X)
        0x34100A81,             // v_lshlrev_b32 v8, 1, v5
        0xD77F0008, 0x00020308, // v_add_nc_i32 v8, v8, v1 (global Y)
        0x34121082,             // v_lshlrev_b32 v9, 2, v8
        0xD77F0009, 0x00020F09, // v_add_nc_i32 v9, v9, v7 (linear index)
        0x34141282,             // v_lshlrev_b32 v10, 2, v9 (byte address)
        0xE0701000, 0x8002090A, // buffer_store_dword v9, v10, s[8:11], 0 offen
        0xBF810000,             // s_endpgm
    ]),
    // A 2x2x2 local size dispatched over two workgroups in Z combines local
    // invocation X/Y/Z (v0/v1/v2) with workgroup Z (s0) into a 2x2x4 index.
    ("exec-dispatch-3d", true, [
        0x7E080200,             // v_mov_b32 v4, s0 (workgroup Z)
        0x340A0881,             // v_lshlrev_b32 v5, 1, v4
        0xD77F0005, 0x00020505, // v_add_nc_i32 v5, v5, v2 (global Z)
        0x340C0A82,             // v_lshlrev_b32 v6, 2, v5
        0x340E0281,             // v_lshlrev_b32 v7, 1, v1
        0xD77F0008, 0x00020F06, // v_add_nc_i32 v8, v6, v7
        0xD77F0008, 0x00020108, // v_add_nc_i32 v8, v8, v0 (linear index)
        0x34121082,             // v_lshlrev_b32 v9, 2, v8 (byte address)
        0xE0701000, 0x80020809, // buffer_store_dword v8, v9, s[8:11], 0 offen
        0xBF810000,             // s_endpgm
    ]),
    // Sixteen lanes compare their local invocation index against eight. The
    // cmpx instruction narrows EXEC, so only the lower half may store.
    ("exec-lane-mask", true, [
        0x7E020288,             // v_mov_b32 v1, 8
        0x7DA20300,             // v_cmpx_lt_u32 v0, v1
        0x34040082,             // v_lshlrev_b32 v2, 2, v0 (byte address)
        0xE0701000, 0x80020002, // buffer_store_dword v0, v2, s[8:11], 0 offen
        0xBF810000,             // s_endpgm
    ]),
    // Save the full EXEC mask, run the lower-half path, restore all lanes,
    // then select the upper half. A broken restore leaves slots 8..15 untouched.
    ("exec-reconverge", true, [
        0xBE80047E,             // s_mov_b64 s[0:1], exec
        0x7E020288,             // v_mov_b32 v1, 8
        0x7DA20300,             // v_cmpx_lt_u32 v0, v1
        0x34040082,             // v_lshlrev_b32 v2, 2, v0 (byte address)
        0xE0701000, 0x80020002, // buffer_store_dword v0, v2, s[8:11], 0 offen
        0xBEFE0400,             // s_mov_b64 exec, s[0:1]
        0x7DAC0300,             // v_cmpx_ge_u32 v0, v1
        0x34040082,             // v_lshlrev_b32 v2, 2, v0 (byte address)
        0xE0701000, 0x80020002, // buffer_store_dword v0, v2, s[8:11], 0 offen
        0xBF810000,             // s_endpgm
    ]),
    // Exercise the conventional VCC + saveexec divergence sequence rather
    // than writing EXEC directly with cmpx. Each half must execute in turn.
    ("exec-saveexec", true, [
        0x7E020288,             // v_mov_b32 v1, 8
        0x7D820300,             // v_cmp_lt_u32 vcc, v0, v1
        0xBE80246A,             // s_and_saveexec_b64 s[0:1], vcc
        0x34040082,             // v_lshlrev_b32 v2, 2, v0 (byte address)
        0xE0701000, 0x80020002, // buffer_store_dword v0, v2, s[8:11], 0 offen
        0xBEFE0400,             // s_mov_b64 exec, s[0:1]
        0x7D8C0300,             // v_cmp_ge_u32 vcc, v0, v1
        0xBE82246A,             // s_and_saveexec_b64 s[2:3], vcc
        0x34040082,             // v_lshlrev_b32 v2, 2, v0 (byte address)
        0xE0701000, 0x80020002, // buffer_store_dword v0, v2, s[8:11], 0 offen
        0xBF810000,             // s_endpgm
    ]),
    // Sixteen invocations contend on one storage-buffer counter. A plain
    // load/add/store would race; the deterministic final value proves that
    // buffer_atomic_add is emitted and synchronized as an actual atomic.
    ("exec-buffer-atomic", true, [
        0x7E0802FF, 0x00000001, // v_mov_b32 v4, 1
        0xE0C80000, 0x80020400, // buffer_atomic_add v4, off, s[8:11], 0
        0xBF810000,             // s_endpgm
    ]),
    // GLC requests the pre-operation value in VDATA. Sequential add/sub
    // operations exercise both the returned values and the final counter.
    ("exec-buffer-atomic-return", true, [
        0x7E0802FF, 0x00000005, // v_mov_b32 v4, 5
        0xE0C84000, 0x80020400, // buffer_atomic_add v4, off, s[8:11], 0 glc
        0xE0700004, 0x80020400, // buffer_store_dword v4, off, s[8:11], 0 offset:4
        0x7E0A02FF, 0x00000003, // v_mov_b32 v5, 3
        0xE0CC4000, 0x80020500, // buffer_atomic_sub v5, off, s[8:11], 0 glc
        0xE0700008, 0x80020500, // buffer_store_dword v5, off, s[8:11], 0 offset:8
        0xBF810000,             // s_endpgm
    ]),
    // A successful compare/swap updates memory and returns the old value. A
    // second operation with a stale comparator must fail while still returning
    // the value that prevented the exchange.
    ("exec-buffer-cmpswap", true, [
        0x7E0802FF, 0x00000063, // v_mov_b32 v4, 99 (desired value)
        0x7E0A02FF, 0x0000000A, // v_mov_b32 v5, 10 (matching comparator)
        0xE0C44000, 0x80020400, // buffer_atomic_cmpswap v[4:5], off, s[8:11], 0 glc
        0xE0700004, 0x80020400, // buffer_store_dword v4, off, s[8:11], 0 offset:4
        0x7E0C02FF, 0x0000004D, // v_mov_b32 v6, 77 (rejected desired value)
        0x7E0E02FF, 0x0000000A, // v_mov_b32 v7, 10 (stale comparator)
        0xE0C44000, 0x80020600, // buffer_atomic_cmpswap v[6:7], off, s[8:11], 0 glc
        0xE0700008, 0x80020600, // buffer_store_dword v6, off, s[8:11], 0 offset:8
        0xBF810000,             // s_endpgm
    ]),
    // INC wraps at its operand and DEC wraps at zero. Together these four
    // operations exercise both branches of each compare-exchange loop while
    // GLC exposes the value observed by every successful exchange.
    ("exec-buffer-inc-dec", true, [
        0x7E0802FF, 0x00000005, // v_mov_b32 v4, 5 (INC limit)
        0xE0F04000, 0x80020400, // buffer_atomic_inc v4, off, s[8:11], 0 glc
        0xE0700004, 0x80020400, // buffer_store_dword v4, off, s[8:11], 0 offset:4
        0x7E0A02FF, 0x00000003, // v_mov_b32 v5, 3 (INC limit)
        0xE0F04000, 0x80020500, // buffer_atomic_inc v5, off, s[8:11], 0 glc
        0xE0700008, 0x80020500, // buffer_store_dword v5, off, s[8:11], 0 offset:8
        0x7E0C02FF, 0x00000003, // v_mov_b32 v6, 3 (DEC limit)
        0xE0F44000, 0x80020600, // buffer_atomic_dec v6, off, s[8:11], 0 glc
        0xE070000C, 0x80020600, // buffer_store_dword v6, off, s[8:11], 0 offset:12
        0x7E0E02FF, 0x00000003, // v_mov_b32 v7, 3 (DEC limit)
        0xE0F44000, 0x80020700, // buffer_atomic_dec v7, off, s[8:11], 0 glc
        0xE0700010, 0x80020700, // buffer_store_dword v7, off, s[8:11], 0 offset:16
        0xBF810000,             // s_endpgm
    ]),
    // Four invocations publish their local IDs to distinct LDS slots, meet at
    // a workgroup barrier, then read the slot owned by the opposite lane.
    ("exec-lds-barrier", true, [
        0x34080082,             // v_lshlrev_b32 v4, 2, v0 (own byte address)
        0xD8340000, 0x00000004, // ds_write_b32 v4, v0
        0xBF8A0000,             // s_barrier
        0x7E0A02FF, 0x00000003, // v_mov_b32 v5, 3
        0xD7760006, 0x00020105, // v_sub_nc_i32 v6, v5, v0 (opposite lane)
        0x340E0C82,             // v_lshlrev_b32 v7, 2, v6 (read byte address)
        0xD8D80000, 0x08000007, // ds_read_b32 v8, v7
        0xE0701000, 0x80020804, // buffer_store_dword v8, v4, s[8:11], 0 offen
        0xBF810000,             // s_endpgm
    ]),
    // Eight invocations atomically initialize and increment one LDS counter.
    // A second barrier makes the final total visible before every lane reads it.
    ("exec-lds-atomic", true, [
        0x7E0802FF, 0x00000000, // v_mov_b32 v4, 0 (LDS byte address)
        0x7E0A02FF, 0x00000000, // v_mov_b32 v5, 0
        0xD8B40000, 0x06000504, // ds_wrxchg_rtn_b32 v6, v4, v5
        0xBF8A0000,             // s_barrier
        0x7E0A02FF, 0x00000001, // v_mov_b32 v5, 1
        0xD8000000, 0x00000504, // ds_add_u32 v4, v5
        0xBF8A0000,             // s_barrier
        0xD8D80000, 0x06000004, // ds_read_b32 v6, v4
        0x340E0082,             // v_lshlrev_b32 v7, 2, v0 (own output address)
        0xE0701000, 0x80020607, // buffer_store_dword v6, v7, s[8:11], 0 offen
        0xBF810000,             // s_endpgm
    ]),
];

// LDS and workgroup barriers are compute-stage concepts. Keep these programs
// in the common decode/compute pipeline without asking the vertex emitter to
// accept operations that are invalid for that execution model.
HashSet<string> computeOnlyPrograms = new(StringComparer.Ordinal)
{
    "exec-lds-atomic",
    "exec-lds-barrier",
};
var outputDirectory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "spv");
Directory.CreateDirectory(outputDirectory);

var failures = 0;
foreach (var (name, expectTranslate, words) in testPrograms)
{
    var memory = new FakeMemory();
    memory.AddRegion(ProgramAddress, words);
    var ctx = new CpuContext(memory, Generation.Gen5);

    Console.WriteLine(
        $"[{name}] decode: " +
        Gen5ShaderTranslator.Describe(ctx, ProgramAddress, ProgramAddress));

    if (!Gen5ShaderTranslator.TryDecodeProgram(ctx, ProgramAddress, out var program, out var decodeError))
    {
        if (expectTranslate)
        {
            failures++;
            Console.WriteLine($"[{name}] FAILED: decode error ({decodeError})");
        }
        else
        {
            Console.WriteLine($"[{name}] decode failed as expected ({decodeError})");
        }

        continue;
    }

    if (!expectTranslate)
    {
        failures++;
        Console.WriteLine(
            $"[{name}] FAILED: decoded successfully but is pinned as a decode failure — " +
            "if the new decode support is intentional, its semantics need verifying here first");
        continue;
    }

    // Buffer memory operations need global-memory bindings. The emitter
    // resolves each one by instruction PC, while ScalarResource identifies
    // the guest descriptor whose PC group becomes one descriptor-array slot.
    var globalBindings = program!.Instructions
        .Where(instruction =>
            instruction.Control is Gen5BufferMemoryControl &&
            (instruction.Opcode.StartsWith("Buffer", StringComparison.Ordinal) ||
             instruction.Opcode.StartsWith("TBuffer", StringComparison.Ordinal)))
        .GroupBy(instruction =>
            ((Gen5BufferMemoryControl)instruction.Control!).ScalarResource)
        .OrderBy(group => group.Key)
        .Select(group => new Gen5GlobalMemoryBinding(
            group.Key,
            0UL,
            group.Select(instruction => instruction.Pc).ToArray(),
            new byte[64]))
        .ToArray();

    var conformanceCase = CreateConformanceCase(name);
    Gen5ComputeSystemRegisters? computeSystemRegisters = conformanceCase is { } computeCase &&
        (computeCase.WorkGroupXRegister is not null ||
         computeCase.WorkGroupYRegister is not null ||
         computeCase.WorkGroupZRegister is not null ||
         computeCase.ThreadGroupSizeRegister is not null)
        ? new Gen5ComputeSystemRegisters(
            computeCase.WorkGroupXRegister,
            computeCase.WorkGroupYRegister,
            computeCase.WorkGroupZRegister,
            computeCase.ThreadGroupSizeRegister)
        : null;

    var state = new Gen5ShaderState(
        program,
        new uint[16],
        Metadata: null,
        ComputeSystemRegisters: computeSystemRegisters);
    var evaluation = new Gen5ShaderEvaluation(
        new uint[256],
        new uint[256],
        new Dictionary<uint, IReadOnlyList<uint>>(),
        Array.Empty<Gen5ImageBinding>(),
        globalBindings,
        ComputeSystemRegisters: computeSystemRegisters);

    if (!computeOnlyPrograms.Contains(name))
    {
        if (Gen5SpirvTranslator.TryCompileVertexShader(
                state,
                evaluation,
                out var vertexShader,
                out var vertexError))
        {
            var path = Path.Combine(outputDirectory, $"{name}.spv");
            File.WriteAllBytes(path, vertexShader.Spirv);
            Console.WriteLine($"[{name}] emit: success, {vertexShader.Spirv.Length} bytes -> {path}");
        }
        else
        {
            failures++;
            Console.WriteLine($"[{name}] emit: FAILED ({vertexError})");
        }
    }
    else
    {
        Console.WriteLine($"[{name}] vertex emit: skipped (compute-only program)");
    }

    if (Gen5SpirvTranslator.TryCompileComputeShader(
            state,
            evaluation,
            conformanceCase?.LocalSizeX ?? 1u,
            conformanceCase?.LocalSizeY ?? 1u,
            conformanceCase?.LocalSizeZ ?? 1u,
            out var computeShader,
            out var computeError))
    {
        var path = Path.Combine(outputDirectory, $"{name}-cs.spv");
        File.WriteAllBytes(path, computeShader.Spirv);
        Console.WriteLine($"[{name}] compute emit: success, {computeShader.Spirv.Length} bytes -> {path}");

        if (conformanceCase is not null)
        {
            var manifestPath = Path.Combine(outputDirectory, $"{name}-cs.conformance.json");
            var conformanceBuffers = new List<SyntheticConformanceBuffer>
            {
                new(
                    "guest buffer 0",
                    conformanceCase.InitialWords,
                    conformanceCase.ExpectedWords,
                    conformanceCase.Labels),
            };
            if (conformanceCase.AdditionalBuffers is not null)
            {
                conformanceBuffers.AddRange(conformanceCase.AdditionalBuffers);
            }

            var manifest = new
            {
                SchemaVersion = 3,
                Name = name,
                Shader = Path.GetFileName(path),
                Buffers = conformanceBuffers,
                conformanceCase.LocalSizeX,
                conformanceCase.LocalSizeY,
                conformanceCase.LocalSizeZ,
                conformanceCase.GroupCountX,
                conformanceCase.GroupCountY,
                conformanceCase.GroupCountZ,
            };
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(
                    manifest,
                    new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            Console.WriteLine($"[{name}] conformance manifest -> {manifestPath}");
        }
    }
    else
    {
        failures++;
        Console.WriteLine($"[{name}] compute emit: FAILED ({computeError})");
    }

    if (name.StartsWith("mrt", StringComparison.Ordinal))
    {
        Gen5PixelOutputBinding[] pixelOutputs = name switch
        {
            "mrt" =>
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(3, 1, Gen5PixelOutputKind.Uint),
                new Gen5PixelOutputBinding(6, 2, Gen5PixelOutputKind.Sint),
            ],
            "mrt-float2" =>
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(1, 1, Gen5PixelOutputKind.Float),
            ],
            "mrt8" =>
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(1, 1, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(2, 2, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(3, 3, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(4, 4, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(5, 5, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(6, 6, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(7, 7, Gen5PixelOutputKind.Float),
            ],
            _ => [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
        };

        if (Gen5SpirvTranslator.TryCompilePixelShader(state, evaluation, pixelOutputs, out var pixelShader, out var pixelError))
        {
            var path = Path.Combine(outputDirectory, $"{name}-ps.spv");
            File.WriteAllBytes(path, pixelShader.Spirv);
            Console.WriteLine($"[{name}] pixel emit: success, {pixelShader.Spirv.Length} bytes -> {path}");
        }
        else
        {
            failures++;
            Console.WriteLine($"[{name}] pixel emit: FAILED ({pixelError})");
        }

        if (name == "mrt")
        {
            Gen5PixelOutputBinding[] invalidOutputs =
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(3, 7, Gen5PixelOutputKind.Float),
            ];
            if (Gen5SpirvTranslator.TryCompilePixelShader(state, evaluation, invalidOutputs, out _, out var invalidError))
            {
                failures++;
                Console.WriteLine("[mrt] FAILED: sparse host locations were accepted");
            }
            else
            {
                Console.WriteLine($"[mrt] sparse host locations rejected as expected ({invalidError})");
            }
        }
    }
}

Console.WriteLine(failures == 0
    ? "RESULT: all programs behaved as expected"
    : $"RESULT: {failures} unexpected outcome(s)");
Environment.ExitCode = failures == 0 ? 0 : 1;

static SyntheticConformanceCase? CreateConformanceCase(string name)
{
    const uint sentinel = 0xCAFEBABE;
    var initialWords = Enumerable.Repeat(sentinel, 16).ToArray();
    var expectedWords = (uint[])initialWords.Clone();
    var labels = Enumerable.Range(0, initialWords.Length)
        .Select(index => $"trailing word [{index}] remains sentinel")
        .ToArray();

    switch (name)
    {
        case "exec":
            expectedWords[0] = 0x41560000; // fma(1.5f, 2.25f, 10.0f)
            expectedWords[1] = 0x00008001; // high signed product word
            expectedWords[2] = 0x7FFEFFFD; // low signed product word
            expectedWords[3] = sentinel;   // EXEC=0 suppresses the store
            expectedWords[4] = 0x3FC00000; // store after EXEC restoration
            expectedWords[5] = 0x00010003; // v_and_b32
            expectedWords[6] = 0x7FFFFFFF; // v_or_b32
            expectedWords[7] = 0x7FFEFFFC; // v_xor_b32
            labels[0] = "v_fmac_f32 fma(1.5, 2.25, 10.0)";
            labels[1] = "v_mul_hi_i32 hi(0x7FFFFFFF*0x10003)";
            labels[2] = "v_mul_lo_i32 lo(0x7FFFFFFF*0x10003)";
            labels[3] = "exec=0 store suppressed (offset 12 sentinel)";
            labels[4] = "store after exec restore (offset 16)";
            labels[5] = "v_and_b32 0x7FFFFFFF & 0x00010003";
            labels[6] = "v_or_b32 0x7FFFFFFF | 0x00010003";
            labels[7] = "v_xor_b32 0x7FFFFFFF ^ 0x00010003";
            break;
        case "exec-shifts":
            expectedWords[0] = 0x08000001; // logical right shift
            expectedWords[1] = 0xF8000001; // arithmetic right shift
            expectedWords[2] = 0x00000100; // left shift, truncating to 32 bits
            expectedWords[3] = 0x80000014; // non-carrying add
            expectedWords[4] = 0x8000000C; // non-carrying subtract
            labels[0] = "v_lshrrev_b32 0x80000010 >> 4";
            labels[1] = "v_ashrrev_i32 (int)0x80000010 >> 4";
            labels[2] = "v_lshlrev_b32 0x80000010 << 4";
            labels[3] = "v_add_nc_i32 0x80000010 + 4";
            labels[4] = "v_sub_nc_i32 0x80000010 - 4";
            break;
        case "exec-control-flow":
            expectedWords[0] = sentinel;
            expectedWords[1] = 0x22222222;
            expectedWords[2] = 0x11111111;
            expectedWords[3] = 0x22222222;
            expectedWords[4] = sentinel;
            expectedWords[5] = 0x22222222;
            labels[0] = "taken s_cbranch_scc1 suppresses skipped store";
            labels[1] = "execution resumes at taken conditional target";
            labels[2] = "not-taken s_cbranch_scc1 executes fallthrough store";
            labels[3] = "execution continues after conditional fallthrough";
            labels[4] = "s_branch suppresses skipped store";
            labels[5] = "execution resumes at unconditional target";
            break;
        case "exec-buffer-load":
            initialWords[0] = 0x7FFFFFFE;
            initialWords[2] = 0x01234567;
            initialWords[3] = 0x89ABCDEF;
            expectedWords[0] = initialWords[0];
            expectedWords[1] = 0x80000002;
            expectedWords[2] = initialWords[2];
            expectedWords[3] = initialWords[3];
            expectedWords[4] = initialWords[2];
            expectedWords[5] = initialWords[3];
            labels[0] = "buffer_load_dword source remains unchanged";
            labels[1] = "loaded dword plus 4 wraps across signed boundary";
            labels[2] = "buffer_load_dwordx2 low source remains unchanged";
            labels[3] = "buffer_load_dwordx2 high source remains unchanged";
            labels[4] = "buffer_load_dwordx2 low result copied to offset 16";
            labels[5] = "buffer_load_dwordx2 high result copied to offset 20";
            break;
        case "exec-buffer-bindings":
        {
            const uint firstValue = 0x11111111;
            const uint secondValue = 0x22222222;
            initialWords[0] = firstValue;
            expectedWords[0] = firstValue;
            expectedWords[1] = secondValue;
            labels[0] = "descriptor 0 source remains unchanged";
            labels[1] = "descriptor 0 receives the value loaded through descriptor 1";

            var secondInitialWords = Enumerable.Repeat(sentinel, 16).ToArray();
            var secondExpectedWords = (uint[])secondInitialWords.Clone();
            var secondLabels = Enumerable.Range(0, secondInitialWords.Length)
                .Select(index => $"descriptor 1 trailing word [{index}] remains sentinel")
                .ToArray();
            secondInitialWords[0] = secondValue;
            secondExpectedWords[0] = secondValue;
            secondExpectedWords[1] = firstValue;
            secondLabels[0] = "descriptor 1 source remains unchanged";
            secondLabels[1] = "descriptor 1 receives the value loaded through descriptor 0";
            return new SyntheticConformanceCase(
                initialWords,
                expectedWords,
                labels,
                AdditionalBuffers:
                [
                    new SyntheticConformanceBuffer(
                        "guest buffer 1",
                        secondInitialWords,
                        secondExpectedWords,
                        secondLabels),
                ]);
        }
        case "exec-scalar-loop":
            expectedWords[0] = 4;
            labels[0] = "backward scalar loop terminates after four iterations";
            break;
        case "exec-subword-memory":
            initialWords[0] = 0x34FF7F01;
            initialWords[1] = 0xCAFEBAF2;
            expectedWords[0] = initialWords[0];
            expectedWords[1] = initialWords[1];
            expectedWords[2] = 0x00000001;
            expectedWords[3] = 0xFFFFFFFF;
            expectedWords[4] = 0x0000F234;
            expectedWords[5] = 0xFFFFF234;
            expectedWords[6] = 0xD4FEBAD4;
            expectedWords[7] = 0xCAFEBAC3;
            labels[0] = "ubyte and sbyte source word remains unchanged";
            labels[1] = "cross-dword halfword source word remains unchanged";
            labels[2] = "buffer_load_ubyte zero-extends 0x01";
            labels[3] = "buffer_load_sbyte sign-extends 0xFF";
            labels[4] = "buffer_load_ushort crosses a dword boundary";
            labels[5] = "buffer_load_sshort sign-extends across a dword boundary";
            labels[6] = "byte store and low byte of crossing short store";
            labels[7] = "high byte of crossing short store";
            break;
        case "exec-dispatch-topology":
            for (uint index = 0; index < 8; index++)
            {
                expectedWords[index] = index;
                labels[index] = $"global invocation X {index} writes its indexed slot";
            }

            return new SyntheticConformanceCase(
                initialWords,
                expectedWords,
                labels,
                LocalSizeX: 4,
                GroupCountX: 2,
                WorkGroupXRegister: 0,
                ThreadGroupSizeRegister: 1);
        case "exec-dispatch-2d":
            for (uint index = 0; index < 16; index++)
            {
                expectedWords[index] = index;
                labels[index] = $"2D global invocation {index % 4},{index / 4} writes slot {index}";
            }

            return new SyntheticConformanceCase(
                initialWords,
                expectedWords,
                labels,
                LocalSizeX: 2,
                LocalSizeY: 2,
                GroupCountX: 2,
                GroupCountY: 2,
                WorkGroupXRegister: 0,
                WorkGroupYRegister: 1);
        case "exec-dispatch-3d":
            for (uint index = 0; index < 16; index++)
            {
                expectedWords[index] = index;
                labels[index] = $"3D global invocation {index % 2},{index / 2 % 2},{index / 4} writes slot {index}";
            }

            return new SyntheticConformanceCase(
                initialWords,
                expectedWords,
                labels,
                LocalSizeX: 2,
                LocalSizeY: 2,
                LocalSizeZ: 2,
                GroupCountZ: 2,
                WorkGroupZRegister: 0);
        case "exec-lane-mask":
            for (uint index = 0; index < 8; index++)
            {
                expectedWords[index] = index;
                labels[index] = $"active lane {index} writes its indexed slot";
            }

            for (var index = 8; index < 16; index++)
            {
                labels[index] = $"inactive lane {index} leaves its slot untouched";
            }

            return new SyntheticConformanceCase(
                initialWords,
                expectedWords,
                labels,
                LocalSizeX: 16);
        case "exec-reconverge":
            for (uint index = 0; index < 16; index++)
            {
                expectedWords[index] = index;
                labels[index] = index < 8
                    ? $"lower-path lane {index} writes before EXEC restoration"
                    : $"upper-path lane {index} writes after EXEC restoration";
            }

            return new SyntheticConformanceCase(
                initialWords,
                expectedWords,
                labels,
                LocalSizeX: 16);
        case "exec-saveexec":
            for (uint index = 0; index < 16; index++)
            {
                expectedWords[index] = index;
                labels[index] = index < 8
                    ? $"VCC/saveexec lower-path lane {index} writes"
                    : $"VCC/saveexec upper-path lane {index} writes after restore";
            }

            return new SyntheticConformanceCase(
                initialWords,
                expectedWords,
                labels,
                LocalSizeX: 16);
        case "exec-buffer-atomic":
            initialWords[0] = 0;
            expectedWords[0] = 16;
            labels[0] = "sixteen concurrent buffer_atomic_add operations accumulate";
            return new SyntheticConformanceCase(
                initialWords,
                expectedWords,
                labels,
                LocalSizeX: 8,
                GroupCountX: 2);
        case "exec-buffer-atomic-return":
            initialWords[0] = 10;
            expectedWords[0] = 12;
            expectedWords[1] = 10;
            expectedWords[2] = 15;
            labels[0] = "atomic add followed by atomic sub updates the counter";
            labels[1] = "GLC atomic add returns its pre-operation value";
            labels[2] = "GLC atomic sub returns its pre-operation value";
            break;
        case "exec-buffer-cmpswap":
            initialWords[0] = 10;
            expectedWords[0] = 99;
            expectedWords[1] = 10;
            expectedWords[2] = 99;
            labels[0] = "matching compare/swap updates the target value";
            labels[1] = "successful GLC compare/swap returns the old value";
            labels[2] = "failed GLC compare/swap returns the mismatched value";
            break;
        case "exec-buffer-inc-dec":
            initialWords[0] = 5;
            expectedWords[0] = 3;
            expectedWords[1] = 5;
            expectedWords[2] = 0;
            expectedWords[3] = 1;
            expectedWords[4] = 0;
            labels[0] = "INC and DEC boundary sequence ends at its DEC limit";
            labels[1] = "INC at its limit returns the limit before wrapping to zero";
            labels[2] = "INC below its limit returns zero before incrementing";
            labels[3] = "DEC inside its range returns one before decrementing";
            labels[4] = "DEC at zero returns zero before wrapping to its limit";
            break;
        case "exec-lds-barrier":
            for (uint index = 0; index < 4; index++)
            {
                expectedWords[index] = 3 - index;
                labels[index] = $"lane {index} reads lane {3 - index} through LDS";
            }

            return new SyntheticConformanceCase(
                initialWords,
                expectedWords,
                labels,
                LocalSizeX: 4);
        case "exec-lds-atomic":
            for (var index = 0; index < 8; index++)
            {
                expectedWords[index] = 8;
                labels[index] = $"lane {index} observes eight atomic LDS increments";
            }

            return new SyntheticConformanceCase(
                initialWords,
                expectedWords,
                labels,
                LocalSizeX: 8);
        default:
            return null;
    }

    return new SyntheticConformanceCase(initialWords, expectedWords, labels);
}

internal sealed record SyntheticConformanceCase(
    uint[] InitialWords,
    uint[] ExpectedWords,
    string[] Labels,
    uint LocalSizeX = 1,
    uint LocalSizeY = 1,
    uint LocalSizeZ = 1,
    uint GroupCountX = 1,
    uint GroupCountY = 1,
    uint GroupCountZ = 1,
    uint? WorkGroupXRegister = null,
    uint? WorkGroupYRegister = null,
    uint? WorkGroupZRegister = null,
    uint? ThreadGroupSizeRegister = null,
    IReadOnlyList<SyntheticConformanceBuffer>? AdditionalBuffers = null);

internal sealed record SyntheticConformanceBuffer(
    string Name,
    uint[] InitialWords,
    uint[] ExpectedWords,
    string[] Labels);

internal sealed class FakeMemory : ICpuMemory
{
    private readonly List<(ulong Base, byte[] Data)> _regions = [];

    public void AddRegion(ulong baseAddress, uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        _regions.Add((baseAddress, bytes));
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        foreach (var (baseAddress, data) in _regions)
        {
            if (virtualAddress >= baseAddress &&
                virtualAddress + (ulong)destination.Length <= baseAddress + (ulong)data.Length)
            {
                data.AsSpan(
                    (int)(virtualAddress - baseAddress),
                    destination.Length).CopyTo(destination);
                return true;
            }
        }

        return false;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
}
