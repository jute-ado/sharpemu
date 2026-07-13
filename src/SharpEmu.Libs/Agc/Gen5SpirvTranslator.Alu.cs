// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

internal static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private bool TryEmitVectorAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            // SPIR-V has no guest-visible floating-point exception state.
            if (instruction.Opcode is "VNop" or "VClrexcp")
            {
                return true;
            }

            if (instruction.Control is Gen5Vop3pControl packedControl)
            {
                return TryEmitPackedAlu(instruction, packedControl, out error);
            }

            if (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal))
            {
                return TryEmitVectorCompare(instruction, out error);
            }

            if (instruction.Opcode.Contains("F64", StringComparison.Ordinal))
            {
                return TryEmitVectorFloat64(instruction, out error);
            }

            if (instruction.Opcode is "VMadU64U32" or "VMadI64I32")
            {
                return TryEmitVectorMad64(instruction, out error);
            }

            if (instruction.Opcode is
                "VMovreldB32" or "VMovrelsB32" or "VMovrelsdB32")
            {
                return TryEmitRelativeMove(instruction, out error);
            }

            if (instruction.Opcode == "VReadfirstlaneB32")
            {
                if (instruction.Destinations.Count == 0 ||
                    instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
                {
                    error = "missing scalar destination";
                    return false;
                }

                var activeLanes = _module.AddInstruction(
                    SpirvOp.GroupNonUniformBallot,
                    _uvec4Type,
                    UInt(3),
                    Load(_boolType, _exec));
                var firstActiveLane = _module.AddInstruction(
                    SpirvOp.GroupNonUniformBallotFindLSB,
                    _uintType,
                    UInt(3),
                    activeLanes);
                var value = _module.AddInstruction(
                    SpirvOp.GroupNonUniformShuffle,
                    _uintType,
                    UInt(3),
                    GetRawSource(instruction, 0),
                    firstActiveLane);
                StoreS(instruction.Destinations[0].Value, value);
                return true;
            }

            if (instruction.Opcode == "VReadlaneB32")
            {
                if (instruction.Destinations.Count == 0 ||
                    instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
                {
                    error = "missing scalar destination";
                    return false;
                }

                var lane = BitwiseAnd(
                    GetRawSource(instruction, 1),
                    UInt(RdnaWaveLaneCount - 1));
                var value = _module.AddInstruction(
                    SpirvOp.GroupNonUniformShuffle,
                    _uintType,
                    UInt(3),
                    GetRawSource(instruction, 0),
                    lane);
                StoreS(instruction.Destinations[0].Value, value);
                return true;
            }

            if (!TryGetVectorDestination(instruction, out var destination))
            {
                error = "missing vector destination";
                return false;
            }

            uint result;
            switch (instruction.Opcode)
            {
                case "VMovB32":
                    result = GetRawSource(instruction, 0);
                    break;
                case "VWritelaneB32":
                {
                    var lane = Load(_uintType, _subgroupInvocationIdInput);
                    var selectedLane = BitwiseAnd(
                        GetRawSource(instruction, 1),
                        UInt(RdnaWaveLaneCount - 1));
                    var selected = _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        lane,
                        selectedLane);
                    result = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        selected,
                        GetRawSource(instruction, 0),
                        LoadV(destination));
                    StoreV(destination, result, guardWithExec: false);
                    return true;
                }
                case "VCndmaskB32":
                {
                    var condition = instruction.Sources.Count > 2
                        ? IsCurrentLaneSet(GetRawSource64(instruction, 2))
                        : Load(_boolType, _vcc);
                    result = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        condition,
                        GetRawSource(instruction, 1),
                        GetRawSource(instruction, 0));
                    break;
                }
                case "VCvtU32F32":
                    result = _module.AddInstruction(
                        SpirvOp.ConvertFToU,
                        _uintType,
                        GetFloatSource(instruction, 0));
                    break;
                case "VCvtI32F32":
                case "VCvtRpiI32F32":
                case "VCvtFlrI32F32":
                {
                    var source = GetFloatSource(instruction, 0);
                    if (instruction.Opcode == "VCvtRpiI32F32")
                    {
                        source = Ext(9, _floatType, source);
                    }
                    else if (instruction.Opcode == "VCvtFlrI32F32")
                    {
                        source = Ext(8, _floatType, source);
                    }

                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.ConvertFToS, _intType, source));
                    break;
                }
                case "VCvtF32I32":
                {
                    var signed = Bitcast(_intType, GetRawSource(instruction, 0));
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.ConvertSToF, _floatType, signed));
                    break;
                }
                case "VCvtF32U32":
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.ConvertUToF,
                            _floatType,
                            GetRawSource(instruction, 0)));
                    break;
                case "VCvtF32Ubyte0":
                case "VCvtF32Ubyte1":
                case "VCvtF32Ubyte2":
                case "VCvtF32Ubyte3":
                {
                    var shift = (uint)(instruction.Opcode[^1] - '0') * 8;
                    var raw = ShiftRightLogical(GetRawSource(instruction, 0), UInt(shift));
                    raw = BitwiseAnd(raw, UInt(0xFF));
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, raw));
                    break;
                }
                case "VCvtF16F32":
                {
                    var vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        GetFloatSource(instruction, 0),
                        Float(0));
                    result = BitwiseAnd(Ext(58, _uintType, vector), UInt(0xFFFF));
                    break;
                }
                case "VCvtF32F16":
                {
                    var unpacked = Ext(62, _vec2Type, GetRawSource(instruction, 0));
                    var value = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _floatType,
                        unpacked,
                        0);
                    result = Bitcast(_uintType, value);
                    break;
                }
                case "VCvtOffF32I4":
                    result = EmitCvtOffF32I4(instruction);
                    break;
                case "VCvtPkU8F32":
                {
                    var converted = _module.AddInstruction(
                        SpirvOp.ConvertFToU,
                        _uintType,
                        GetFloatSource(instruction, 0));
                    var offset = ShiftLeftLogical(
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(3)),
                        UInt(3));
                    result = _module.AddInstruction(
                        SpirvOp.BitFieldInsert,
                        _uintType,
                        GetRawSource(instruction, 2),
                        converted,
                        offset,
                        UInt(8));
                    break;
                }
                case "VCvtPknormI16F32":
                case "VCvtPknormU16F32":
                {
                    var vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        GetFloatSource(instruction, 0),
                        GetFloatSource(instruction, 1));
                    result = Ext(
                        instruction.Opcode == "VCvtPknormI16F32" ? 56u : 57u,
                        _uintType,
                        vector);
                    break;
                }
                case "VFrexpExpI32F32":
                case "VFrexpMantF32":
                {
                    var resultType = _module.TypeStruct(_floatType, _intType);
                    var decomposed = Ext(
                        52,
                        resultType,
                        GetFloatSource(instruction, 0));
                    if (instruction.Opcode == "VFrexpExpI32F32")
                    {
                        result = Bitcast(
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.CompositeExtract,
                                _intType,
                                decomposed,
                                1));
                    }
                    else
                    {
                        result = EmitFloatResult(
                            instruction,
                            _module.AddInstruction(
                                SpirvOp.CompositeExtract,
                                _floatType,
                                decomposed,
                                0));
                    }

                    break;
                }
                case "VRcpF32":
                case "VRcpIflagF32":
                    result = EmitFloatResult(
                        instruction,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            Float(1),
                            GetFloatSource(instruction, 0)));
                    break;
                case "VLogF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(30, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VLdexpF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            53,
                            _floatType,
                            GetFloatSource(instruction, 0),
                            Bitcast(_intType, GetRawSource(instruction, 1))));
                    break;
                case "VExpF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(29, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VRsqF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(32, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VFractF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(10, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VTruncF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(3, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VCeilF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(9, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VRndneF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(2, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VFloorF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(8, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VSqrtF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(31, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VSinF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            13,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.FMul,
                                _floatType,
                                GetFloatSource(instruction, 0),
                                Float(MathF.Tau))));
                    break;
                case "VCosF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            14,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.FMul,
                                _floatType,
                                GetFloatSource(instruction, 0),
                                Float(MathF.Tau))));
                    break;
                case "VAddF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FAdd);
                    break;
                case "VSubF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FSub);
                    break;
                case "VSubrevF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FSub, reverse: true);
                    break;
                case "VMulF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FMul);
                    break;
                case "VMulLegacyF32":
                case "VMullitF32":
                {
                    var left = GetFloatSource(instruction, 0);
                    var right = GetFloatSource(instruction, 1);
                    var leftZero = _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        BitwiseAnd(Bitcast(_uintType, left), UInt(0x7FFF_FFFF)),
                        UInt(0));
                    var rightZero = _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        BitwiseAnd(Bitcast(_uintType, right), UInt(0x7FFF_FFFF)),
                        UInt(0));
                    var eitherZero = _module.AddInstruction(
                        SpirvOp.LogicalOr,
                        _boolType,
                        leftZero,
                        rightZero);
                    var multiplied = _module.AddInstruction(
                        SpirvOp.FMul,
                        _floatType,
                        left,
                        right);
                    result = EmitFloatResult(
                        instruction,
                        _module.AddInstruction(
                            SpirvOp.Select,
                            _floatType,
                            eitherZero,
                            Float(0),
                            multiplied));
                    break;
                }
                case "VDot2cF32F16":
                    result = EmitDot2cF16(instruction, destination);
                    break;
                case "VMinF32":
                    result = EmitFloatExtBinary(instruction, 37);
                    break;
                case "VMaxF32":
                    result = EmitFloatExtBinary(instruction, 40);
                    break;
                case "VFmaF32":
                case "VMadMkF32":
                case "VMadAkF32":
                case "VFmamkF32":
                case "VFmaakF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            50,
                            _floatType,
                            GetFloatSource(instruction, 0),
                            GetFloatSource(instruction, 1),
                            GetFloatSource(instruction, 2)));
                    break;
                case "VFmaF16":
                    result = EmitFloat16Fma(instruction, destination);
                    break;
                case "VMin3F16":
                case "VMax3F16":
                case "VMed3F16":
                    result = EmitFloat16Ternary(instruction, destination);
                    break;
                case "VMin3I16":
                case "VMax3I16":
                case "VMed3I16":
                case "VMin3U16":
                case "VMax3U16":
                case "VMed3U16":
                    result = EmitInteger16Ternary(instruction, destination);
                    break;
                case "VDivFmasF32":
                {
                    var fused = Ext(
                        50,
                        _floatType,
                        GetFloatSource(instruction, 0),
                        GetFloatSource(instruction, 1),
                        GetFloatSource(instruction, 2));
                    var scaled = Ext(
                        53,
                        _floatType,
                        fused,
                        _module.Constant(_intType, 32));
                    result = EmitFloatResult(
                        instruction,
                        _module.AddInstruction(
                            SpirvOp.Select,
                            _floatType,
                            Load(_boolType, _vcc),
                            scaled,
                            fused));
                    break;
                }
                case "VDivFixupF32":
                    result = EmitDivisionFixupF32(instruction);
                    break;
                case "VDivScaleF32":
                    result = EmitDivisionScaleF32(instruction);
                    break;
                case "VMacF32":
                case "VFmacF32":
                {
                    var addend = Bitcast(_floatType, LoadV(destination));
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            50,
                            _floatType,
                            GetFloatSource(instruction, 0),
                            GetFloatSource(instruction, 1),
                            addend));
                    break;
                }
                case "VMin3F32":
                    result = EmitFloatTernaryExt(instruction, 37);
                    break;
                case "VMax3F32":
                    result = EmitFloatTernaryExt(instruction, 40);
                    break;
                case "VAndB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.BitwiseAnd);
                    break;
                case "VOrB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.BitwiseOr);
                    break;
                case "VXorB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.BitwiseXor);
                    break;
                case "VXnorB32":
                    result = _module.AddInstruction(
                        SpirvOp.Not,
                        _uintType,
                        EmitIntegerBinary(instruction, SpirvOp.BitwiseXor));
                    break;
                case "VNotB32":
                    result = _module.AddInstruction(
                        SpirvOp.Not,
                        _uintType,
                        GetRawSource(instruction, 0));
                    break;
                case "VBfrevB32":
                    result = _module.AddInstruction(
                        SpirvOp.BitReverse,
                        _uintType,
                        GetRawSource(instruction, 0));
                    break;
                case "VFfblB32":
                    result = Bitcast(
                        _uintType,
                        Ext(
                            73,
                            _intType,
                            Bitcast(_intType, GetRawSource(instruction, 0))));
                    break;
                case "VFfbhU32":
                    result = EmitFindFirstBitHigh(instruction, signed: false);
                    break;
                case "VFfbhI32":
                    result = EmitFindFirstBitHigh(instruction, signed: true);
                    break;
                case "VBcntU32B32":
                    result = IAdd(
                        _module.AddInstruction(
                            SpirvOp.BitCount,
                            _uintType,
                            GetRawSource(instruction, 0)),
                        GetRawSource(instruction, 1));
                    break;
                case "VMbcntLoU32B32":
                    result = EmitMbcnt(instruction, highHalf: false);
                    break;
                case "VMbcntHiU32B32":
                    result = EmitMbcnt(instruction, highHalf: true);
                    break;
                case "VAddI32":
                case "VAddU32":
                    result = EmitIntegerBinary(instruction, SpirvOp.IAdd);
                    break;
                case "VAddcU32":
                case "VAddCoCiU32":
                    result = EmitAddWithCarry(instruction);
                    break;
                case "VSubI32":
                case "VSubU32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ISub);
                    break;
                case "VSubrevI32":
                case "VSubrevU32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ISub, reverse: true);
                    break;
                case "VSubbU32":
                case "VSubCoCiU32":
                    result = EmitSubtractWithBorrow(instruction, reverse: false);
                    break;
                case "VSubbrevU32":
                case "VSubrevCoCiU32":
                    result = EmitSubtractWithBorrow(instruction, reverse: true);
                    break;
                case "VMulLoU32":
                case "VMulU32U24":
                    result = EmitIntegerBinary(instruction, SpirvOp.IMul);
                    break;
                case "VMulI32I24":
                {
                    var left = ExtractSignedBits(GetRawSource(instruction, 0), 24);
                    var right = ExtractSignedBits(GetRawSource(instruction, 1), 24);
                    result = _module.AddInstruction(
                        SpirvOp.IMul,
                        _uintType,
                        left,
                        right);
                    break;
                }
                case "VMulHiI32":
                case "VMulHiI32I24":
                {
                    var left = GetRawSource(instruction, 0);
                    var right = GetRawSource(instruction, 1);
                    if (instruction.Opcode == "VMulHiI32I24")
                    {
                        left = ExtractSignedBits(left, 24);
                        right = ExtractSignedBits(right, 24);
                    }

                    var wideLeft = _module.AddInstruction(
                        SpirvOp.SConvert,
                        _longType,
                        Bitcast(_intType, left));
                    var wideRight = _module.AddInstruction(
                        SpirvOp.SConvert,
                        _longType,
                        Bitcast(_intType, right));
                    var product = _module.AddInstruction(
                        SpirvOp.IMul,
                        _longType,
                        wideLeft,
                        wideRight);
                    var high = _module.AddInstruction(
                        SpirvOp.ShiftRightArithmetic,
                        _longType,
                        product,
                        _module.Constant64(_ulongType, 32));
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.SConvert, _intType, high));
                    break;
                }
                case "VMulHiU32":
                case "VMulHiU32U24":
                {
                    var left = GetRawSource(instruction, 0);
                    var right = GetRawSource(instruction, 1);
                    if (instruction.Opcode == "VMulHiU32U24")
                    {
                        left = BitwiseAnd(left, UInt(0x00FF_FFFF));
                        right = BitwiseAnd(right, UInt(0x00FF_FFFF));
                    }

                    var wideLeft = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        left);
                    var wideRight = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        right);
                    var product = _module.AddInstruction(
                        SpirvOp.IMul,
                        _ulongType,
                        wideLeft,
                        wideRight);
                    result = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _uintType,
                        ShiftRightLogical64(
                            product,
                            _module.Constant64(_ulongType, 32)));
                    break;
                }
                case "VMadU32U24":
                {
                    var left = BitwiseAnd(
                        GetRawSource(instruction, 0),
                        UInt(0x00FF_FFFF));
                    var right = BitwiseAnd(
                        GetRawSource(instruction, 1),
                        UInt(0x00FF_FFFF));
                    result = IAdd(
                        _module.AddInstruction(
                            SpirvOp.IMul,
                            _uintType,
                            left,
                            right),
                        GetRawSource(instruction, 2));
                    break;
                }
                case "VMadI32I16":
                case "VMadU32U16":
                {
                    var signed = instruction.Opcode == "VMadI32I16";
                    var left = GetInteger16Source(instruction, 0, signed);
                    var right = GetInteger16Source(instruction, 1, signed);
                    if (signed)
                    {
                        left = Bitcast(_uintType, left);
                        right = Bitcast(_uintType, right);
                    }

                    result = IAdd(
                        _module.AddInstruction(
                            SpirvOp.IMul,
                            _uintType,
                            left,
                            right),
                        GetRawSource(instruction, 2));
                    break;
                }
                case "VMadI16":
                case "VMadU16":
                {
                    var signed = instruction.Opcode == "VMadI16";
                    var left = GetInteger16Source(instruction, 0, signed);
                    var right = GetInteger16Source(instruction, 1, signed);
                    var addend = GetInteger16Source(instruction, 2, signed);
                    if (signed)
                    {
                        left = Bitcast(_uintType, left);
                        right = Bitcast(_uintType, right);
                        addend = Bitcast(_uintType, addend);
                    }

                    result = Emit16BitResult(
                        instruction,
                        destination,
                        IAdd(
                            _module.AddInstruction(
                                SpirvOp.IMul,
                                _uintType,
                                left,
                                right),
                            addend));
                    break;
                }
                case "VMadI32I24":
                {
                    var left = GetRawSource(instruction, 0);
                    var right = GetRawSource(instruction, 1);
                    left = ExtractSignedBits(left, 24);
                    right = ExtractSignedBits(right, 24);

                    result = IAdd(
                        _module.AddInstruction(
                            SpirvOp.IMul,
                            _uintType,
                            left,
                            right),
                        GetRawSource(instruction, 2));
                    break;
                }
                case "VLshrB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ShiftRightLogical);
                    break;
                case "VLshrrevB32":
                    result = EmitIntegerBinary(
                        instruction,
                        SpirvOp.ShiftRightLogical,
                        reverse: true);
                    break;
                case "VLshlB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ShiftLeftLogical);
                    break;
                case "VLshlrevB32":
                    result = EmitIntegerBinary(
                        instruction,
                        SpirvOp.ShiftLeftLogical,
                        reverse: true);
                    break;
                case "VAshrI32":
                case "VAshrrevI32":
                {
                    var reverse = instruction.Opcode == "VAshrrevI32";
                    var left = GetRawSource(instruction, reverse ? 1 : 0);
                    var right = GetRawSource(instruction, reverse ? 0 : 1);
                    result = ShiftRightArithmetic(left, right);
                    break;
                }
                case "VLshlAddU32":
                {
                    var shifted = ShiftLeftLogical(
                        GetRawSource(instruction, 0),
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(31)));
                    result = IAdd(shifted, GetRawSource(instruction, 2));
                    break;
                }
                case "VLshlOrB32":
                {
                    var shifted = ShiftLeftLogical(
                        GetRawSource(instruction, 0),
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(31)));
                    result = BitwiseOr(
                        shifted,
                        GetRawSource(instruction, 2));
                    break;
                }
                case "VAlignbitB32":
                    result = EmitAlign(instruction, byteAligned: false);
                    break;
                case "VAlignbyteB32":
                    result = EmitAlign(instruction, byteAligned: true);
                    break;
                case "VPermB32":
                    result = EmitBytePermute(instruction);
                    break;
                case "VAndOrB32":
                    result = BitwiseOr(
                        BitwiseAnd(
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VOr3B32":
                    result = BitwiseOr(
                        BitwiseOr(
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VXadU32":
                    result = IAdd(
                        BitwiseXor(
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VLerpU8":
                    result = EmitLerpU8(instruction);
                    break;
                case "VSadU8":
                    result = EmitPackedSad(instruction, 8, shiftResult: false, masked: false);
                    break;
                case "VSadHiU8":
                    result = EmitPackedSad(instruction, 8, shiftResult: true, masked: false);
                    break;
                case "VSadU16":
                    result = EmitPackedSad(instruction, 16, shiftResult: false, masked: false);
                    break;
                case "VSadU32":
                    result = EmitPackedSad(instruction, 32, shiftResult: false, masked: false);
                    break;
                case "VMsadU8":
                    result = EmitPackedSad(instruction, 8, shiftResult: false, masked: true);
                    break;
                case "VPermlane16B32":
                    result = EmitPermlane16(instruction, exchangeRows: false);
                    break;
                case "VPermlanex16B32":
                    result = EmitPermlane16(instruction, exchangeRows: true);
                    break;
                case "VAddLshlU32":
                {
                    var added = IAdd(
                        GetRawSource(instruction, 0),
                        GetRawSource(instruction, 1));
                    result = ShiftLeftLogical(added, GetRawSource(instruction, 2));
                    break;
                }
                case "VAdd3U32":
                    result = IAdd(
                        IAdd(
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VMinU32":
                    result = Ext(
                        38,
                        _uintType,
                        GetRawSource(instruction, 0),
                        GetRawSource(instruction, 1));
                    break;
                case "VMaxU32":
                    result = Ext(
                        41,
                        _uintType,
                        GetRawSource(instruction, 0),
                        GetRawSource(instruction, 1));
                    break;
                case "VMin3U32":
                    result = Ext(
                        38,
                        _uintType,
                        Ext(
                            38,
                            _uintType,
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VMax3U32":
                    result = Ext(
                        41,
                        _uintType,
                        Ext(
                            41,
                            _uintType,
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VMinI32":
                case "VMaxI32":
                {
                    var signedResult = Ext(
                        instruction.Opcode == "VMinI32" ? 39u : 42u,
                        _intType,
                        Bitcast(_intType, GetRawSource(instruction, 0)),
                        Bitcast(_intType, GetRawSource(instruction, 1)));
                    result = Bitcast(_uintType, signedResult);
                    break;
                }
                case "VMin3I32":
                case "VMax3I32":
                {
                    var operation = instruction.Opcode == "VMin3I32" ? 39u : 42u;
                    var left = Bitcast(
                        _intType,
                        GetRawSource(instruction, 0));
                    var middle = Bitcast(
                        _intType,
                        GetRawSource(instruction, 1));
                    var right = Bitcast(
                        _intType,
                        GetRawSource(instruction, 2));
                    result = Bitcast(
                        _uintType,
                        Ext(
                            operation,
                            _intType,
                            Ext(operation, _intType, left, middle),
                            right));
                    break;
                }
                case "VMed3U32":
                {
                    var left = GetRawSource(instruction, 0);
                    var middle = GetRawSource(instruction, 1);
                    var right = GetRawSource(instruction, 2);
                    var low = Ext(38, _uintType, left, middle);
                    var high = Ext(41, _uintType, left, middle);
                    result = Ext(
                        41,
                        _uintType,
                        low,
                        Ext(38, _uintType, high, right));
                    break;
                }
                case "VMed3I32":
                {
                    var left = Bitcast(_intType, GetRawSource(instruction, 0));
                    var middle = Bitcast(_intType, GetRawSource(instruction, 1));
                    var right = Bitcast(_intType, GetRawSource(instruction, 2));
                    var low = Ext(39, _intType, left, middle);
                    var high = Ext(42, _intType, left, middle);
                    result = Bitcast(
                        _uintType,
                        Ext(
                            42,
                            _intType,
                            low,
                            Ext(39, _intType, high, right)));
                    break;
                }
                case "VMed3F32":
                {
                    var left = GetFloatSource(instruction, 0);
                    var middle = GetFloatSource(instruction, 1);
                    var right = GetFloatSource(instruction, 2);
                    var low = Ext(37, _floatType, left, middle);
                    var high = Ext(40, _floatType, left, middle);
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            40,
                            _floatType,
                            low,
                            Ext(37, _floatType, high, right)));
                    break;
                }
                case "VCubeidF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Id);
                    break;
                case "VCubescF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Sc);
                    break;
                case "VCubetcF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Tc);
                    break;
                case "VCubemaF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Ma);
                    break;
                case "VAddCoU32":
                {
                    var left = GetRawSource(instruction, 0);
                    var right = GetRawSource(instruction, 1);
                    result = IAdd(left, right);
                    var carry = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        result,
                        left);
                    StoreCarryOut(instruction, carry);
                    break;
                }
                case "VSubCoU32":
                case "VSubrevCoU32":
                {
                    var reverse = instruction.Opcode == "VSubrevCoU32";
                    var left = GetRawSource(instruction, reverse ? 1 : 0);
                    var right = GetRawSource(instruction, reverse ? 0 : 1);
                    result = _module.AddInstruction(SpirvOp.ISub, _uintType, left, right);
                    var borrow = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        left,
                        right);
                    StoreCarryOut(instruction, borrow);
                    break;
                }
                case "VBfeU32":
                {
                    var width = BitwiseAnd(GetRawSource(instruction, 2), UInt(31));
                    result = _module.AddInstruction(
                        SpirvOp.BitFieldUExtract,
                        _uintType,
                        GetRawSource(instruction, 0),
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(31)),
                        width);
                    break;
                }
                case "VBfeI32":
                {
                    var width = BitwiseAnd(GetRawSource(instruction, 2), UInt(31));
                    var extracted = _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _intType,
                        Bitcast(_intType, GetRawSource(instruction, 0)),
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(31)),
                        width);
                    result = Bitcast(_uintType, extracted);
                    break;
                }
                case "VBfiB32":
                {
                    var mask = GetRawSource(instruction, 0);
                    var insert = GetRawSource(instruction, 1);
                    var source = GetRawSource(instruction, 2);
                    result = _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _uintType,
                        BitwiseAnd(mask, insert),
                        BitwiseAnd(
                            _module.AddInstruction(SpirvOp.Not, _uintType, mask),
                            source));
                    break;
                }
                case "VCvtPkrtzF16F32":
                {
                    var first = TruncateFloat32ForPack(GetFloatSource(instruction, 0));
                    var second = TruncateFloat32ForPack(GetFloatSource(instruction, 1));
                    var vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        first,
                        second);
                    result = Ext(58, _uintType, vector);
                    break;
                }
                case "VCvtPkU16U32":
                {
                    var low = Ext(
                        38,
                        _uintType,
                        GetRawSource(instruction, 0),
                        UInt(ushort.MaxValue));
                    var high = Ext(
                        38,
                        _uintType,
                        GetRawSource(instruction, 1),
                        UInt(ushort.MaxValue));
                    result = BitwiseOr(
                        low,
                        ShiftLeftLogical(high, UInt(16)));
                    break;
                }
                case "VCvtPkI16I32":
                {
                    var minimum = _module.Constant(
                        _intType,
                        unchecked((uint)short.MinValue));
                    var maximum = _module.Constant(_intType, (uint)short.MaxValue);
                    var low = ClampSigned16(GetRawSource(instruction, 0), minimum, maximum);
                    var high = ClampSigned16(GetRawSource(instruction, 1), minimum, maximum);
                    result = BitwiseOr(
                        BitwiseAnd(low, UInt(ushort.MaxValue)),
                        ShiftLeftLogical(
                            BitwiseAnd(high, UInt(ushort.MaxValue)),
                            UInt(16)));
                    break;
                }
                default:
                    error = $"unsupported vector opcode {instruction.Opcode}";
                    return false;
            }

            StoreV(destination, result);
            return true;
        }

        private bool TryEmitRelativeMove(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (!TryGetVectorDestination(instruction, out var destination) ||
                instruction.Sources.Count == 0)
            {
                error = "missing relative vector move operand";
                return false;
            }

            var relativeSource = instruction.Opcode is
                "VMovrelsB32" or "VMovrelsdB32";
            var relativeDestination = instruction.Opcode is
                "VMovreldB32" or "VMovrelsdB32";
            var offset = LoadS(M0Register);
            uint value;
            if (relativeSource)
            {
                var source = instruction.Sources[0];
                if (source.Kind != Gen5OperandKind.VectorRegister)
                {
                    error = "relative move source is not a vector register";
                    return false;
                }

                value = LoadVAt(IAdd(UInt(source.Value), offset));
                value = ApplySdwaSourceSelection(instruction, 0, value);
            }
            else
            {
                value = GetRawSource(instruction, 0);
            }

            if (relativeDestination)
            {
                StoreVAt(IAdd(UInt(destination), offset), value);
            }
            else
            {
                StoreV(destination, value);
            }

            return true;
        }

        private uint EmitFloat16Fma(
            Gen5ShaderInstruction instruction,
            uint destination)
        {
            var value = Ext(
                50,
                _floatType,
                GetFloat16Source(instruction, 0),
                GetFloat16Source(instruction, 1),
                GetFloat16Source(instruction, 2));
            return EmitFloat16Result(instruction, destination, value);
        }

        private uint EmitFloat16Ternary(
            Gen5ShaderInstruction instruction,
            uint destination)
        {
            var minimum = instruction.Opcode is "VMin3F16" or "VMed3F16";
            var maximum = instruction.Opcode is "VMax3F16" or "VMed3F16";
            var left = GetFloat16Source(instruction, 0);
            var middle = GetFloat16Source(instruction, 1);
            var right = GetFloat16Source(instruction, 2);
            uint value;
            if (minimum && maximum)
            {
                var low = Ext(37, _floatType, left, middle);
                var high = Ext(40, _floatType, left, middle);
                value = Ext(40, _floatType, low, Ext(37, _floatType, high, right));
            }
            else
            {
                var operation = minimum ? 37u : 40u;
                value = Ext(
                    operation,
                    _floatType,
                    Ext(operation, _floatType, left, middle),
                    right);
            }

            return EmitFloat16Result(instruction, destination, value);
        }

        private uint EmitInteger16Ternary(
            Gen5ShaderInstruction instruction,
            uint destination)
        {
            var signed = instruction.Opcode.EndsWith("I16", StringComparison.Ordinal);
            var minimum = instruction.Opcode.StartsWith("VMin", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("VMed", StringComparison.Ordinal);
            var maximum = instruction.Opcode.StartsWith("VMax", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("VMed", StringComparison.Ordinal);
            var integerType = signed ? _intType : _uintType;
            var minimumOperation = signed ? 39u : 38u;
            var maximumOperation = signed ? 42u : 41u;
            var left = GetInteger16Source(instruction, 0, signed);
            var middle = GetInteger16Source(instruction, 1, signed);
            var right = GetInteger16Source(instruction, 2, signed);
            uint value;
            if (minimum && maximum)
            {
                var low = Ext(minimumOperation, integerType, left, middle);
                var high = Ext(maximumOperation, integerType, left, middle);
                value = Ext(
                    maximumOperation,
                    integerType,
                    low,
                    Ext(minimumOperation, integerType, high, right));
            }
            else
            {
                var operation = minimum ? minimumOperation : maximumOperation;
                value = Ext(
                    operation,
                    integerType,
                    Ext(operation, integerType, left, middle),
                    right);
            }

            return Emit16BitResult(
                instruction,
                destination,
                signed ? Bitcast(_uintType, value) : value);
        }

        private uint EmitFloat16Result(
            Gen5ShaderInstruction instruction,
            uint destination,
            uint value)
        {
            if (instruction.Control is not Gen5Vop3Control control)
            {
                throw new InvalidOperationException("F16 result requires VOP3 control");
            }

            value = control.OutputModifier switch
            {
                1 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(2)),
                2 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(4)),
                3 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(0.5f)),
                _ => value,
            };
            if (control.Clamp)
            {
                value = Ext(43, _floatType, value, Float(0), Float(1));
            }

            return Emit16BitResult(
                instruction,
                destination,
                PackHalf2(value, Float(0)));
        }

        private uint Emit16BitResult(
            Gen5ShaderInstruction instruction,
            uint destination,
            uint value)
        {
            if (instruction.Control is not Gen5Vop3Control control)
            {
                throw new InvalidOperationException("16-bit result requires VOP3 control");
            }

            value = BitwiseAnd(value, UInt(0xFFFF));

            if ((control.OpSelectMask & 8) == 0)
            {
                // RDNA2 clears the unused upper half when writing the low half.
                return value;
            }

            // A high-half write preserves the destination's low 16 bits.
            return BitwiseOr(
                BitwiseAnd(LoadV(destination), UInt(0xFFFF)),
                ShiftLeftLogical(value, UInt(16)));
        }

        private uint EmitDivisionFixupF32(Gen5ShaderInstruction instruction)
        {
            var quotient = GetFloatSource(instruction, 0);
            var denominator = GetFloatSource(instruction, 1);
            var numerator = GetFloatSource(instruction, 2);
            var quotientRaw = Bitcast(_uintType, quotient);
            var denominatorRaw = Bitcast(_uintType, denominator);
            var numeratorRaw = Bitcast(_uintType, numerator);
            var denominatorAbs = BitwiseAnd(denominatorRaw, UInt(0x7FFF_FFFF));
            var numeratorAbs = BitwiseAnd(numeratorRaw, UInt(0x7FFF_FFFF));
            var sign = BitwiseAnd(
                BitwiseXor(denominatorRaw, numeratorRaw),
                UInt(0x8000_0000));

            uint Equal(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.IEqual, _boolType, left, right);
            uint Both(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.LogicalAnd, _boolType, left, right);
            uint Either(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.LogicalOr, _boolType, left, right);
            uint Select(uint condition, uint whenTrue, uint whenFalse) =>
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    condition,
                    whenTrue,
                    whenFalse);

            var denominatorZero = Equal(denominatorAbs, UInt(0));
            var numeratorZero = Equal(numeratorAbs, UInt(0));
            var denominatorInfinity = Equal(denominatorAbs, UInt(0x7F80_0000));
            var numeratorInfinity = Equal(numeratorAbs, UInt(0x7F80_0000));
            var denominatorNan = _module.AddInstruction(
                SpirvOp.IsNan,
                _boolType,
                denominator);
            var numeratorNan = _module.AddInstruction(
                SpirvOp.IsNan,
                _boolType,
                numerator);

            var denominatorExponent = ShiftRightLogical(denominatorAbs, UInt(23));
            var numeratorExponent = ShiftRightLogical(numeratorAbs, UInt(23));
            var underflow = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                IAdd(numeratorExponent, UInt(150)),
                denominatorExponent);
            var overflow = Equal(denominatorExponent, UInt(255));
            var exactExtreme = Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.FDiv,
                    _floatType,
                    numerator,
                    denominator));

            var value = BitwiseOr(
                BitwiseAnd(quotientRaw, UInt(0x7FFF_FFFF)),
                sign);
            value = Select(overflow, exactExtreme, value);
            value = Select(underflow, exactExtreme, value);
            value = Select(
                Either(denominatorInfinity, numeratorZero),
                sign,
                value);
            value = Select(
                Either(denominatorZero, numeratorInfinity),
                BitwiseOr(sign, UInt(0x7F80_0000)),
                value);
            value = Select(
                Both(denominatorInfinity, numeratorInfinity),
                UInt(0xFFC0_0000),
                value);
            value = Select(
                Both(denominatorZero, numeratorZero),
                UInt(0xFFC0_0000),
                value);
            value = Select(
                denominatorNan,
                BitwiseOr(denominatorRaw, UInt(0x0040_0000)),
                value);
            value = Select(
                numeratorNan,
                BitwiseOr(numeratorRaw, UInt(0x0040_0000)),
                value);
            return EmitFloatResult(instruction, Bitcast(_floatType, value));
        }

        private uint EmitDivisionScaleF32(Gen5ShaderInstruction instruction)
        {
            var input = GetFloatSource(instruction, 0);
            var denominator = GetFloatSource(instruction, 1);
            var numerator = GetFloatSource(instruction, 2);
            var denominatorAbs = BitwiseAnd(
                Bitcast(_uintType, denominator),
                UInt(0x7FFF_FFFF));
            var numeratorAbs = BitwiseAnd(
                Bitcast(_uintType, numerator),
                UInt(0x7FFF_FFFF));

            uint Equal(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.IEqual, _boolType, left, right);
            uint Both(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.LogicalAnd, _boolType, left, right);
            uint Either(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.LogicalOr, _boolType, left, right);
            uint SelectFloat(uint condition, uint whenTrue, uint whenFalse) =>
                _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    condition,
                    whenTrue,
                    whenFalse);
            uint SelectBool(uint condition, uint whenTrue, uint whenFalse) =>
                _module.AddInstruction(
                    SpirvOp.Select,
                    _boolType,
                    condition,
                    whenTrue,
                    whenFalse);
            uint IsDenormal(uint value)
            {
                var absolute = BitwiseAnd(
                    Bitcast(_uintType, value),
                    UInt(0x7FFF_FFFF));
                return Both(
                    Equal(BitwiseAnd(absolute, UInt(0x7F80_0000)), UInt(0)),
                    _module.AddInstruction(
                        SpirvOp.INotEqual,
                        _boolType,
                        absolute,
                        UInt(0)));
            }

            var denominatorZero = Equal(denominatorAbs, UInt(0));
            var numeratorZero = Equal(numeratorAbs, UInt(0));
            var eitherZero = Either(denominatorZero, numeratorZero);
            var denominatorExponent = ShiftRightLogical(denominatorAbs, UInt(23));
            var numeratorExponent = ShiftRightLogical(numeratorAbs, UInt(23));
            var nearMaximum = _module.AddInstruction(
                SpirvOp.UGreaterThanEqual,
                _boolType,
                numeratorExponent,
                IAdd(denominatorExponent, UInt(96)));
            var denominatorDenormal = IsDenormal(denominator);
            var reciprocal = _module.AddInstruction(
                SpirvOp.FDiv,
                _floatType,
                Float(1),
                denominator);
            var quotient = _module.AddInstruction(
                SpirvOp.FDiv,
                _floatType,
                numerator,
                denominator);
            var reciprocalDenormal = IsDenormal(reciprocal);
            var quotientDenormal = IsDenormal(quotient);
            var bothResultsDenormal = Both(reciprocalDenormal, quotientDenormal);
            var tinyNumerator = _module.AddInstruction(
                SpirvOp.ULessThanEqual,
                _boolType,
                numeratorExponent,
                UInt(23));
            var inputIsDenominator = _module.AddInstruction(
                SpirvOp.FOrdEqual,
                _boolType,
                input,
                denominator);
            var inputIsNumerator = _module.AddInstruction(
                SpirvOp.FOrdEqual,
                _boolType,
                input,
                numerator);
            var scaleUp = Ext(
                53,
                _floatType,
                input,
                _module.Constant(_intType, 64));
            var scaleDown = Ext(
                53,
                _floatType,
                input,
                _module.Constant(_intType, unchecked((uint)-64)));

            var value = input;
            value = SelectFloat(tinyNumerator, scaleUp, value);
            value = SelectFloat(
                quotientDenormal,
                SelectFloat(inputIsNumerator, scaleUp, input),
                value);
            value = SelectFloat(reciprocalDenormal, scaleDown, value);
            value = SelectFloat(
                bothResultsDenormal,
                SelectFloat(inputIsDenominator, scaleUp, input),
                value);
            value = SelectFloat(denominatorDenormal, scaleUp, value);
            value = SelectFloat(
                nearMaximum,
                SelectFloat(inputIsDenominator, scaleUp, input),
                value);
            value = SelectFloat(
                eitherZero,
                Bitcast(_floatType, UInt(0x7FC0_0000)),
                value);

            var falseValue = _module.ConstantBool(false);
            var trueValue = _module.ConstantBool(true);
            var scaleQuotient = falseValue;
            scaleQuotient = SelectBool(quotientDenormal, trueValue, scaleQuotient);
            scaleQuotient = SelectBool(reciprocalDenormal, falseValue, scaleQuotient);
            scaleQuotient = SelectBool(bothResultsDenormal, trueValue, scaleQuotient);
            scaleQuotient = SelectBool(denominatorDenormal, falseValue, scaleQuotient);
            scaleQuotient = SelectBool(nearMaximum, trueValue, scaleQuotient);
            scaleQuotient = SelectBool(eitherZero, falseValue, scaleQuotient);
            StoreCarryOut(instruction, scaleQuotient);
            return EmitFloatResult(instruction, value);
        }

        private uint EmitDivisionFixupF64(Gen5ShaderInstruction instruction)
        {
            var quotient = GetDoubleSource(instruction, 0);
            var denominator = GetDoubleSource(instruction, 1);
            var numerator = GetDoubleSource(instruction, 2);
            var quotientRaw = Bitcast(_ulongType, quotient);
            var denominatorRaw = Bitcast(_ulongType, denominator);
            var numeratorRaw = Bitcast(_ulongType, numerator);

            uint U64(ulong value) => _module.Constant64(_ulongType, value);
            uint Binary(SpirvOp operation, uint left, uint right) =>
                _module.AddInstruction(operation, _ulongType, left, right);
            uint Equal(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.IEqual, _boolType, left, right);
            uint Both(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.LogicalAnd, _boolType, left, right);
            uint Either(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.LogicalOr, _boolType, left, right);
            uint Select(uint condition, uint whenTrue, uint whenFalse) =>
                _module.AddInstruction(
                    SpirvOp.Select,
                    _ulongType,
                    condition,
                    whenTrue,
                    whenFalse);

            var absoluteMask = U64(0x7FFF_FFFF_FFFF_FFFFUL);
            var signMask = U64(0x8000_0000_0000_0000UL);
            var infinity = U64(0x7FF0_0000_0000_0000UL);
            var quietNanBit = U64(0x0008_0000_0000_0000UL);
            var denominatorAbs = Binary(
                SpirvOp.BitwiseAnd,
                denominatorRaw,
                absoluteMask);
            var numeratorAbs = Binary(SpirvOp.BitwiseAnd, numeratorRaw, absoluteMask);
            var sign = Binary(
                SpirvOp.BitwiseAnd,
                Binary(SpirvOp.BitwiseXor, denominatorRaw, numeratorRaw),
                signMask);

            var denominatorZero = Equal(denominatorAbs, U64(0));
            var numeratorZero = Equal(numeratorAbs, U64(0));
            var denominatorInfinity = Equal(denominatorAbs, infinity);
            var numeratorInfinity = Equal(numeratorAbs, infinity);
            var denominatorNan = _module.AddInstruction(
                SpirvOp.IsNan,
                _boolType,
                denominator);
            var numeratorNan = _module.AddInstruction(
                SpirvOp.IsNan,
                _boolType,
                numerator);

            var denominatorExponent = ShiftRightLogical64(denominatorAbs, U64(52));
            var numeratorExponent = ShiftRightLogical64(numeratorAbs, U64(52));
            var underflow = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                Binary(SpirvOp.IAdd, numeratorExponent, U64(1075)),
                denominatorExponent);
            var overflow = Equal(denominatorExponent, U64(2047));
            var exactExtreme = Bitcast(
                _ulongType,
                _module.AddInstruction(
                    SpirvOp.FDiv,
                    _doubleType,
                    numerator,
                    denominator));

            var value = Binary(
                SpirvOp.BitwiseOr,
                Binary(SpirvOp.BitwiseAnd, quotientRaw, absoluteMask),
                sign);
            value = Select(overflow, exactExtreme, value);
            value = Select(underflow, exactExtreme, value);
            value = Select(Either(denominatorInfinity, numeratorZero), sign, value);
            value = Select(
                Either(denominatorZero, numeratorInfinity),
                Binary(SpirvOp.BitwiseOr, sign, infinity),
                value);
            value = Select(
                Both(denominatorInfinity, numeratorInfinity),
                U64(0xFFF8_0000_0000_0000UL),
                value);
            value = Select(
                Both(denominatorZero, numeratorZero),
                U64(0xFFF8_0000_0000_0000UL),
                value);
            value = Select(
                denominatorNan,
                Binary(SpirvOp.BitwiseOr, denominatorRaw, quietNanBit),
                value);
            value = Select(
                numeratorNan,
                Binary(SpirvOp.BitwiseOr, numeratorRaw, quietNanBit),
                value);
            return Bitcast(_doubleType, value);
        }

        private uint EmitDivisionScaleF64(Gen5ShaderInstruction instruction)
        {
            var input = GetDoubleSource(instruction, 0);
            var denominator = GetDoubleSource(instruction, 1);
            var numerator = GetDoubleSource(instruction, 2);

            uint U64(ulong value) => _module.Constant64(_ulongType, value);
            uint Binary(SpirvOp operation, uint left, uint right) =>
                _module.AddInstruction(operation, _ulongType, left, right);
            uint Equal(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.IEqual, _boolType, left, right);
            uint Both(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.LogicalAnd, _boolType, left, right);
            uint Either(uint left, uint right) =>
                _module.AddInstruction(SpirvOp.LogicalOr, _boolType, left, right);
            uint SelectDouble(uint condition, uint whenTrue, uint whenFalse) =>
                _module.AddInstruction(
                    SpirvOp.Select,
                    _doubleType,
                    condition,
                    whenTrue,
                    whenFalse);
            uint SelectBool(uint condition, uint whenTrue, uint whenFalse) =>
                _module.AddInstruction(
                    SpirvOp.Select,
                    _boolType,
                    condition,
                    whenTrue,
                    whenFalse);
            uint IsDenormal(uint value)
            {
                var absolute = Binary(
                    SpirvOp.BitwiseAnd,
                    Bitcast(_ulongType, value),
                    U64(0x7FFF_FFFF_FFFF_FFFFUL));
                return Both(
                    Equal(
                        Binary(
                            SpirvOp.BitwiseAnd,
                            absolute,
                            U64(0x7FF0_0000_0000_0000UL)),
                        U64(0)),
                    _module.AddInstruction(
                        SpirvOp.INotEqual,
                        _boolType,
                        absolute,
                        U64(0)));
            }

            var denominatorAbs = Binary(
                SpirvOp.BitwiseAnd,
                Bitcast(_ulongType, denominator),
                U64(0x7FFF_FFFF_FFFF_FFFFUL));
            var numeratorAbs = Binary(
                SpirvOp.BitwiseAnd,
                Bitcast(_ulongType, numerator),
                U64(0x7FFF_FFFF_FFFF_FFFFUL));
            var denominatorZero = Equal(denominatorAbs, U64(0));
            var numeratorZero = Equal(numeratorAbs, U64(0));
            var eitherZero = Either(denominatorZero, numeratorZero);
            var denominatorExponent = ShiftRightLogical64(denominatorAbs, U64(52));
            var numeratorExponent = ShiftRightLogical64(numeratorAbs, U64(52));
            var nearMaximum = _module.AddInstruction(
                SpirvOp.UGreaterThanEqual,
                _boolType,
                numeratorExponent,
                Binary(SpirvOp.IAdd, denominatorExponent, U64(768)));
            var denominatorDenormal = IsDenormal(denominator);
            var reciprocal = _module.AddInstruction(
                SpirvOp.FDiv,
                _doubleType,
                Double(1),
                denominator);
            var quotient = _module.AddInstruction(
                SpirvOp.FDiv,
                _doubleType,
                numerator,
                denominator);
            var reciprocalDenormal = IsDenormal(reciprocal);
            var quotientDenormal = IsDenormal(quotient);
            var bothResultsDenormal = Both(reciprocalDenormal, quotientDenormal);
            var tinyNumerator = _module.AddInstruction(
                SpirvOp.ULessThanEqual,
                _boolType,
                numeratorExponent,
                U64(53));
            var inputIsDenominator = _module.AddInstruction(
                SpirvOp.FOrdEqual,
                _boolType,
                input,
                denominator);
            var inputIsNumerator = _module.AddInstruction(
                SpirvOp.FOrdEqual,
                _boolType,
                input,
                numerator);
            var scaleUp = Ext(
                53,
                _doubleType,
                input,
                _module.Constant(_intType, 128));
            var scaleDown = Ext(
                53,
                _doubleType,
                input,
                _module.Constant(_intType, unchecked((uint)-128)));

            var scaled = input;
            scaled = SelectDouble(tinyNumerator, scaleUp, scaled);
            scaled = SelectDouble(
                quotientDenormal,
                SelectDouble(inputIsNumerator, scaleUp, input),
                scaled);
            scaled = SelectDouble(reciprocalDenormal, scaleDown, scaled);
            scaled = SelectDouble(
                bothResultsDenormal,
                SelectDouble(inputIsDenominator, scaleUp, input),
                scaled);
            scaled = SelectDouble(denominatorDenormal, scaleUp, scaled);
            scaled = SelectDouble(
                nearMaximum,
                SelectDouble(inputIsDenominator, scaleUp, input),
                scaled);
            scaled = SelectDouble(
                eitherZero,
                Bitcast(_doubleType, U64(0x7FF8_0000_0000_0000UL)),
                scaled);

            var falseValue = _module.ConstantBool(false);
            var trueValue = _module.ConstantBool(true);
            var scaleQuotient = falseValue;
            scaleQuotient = SelectBool(quotientDenormal, trueValue, scaleQuotient);
            scaleQuotient = SelectBool(reciprocalDenormal, falseValue, scaleQuotient);
            scaleQuotient = SelectBool(bothResultsDenormal, trueValue, scaleQuotient);
            scaleQuotient = SelectBool(denominatorDenormal, falseValue, scaleQuotient);
            scaleQuotient = SelectBool(nearMaximum, trueValue, scaleQuotient);
            scaleQuotient = SelectBool(eitherZero, falseValue, scaleQuotient);
            StoreCarryOut(instruction, scaleQuotient);
            return scaled;
        }

        private uint GetFloat16Source(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            var control = instruction.Control as Gen5Vop3Control;
            uint value;
            if (!TryGetInlineFloatSource(instruction, sourceIndex, out value))
            {
                var unpacked = Ext(
                    62,
                    _vec2Type,
                    GetRawSource(instruction, sourceIndex));
                value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    unpacked,
                    ((control?.OpSelectMask ?? 0) >> sourceIndex) & 1);
            }

            if (((control?.AbsoluteMask ?? 0) & (1u << sourceIndex)) != 0)
            {
                value = Ext(4, _floatType, value);
            }

            return ((control?.NegateMask ?? 0) & (1u << sourceIndex)) != 0
                ? _module.AddInstruction(SpirvOp.FNegate, _floatType, value)
                : value;
        }

        private bool TryEmitVectorFloat64(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (_doubleType == 0 ||
                !TryGetVectorDestination(instruction, out var destination))
            {
                error = "missing FP64 vector destination";
                return false;
            }

            if (instruction.Opcode is "VCvtF64I32" or "VCvtF64U32" or "VCvtF64F32")
            {
                var converted = instruction.Opcode switch
                {
                    "VCvtF64I32" => _module.AddInstruction(
                        SpirvOp.ConvertSToF,
                        _doubleType,
                        Bitcast(_intType, GetRawSource(instruction, 0))),
                    "VCvtF64U32" => _module.AddInstruction(
                        SpirvOp.ConvertUToF,
                        _doubleType,
                        GetRawSource(instruction, 0)),
                    _ => _module.AddInstruction(
                        SpirvOp.FConvert,
                        _doubleType,
                        GetFloatSource(instruction, 0)),
                };
                StoreDouble(destination, EmitDoubleResult(instruction, converted));
                return true;
            }

            var left = GetDoubleSource(instruction, 0);
            uint result;
            switch (instruction.Opcode)
            {
                case "VCvtI32F64":
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.ConvertFToS, _intType, left));
                    StoreV(destination, result);
                    return true;
                case "VCvtU32F64":
                    result = _module.AddInstruction(
                        SpirvOp.ConvertFToU,
                        _uintType,
                        left);
                    StoreV(destination, result);
                    return true;
                case "VCvtF32F64":
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.FConvert, _floatType, left));
                    StoreV(destination, result);
                    return true;
                case "VFrexpExpI32F64":
                {
                    var resultType = _module.TypeStruct(_doubleType, _intType);
                    var decomposed = Ext(52, resultType, left);
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _intType,
                            decomposed,
                            1));
                    StoreV(destination, result);
                    return true;
                }
                case "VTruncF64":
                    result = Ext(3, _doubleType, left);
                    break;
                case "VCeilF64":
                    result = Ext(9, _doubleType, left);
                    break;
                case "VRndneF64":
                    result = Ext(2, _doubleType, left);
                    break;
                case "VFloorF64":
                    result = Ext(8, _doubleType, left);
                    break;
                case "VFractF64":
                    result = Ext(10, _doubleType, left);
                    break;
                case "VFrexpMantF64":
                {
                    var resultType = _module.TypeStruct(_doubleType, _intType);
                    var decomposed = Ext(52, resultType, left);
                    result = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _doubleType,
                        decomposed,
                        0);
                    break;
                }
                case "VRcpF64":
                    result = _module.AddInstruction(
                        SpirvOp.FDiv,
                        _doubleType,
                        Double(1),
                        left);
                    break;
                case "VRsqF64":
                    result = Ext(32, _doubleType, left);
                    break;
                case "VSqrtF64":
                    result = Ext(31, _doubleType, left);
                    break;
                case "VAddF64":
                    result = _module.AddInstruction(
                        SpirvOp.FAdd,
                        _doubleType,
                        left,
                        GetDoubleSource(instruction, 1));
                    break;
                case "VMulF64":
                    result = _module.AddInstruction(
                        SpirvOp.FMul,
                        _doubleType,
                        left,
                        GetDoubleSource(instruction, 1));
                    break;
                case "VFmaF64":
                    result = Ext(
                        50,
                        _doubleType,
                        left,
                        GetDoubleSource(instruction, 1),
                        GetDoubleSource(instruction, 2));
                    break;
                case "VDivFmasF64":
                {
                    var fused = Ext(
                        50,
                        _doubleType,
                        left,
                        GetDoubleSource(instruction, 1),
                        GetDoubleSource(instruction, 2));
                    var scaled = Ext(
                        53,
                        _doubleType,
                        fused,
                        _module.Constant(_intType, 64));
                    result = _module.AddInstruction(
                        SpirvOp.Select,
                        _doubleType,
                        Load(_boolType, _vcc),
                        scaled,
                        fused);
                    break;
                }
                case "VDivFixupF64":
                    result = EmitDivisionFixupF64(instruction);
                    break;
                case "VDivScaleF64":
                    result = EmitDivisionScaleF64(instruction);
                    break;
                case "VMinF64":
                    result = Ext(37, _doubleType, left, GetDoubleSource(instruction, 1));
                    break;
                case "VMaxF64":
                    result = Ext(40, _doubleType, left, GetDoubleSource(instruction, 1));
                    break;
                default:
                    error = $"unsupported FP64 vector opcode {instruction.Opcode}";
                    return false;
            }

            StoreDouble(destination, EmitDoubleResult(instruction, result));
            return true;
        }

        private uint GetDoubleSource(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            if ((uint)sourceIndex >= instruction.Sources.Count)
            {
                throw new InvalidOperationException($"missing FP64 source {sourceIndex}");
            }

            var operand = instruction.Sources[sourceIndex];
            uint value;
            if (TryGetInlineDoubleSource(operand, out var inline))
            {
                value = inline;
            }
            else
            {
                var raw = operand.Kind switch
                {
                    Gen5OperandKind.VectorRegister => LoadV64(operand.Value),
                    Gen5OperandKind.ScalarRegister => LoadS64(operand.Value),
                    Gen5OperandKind.LiteralConstant =>
                        _module.Constant64(_ulongType, operand.Value),
                    _ => throw new InvalidOperationException(
                        $"unsupported FP64 source {operand}"),
                };
                value = Bitcast(_doubleType, raw);
            }

            if (instruction.Control is Gen5Vop3Control control)
            {
                if ((control.AbsoluteMask & (1u << sourceIndex)) != 0)
                {
                    value = Ext(4, _doubleType, value);
                }

                if ((control.NegateMask & (1u << sourceIndex)) != 0)
                {
                    value = _module.AddInstruction(
                        SpirvOp.FNegate,
                        _doubleType,
                        value);
                }
            }

            return value;
        }

        private uint GetInteger16Source(
            Gen5ShaderInstruction instruction,
            int sourceIndex,
            bool signed)
        {
            var control = instruction.Control as Gen5Vop3Control;
            var offset = ((control?.OpSelectMask ?? 0) & (1u << sourceIndex)) != 0
                ? 16u
                : 0u;
            return _module.AddInstruction(
                signed ? SpirvOp.BitFieldSExtract : SpirvOp.BitFieldUExtract,
                signed ? _intType : _uintType,
                signed
                    ? Bitcast(_intType, GetRawSource(instruction, sourceIndex))
                    : GetRawSource(instruction, sourceIndex),
                UInt(offset),
                UInt(16));
        }

        private uint GetInteger64Source(
            Gen5ShaderInstruction instruction,
            int sourceIndex,
            bool signed)
        {
            if ((uint)sourceIndex >= instruction.Sources.Count)
            {
                throw new InvalidOperationException($"missing integer64 source {sourceIndex}");
            }

            var operand = instruction.Sources[sourceIndex];
            uint value;
            if (operand.Kind == Gen5OperandKind.VectorRegister)
            {
                value = LoadV64(operand.Value);
            }
            else if (operand.Kind == Gen5OperandKind.ScalarRegister)
            {
                value = LoadS64(operand.Value);
            }
            else
            {
                var raw = operand.Kind switch
                {
                    Gen5OperandKind.LiteralConstant => operand.Value,
                    Gen5OperandKind.EncodedConstant when TryDecodeInlineConstant(
                        operand.Value,
                        out var inline) => inline,
                    _ => throw new InvalidOperationException(
                        $"unsupported integer64 source {operand}"),
                };
                return signed
                    ? _module.AddInstruction(
                        SpirvOp.SConvert,
                        _longType,
                        Bitcast(_intType, UInt(raw)))
                    : _module.AddInstruction(SpirvOp.UConvert, _ulongType, UInt(raw));
            }

            return signed ? Bitcast(_longType, value) : value;
        }

        private bool TryGetInlineDoubleSource(Gen5Operand operand, out uint value)
        {
            if (operand.Kind != Gen5OperandKind.EncodedConstant)
            {
                value = 0;
                return false;
            }

            double? inline = operand.Value switch
            {
                125 => 0,
                >= 128 and <= 192 => operand.Value - 128,
                >= 193 and <= 208 => -(operand.Value - 192),
                240 => 0.5,
                241 => -0.5,
                242 => 1,
                243 => -1,
                244 => 2,
                245 => -2,
                246 => 4,
                247 => -4,
                248 => 1 / (2 * Math.PI),
                _ => null,
            };
            if (!inline.HasValue)
            {
                value = 0;
                return false;
            }

            value = Double(inline.Value);
            return true;
        }

        private uint LoadV64(uint register)
        {
            var low = _module.AddInstruction(
                SpirvOp.UConvert,
                _ulongType,
                LoadV(register));
            var high = ShiftLeftLogical64(
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    LoadV(register + 1)),
                _module.Constant64(_ulongType, 32));
            return _module.AddInstruction(
                SpirvOp.BitwiseOr,
                _ulongType,
                low,
                high);
        }

        private void StoreV64(uint register, uint value)
        {
            StoreV(
                register,
                _module.AddInstruction(SpirvOp.UConvert, _uintType, value));
            StoreV(
                register + 1,
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _uintType,
                    ShiftRightLogical64(
                        value,
                        _module.Constant64(_ulongType, 32))));
        }

        private void StoreDouble(uint register, uint value) =>
            StoreV64(register, Bitcast(_ulongType, value));

        private bool TryEmitVectorMad64(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (!TryGetVectorDestination(instruction, out var destination))
            {
                error = "missing 64-bit multiply-add destination";
                return false;
            }

            var signed = instruction.Opcode == "VMadI64I32";
            var left = GetRawSource(instruction, 0);
            var right = GetRawSource(instruction, 1);
            var addend = GetRawSource64(instruction, 2);
            uint result;
            uint overflow;
            if (signed)
            {
                var wideLeft = _module.AddInstruction(
                    SpirvOp.SConvert,
                    _longType,
                    Bitcast(_intType, left));
                var wideRight = _module.AddInstruction(
                    SpirvOp.SConvert,
                    _longType,
                    Bitcast(_intType, right));
                var product = _module.AddInstruction(
                    SpirvOp.IMul,
                    _longType,
                    wideLeft,
                    wideRight);
                var signedAddend = Bitcast(_longType, addend);
                var signedResult = _module.AddInstruction(
                    SpirvOp.IAdd,
                    _longType,
                    product,
                    signedAddend);
                overflow = SignedAddOverflow64(product, signedAddend, signedResult);
                result = Bitcast(_ulongType, signedResult);
            }
            else
            {
                var wideLeft = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    left);
                var wideRight = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    right);
                var product = _module.AddInstruction(
                    SpirvOp.IMul,
                    _ulongType,
                    wideLeft,
                    wideRight);
                result = _module.AddInstruction(
                    SpirvOp.IAdd,
                    _ulongType,
                    product,
                    addend);
                overflow = _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    result,
                    addend);
            }

            StoreV64(destination, result);
            StoreCarryOut(instruction, overflow);
            return true;
        }

        private uint SignedAddOverflow64(uint left, uint right, uint result)
        {
            var zero = _module.Constant64(_longType, 0);
            var leftNegative = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                left,
                zero);
            var rightNegative = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                right,
                zero);
            var resultNegative = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                result,
                zero);
            return _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                _module.AddInstruction(
                    SpirvOp.LogicalEqual,
                    _boolType,
                    leftNegative,
                    rightNegative),
                _module.AddInstruction(
                    SpirvOp.LogicalNotEqual,
                    _boolType,
                    leftNegative,
                    resultNegative));
        }

        private uint EmitDoubleResult(
            Gen5ShaderInstruction instruction,
            uint value)
        {
            if (instruction.Control is not Gen5Vop3Control control)
            {
                return value;
            }

            value = control.OutputModifier switch
            {
                1 => _module.AddInstruction(
                    SpirvOp.FMul,
                    _doubleType,
                    value,
                    Double(2)),
                2 => _module.AddInstruction(
                    SpirvOp.FMul,
                    _doubleType,
                    value,
                    Double(4)),
                3 => _module.AddInstruction(
                    SpirvOp.FMul,
                    _doubleType,
                    value,
                    Double(0.5)),
                _ => value,
            };
            return control.Clamp
                ? Ext(43, _doubleType, value, Double(0), Double(1))
                : value;
        }

        private uint Double(double value) =>
            _module.Constant64(
                _doubleType,
                unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

        private uint EmitAlign(
            Gen5ShaderInstruction instruction,
            bool byteAligned)
        {
            var high = ShiftLeftLogical64(
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    GetRawSource(instruction, 0)),
                _module.Constant64(_ulongType, 32));
            var low = _module.AddInstruction(
                SpirvOp.UConvert,
                _ulongType,
                GetRawSource(instruction, 1));
            var combined = _module.AddInstruction(
                SpirvOp.BitwiseOr,
                _ulongType,
                high,
                low);
            var offset = BitwiseAnd(
                GetRawSource(instruction, 2),
                UInt(byteAligned ? 3u : 31u));
            if (byteAligned)
            {
                offset = ShiftLeftLogical(offset, UInt(3));
            }

            return _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                ShiftRightLogical64(
                    combined,
                    _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        offset)));
        }

        private uint EmitBytePermute(Gen5ShaderInstruction instruction)
        {
            // The ISA defines in[0..7] over the 64-bit value S0:S1, so the
            // four low-index bytes come from S1 and the high four from S0.
            var high = ShiftLeftLogical64(
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    GetRawSource(instruction, 0)),
                _module.Constant64(_ulongType, 32));
            var low = _module.AddInstruction(
                SpirvOp.UConvert,
                _ulongType,
                GetRawSource(instruction, 1));
            var inputs = _module.AddInstruction(
                SpirvOp.BitwiseOr,
                _ulongType,
                high,
                low);
            var selectors = GetRawSource(instruction, 2);
            var result = UInt(0);
            for (uint component = 0; component < 4; component++)
            {
                var selector = ExtractUnsignedBits(selectors, component * 8, 8);
                var selectedByte = ExtractByte64(inputs, selector);

                // Selectors 8..11 sign-extend bytes 1, 3, 5, and 7.
                var signByteIndex = _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    ShiftLeftLogical(selector, UInt(1)),
                    UInt(15));
                var signByte = ExtractByte64(inputs, signByteIndex);
                var signExtension = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    IsNotZero(BitwiseAnd(signByte, UInt(0x80))),
                    UInt(0xFF),
                    UInt(0));
                var outputByte = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.UGreaterThanEqual,
                        _boolType,
                        selector,
                        UInt(8)),
                    signExtension,
                    selectedByte);
                outputByte = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        selector,
                        UInt(12)),
                    UInt(0),
                    outputByte);
                outputByte = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.UGreaterThanEqual,
                        _boolType,
                        selector,
                        UInt(13)),
                    UInt(0xFF),
                    outputByte);
                result = BitwiseOr(
                    result,
                    ShiftLeftLogical(outputByte, UInt(component * 8)));
            }

            return result;
        }

        private uint ExtractByte64(uint value, uint index)
        {
            var shift = _module.AddInstruction(
                SpirvOp.UConvert,
                _ulongType,
                ShiftLeftLogical(index, UInt(3)));
            return _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                BitwiseAnd64(
                    ShiftRightLogical64(value, shift),
                    _module.Constant64(_ulongType, 0xFF)));
        }

        private uint EmitDot2cF16(
            Gen5ShaderInstruction instruction,
            uint destination)
        {
            var left = Ext(62, _vec2Type, GetRawSource(instruction, 0));
            var right = Ext(62, _vec2Type, GetRawSource(instruction, 1));
            uint Component(uint vector, uint index) =>
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    vector,
                    index);

            var accumulated = Ext(
                50,
                _floatType,
                Component(left, 0),
                Component(right, 0),
                Bitcast(_floatType, LoadV(destination)));
            accumulated = Ext(
                50,
                _floatType,
                Component(left, 1),
                Component(right, 1),
                accumulated);
            return EmitFloatResult(instruction, accumulated);
        }

        private uint EmitLerpU8(Gen5ShaderInstruction instruction)
        {
            var result = UInt(0);
            for (uint component = 0; component < 4; component++)
            {
                var offset = component * 8;
                var left = ExtractUnsignedBits(
                    GetRawSource(instruction, 0),
                    offset,
                    8);
                var right = ExtractUnsignedBits(
                    GetRawSource(instruction, 1),
                    offset,
                    8);
                var rounding = BitwiseAnd(
                    ShiftRightLogical(
                        GetRawSource(instruction, 2),
                        UInt(offset)),
                    UInt(1));
                var average = ShiftRightLogical(
                    IAdd(IAdd(left, right), rounding),
                    UInt(1));
                result = BitwiseOr(
                    result,
                    ShiftLeftLogical(average, UInt(offset)));
            }

            return result;
        }

        private uint EmitPackedSad(
            Gen5ShaderInstruction instruction,
            uint componentBits,
            bool shiftResult,
            bool masked)
        {
            var sum = UInt(0);
            var componentCount = 32u / componentBits;
            for (uint component = 0; component < componentCount; component++)
            {
                var offset = component * componentBits;
                var left = ExtractUnsignedBits(
                    GetRawSource(instruction, 0),
                    offset,
                    componentBits);
                var right = ExtractUnsignedBits(
                    GetRawSource(instruction, 1),
                    offset,
                    componentBits);
                var difference = _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    Ext(41, _uintType, left, right),
                    Ext(38, _uintType, left, right));
                if (masked)
                {
                    difference = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.IEqual,
                            _boolType,
                            right,
                            UInt(0)),
                        UInt(0),
                        difference);
                }

                sum = IAdd(sum, difference);
            }

            if (shiftResult)
            {
                sum = ShiftLeftLogical(sum, UInt(16));
            }

            return IAdd(sum, GetRawSource(instruction, 2));
        }

        private uint EmitFindFirstBitHigh(
            Gen5ShaderInstruction instruction,
            bool signed)
        {
            var raw = GetRawSource(instruction, 0);
            var found = Ext(
                signed ? 74u : 75u,
                _intType,
                signed ? Bitcast(_intType, raw) : raw);
            var notFound = _module.Constant(_intType, uint.MaxValue);
            var count = _module.AddInstruction(
                SpirvOp.ISub,
                _intType,
                _module.Constant(_intType, 31),
                found);
            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _intType,
                    _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        found,
                        notFound),
                    notFound,
                    count));
        }

        private uint EmitMbcnt(
            Gen5ShaderInstruction instruction,
            bool highHalf)
        {
            var lane = _subgroupInvocationIdInput == 0
                ? UInt(0)
                : Load(_uintType, _subgroupInvocationIdInput);
            var inHalf = highHalf
                ? _module.AddInstruction(
                    SpirvOp.UGreaterThanEqual,
                    _boolType,
                    lane,
                    UInt(32))
                : _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    lane,
                    UInt(32));
            var localLane = highHalf
                ? _module.AddInstruction(SpirvOp.ISub, _uintType, lane, UInt(32))
                : lane;
            var prefixMask = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                ShiftLeftLogical(UInt(1), localLane),
                UInt(1));
            if (!highHalf)
            {
                prefixMask = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    inHalf,
                    prefixMask,
                    UInt(uint.MaxValue));
            }
            else
            {
                prefixMask = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    inHalf,
                    prefixMask,
                    UInt(0));
            }

            var count = _module.AddInstruction(
                SpirvOp.BitCount,
                _uintType,
                BitwiseAnd(GetRawSource(instruction, 0), prefixMask));
            return IAdd(count, GetRawSource(instruction, 1));
        }

        private uint ExtractSignedBits(uint value, uint width) =>
            Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.BitFieldSExtract,
                    _intType,
                    Bitcast(_intType, value),
                    UInt(0),
                    UInt(width)));

        private uint ExtractUnsignedBits(uint value, uint offset, uint width) =>
            _module.AddInstruction(
                SpirvOp.BitFieldUExtract,
                _uintType,
                value,
                UInt(offset),
                UInt(width));

        private uint ClampSigned16(uint value, uint minimum, uint maximum)
        {
            var signed = Bitcast(_intType, value);
            var clamped = Ext(
                42,
                _intType,
                Ext(39, _intType, signed, maximum),
                minimum);
            return Bitcast(_uintType, clamped);
        }

        private uint EmitFloatClassCondition(Gen5ShaderInstruction instruction)
        {
            var isFloat64 = instruction.Opcode.EndsWith(
                "F64",
                StringComparison.Ordinal);
            var isFloat16 = instruction.Opcode.EndsWith(
                "F16",
                StringComparison.Ordinal);
            var integerType = isFloat64 ? _ulongType : _uintType;
            var raw = isFloat64
                ? GetRawSource64(instruction, 0)
                : isFloat16
                    ? GetInteger16Source(instruction, 0, signed: false)
                    : GetRawSource(instruction, 0);
            var mask = GetRawSource(instruction, 1);
            var signMask = isFloat64
                ? 0x8000_0000_0000_0000UL
                : isFloat16
                    ? 0x8000UL
                    : 0x8000_0000UL;
            var exponentMask = isFloat64
                ? 0x7FF0_0000_0000_0000UL
                : isFloat16
                    ? 0x7C00UL
                    : 0x7F80_0000UL;
            var mantissaMask = isFloat64
                ? 0x000F_FFFF_FFFF_FFFFUL
                : isFloat16
                    ? 0x03FFUL
                    : 0x007F_FFFFUL;
            var quietMask = isFloat64
                ? 0x0008_0000_0000_0000UL
                : isFloat16
                    ? 0x0200UL
                    : 0x0040_0000UL;

            uint Constant(ulong value) => isFloat64
                ? _module.Constant64(_ulongType, value)
                : UInt((uint)value);

            uint IntegerAnd(uint left, ulong right) => _module.AddInstruction(
                SpirvOp.BitwiseAnd,
                integerType,
                left,
                Constant(right));

            uint IsSet(uint value) => isFloat64
                ? IsNotZero64(value)
                : IsNotZero(value);

            uint Equal(uint value, ulong expected) => _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                value,
                Constant(expected));

            uint Not(uint value) => _module.AddInstruction(
                SpirvOp.LogicalNot,
                _boolType,
                value);

            uint And(uint left, uint right) => _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                left,
                right);

            uint Or(uint left, uint right) => _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                left,
                right);

            if (instruction.Control is Gen5Vop3Control control)
            {
                if ((control.AbsoluteMask & 1) != 0)
                {
                    raw = IntegerAnd(raw, signMask - 1);
                }

                if ((control.NegateMask & 1) != 0)
                {
                    raw = _module.AddInstruction(
                        SpirvOp.BitwiseXor,
                        integerType,
                        raw,
                        Constant(signMask));
                }
            }

            var negative = IsSet(IntegerAnd(raw, signMask));
            var positive = Not(negative);
            var exponent = IntegerAnd(raw, exponentMask);
            var mantissa = IntegerAnd(raw, mantissaMask);
            var exponentAllOnes = Equal(exponent, exponentMask);
            var exponentZero = Equal(exponent, 0);
            var mantissaZero = Equal(mantissa, 0);
            var nan = And(exponentAllOnes, Not(mantissaZero));
            var quiet = IsSet(IntegerAnd(mantissa, quietMask));
            var signalingNan = And(nan, Not(quiet));
            var quietNan = And(nan, quiet);
            var infinity = And(exponentAllOnes, mantissaZero);
            var zero = And(exponentZero, mantissaZero);
            var subnormal = And(exponentZero, Not(mantissaZero));
            var normal = And(Not(exponentZero), Not(exponentAllOnes));

            uint MaskedClass(uint bits, uint value) => And(
                IsNotZero(BitwiseAnd(mask, UInt(bits))),
                value);

            uint SignedClass(uint negativeBit, uint positiveBit, uint value) => Or(
                MaskedClass(negativeBit, And(negative, value)),
                MaskedClass(positiveBit, And(positive, value)));

            var condition = Or(
                MaskedClass(0x001, signalingNan),
                MaskedClass(0x002, quietNan));
            condition = Or(condition, SignedClass(0x004, 0x200, infinity));
            condition = Or(condition, SignedClass(0x008, 0x100, normal));
            condition = Or(condition, SignedClass(0x010, 0x080, subnormal));
            return Or(condition, SignedClass(0x020, 0x040, zero));
        }

        private bool TryEmitVectorCompare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Destinations.Count != 1 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = "missing vector compare mask destination";
                return false;
            }

            var destination = instruction.Destinations[0].Value;
            uint condition = _module.ConstantBool(false);
            var opcode = instruction.Opcode;
            var predicateOpcode = opcode;
            if (opcode.EndsWith("F64", StringComparison.Ordinal) ||
                opcode.EndsWith("F16", StringComparison.Ordinal))
            {
                predicateOpcode = opcode[..^3] + "F32";
            }
            else if (opcode.EndsWith("I64", StringComparison.Ordinal) ||
                opcode.EndsWith("I16", StringComparison.Ordinal))
            {
                predicateOpcode = opcode[..^3] + "I32";
            }
            else if (opcode.EndsWith("U64", StringComparison.Ordinal) ||
                opcode.EndsWith("U16", StringComparison.Ordinal))
            {
                predicateOpcode = opcode[..^3] + "U32";
            }
            if (predicateOpcode is "VCmpClassF32" or "VCmpxClassF32")
            {
                condition = EmitFloatClassCondition(instruction);
            }
            else if (predicateOpcode is "VCmpFF32" or "VCmpxFF32" or
                     "VCmpFI32" or "VCmpxFI32" or
                     "VCmpFU32" or "VCmpxFU32")
            {
                condition = _module.ConstantBool(false);
            }
            else if (predicateOpcode is "VCmpTruF32" or "VCmpxTruF32" or
                     "VCmpTI32" or "VCmpxTI32" or
                     "VCmpTU32" or "VCmpxTU32")
            {
                condition = _module.ConstantBool(true);
            }
            else if (predicateOpcode is not ("VCmpClassF32" or "VCmpxClassF32") &&
                     (opcode.EndsWith("F32", StringComparison.Ordinal) ||
                      opcode.EndsWith("F64", StringComparison.Ordinal) ||
                      opcode.EndsWith("F16", StringComparison.Ordinal)))
            {
                var isFloat64 = opcode.EndsWith("F64", StringComparison.Ordinal);
                var isFloat16 = opcode.EndsWith("F16", StringComparison.Ordinal);
                var left = isFloat64
                    ? GetDoubleSource(instruction, 0)
                    : isFloat16
                        ? GetFloat16Source(instruction, 0)
                        : GetFloatSource(instruction, 0);
                var right = isFloat64
                    ? GetDoubleSource(instruction, 1)
                    : isFloat16
                        ? GetFloat16Source(instruction, 1)
                        : GetFloatSource(instruction, 1);
                var operation = predicateOpcode switch
                {
                    "VCmpLtF32" or "VCmpxLtF32" => SpirvOp.FOrdLessThan,
                    "VCmpEqF32" or "VCmpxEqF32" => SpirvOp.FOrdEqual,
                    "VCmpLeF32" or "VCmpxLeF32" => SpirvOp.FOrdLessThanEqual,
                    "VCmpGtF32" or "VCmpxGtF32" => SpirvOp.FOrdGreaterThan,
                    "VCmpLgF32" or "VCmpxLgF32" => SpirvOp.FOrdNotEqual,
                    "VCmpGeF32" or "VCmpxGeF32" => SpirvOp.FOrdGreaterThanEqual,
                    "VCmpNlgF32" or "VCmpxNlgF32" => SpirvOp.FUnordEqual,
                    "VCmpNeqF32" or "VCmpxNeqF32" => SpirvOp.FUnordNotEqual,
                    "VCmpNltF32" or "VCmpxNltF32" => SpirvOp.FUnordGreaterThanEqual,
                    "VCmpNleF32" or "VCmpxNleF32" => SpirvOp.FUnordGreaterThan,
                    "VCmpNgtF32" or "VCmpxNgtF32" => SpirvOp.FUnordLessThanEqual,
                    "VCmpNgeF32" or "VCmpxNgeF32" => SpirvOp.FUnordLessThan,
                    _ => SpirvOp.Nop,
                };
                if (predicateOpcode is "VCmpOF32" or "VCmpxOF32" or
                    "VCmpUF32" or "VCmpxUF32")
                {
                    var unordered = _module.AddInstruction(
                        SpirvOp.LogicalOr,
                        _boolType,
                        _module.AddInstruction(SpirvOp.IsNan, _boolType, left),
                        _module.AddInstruction(SpirvOp.IsNan, _boolType, right));
                    condition = predicateOpcode is "VCmpUF32" or "VCmpxUF32"
                        ? unordered
                        : _module.AddInstruction(
                            SpirvOp.LogicalNot,
                            _boolType,
                            unordered);
                }
                else if (operation == SpirvOp.Nop)
                {
                    error = $"unsupported float compare {opcode}";
                    return false;
                }
                else
                {
                    condition = _module.AddInstruction(operation, _boolType, left, right);
                }
            }
            else if (predicateOpcode is not ("VCmpClassF32" or "VCmpxClassF32"))
            {
                var signed = opcode.EndsWith("I32", StringComparison.Ordinal) ||
                    opcode.EndsWith("I64", StringComparison.Ordinal) ||
                    opcode.EndsWith("I16", StringComparison.Ordinal);
                var is64Bit = opcode.EndsWith("I64", StringComparison.Ordinal) ||
                    opcode.EndsWith("U64", StringComparison.Ordinal);
                var is16Bit = opcode.EndsWith("I16", StringComparison.Ordinal) ||
                    opcode.EndsWith("U16", StringComparison.Ordinal);
                var left = is64Bit
                    ? GetInteger64Source(instruction, 0, signed)
                    : is16Bit
                        ? GetInteger16Source(instruction, 0, signed)
                        : GetRawSource(instruction, 0);
                var right = is64Bit
                    ? GetInteger64Source(instruction, 1, signed)
                    : is16Bit
                        ? GetInteger16Source(instruction, 1, signed)
                        : GetRawSource(instruction, 1);
                if (signed && !is64Bit && !is16Bit)
                {
                    left = Bitcast(_intType, left);
                    right = Bitcast(_intType, right);
                }

                var operation = predicateOpcode switch
                {
                    "VCmpEqI32" or "VCmpxEqI32" or
                    "VCmpEqU32" or "VCmpxEqU32" => SpirvOp.IEqual,
                    "VCmpNeI32" or "VCmpxNeI32" or
                    "VCmpNeU32" or "VCmpxNeU32" => SpirvOp.INotEqual,
                    "VCmpLtI32" or "VCmpxLtI32" => SpirvOp.SLessThan,
                    "VCmpLeI32" or "VCmpxLeI32" => SpirvOp.SLessThanEqual,
                    "VCmpGtI32" or "VCmpxGtI32" => SpirvOp.SGreaterThan,
                    "VCmpGeI32" or "VCmpxGeI32" => SpirvOp.SGreaterThanEqual,
                    "VCmpLtU32" or "VCmpxLtU32" => SpirvOp.ULessThan,
                    "VCmpLeU32" or "VCmpxLeU32" => SpirvOp.ULessThanEqual,
                    "VCmpGtU32" or "VCmpxGtU32" => SpirvOp.UGreaterThan,
                    "VCmpGeU32" or "VCmpxGeU32" => SpirvOp.UGreaterThanEqual,
                    _ => SpirvOp.Nop,
                };
                if (operation == SpirvOp.Nop)
                {
                    error = $"unsupported integer compare {opcode}";
                    return false;
                }

                condition = _module.AddInstruction(operation, _boolType, left, right);
            }

            if (opcode.StartsWith("VCmpx", StringComparison.Ordinal))
            {
                var active = _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    Load(_boolType, _exec),
                    condition);
                StoreWaveMask(destination, active);
            }
            else
            {
                StoreWaveMask(destination, condition);
            }

            return true;
        }

        private bool TryEmitScalarAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Encoding == Gen5ShaderEncoding.Sopc)
            {
                return TryEmitScalarCompare(instruction, out error);
            }

            if (instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = "missing scalar destination";
                return false;
            }

            var destination = instruction.Destinations[0].Value;
            if (instruction.Opcode.StartsWith("SMovrel", StringComparison.Ordinal))
            {
                return TryEmitRelativeScalarMove(instruction, destination, out error);
            }

            if (instruction.Encoding == Gen5ShaderEncoding.Sopk)
            {
                var immediate = unchecked((uint)(short)(instruction.Words[0] & 0xFFFF));
                if (instruction.Opcode.StartsWith("SCmpk", StringComparison.Ordinal))
                {
                    return TryEmitScalarCompareK(instruction, destination, immediate, out error);
                }

                var current = LoadS(destination);
                var value = instruction.Opcode switch
                {
                    "SMovkI32" => UInt(immediate),
                    "SCmovkI32" => _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        Load(_boolType, _scc),
                        UInt(immediate),
                        current),
                    "SAddkI32" => IAdd(current, UInt(immediate)),
                    "SMulkI32" => _module.AddInstruction(
                        SpirvOp.IMul,
                        _uintType,
                        current,
                        UInt(immediate)),
                    _ => 0u,
                };
                if (value == 0)
                {
                    error = $"unsupported scalar immediate {instruction.Opcode}";
                    return false;
                }

                StoreS(destination, value);
                return true;
            }

            if (instruction.Opcode == "SGetpcB64")
            {
                var pc = _state.Program.Address +
                    instruction.Pc +
                    (ulong)(instruction.Words.Count * sizeof(uint));
                StoreS(destination, UInt((uint)pc));
                StoreS(destination + 1, UInt((uint)(pc >> 32)));
                return true;
            }

            if (instruction.Opcode is
                "SBcnt0I32B64" or
                "SBcnt1I32B64" or
                "SFF0I32B64" or
                "SFF1I32B64" or
                "SFlbitI32B64" or
                "SFlbitI32I64")
            {
                return TryEmitScalar64Result32(instruction, destination, out error);
            }

            if (instruction.Opcode == "SBitreplicateB64B32")
            {
                StoreS64(destination, EmitBitReplicate64(GetRawSource(instruction, 0)));
                return true;
            }

            if (instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                instruction.Opcode.Contains("Wrexec", StringComparison.Ordinal))
            {
                return TryEmitScalarExecMask(instruction, destination, out error);
            }

            if (instruction.Opcode.EndsWith("B64", StringComparison.Ordinal) ||
                instruction.Opcode is "SWqmB64" or "SBfeU64" or "SBfeI64" or "SAshrI64")
            {
                return TryEmitScalar64(instruction, destination, out error);
            }

            var left = GetRawSource(instruction, 0);
            uint result;
            switch (instruction.Opcode)
            {
                case "SMovB32":
                    result = left;
                    break;
                case "SCmovB32":
                    result = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        Load(_boolType, _scc),
                        left,
                        LoadS(destination));
                    break;
                case "SNotB32":
                    result = _module.AddInstruction(SpirvOp.Not, _uintType, left);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SWqmB32":
                    result = WholeQuadMode(left, _uintType, UInt(0x1111_1111), UInt(15));
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SBrevB32":
                    result = _module.AddInstruction(SpirvOp.BitReverse, _uintType, left);
                    StoreS(destination, result);
                    return true;
                case "SBcnt0I32B32":
                    result = _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        UInt(32),
                        _module.AddInstruction(SpirvOp.BitCount, _uintType, left));
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SBcnt1I32B32":
                    result = _module.AddInstruction(SpirvOp.BitCount, _uintType, left);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SFF0I32B32":
                    result = Ext(
                        73,
                        _uintType,
                        _module.AddInstruction(SpirvOp.Not, _uintType, left));
                    StoreS(destination, result);
                    return true;
                case "SFF1I32B32":
                    result = Ext(73, _uintType, left);
                    StoreS(destination, result);
                    return true;
                case "SFlbitI32B32":
                    result = EmitLeadingBitCount(left, _uintType, UInt(31));
                    StoreS(destination, result);
                    return true;
                case "SFlbitI32":
                    result = EmitSignedLeadingBitCount(left, _uintType, UInt(31));
                    StoreS(destination, result);
                    return true;
                case "SSextI32I8":
                case "SSextI32I16":
                    result = _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _uintType,
                        left,
                        UInt(0),
                        UInt(instruction.Opcode == "SSextI32I8" ? 8u : 16u));
                    StoreS(destination, result);
                    return true;
                case "SQuadmaskB32":
                    result = EmitQuadMask(left, _uintType, 8);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SBitset0B32":
                case "SBitset1B32":
                    result = _module.AddInstruction(
                        SpirvOp.BitFieldInsert,
                        _uintType,
                        LoadS(destination),
                        UInt(instruction.Opcode == "SBitset1B32" ? 1u : 0u),
                        BitwiseAnd(left, UInt(31)),
                        UInt(1));
                    StoreS(destination, result);
                    return true;
                case "SAbsI32":
                    result = Ext(5, _intType, Bitcast(_intType, left));
                    result = Bitcast(_uintType, result);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                default:
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = $"missing scalar source for {instruction.Opcode}";
                        return false;
                    }

                    var right = GetRawSource(instruction, 1);
                    switch (instruction.Opcode)
                    {
                        case "SAddU32":
                            result = IAdd(left, right);
                            Store(_scc, _module.AddInstruction(
                                SpirvOp.ULessThan,
                                _boolType,
                                result,
                                left));
                            break;
                        case "SSubU32":
                            result = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                left,
                                right);
                            Store(_scc, _module.AddInstruction(
                                SpirvOp.UGreaterThan,
                                _boolType,
                                right,
                                left));
                            break;
                        case "SAddI32":
                            result = IAdd(left, right);
                            Store(_scc, SignedAddOverflow(left, right, result));
                            break;
                        case "SSubI32":
                            result = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                left,
                                right);
                            Store(_scc, SignedSubOverflow(left, right, result));
                            break;
                        case "SAddcU32":
                        {
                            var carryIn = _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                Load(_boolType, _scc),
                                UInt(1),
                                UInt(0));
                            var partial = IAdd(left, right);
                            result = IAdd(partial, carryIn);
                            var firstCarry = _module.AddInstruction(
                                SpirvOp.ULessThan,
                                _boolType,
                                partial,
                                left);
                            var secondCarry = _module.AddInstruction(
                                SpirvOp.ULessThan,
                                _boolType,
                                result,
                                partial);
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.LogicalOr,
                                    _boolType,
                                    firstCarry,
                                    secondCarry));
                            break;
                        }
                        case "SSubbU32":
                        {
                            var borrow = _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                Load(_boolType, _scc),
                                UInt(1),
                                UInt(0));
                            var partial = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                left,
                                right);
                            result = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                partial,
                                borrow);
                            var firstBorrow = _module.AddInstruction(
                                SpirvOp.UGreaterThan,
                                _boolType,
                                right,
                                left);
                            var secondBorrow = _module.AddInstruction(
                                SpirvOp.LogicalAnd,
                                _boolType,
                                _module.AddInstruction(
                                    SpirvOp.IEqual,
                                    _boolType,
                                    borrow,
                                    UInt(1)),
                                _module.AddInstruction(
                                    SpirvOp.IEqual,
                                    _boolType,
                                    right,
                                    left));
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.LogicalOr,
                                    _boolType,
                                    firstBorrow,
                                    secondBorrow));
                            break;
                        }
                        case "SMulI32":
                            result = _module.AddInstruction(
                                SpirvOp.IMul,
                                _uintType,
                                left,
                                right);
                            break;
                        case "SMulHiU32":
                        {
                            var product = _module.AddInstruction(
                                SpirvOp.IMul,
                                _ulongType,
                                _module.AddInstruction(
                                    SpirvOp.UConvert,
                                    _ulongType,
                                    left),
                                _module.AddInstruction(
                                    SpirvOp.UConvert,
                                    _ulongType,
                                    right));
                            result = _module.AddInstruction(
                                SpirvOp.UConvert,
                                _uintType,
                                ShiftRightLogical64(
                                    product,
                                    _module.Constant64(_ulongType, 32)));
                            break;
                        }
                        case "SMulHiI32":
                        {
                            var product = _module.AddInstruction(
                                SpirvOp.IMul,
                                _longType,
                                _module.AddInstruction(
                                    SpirvOp.SConvert,
                                    _longType,
                                    Bitcast(_intType, left)),
                                _module.AddInstruction(
                                    SpirvOp.SConvert,
                                    _longType,
                                    Bitcast(_intType, right)));
                            var high = _module.AddInstruction(
                                SpirvOp.ShiftRightArithmetic,
                                _longType,
                                product,
                                _module.Constant64(_ulongType, 32));
                            result = Bitcast(
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.SConvert,
                                    _intType,
                                    high));
                            break;
                        }
                        case "SAbsdiffI32":
                        {
                            var difference = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                left,
                                right);
                            var negative = _module.AddInstruction(
                                SpirvOp.SLessThan,
                                _boolType,
                                Bitcast(_intType, difference),
                                Bitcast(_intType, UInt(0)));
                            result = _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                negative,
                                _module.AddInstruction(
                                    SpirvOp.ISub,
                                    _uintType,
                                    UInt(0),
                                    difference),
                                difference);
                            Store(_scc, IsNotZero(result));
                            break;
                        }
                        case "SPackLlB32B16":
                            result = BitwiseOr(
                                BitwiseAnd(left, UInt(0xFFFF)),
                                ShiftLeftLogical(
                                    BitwiseAnd(right, UInt(0xFFFF)),
                                    UInt(16)));
                            break;
                        case "SPackLhB32B16":
                            result = BitwiseOr(
                                BitwiseAnd(left, UInt(0xFFFF)),
                                BitwiseAnd(right, UInt(0xFFFF_0000)));
                            break;
                        case "SPackHhB32B16":
                            result = BitwiseOr(
                                ShiftRightLogical(left, UInt(16)),
                                BitwiseAnd(right, UInt(0xFFFF_0000)));
                            break;
                        case "SAndB32":
                            result = BitwiseAnd(left, right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SOrB32":
                            result = _module.AddInstruction(
                                SpirvOp.BitwiseOr,
                                _uintType,
                                left,
                                right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SXorB32":
                            result = _module.AddInstruction(
                                SpirvOp.BitwiseXor,
                                _uintType,
                                left,
                                right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SAndn2B32":
                            result = BitwiseAnd(
                                left,
                                _module.AddInstruction(SpirvOp.Not, _uintType, right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SOrn2B32":
                            result = _module.AddInstruction(
                                SpirvOp.BitwiseOr,
                                _uintType,
                                left,
                                _module.AddInstruction(SpirvOp.Not, _uintType, right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SNandB32":
                            result = _module.AddInstruction(
                                SpirvOp.Not,
                                _uintType,
                                BitwiseAnd(left, right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SNorB32":
                            result = _module.AddInstruction(
                                SpirvOp.Not,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.BitwiseOr,
                                    _uintType,
                                    left,
                                    right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SXnorB32":
                            result = _module.AddInstruction(
                                SpirvOp.Not,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.BitwiseXor,
                                    _uintType,
                                    left,
                                    right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SLshlB32":
                            result = ShiftLeftLogical(left, right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SLshrB32":
                            result = ShiftRightLogical(
                                left,
                                BitwiseAnd(right, UInt(31)));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SAshrI32":
                            result = ShiftRightArithmetic(left, right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SBfmB32":
                            result = _module.AddInstruction(
                                SpirvOp.BitFieldInsert,
                                _uintType,
                                UInt(0),
                                UInt(uint.MaxValue),
                                BitwiseAnd(right, UInt(31)),
                                BitwiseAnd(left, UInt(31)));
                            break;
                        case "SBfeU32":
                        case "SBfeI32":
                        {
                            var offset = BitwiseAnd(right, UInt(31));
                            var requestedWidth = BitwiseAnd(
                                ShiftRightLogical(right, UInt(16)),
                                UInt(0x7F));
                            var remaining = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                UInt(32),
                                offset);
                            var width = Ext(
                                38,
                                _uintType,
                                requestedWidth,
                                remaining);
                            result = instruction.Opcode == "SBfeI32"
                                ? Bitcast(
                                    _uintType,
                                    _module.AddInstruction(
                                        SpirvOp.BitFieldSExtract,
                                        _intType,
                                        Bitcast(_intType, left),
                                        offset,
                                        width))
                                : _module.AddInstruction(
                                    SpirvOp.BitFieldUExtract,
                                    _uintType,
                                    left,
                                    offset,
                                    width);
                            Store(_scc, IsNotZero(result));
                            break;
                        }
                        case "SCselectB32":
                            result = _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                Load(_boolType, _scc),
                                left,
                                right);
                            break;
                        case "SMinU32":
                            result = Ext(38, _uintType, left, right);
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.ULessThan,
                                    _boolType,
                                    left,
                                    right));
                            break;
                        case "SMinI32":
                            result = Bitcast(
                                _uintType,
                                Ext(39, _intType, Bitcast(_intType, left), Bitcast(_intType, right)));
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.SLessThan,
                                    _boolType,
                                    Bitcast(_intType, left),
                                    Bitcast(_intType, right)));
                            break;
                        case "SMaxU32":
                            result = Ext(41, _uintType, left, right);
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.UGreaterThan,
                                    _boolType,
                                    left,
                                    right));
                            break;
                        case "SMaxI32":
                            result = Bitcast(
                                _uintType,
                                Ext(42, _intType, Bitcast(_intType, left), Bitcast(_intType, right)));
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.SGreaterThan,
                                    _boolType,
                                    Bitcast(_intType, left),
                                    Bitcast(_intType, right)));
                            break;
                        case "SLshl1AddU32":
                        case "SLshl2AddU32":
                        case "SLshl3AddU32":
                        case "SLshl4AddU32":
                        {
                            var shift = (uint)(instruction.Opcode[5] - '0');
                            var wideResult = _module.AddInstruction(
                                SpirvOp.IAdd,
                                _ulongType,
                                ShiftLeftLogical64(
                                    _module.AddInstruction(
                                        SpirvOp.UConvert,
                                        _ulongType,
                                        left),
                                    _module.Constant64(_ulongType, shift)),
                                _module.AddInstruction(
                                    SpirvOp.UConvert,
                                    _ulongType,
                                    right));
                            result = _module.AddInstruction(
                                SpirvOp.UConvert,
                                _uintType,
                                wideResult);
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.UGreaterThan,
                                    _boolType,
                                    wideResult,
                                    _module.Constant64(_ulongType, uint.MaxValue)));
                            break;
                        }
                        default:
                            error = $"unsupported scalar opcode {instruction.Opcode}";
                            return false;
                    }

                    break;
                }
            }

            StoreS(destination, result);
            return true;
        }

        private bool TryEmitRelativeScalarMove(
            Gen5ShaderInstruction instruction,
            uint destination,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count == 0 ||
                instruction.Sources[0].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = $"invalid scalar relative source {instruction.Opcode}";
                return false;
            }

            var source = instruction.Sources[0].Value;
            var m0 = LoadS(M0Register);
            var zero = UInt(0);
            uint sourceOffset = zero;
            uint destinationOffset = zero;
            if (instruction.Opcode is "SMovrelsB32" or "SMovrelsB64")
            {
                sourceOffset = m0;
            }
            else if (instruction.Opcode is "SMovreldB32" or "SMovreldB64")
            {
                destinationOffset = m0;
            }
            else if (instruction.Opcode == "SMovrelsd2B32")
            {
                sourceOffset = BitwiseAnd(m0, UInt(0x3FF));
                destinationOffset = BitwiseAnd(
                    ShiftRightLogical(m0, UInt(16)),
                    UInt(0x3FF));
            }
            else
            {
                error = $"unsupported scalar relative move {instruction.Opcode}";
                return false;
            }

            var sourceIndex = IAdd(UInt(source), sourceOffset);
            var destinationIndex = IAdd(UInt(destination), destinationOffset);
            var relativeSource = instruction.Opcode is
                "SMovrelsB32" or "SMovrelsB64" or "SMovrelsd2B32";
            var relativeDestination = instruction.Opcode is
                "SMovreldB32" or "SMovreldB64" or "SMovrelsd2B32";
            if (instruction.Opcode.EndsWith("B64", StringComparison.Ordinal))
            {
                var value = relativeSource
                    ? LoadS64At(sourceIndex)
                    : GetRawSource64(instruction, 0);
                if (relativeDestination)
                {
                    StoreS64At(destinationIndex, value);
                }
                else
                {
                    StoreS64(destination, value);
                }

                return true;
            }

            var value32 = relativeSource
                ? LoadSAt(sourceIndex)
                : GetRawSource(instruction, 0);
            if (relativeDestination)
            {
                StoreSAt(destinationIndex, value32);
            }
            else
            {
                StoreS(destination, value32);
            }

            return true;
        }

        private bool TryEmitScalarCompare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 2)
            {
                error = "missing scalar compare source";
                return false;
            }

            var left = GetRawSource(instruction, 0);
            var right = GetRawSource(instruction, 1);
            if (instruction.Opcode is "SBitcmp0B32" or "SBitcmp1B32")
            {
                var shifted = ShiftRightLogical(
                    left,
                    BitwiseAnd(right, UInt(31)));
                var isSet = IsNotZero(BitwiseAnd(shifted, UInt(1)));
                Store(
                    _scc,
                    instruction.Opcode == "SBitcmp1B32"
                        ? isSet
                        : _module.AddInstruction(
                            SpirvOp.LogicalNot,
                            _boolType,
                            isSet));
                return true;
            }

            if (instruction.Opcode is "SBitcmp0B64" or "SBitcmp1B64")
            {
                var bit = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    BitwiseAnd(right, UInt(63)));
                var shifted = ShiftRightLogical64(GetRawSource64(instruction, 0), bit);
                var isSet = IsNotZero64(
                    _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        shifted,
                        _module.Constant64(_ulongType, 1)));
                Store(
                    _scc,
                    instruction.Opcode == "SBitcmp1B64"
                        ? isSet
                        : _module.AddInstruction(
                            SpirvOp.LogicalNot,
                            _boolType,
                            isSet));
                return true;
            }

            if (instruction.Opcode is "SCmpEqU64" or "SCmpLgU64")
            {
                var operation64 = instruction.Opcode == "SCmpEqU64"
                    ? SpirvOp.IEqual
                    : SpirvOp.INotEqual;
                Store(
                    _scc,
                    _module.AddInstruction(
                        operation64,
                        _boolType,
                        GetRawSource64(instruction, 0),
                        GetRawSource64(instruction, 1)));
                return true;
            }

            var operation = instruction.Opcode switch
            {
                "SCmpEqI32" or "SCmpEqU32" => SpirvOp.IEqual,
                "SCmpLgI32" or "SCmpLgU32" => SpirvOp.INotEqual,
                "SCmpGtI32" => SpirvOp.SGreaterThan,
                "SCmpGeI32" => SpirvOp.SGreaterThanEqual,
                "SCmpLtI32" => SpirvOp.SLessThan,
                "SCmpLeI32" => SpirvOp.SLessThanEqual,
                "SCmpGtU32" => SpirvOp.UGreaterThan,
                "SCmpGeU32" => SpirvOp.UGreaterThanEqual,
                "SCmpLtU32" => SpirvOp.ULessThan,
                "SCmpLeU32" => SpirvOp.ULessThanEqual,
                _ => SpirvOp.Nop,
            };
            if (operation == SpirvOp.Nop)
            {
                error = $"unsupported scalar compare {instruction.Opcode}";
                return false;
            }

            if (instruction.Opcode.EndsWith("I32", StringComparison.Ordinal))
            {
                left = Bitcast(_intType, left);
                right = Bitcast(_intType, right);
            }

            Store(_scc, _module.AddInstruction(operation, _boolType, left, right));
            return true;
        }

        private bool TryEmitScalarCompareK(
            Gen5ShaderInstruction instruction,
            uint destination,
            uint immediate,
            out string error)
        {
            error = string.Empty;
            var left = LoadS(destination);
            var right = UInt(immediate);
            var operation = instruction.Opcode switch
            {
                "SCmpkEqI32" or "SCmpkEqU32" => SpirvOp.IEqual,
                "SCmpkLgI32" or "SCmpkLgU32" => SpirvOp.INotEqual,
                "SCmpkGtI32" => SpirvOp.SGreaterThan,
                "SCmpkGeI32" => SpirvOp.SGreaterThanEqual,
                "SCmpkLtI32" => SpirvOp.SLessThan,
                "SCmpkLeI32" => SpirvOp.SLessThanEqual,
                "SCmpkGtU32" => SpirvOp.UGreaterThan,
                "SCmpkGeU32" => SpirvOp.UGreaterThanEqual,
                "SCmpkLtU32" => SpirvOp.ULessThan,
                "SCmpkLeU32" => SpirvOp.ULessThanEqual,
                _ => SpirvOp.Nop,
            };
            if (operation == SpirvOp.Nop)
            {
                error = $"unsupported scalar immediate compare {instruction.Opcode}";
                return false;
            }

            if (instruction.Opcode.EndsWith("I32", StringComparison.Ordinal))
            {
                left = Bitcast(_intType, left);
                right = Bitcast(_intType, right);
            }

            Store(_scc, _module.AddInstruction(operation, _boolType, left, right));
            return true;
        }

        private bool TryEmitScalar64Result32(
            Gen5ShaderInstruction instruction,
            uint destination,
            out string error)
        {
            error = string.Empty;
            var source = GetRawSource64(instruction, 0);
            uint result;
            if (instruction.Opcode is "SBcnt0I32B64" or "SBcnt1I32B64")
            {
                result = EmitBitCount64(source);
                if (instruction.Opcode == "SBcnt0I32B64")
                {
                    result = _module.AddInstruction(SpirvOp.ISub, _uintType, UInt(64), result);
                }

                StoreS(destination, result);
                Store(_scc, IsNotZero(result));
                return true;
            }

            if (instruction.Opcode is "SFF0I32B64" or "SFF1I32B64")
            {
                if (instruction.Opcode == "SFF0I32B64")
                {
                    source = _module.AddInstruction(SpirvOp.Not, _ulongType, source);
                }

                result = EmitFirstSetBit64(source);
                StoreS(destination, result);
                return true;
            }

            result = instruction.Opcode == "SFlbitI32B64"
                ? EmitLeadingBitCount64(source)
                : EmitSignedLeadingBitCount64(source);
            StoreS(destination, result);
            return true;
        }

        private bool TryEmitScalarExecMask(
            Gen5ShaderInstruction instruction,
            uint destination,
            out string error)
        {
            error = string.Empty;
            var isSaveExec = instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal);
            var is64Bit = instruction.Opcode.EndsWith("B64", StringComparison.Ordinal);
            if (is64Bit)
            {
                var source = GetRawSource64(instruction, 0);
                var oldExec = BooleanToLaneMask(Load(_boolType, _exec));
                var notSource = _module.AddInstruction(SpirvOp.Not, _ulongType, source);
                var notOldExec = _module.AddInstruction(SpirvOp.Not, _ulongType, oldExec);
                var newExec = instruction.Opcode switch
                {
                    "SAndSaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd, _ulongType, oldExec, source),
                    "SOrSaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr, _ulongType, oldExec, source),
                    "SXorSaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseXor, _ulongType, oldExec, source),
                    "SAndn1SaveexecB64" or "SAndn1WrexecB64" =>
                        _module.AddInstruction(
                            SpirvOp.BitwiseAnd, _ulongType, notSource, oldExec),
                    "SAndn2SaveexecB64" or "SAndn2WrexecB64" =>
                        _module.AddInstruction(
                            SpirvOp.BitwiseAnd, _ulongType, source, notOldExec),
                    "SOrn1SaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr, _ulongType, notSource, oldExec),
                    "SOrn2SaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr, _ulongType, source, notOldExec),
                    "SNandSaveexecB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseAnd, _ulongType, source, oldExec)),
                    "SNorSaveexecB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseOr, _ulongType, source, oldExec)),
                    "SXnorSaveexecB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseXor, _ulongType, oldExec, source)),
                    _ => 0u,
                };
                if (newExec == 0)
                {
                    error = $"unsupported scalar EXEC-mask opcode {instruction.Opcode}";
                    return false;
                }

                StoreS64(destination, isSaveExec ? oldExec : newExec);
                StoreS64(126, newExec);
                Store(_scc, IsNotZero64(newExec));
                return true;
            }

            var source32 = GetRawSource(instruction, 0);
            var oldExec32 = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                BooleanToLaneMask(Load(_boolType, _exec)));
            var notSource32 = _module.AddInstruction(SpirvOp.Not, _uintType, source32);
            var notOldExec32 = _module.AddInstruction(SpirvOp.Not, _uintType, oldExec32);
            var newExec32 = instruction.Opcode switch
            {
                "SAndSaveexecB32" => BitwiseAnd(oldExec32, source32),
                "SOrSaveexecB32" => BitwiseOr(oldExec32, source32),
                "SXorSaveexecB32" => BitwiseXor(oldExec32, source32),
                "SAndn1SaveexecB32" or "SAndn1WrexecB32" =>
                    BitwiseAnd(notSource32, oldExec32),
                "SAndn2SaveexecB32" or "SAndn2WrexecB32" =>
                    BitwiseAnd(source32, notOldExec32),
                "SOrn1SaveexecB32" => BitwiseOr(notSource32, oldExec32),
                "SOrn2SaveexecB32" => BitwiseOr(source32, notOldExec32),
                "SNandSaveexecB32" => _module.AddInstruction(
                    SpirvOp.Not,
                    _uintType,
                    BitwiseAnd(source32, oldExec32)),
                "SNorSaveexecB32" => _module.AddInstruction(
                    SpirvOp.Not,
                    _uintType,
                    BitwiseOr(source32, oldExec32)),
                "SXnorSaveexecB32" => _module.AddInstruction(
                    SpirvOp.Not,
                    _uintType,
                    BitwiseXor(oldExec32, source32)),
                _ => 0u,
            };
            if (newExec32 == 0)
            {
                error = $"unsupported scalar EXEC-mask opcode {instruction.Opcode}";
                return false;
            }

            StoreS(destination, isSaveExec ? oldExec32 : newExec32);
            StoreS(126, newExec32);
            Store(_scc, IsNotZero(newExec32));
            return true;
        }

        private bool TryEmitScalar64(
            Gen5ShaderInstruction instruction,
            uint destination,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode == "SBfmB64")
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit mask source";
                    return false;
                }

                var width = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    BitwiseAnd(GetRawSource(instruction, 0), UInt(63)));
                var offset = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    BitwiseAnd(GetRawSource(instruction, 1), UInt(63)));
                var one = _module.Constant64(_ulongType, 1);
                var mask = _module.AddInstruction(
                    SpirvOp.ISub,
                    _ulongType,
                    ShiftLeftLogical64(one, width),
                    one);
                StoreS64(destination, ShiftLeftLogical64(mask, offset));
                return true;
            }

            if (instruction.Opcode is "SBitset0B64" or "SBitset1B64")
            {
                var bit = ShiftLeftLogical64(
                    _module.Constant64(_ulongType, 1),
                    _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        BitwiseAnd(GetRawSource(instruction, 0), UInt(63))));
                var bitsetValue = instruction.Opcode == "SBitset1B64"
                    ? _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        LoadS64(destination),
                        bit)
                    : _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        LoadS64(destination),
                        _module.AddInstruction(SpirvOp.Not, _ulongType, bit));
                StoreS64(destination, bitsetValue);
                return true;
            }

            var left = GetRawSource64(instruction, 0);

            if (instruction.Opcode is "SLshlB64" or "SLshrB64" or "SAshrI64")
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit shift source";
                    return false;
                }

                var shift = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    GetRawSource(instruction, 1));
                var shiftedValue = instruction.Opcode switch
                {
                    "SLshlB64" => ShiftLeftLogical64(left, shift),
                    "SLshrB64" => ShiftRightLogical64(left, shift),
                    _ => ShiftRightArithmetic64(left, shift),
                };
                StoreS64(destination, shiftedValue);
                Store(_scc, IsNotZero64(shiftedValue));
                return true;
            }

            if (instruction.Opcode is "SBfeU64" or "SBfeI64")
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit bitfield source";
                    return false;
                }

                var control = GetRawSource(instruction, 1);
                var offset = BitwiseAnd(control, UInt(63));
                var requestedWidth = BitwiseAnd(
                    ShiftRightLogical(control, UInt(16)),
                    UInt(0x7F));
                var remaining = _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    UInt(64),
                    offset);
                var width = Ext(
                    38,
                    _uintType,
                    requestedWidth,
                    remaining);
                var offset64 = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    offset);
                var width64 = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    width);
                var one64 = _module.Constant64(_ulongType, 1);
                var shifted = ShiftRightLogical64(left, offset64);
                var partialMask = _module.AddInstruction(
                    SpirvOp.ISub,
                    _ulongType,
                    ShiftLeftLogical64(one64, width64),
                    one64);
                var fullWidth = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    width,
                    UInt(64));
                var mask = _module.AddInstruction(
                    SpirvOp.Select,
                    _ulongType,
                    fullWidth,
                    _module.Constant64(_ulongType, ulong.MaxValue),
                    partialMask);
                var extracted = _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    shifted,
                    mask);
                if (instruction.Opcode == "SBfeI64")
                {
                    var signShift = _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        width,
                        UInt(1));
                    var signBit = ShiftLeftLogical64(
                        one64,
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _ulongType,
                            signShift));
                    var signExtended = _module.AddInstruction(
                        SpirvOp.ISub,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseXor,
                            _ulongType,
                            extracted,
                            signBit),
                        signBit);
                    extracted = _module.AddInstruction(
                        SpirvOp.Select,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.IEqual,
                            _boolType,
                            width,
                            UInt(0)),
                        _module.Constant64(_ulongType, 0),
                        signExtended);
                }

                StoreS64(destination, extracted);
                Store(_scc, IsNotZero64(extracted));
                return true;
            }

            uint value;
            if (instruction.Opcode == "SMovB64")
            {
                value = left;
            }
            else if (instruction.Opcode == "SCmovB64")
            {
                value = _module.AddInstruction(
                    SpirvOp.Select,
                    _ulongType,
                    Load(_boolType, _scc),
                    left,
                    LoadS64(destination));
            }
            else if (instruction.Opcode == "SNotB64")
            {
                value = _module.AddInstruction(SpirvOp.Not, _ulongType, left);
            }
            else if (instruction.Opcode == "SWqmB64")
            {
                value = WholeQuadMode(
                    left,
                    _ulongType,
                    _module.Constant64(_ulongType, 0x1111_1111_1111_1111UL),
                    _module.Constant64(_ulongType, 15));
            }
            else if (instruction.Opcode == "SBrevB64")
            {
                value = EmitBitReverse64(left);
            }
            else if (instruction.Opcode == "SQuadmaskB64")
            {
                value = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    EmitQuadMask(left, _ulongType, 16));
            }
            else
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit source";
                    return false;
                }

                var right = GetRawSource64(instruction, 1);
                value = instruction.Opcode switch
                {
                    "SAndB64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd, _ulongType, left, right),
                    "SOrB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr, _ulongType, left, right),
                    "SXorB64" => _module.AddInstruction(
                        SpirvOp.BitwiseXor, _ulongType, left, right),
                    "SNandB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseAnd, _ulongType, left, right)),
                    "SNorB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseOr, _ulongType, left, right)),
                    "SXnorB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseXor, _ulongType, left, right)),
                    "SAndn1B64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, left),
                        right),
                    "SAndn2B64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        left,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, right)),
                    "SOrn1B64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, left),
                        right),
                    "SOrn2B64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        left,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, right)),
                    "SCselectB64" => _module.AddInstruction(
                        SpirvOp.Select,
                        _ulongType,
                        Load(_boolType, _scc),
                        left,
                        right),
                    _ => 0,
                };
                if (value == 0)
                {
                    error = $"unsupported scalar 64-bit opcode {instruction.Opcode}";
                    return false;
                }
            }

            StoreS64(destination, value);
            if (instruction.Opcode is
                "SNotB64" or
                "SWqmB64" or
                "SQuadmaskB64" or
                "SAndB64" or
                "SOrB64" or
                "SXorB64" or
                "SAndn1B64" or
                "SAndn2B64" or
                "SOrn1B64" or
                "SOrn2B64" or
                "SNandB64" or
                "SNorB64" or
                "SXnorB64")
            {
                Store(_scc, IsNotZero64(value));
            }

            return true;
        }

        private bool TryEmitPackedAlu(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            out string error)
        {
            error = string.Empty;
            if (!TryGetVectorDestination(instruction, out var destination))
            {
                error = "missing packed vector destination";
                return false;
            }

            uint result;
            if (instruction.Opcode.StartsWith("VPk", StringComparison.Ordinal))
            {
                var isFloat = instruction.Opcode.EndsWith("F16", StringComparison.Ordinal);
                var low = isFloat
                    ? EmitPackedFloatComponent(instruction, control, high: false)
                    : EmitPackedIntegerComponent(instruction, control, high: false);
                var high = isFloat
                    ? EmitPackedFloatComponent(instruction, control, high: true)
                    : EmitPackedIntegerComponent(instruction, control, high: true);
                result = isFloat
                    ? PackHalf2(low, high)
                    : BitwiseOr(
                        BitwiseAnd(low, UInt(0xFFFF)),
                        ShiftLeftLogical(BitwiseAnd(high, UInt(0xFFFF)), UInt(16)));
            }
            else if (instruction.Opcode == "VDot2F32F16")
            {
                result = Bitcast(_uintType, EmitDot2F16(instruction, control));
            }
            else if (instruction.Opcode is
                "VDot2I32I16" or "VDot2U32U16" or
                "VDot4I32I8" or "VDot4U32U8" or
                "VDot8I32I4" or "VDot8U32U4")
            {
                result = EmitIntegerDot(instruction, control);
            }
            else if (instruction.Opcode is
                "VFmaMixF32" or "VFmaMixloF16" or "VFmaMixhiF16")
            {
                var value = Ext(
                    50,
                    _floatType,
                    GetMixFloatSource(instruction, control, 0),
                    GetMixFloatSource(instruction, control, 1),
                    GetMixFloatSource(instruction, control, 2));
                if (control.Clamp)
                {
                    value = Ext(43, _floatType, value, Float(0), Float(1));
                }

                result = instruction.Opcode switch
                {
                    "VFmaMixF32" => Bitcast(_uintType, value),
                    "VFmaMixloF16" => BitwiseAnd(PackHalf2(value, Float(0)), UInt(0xFFFF)),
                    _ => BitwiseAnd(PackHalf2(Float(0), value), UInt(0xFFFF_0000)),
                };
            }
            else
            {
                error = $"unsupported packed opcode {instruction.Opcode}";
                return false;
            }

            StoreV(destination, result);
            return true;
        }

        private uint EmitPackedIntegerComponent(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            bool high)
        {
            var signed = instruction.Opcode is
                "VPkMadI16" or "VPkAddI16" or "VPkSubI16" or
                "VPkAshrrevI16" or "VPkMaxI16" or "VPkMinI16";
            var left = ExtractPackedInteger(instruction, control, 0, high, signed);
            var right = ExtractPackedInteger(instruction, control, 1, high, signed);
            var third = ExtractPackedInteger(instruction, control, 2, high, signed);

            return instruction.Opcode switch
            {
                "VPkMadI16" or "VPkMadU16" => IAdd(
                    _module.AddInstruction(SpirvOp.IMul, _uintType, left, right),
                    third),
                "VPkMulLoU16" => _module.AddInstruction(
                    SpirvOp.IMul,
                    _uintType,
                    left,
                    right),
                "VPkAddI16" or "VPkAddU16" => IAdd(left, right),
                "VPkSubI16" or "VPkSubU16" => _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    left,
                    right),
                "VPkLshlrevB16" => ShiftLeftLogical(right, BitwiseAnd(left, UInt(15))),
                "VPkLshrrevB16" => ShiftRightLogical(right, BitwiseAnd(left, UInt(15))),
                "VPkAshrrevI16" => ShiftRightArithmetic(right, BitwiseAnd(left, UInt(15))),
                "VPkMaxI16" => Bitcast(
                    _uintType,
                    Ext(42, _intType, Bitcast(_intType, left), Bitcast(_intType, right))),
                "VPkMinI16" => Bitcast(
                    _uintType,
                    Ext(39, _intType, Bitcast(_intType, left), Bitcast(_intType, right))),
                "VPkMaxU16" => Ext(41, _uintType, left, right),
                "VPkMinU16" => Ext(38, _uintType, left, right),
                _ => throw new InvalidOperationException(
                    $"unsupported packed integer opcode {instruction.Opcode}"),
            };
        }

        private uint ExtractPackedInteger(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            int sourceIndex,
            bool high,
            bool signed)
        {
            var operand = instruction.Sources[sourceIndex];
            if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                TryDecodeInlineConstant(operand.Value, out var inline))
            {
                return UInt(inline);
            }

            var selectorMask = high ? control.OpSelectHighMask : control.OpSelectMask;
            var offset = (selectorMask & (1u << sourceIndex)) != 0 ? 16u : 0u;
            var operation = signed ? SpirvOp.BitFieldSExtract : SpirvOp.BitFieldUExtract;
            var type = signed ? _intType : _uintType;
            var value = _module.AddInstruction(
                operation,
                type,
                signed
                    ? Bitcast(_intType, GetRawSource(instruction, sourceIndex))
                    : GetRawSource(instruction, sourceIndex),
                UInt(offset),
                UInt(16));
            return signed ? Bitcast(_uintType, value) : value;
        }

        private uint EmitPackedFloatComponent(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            bool high)
        {
            var left = GetPackedFloatSource(instruction, control, 0, high);
            var right = GetPackedFloatSource(instruction, control, 1, high);
            var third = GetPackedFloatSource(instruction, control, 2, high);
            var result = instruction.Opcode switch
            {
                "VPkFmaF16" => Ext(50, _floatType, left, right, third),
                "VPkAddF16" => _module.AddInstruction(
                    SpirvOp.FAdd,
                    _floatType,
                    left,
                    right),
                "VPkMulF16" => _module.AddInstruction(
                    SpirvOp.FMul,
                    _floatType,
                    left,
                    right),
                "VPkMinF16" => Ext(37, _floatType, left, right),
                "VPkMaxF16" => Ext(40, _floatType, left, right),
                _ => throw new InvalidOperationException(
                    $"unsupported packed float opcode {instruction.Opcode}"),
            };
            return control.Clamp
                ? Ext(43, _floatType, result, Float(0), Float(1))
                : result;
        }

        private uint GetPackedFloatSource(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            int sourceIndex,
            bool high)
        {
            if (TryGetInlineFloatSource(instruction, sourceIndex, out var inline))
            {
                var inlineNegateMask = high
                    ? control.NegateHighMask
                    : control.NegateLowMask;
                return (inlineNegateMask & (1u << sourceIndex)) != 0
                    ? _module.AddInstruction(SpirvOp.FNegate, _floatType, inline)
                    : inline;
            }

            var selectorMask = high ? control.OpSelectHighMask : control.OpSelectMask;
            var component = (selectorMask & (1u << sourceIndex)) != 0 ? 1u : 0u;
            var unpacked = Ext(62, _vec2Type, GetRawSource(instruction, sourceIndex));
            var value = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                unpacked,
                component);
            var negateMask = high ? control.NegateHighMask : control.NegateLowMask;
            return (negateMask & (1u << sourceIndex)) != 0
                ? _module.AddInstruction(SpirvOp.FNegate, _floatType, value)
                : value;
        }

        private uint GetMixFloatSource(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            int sourceIndex)
        {
            if (TryGetInlineFloatSource(instruction, sourceIndex, out var inline))
            {
                if ((control.NegateHighMask & (1u << sourceIndex)) != 0)
                {
                    inline = Ext(4, _floatType, inline);
                }

                return (control.NegateLowMask & (1u << sourceIndex)) != 0
                    ? _module.AddInstruction(SpirvOp.FNegate, _floatType, inline)
                    : inline;
            }

            var selector =
                ((control.OpSelectMask >> sourceIndex) & 1) |
                (((control.OpSelectHighMask >> sourceIndex) & 1) << 1);
            uint value;
            if (selector < 2)
            {
                value = Bitcast(_floatType, GetRawSource(instruction, sourceIndex));
            }
            else
            {
                var unpacked = Ext(62, _vec2Type, GetRawSource(instruction, sourceIndex));
                value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    unpacked,
                    selector - 2);
            }

            if ((control.NegateHighMask & (1u << sourceIndex)) != 0)
            {
                value = Ext(4, _floatType, value);
            }

            return (control.NegateLowMask & (1u << sourceIndex)) != 0
                ? _module.AddInstruction(SpirvOp.FNegate, _floatType, value)
                : value;
        }

        private uint EmitDot2F16(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control)
        {
            var leftLow = GetDotHalfSource(instruction, control, 0, high: false);
            var rightLow = GetDotHalfSource(instruction, control, 1, high: false);
            var leftHigh = GetDotHalfSource(instruction, control, 0, high: true);
            var rightHigh = GetDotHalfSource(instruction, control, 1, high: true);
            var addend = TryGetInlineFloatSource(instruction, 2, out var inlineAddend)
                ? inlineAddend
                : Bitcast(_floatType, GetRawSource(instruction, 2));
            if ((control.NegateHighMask & 4) != 0)
            {
                addend = Ext(4, _floatType, addend);
            }

            if ((control.NegateLowMask & 4) != 0)
            {
                addend = _module.AddInstruction(SpirvOp.FNegate, _floatType, addend);
            }

            var result = Ext(50, _floatType, leftLow, rightLow, addend);
            result = Ext(50, _floatType, leftHigh, rightHigh, result);
            return control.Clamp
                ? Ext(43, _floatType, result, Float(0), Float(1))
                : result;
        }

        private uint GetDotHalfSource(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            int sourceIndex,
            bool high)
        {
            if (TryGetInlineFloatSource(instruction, sourceIndex, out var inline))
            {
                var inlineNegateMask = high
                    ? control.NegateHighMask
                    : control.NegateLowMask;
                return (inlineNegateMask & (1u << sourceIndex)) != 0
                    ? _module.AddInstruction(SpirvOp.FNegate, _floatType, inline)
                    : inline;
            }

            var unpacked = Ext(62, _vec2Type, GetRawSource(instruction, sourceIndex));
            var value = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                unpacked,
                high ? 1u : 0u);
            var negateMask = high ? control.NegateHighMask : control.NegateLowMask;
            return (negateMask & (1u << sourceIndex)) != 0
                ? _module.AddInstruction(SpirvOp.FNegate, _floatType, value)
                : value;
        }

        private uint EmitIntegerDot(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control)
        {
            var signed = instruction.Opcode.Contains('I', StringComparison.Ordinal);
            var componentBits = instruction.Opcode.StartsWith("VDot2", StringComparison.Ordinal)
                ? 16u
                : instruction.Opcode.StartsWith("VDot4", StringComparison.Ordinal) ? 8u : 4u;
            var componentCount = 32u / componentBits;
            return signed
                ? EmitSignedIntegerDot(instruction, control.Clamp, componentBits, componentCount)
                : EmitUnsignedIntegerDot(instruction, control.Clamp, componentBits, componentCount);
        }

        private uint EmitSignedIntegerDot(
            Gen5ShaderInstruction instruction,
            bool clamp,
            uint componentBits,
            uint componentCount)
        {
            var addend = Bitcast(_intType, GetRawSource(instruction, 2));
            var accumulator = _module.AddInstruction(SpirvOp.SConvert, _longType, addend);
            for (uint component = 0; component < componentCount; component++)
            {
                var left = ExtractDotComponent(instruction, 0, component, componentBits, signed: true);
                var right = ExtractDotComponent(instruction, 1, component, componentBits, signed: true);
                var left64 = _module.AddInstruction(SpirvOp.SConvert, _longType, left);
                var right64 = _module.AddInstruction(SpirvOp.SConvert, _longType, right);
                accumulator = _module.AddInstruction(
                    SpirvOp.IAdd,
                    _longType,
                    accumulator,
                    _module.AddInstruction(SpirvOp.IMul, _longType, left64, right64));
            }

            if (clamp)
            {
                var min = _module.Constant64(_longType, unchecked((ulong)int.MinValue));
                var max = _module.Constant64(_longType, int.MaxValue);
                accumulator = _module.AddInstruction(
                    SpirvOp.Select,
                    _longType,
                    _module.AddInstruction(SpirvOp.SLessThan, _boolType, accumulator, min),
                    min,
                    accumulator);
                accumulator = _module.AddInstruction(
                    SpirvOp.Select,
                    _longType,
                    _module.AddInstruction(SpirvOp.SGreaterThan, _boolType, accumulator, max),
                    max,
                    accumulator);
            }

            return Bitcast(
                _uintType,
                _module.AddInstruction(SpirvOp.SConvert, _intType, accumulator));
        }

        private uint EmitUnsignedIntegerDot(
            Gen5ShaderInstruction instruction,
            bool clamp,
            uint componentBits,
            uint componentCount)
        {
            var accumulator = _module.AddInstruction(
                SpirvOp.UConvert,
                _ulongType,
                GetRawSource(instruction, 2));
            for (uint component = 0; component < componentCount; component++)
            {
                var left = ExtractDotComponent(instruction, 0, component, componentBits, signed: false);
                var right = ExtractDotComponent(instruction, 1, component, componentBits, signed: false);
                var left64 = _module.AddInstruction(SpirvOp.UConvert, _ulongType, left);
                var right64 = _module.AddInstruction(SpirvOp.UConvert, _ulongType, right);
                accumulator = _module.AddInstruction(
                    SpirvOp.IAdd,
                    _ulongType,
                    accumulator,
                    _module.AddInstruction(SpirvOp.IMul, _ulongType, left64, right64));
            }

            if (clamp)
            {
                var max = _module.Constant64(_ulongType, uint.MaxValue);
                accumulator = _module.AddInstruction(
                    SpirvOp.Select,
                    _ulongType,
                    _module.AddInstruction(SpirvOp.UGreaterThan, _boolType, accumulator, max),
                    max,
                    accumulator);
            }

            return _module.AddInstruction(SpirvOp.UConvert, _uintType, accumulator);
        }

        private uint ExtractDotComponent(
            Gen5ShaderInstruction instruction,
            int sourceIndex,
            uint component,
            uint componentBits,
            bool signed)
        {
            var operand = instruction.Sources[sourceIndex];
            if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                TryDecodeInlineConstant(operand.Value, out var inline))
            {
                return signed ? Bitcast(_intType, UInt(inline)) : UInt(inline);
            }

            var type = signed ? _intType : _uintType;
            return _module.AddInstruction(
                signed ? SpirvOp.BitFieldSExtract : SpirvOp.BitFieldUExtract,
                type,
                signed
                    ? Bitcast(_intType, GetRawSource(instruction, sourceIndex))
                    : GetRawSource(instruction, sourceIndex),
                UInt(component * componentBits),
                UInt(componentBits));
        }

        private uint PackHalf2(uint low, uint high)
        {
            var vector = _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec2Type,
                low,
                high);
            return Ext(58, _uintType, vector);
        }

        private bool TryGetInlineFloatSource(
            Gen5ShaderInstruction instruction,
            int sourceIndex,
            out uint value)
        {
            var operand = instruction.Sources[sourceIndex];
            if (operand.Kind != Gen5OperandKind.EncodedConstant)
            {
                value = 0;
                return false;
            }

            if (operand.Value == 125)
            {
                value = Float(0);
                return true;
            }

            if (operand.Value is >= 128 and <= 192)
            {
                value = Float(operand.Value - 128);
                return true;
            }

            if (operand.Value is >= 193 and <= 208)
            {
                value = Float(-(operand.Value - 192));
                return true;
            }

            if (TryDecodeInlineConstant(operand.Value, out var raw))
            {
                value = Bitcast(_floatType, UInt(raw));
                return true;
            }

            value = 0;
            return false;
        }

        private uint GetRawSource(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            if ((uint)sourceIndex >= instruction.Sources.Count)
            {
                throw new InvalidOperationException($"missing source {sourceIndex}");
            }

            var operand = instruction.Sources[sourceIndex];
            uint value = operand.Kind switch
            {
                Gen5OperandKind.VectorRegister => LoadV(operand.Value),
                Gen5OperandKind.ScalarRegister => LoadS(operand.Value),
                Gen5OperandKind.LiteralConstant => UInt(operand.Value),
                Gen5OperandKind.EncodedConstant when TryDecodeInlineConstant(
                    operand.Value,
                    out var inline) => UInt(inline),
                _ => throw new InvalidOperationException($"unsupported source {operand}"),
            };

            return ApplySdwaSourceSelection(instruction, sourceIndex, value);
        }

        private uint ApplySdwaSourceSelection(
            Gen5ShaderInstruction instruction,
            int sourceIndex,
            uint value)
        {
            if (instruction.Control is Gen5SdwaControl sdwa)
            {
                var selector = sourceIndex switch
                {
                    0 => sdwa.Source0Select,
                    1 => sdwa.Source1Select,
                    _ => 6u,
                };
                value = selector switch
                {
                    0 => BitwiseAnd(value, UInt(0xFF)),
                    1 => BitwiseAnd(ShiftRightLogical(value, UInt(8)), UInt(0xFF)),
                    2 => BitwiseAnd(ShiftRightLogical(value, UInt(16)), UInt(0xFF)),
                    3 => BitwiseAnd(ShiftRightLogical(value, UInt(24)), UInt(0xFF)),
                    4 => BitwiseAnd(value, UInt(0xFFFF)),
                    5 => BitwiseAnd(ShiftRightLogical(value, UInt(16)), UInt(0xFFFF)),
                    _ => value,
                };
            }

            return value;
        }

        private uint GetFloatSource(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            var operand = instruction.Sources[sourceIndex];
            uint value;
            if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                operand.Value is >= 128 and <= 192)
            {
                value = Float(operand.Value - 128);
            }
            else if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                     operand.Value is >= 193 and <= 208)
            {
                value = Float(-(operand.Value - 192));
            }
            else
            {
                value = Bitcast(_floatType, GetRawSource(instruction, sourceIndex));
            }

            uint absoluteMask = 0;
            uint negateMask = 0;
            switch (instruction.Control)
            {
                case Gen5Vop3Control control:
                    absoluteMask = control.AbsoluteMask;
                    negateMask = control.NegateMask;
                    break;
                case Gen5SdwaControl control:
                    absoluteMask = control.AbsoluteMask;
                    negateMask = control.NegateMask;
                    break;
                case Gen5DppControl control:
                    absoluteMask = control.AbsoluteMask;
                    negateMask = control.NegateMask;
                    break;
            }

            if ((absoluteMask & (1u << sourceIndex)) != 0)
            {
                value = Ext(4, _floatType, value);
            }

            if ((negateMask & (1u << sourceIndex)) != 0)
            {
                value = _module.AddInstruction(SpirvOp.FNegate, _floatType, value);
            }

            return value;
        }

        private uint GetRawSource64(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            var operand = instruction.Sources[sourceIndex];
            if (operand.Kind == Gen5OperandKind.ScalarRegister)
            {
                return LoadS64(operand.Value);
            }

            if (operand.Kind == Gen5OperandKind.VectorRegister)
            {
                return LoadV64(operand.Value);
            }

            var low = GetRawSource(instruction, sourceIndex);
            return _module.AddInstruction(SpirvOp.UConvert, _ulongType, low);
        }

        private uint LoadS64(uint register)
        {
            var low = _module.AddInstruction(SpirvOp.UConvert, _ulongType, LoadS(register));
            var high = _module.AddInstruction(
                SpirvOp.UConvert,
                _ulongType,
                LoadS(register + 1));
            high = ShiftLeftLogical64(high, _module.Constant64(_ulongType, 32));
            return _module.AddInstruction(SpirvOp.BitwiseOr, _ulongType, low, high);
        }

        private uint LoadS64At(uint index)
        {
            var low = _module.AddInstruction(
                SpirvOp.UConvert,
                _ulongType,
                LoadSAt(index));
            var high = _module.AddInstruction(
                SpirvOp.UConvert,
                _ulongType,
                LoadSAt(IAdd(index, UInt(1))));
            high = ShiftLeftLogical64(high, _module.Constant64(_ulongType, 32));
            return _module.AddInstruction(SpirvOp.BitwiseOr, _ulongType, low, high);
        }

        private void StoreS64(uint register, uint value)
        {
            StoreS(
                register,
                _module.AddInstruction(SpirvOp.UConvert, _uintType, value));
            var high = ShiftRightLogical64(
                value,
                _module.Constant64(_ulongType, 32));
            StoreS(
                register + 1,
                _module.AddInstruction(SpirvOp.UConvert, _uintType, high));
        }

        private void StoreS64At(uint index, uint value)
        {
            StoreSAt(index, _module.AddInstruction(SpirvOp.UConvert, _uintType, value));
            var high = ShiftRightLogical64(
                value,
                _module.Constant64(_ulongType, 32));
            StoreSAt(
                IAdd(index, UInt(1)),
                _module.AddInstruction(SpirvOp.UConvert, _uintType, high));
        }

        private uint EmitFloatBinary(
            Gen5ShaderInstruction instruction,
            SpirvOp operation,
            bool reverse = false)
        {
            var left = GetFloatSource(instruction, reverse ? 1 : 0);
            var right = GetFloatSource(instruction, reverse ? 0 : 1);
            return EmitFloatResult(
                instruction,
                _module.AddInstruction(operation, _floatType, left, right));
        }

        private uint EmitFloatExtBinary(
            Gen5ShaderInstruction instruction,
            uint operation) =>
            EmitFloatResult(
                instruction,
                Ext(
                    operation,
                    _floatType,
                    GetFloatSource(instruction, 0),
                    GetFloatSource(instruction, 1)));

        private uint EmitFloatTernaryExt(
            Gen5ShaderInstruction instruction,
            uint operation)
        {
            var first = Ext(
                operation,
                _floatType,
                GetFloatSource(instruction, 0),
                GetFloatSource(instruction, 1));
            return EmitFloatResult(
                instruction,
                Ext(operation, _floatType, first, GetFloatSource(instruction, 2)));
        }

        private uint EmitIntegerBinary(
            Gen5ShaderInstruction instruction,
            SpirvOp operation,
            bool reverse = false)
        {
            var left = GetRawSource(instruction, reverse ? 1 : 0);
            var right = GetRawSource(instruction, reverse ? 0 : 1);
            if (operation == SpirvOp.ShiftLeftLogical)
            {
                return ShiftLeftLogical(left, right);
            }

            if (operation == SpirvOp.ShiftRightLogical)
            {
                return ShiftRightLogical(left, right);
            }

            if (operation == SpirvOp.ShiftRightArithmetic)
            {
                return ShiftRightArithmetic(left, right);
            }

            return _module.AddInstruction(operation, _uintType, left, right);
        }

        private enum CubeCoordinate
        {
            Id,
            Sc,
            Tc,
            Ma,
        }

        private uint EmitCvtOffF32I4(Gen5ShaderInstruction instruction)
        {
            var index = BitwiseAnd(GetRawSource(instruction, 0), UInt(15));
            ReadOnlySpan<float> table =
            [
                0.0f,
                0.0625f,
                0.1250f,
                0.1875f,
                0.2500f,
                0.3125f,
                0.3750f,
                0.4375f,
                -0.5000f,
                -0.4375f,
                -0.3750f,
                -0.3125f,
                -0.2500f,
                -0.1875f,
                -0.1250f,
                -0.0625f,
            ];

            var result = UInt(BitConverter.SingleToUInt32Bits(table[^1]));
            for (var tableIndex = table.Length - 2; tableIndex >= 0; tableIndex--)
            {
                var matches = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    index,
                    UInt((uint)tableIndex));
                result = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    matches,
                    UInt(BitConverter.SingleToUInt32Bits(table[tableIndex])),
                    result);
            }

            return result;
        }

        private uint EmitCubeCoordinate(
            Gen5ShaderInstruction instruction,
            CubeCoordinate coordinate)
        {
            var x = GetFloatSource(instruction, 0);
            var y = GetFloatSource(instruction, 1);
            var z = GetFloatSource(instruction, 2);
            var nx = _module.AddInstruction(SpirvOp.FNegate, _floatType, x);
            var ny = _module.AddInstruction(SpirvOp.FNegate, _floatType, y);
            var nz = _module.AddInstruction(SpirvOp.FNegate, _floatType, z);
            var ax = Ext(4, _floatType, x);
            var ay = Ext(4, _floatType, y);
            var az = Ext(4, _floatType, z);
            var amaxXY = Ext(40, _floatType, ax, ay);
            var amax = Ext(40, _floatType, az, amaxXY);
            var ma = _module.AddInstruction(
                SpirvOp.FMul,
                _floatType,
                Float(2),
                amax);
            if (coordinate == CubeCoordinate.Ma)
            {
                return EmitFloatResult(instruction, ma);
            }

            var isZMax = _module.AddInstruction(
                SpirvOp.FOrdGreaterThanEqual,
                _boolType,
                az,
                amaxXY);
            var yGreaterOrEqualX = _module.AddInstruction(
                SpirvOp.FOrdGreaterThanEqual,
                _boolType,
                ay,
                ax);
            var isYMax = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                _module.AddInstruction(SpirvOp.LogicalNot, _boolType, isZMax),
                yGreaterOrEqualX);
            if (coordinate == CubeCoordinate.Id)
            {
                var isZNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    z,
                    Float(0));
                var isYNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    y,
                    Float(0));
                var isXNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    x,
                    Float(0));
                var zCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isZNeg,
                    Float(5),
                    Float(4));
                var yCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isYNeg,
                    Float(3),
                    Float(2));
                var xCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isXNeg,
                    Float(1),
                    Float(0));
                var xyCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    yGreaterOrEqualX,
                    yCase,
                    xCase);
                return EmitFloatResult(
                    instruction,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _floatType,
                        isZMax,
                        zCase,
                        xyCase));
            }

            if (coordinate == CubeCoordinate.Sc)
            {
                var isZNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    z,
                    Float(0));
                var isXNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    x,
                    Float(0));
                var zCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isZNeg,
                    nx,
                    x);
                var xCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isXNeg,
                    z,
                    nz);
                var nonZCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isYMax,
                    x,
                    xCase);
                return EmitFloatResult(
                    instruction,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _floatType,
                        isZMax,
                        zCase,
                        nonZCase));
            }

            var tcIsYNeg = _module.AddInstruction(
                SpirvOp.FOrdLessThan,
                _boolType,
                y,
                Float(0));
            var tcYCase = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                tcIsYNeg,
                nz,
                z);
            return EmitFloatResult(
                instruction,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isYMax,
                    tcYCase,
                    ny));
        }

        private uint EmitAddWithCarry(Gen5ShaderInstruction instruction)
        {
            var left = GetRawSource(instruction, 0);
            var right = GetRawSource(instruction, 1);
            var carryIn = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                GetCarryIn(instruction),
                UInt(1),
                UInt(0));
            var partial = IAdd(left, right);
            var result = IAdd(partial, carryIn);
            var carry = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, partial, left),
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, result, partial));
            StoreCarryOut(instruction, carry);
            return result;
        }

        private uint EmitSubtractWithBorrow(
            Gen5ShaderInstruction instruction,
            bool reverse)
        {
            var left = GetRawSource(instruction, reverse ? 1 : 0);
            var right = GetRawSource(instruction, reverse ? 0 : 1);
            var borrowIn = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                GetCarryIn(instruction),
                UInt(1),
                UInt(0));
            var partial = _module.AddInstruction(SpirvOp.ISub, _uintType, left, right);
            var result = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                partial,
                borrowIn);
            var borrow = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, left, right),
                _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    partial,
                    borrowIn));
            StoreCarryOut(instruction, borrow);
            return result;
        }

        private uint GetCarryIn(Gen5ShaderInstruction instruction)
        {
            if (instruction.Sources.Count >= 3)
            {
                var operand = instruction.Sources[2];
                if (operand.Kind != Gen5OperandKind.ScalarRegister)
                {
                    throw new InvalidOperationException(
                        "vector carry-in source is not a scalar wave mask");
                }

                return IsWaveMaskActive(LoadS64(operand.Value));
            }

            return Load(_boolType, _vcc);
        }

        private void StoreCarryOut(
            Gen5ShaderInstruction instruction,
            uint carry)
        {
            if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } register })
            {
                StoreWaveMask(register, carry);
                return;
            }

            StoreWaveMask(106, carry);
        }

        private uint EmitPermlane16(
            Gen5ShaderInstruction instruction,
            bool exchangeRows)
        {
            var value = GetRawSource(instruction, 0);
            var selectorLow = GetRawSource(instruction, 1);
            var selectorHigh = GetRawSource(instruction, 2);
            var lane = Load(_uintType, _subgroupInvocationIdInput);
            var localLane = BitwiseAnd(lane, UInt(15));
            var lowHalf = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                localLane,
                UInt(8));
            var lowShift = ShiftLeftLogical(localLane, UInt(2));
            var highLane = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                localLane,
                UInt(8));
            var highShift = ShiftLeftLogical(highLane, UInt(2));
            var lowSelector = BitwiseAnd(
                ShiftRightLogical(selectorLow, lowShift),
                UInt(15));
            var highSelector = BitwiseAnd(
                ShiftRightLogical(selectorHigh, highShift),
                UInt(15));
            var selector = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                lowHalf,
                lowSelector,
                highSelector);
            var rowBase = BitwiseAnd(lane, UInt(0xFFFF_FFF0));
            if (exchangeRows)
            {
                rowBase = BitwiseXor(rowBase, UInt(16));
            }

            var targetLane = IAdd(rowBase, selector);
            return _module.AddInstruction(
                SpirvOp.GroupNonUniformShuffle,
                _uintType,
                UInt(3),
                value,
                targetLane);
        }

        private uint EmitFloatResult(
            Gen5ShaderInstruction instruction,
            uint value)
        {
            uint outputModifier = 0;
            var clamp = false;
            switch (instruction.Control)
            {
                case Gen5Vop3Control control:
                    outputModifier = control.OutputModifier;
                    clamp = control.Clamp;
                    break;
                case Gen5SdwaControl control:
                    outputModifier = control.OutputModifier;
                    clamp = control.Clamp;
                    break;
            }

            value = outputModifier switch
            {
                1 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(2)),
                2 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(4)),
                3 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(0.5f)),
                _ => value,
            };
            if (clamp)
            {
                value = Ext(43, _floatType, value, Float(0), Float(1));
            }

            return Bitcast(_uintType, value);
        }

        private uint TruncateFloat32ForPack(uint value)
        {
            var raw = BitwiseAnd(
                Bitcast(_uintType, value),
                UInt(0xFFFF_E000));
            return Bitcast(_floatType, raw);
        }

        private uint Ext(uint operation, uint resultType, params uint[] operands)
        {
            var values = new uint[2 + operands.Length];
            values[0] = _glsl;
            values[1] = operation;
            operands.CopyTo(values, 2);
            return _module.AddInstruction(SpirvOp.ExtInst, resultType, values);
        }

        private uint IsNotZero(uint value) =>
            _module.AddInstruction(SpirvOp.INotEqual, _boolType, value, UInt(0));

        private uint IsNotZero64(uint value) =>
            _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                value,
                _module.Constant64(_ulongType, 0));

        private uint WholeQuadMode(
            uint value,
            uint integerType,
            uint groupMask,
            uint multiplier)
        {
            var groups = value;
            for (uint shift = 1; shift <= 3; shift++)
            {
                groups = _module.AddInstruction(
                    SpirvOp.BitwiseOr,
                    integerType,
                    groups,
                    ShiftRightForType(value, integerType, shift));
            }

            groups = _module.AddInstruction(
                SpirvOp.BitwiseAnd,
                integerType,
                groups,
                groupMask);
            return _module.AddInstruction(SpirvOp.IMul, integerType, groups, multiplier);
        }

        private uint EmitQuadMask(uint value, uint integerType, uint groupCount)
        {
            var result = UInt(0);
            var nibbleMask = integerType == _ulongType
                ? _module.Constant64(_ulongType, 0xF)
                : UInt(0xF);
            for (uint group = 0; group < groupCount; group++)
            {
                var nibble = _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    integerType,
                    ShiftRightForType(value, integerType, group * 4),
                    nibbleMask);
                var active = integerType == _ulongType
                    ? IsNotZero64(nibble)
                    : IsNotZero(nibble);
                result = _module.AddInstruction(
                    SpirvOp.BitwiseOr,
                    _uintType,
                    result,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        active,
                        UInt(1u << (int)group),
                        UInt(0)));
            }

            return result;
        }

        private uint EmitBitReplicate64(uint value)
        {
            var result = _module.AddInstruction(SpirvOp.UConvert, _ulongType, value);
            foreach (var (shift, mask) in new (uint Shift, ulong Mask)[]
                     {
                         (16, 0x0000_FFFF_0000_FFFFUL),
                         (8, 0x00FF_00FF_00FF_00FFUL),
                         (4, 0x0F0F_0F0F_0F0F_0F0FUL),
                         (2, 0x3333_3333_3333_3333UL),
                         (1, 0x5555_5555_5555_5555UL),
                     })
            {
                result = _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        result,
                        ShiftLeftLogical64(result, _module.Constant64(_ulongType, shift))),
                    _module.Constant64(_ulongType, mask));
            }

            return _module.AddInstruction(
                SpirvOp.BitwiseOr,
                _ulongType,
                result,
                ShiftLeftLogical64(result, _module.Constant64(_ulongType, 1)));
        }

        private uint EmitBitReverse64(uint value)
        {
            var low = _module.AddInstruction(SpirvOp.UConvert, _uintType, value);
            var high = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                ShiftRightLogical64(value, _module.Constant64(_ulongType, 32)));
            var reversedLow = _module.AddInstruction(SpirvOp.BitReverse, _uintType, low);
            var reversedHigh = _module.AddInstruction(SpirvOp.BitReverse, _uintType, high);
            return _module.AddInstruction(
                SpirvOp.BitwiseOr,
                _ulongType,
                _module.AddInstruction(SpirvOp.UConvert, _ulongType, reversedHigh),
                ShiftLeftLogical64(
                    _module.AddInstruction(SpirvOp.UConvert, _ulongType, reversedLow),
                    _module.Constant64(_ulongType, 32)));
        }

        private uint EmitBitCount64(uint value)
        {
            var low = _module.AddInstruction(SpirvOp.UConvert, _uintType, value);
            var high = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                ShiftRightLogical64(value, _module.Constant64(_ulongType, 32)));
            return _module.AddInstruction(
                SpirvOp.IAdd,
                _uintType,
                _module.AddInstruction(SpirvOp.BitCount, _uintType, low),
                _module.AddInstruction(SpirvOp.BitCount, _uintType, high));
        }

        private uint EmitFirstSetBit64(uint value)
        {
            var low = _module.AddInstruction(SpirvOp.UConvert, _uintType, value);
            var high = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                ShiftRightLogical64(value, _module.Constant64(_ulongType, 32)));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                IsNotZero(low),
                Ext(73, _uintType, low),
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    IsNotZero(high),
                    _module.AddInstruction(
                        SpirvOp.IAdd,
                        _uintType,
                        UInt(32),
                        Ext(73, _uintType, high)),
                    UInt(uint.MaxValue)));
        }

        private uint EmitLeadingBitCount64(uint value)
        {
            var low = _module.AddInstruction(SpirvOp.UConvert, _uintType, value);
            var high = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                ShiftRightLogical64(value, _module.Constant64(_ulongType, 32)));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                IsNotZero(high),
                EmitLeadingBitCount(high, _uintType, UInt(31)),
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    IsNotZero(low),
                    _module.AddInstruction(
                        SpirvOp.IAdd,
                        _uintType,
                        UInt(32),
                        EmitLeadingBitCount(low, _uintType, UInt(31))),
                    UInt(uint.MaxValue)));
        }

        private uint EmitSignedLeadingBitCount64(uint value)
        {
            var normalized = _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                IsNotZero64(ShiftRightForType(value, _ulongType, 63)),
                _module.AddInstruction(SpirvOp.Not, _ulongType, value),
                value);
            return EmitLeadingBitCount64(normalized);
        }

        private uint EmitLeadingBitCount(uint value, uint integerType, uint highestBit)
        {
            var mostSignificantBit = Ext(75, integerType, value);
            if (integerType == _ulongType)
            {
                mostSignificantBit = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _uintType,
                    mostSignificantBit);
            }

            var isZero = _module.AddInstruction(
                SpirvOp.LogicalNot,
                _boolType,
                integerType == _ulongType ? IsNotZero64(value) : IsNotZero(value));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                isZero,
                UInt(uint.MaxValue),
                _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    highestBit,
                    mostSignificantBit));
        }

        private uint EmitSignedLeadingBitCount(uint value, uint integerType, uint highestBit)
        {
            var sign = integerType == _ulongType
                ? IsNotZero64(ShiftRightForType(value, integerType, 63))
                : IsNotZero(ShiftRightForType(value, integerType, 31));
            var normalized = _module.AddInstruction(
                SpirvOp.Select,
                integerType,
                sign,
                _module.AddInstruction(SpirvOp.Not, integerType, value),
                value);
            return EmitLeadingBitCount(normalized, integerType, highestBit);
        }

        private uint ShiftRightForType(uint value, uint integerType, uint shift) =>
            integerType == _ulongType
                ? ShiftRightLogical64(value, _module.Constant64(_ulongType, shift))
                : ShiftRightLogical(value, UInt(shift));

        private uint SignBit(uint value) =>
            ShiftRightLogical(value, UInt(31));

        private uint SignedAddOverflow(uint left, uint right, uint result)
        {
            var leftSign = SignBit(left);
            var rightSign = SignBit(right);
            var resultSign = SignBit(result);
            var sameSourceSign = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                leftSign,
                rightSign);
            var resultSignChanged = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                leftSign,
                resultSign);
            return _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                sameSourceSign,
                resultSignChanged);
        }

        private uint SignedSubOverflow(uint left, uint right, uint result)
        {
            var leftSign = SignBit(left);
            var rightSign = SignBit(right);
            var resultSign = SignBit(result);
            var differentSourceSign = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                leftSign,
                rightSign);
            var resultSignChanged = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                leftSign,
                resultSign);
            return _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                differentSourceSign,
                resultSignChanged);
        }

        private static bool TryDecodeInlineConstant(uint encoded, out uint value)
        {
            if (encoded == 125)
            {
                value = 0;
                return true;
            }

            if (encoded is >= 128 and <= 192)
            {
                value = encoded - 128;
                return true;
            }

            if (encoded is >= 193 and <= 208)
            {
                value = unchecked((uint)-(int)(encoded - 192));
                return true;
            }

            var floatingPoint = encoded switch
            {
                240 => 0.5f,
                241 => -0.5f,
                242 => 1.0f,
                243 => -1.0f,
                244 => 2.0f,
                245 => -2.0f,
                246 => 4.0f,
                247 => -4.0f,
                248 => 1.0f / (2.0f * MathF.PI),
                _ => float.NaN,
            };
            if (float.IsNaN(floatingPoint))
            {
                value = 0;
                return false;
            }

            value = BitConverter.SingleToUInt32Bits(floatingPoint);
            return true;
        }
    }
}
