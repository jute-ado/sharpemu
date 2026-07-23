// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private const uint ScalarRegisterCount = 256;
    private const uint VectorRegisterCount = 512;
    private const uint M0Register = 124;
    private const uint LdsDwordCount = 8192;
    private const uint ScratchDwordCount = 4096;
    private const uint RdnaWaveLaneCount = 32;
    private const uint GraphicsWaveLaneCount = 64;

    public static bool RequiresFlatInput(
        Gen5SpirvStage stage,
        bool integerType) =>
        stage == Gen5SpirvStage.Pixel && integerType;

    public static (bool ProgramActive, bool ExecActive) ResolveComputeInvocationState(
        bool invocationInBounds,
        bool emulateWave64) =>
        (
            ProgramActive: invocationInBounds || emulateWave64,
            ExecActive: invocationInBounds);

    public static SpirvImageDim GetImageDimension(uint guestDimension) =>
        guestDimension switch
        {
            0 or 4 => SpirvImageDim.Dim1D,
            2 => SpirvImageDim.Dim3D,
            3 => SpirvImageDim.Cube,
            _ => SpirvImageDim.Dim2D,
        };

    public static uint GetImageCoordinateComponentCount(
        uint guestDimension,
        bool arrayed)
    {
        var spatialComponents = guestDimension switch
        {
            0 or 4 => 1u,
            2 or 3 => 3u,
            _ => 2u,
        };
        return spatialComponents + (arrayed ? 1u : 0u);
    }

    public static SpirvImageFormat DecodeStorageImageFormat(
        uint dataFormat,
        uint numberType) =>
        (dataFormat, numberType) switch
        {
            (1, 0 or 9) => SpirvImageFormat.R8,
            (1, 1) => SpirvImageFormat.R8Snorm,
            (1, 4) => SpirvImageFormat.R8ui,
            (1, 5) => SpirvImageFormat.R8i,
            (2, 0) => SpirvImageFormat.R16,
            (2, 1) => SpirvImageFormat.R16Snorm,
            (2, 4) => SpirvImageFormat.R16ui,
            (2, 5) => SpirvImageFormat.R16i,
            (2, 7) => SpirvImageFormat.R16f,
            (3, 0 or 9) => SpirvImageFormat.Rg8,
            (3, 1) => SpirvImageFormat.Rg8Snorm,
            (3, 4) => SpirvImageFormat.Rg8ui,
            (3, 5) => SpirvImageFormat.Rg8i,
            (4, 4) => SpirvImageFormat.R32ui,
            (4, 5) => SpirvImageFormat.R32i,
            (4, 7) => SpirvImageFormat.R32f,
            (5, 0) => SpirvImageFormat.Rg16,
            (5, 1) => SpirvImageFormat.Rg16Snorm,
            (5, 4) => SpirvImageFormat.Rg16ui,
            (5, 5) => SpirvImageFormat.Rg16i,
            (5, 7) => SpirvImageFormat.Rg16f,
            (6 or 7, 7) => SpirvImageFormat.R11fG11fB10f,
            (8 or 9, 0) => SpirvImageFormat.Rgb10A2,
            (8 or 9, 4) => SpirvImageFormat.Rgb10A2ui,
            (10, 0 or 9) => SpirvImageFormat.Rgba8,
            (10, 1) => SpirvImageFormat.Rgba8Snorm,
            (10, 4) => SpirvImageFormat.Rgba8ui,
            (10, 5) => SpirvImageFormat.Rgba8i,
            (11, 4) => SpirvImageFormat.Rg32ui,
            (11, 5) => SpirvImageFormat.Rg32i,
            (11, 7) => SpirvImageFormat.Rg32f,
            (12, 0) => SpirvImageFormat.Rgba16,
            (12, 1) => SpirvImageFormat.Rgba16Snorm,
            (12, 4) => SpirvImageFormat.Rgba16ui,
            (12, 5) => SpirvImageFormat.Rgba16i,
            (12, 7) => SpirvImageFormat.Rgba16f,
            (13 or 14, 4) => SpirvImageFormat.Rgba32ui,
            (13 or 14, 5) => SpirvImageFormat.Rgba32i,
            (13 or 14, 7) => SpirvImageFormat.Rgba32f,
            (20, _) => SpirvImageFormat.R32ui,
            (21, _) => SpirvImageFormat.R32i,
            (22, _) => SpirvImageFormat.Rgba16f,
            _ => SpirvImageFormat.Unknown,
        };

    public static bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        Gen5PixelOutputKind outputKind,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int initialScalarBufferIndex = -1) =>
        TryCompilePixelShader(
            state,
            evaluation,
            [new Gen5PixelOutputBinding(0, 0, outputKind)],
            out shader,
            out error,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            initialScalarBufferIndex);

    public static bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        IReadOnlyList<Gen5PixelOutputBinding> outputs,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int initialScalarBufferIndex = -1,
        uint pixelInputEnable = 0x300,
        uint pixelInputAddress = 0x300,
        ulong storageBufferOffsetAlignment =
            Gen5GlobalMemoryBinding.PortableDescriptorOffsetAlignment)
    {
        if (outputs.Count > 8 || outputs.Any(output => output.GuestSlot > 7))
        {
            shader = default!;
            error = "pixel outputs must contain at most eight guest slots in the 0..7 range";
            return false;
        }

        if (outputs.Select(output => output.GuestSlot).Distinct().Count() != outputs.Count ||
            outputs.Select(output => output.HostLocation).Distinct().Count() != outputs.Count)
        {
            shader = default!;
            error = "pixel output guest slots and host locations must be unique";
            return false;
        }

        if (!outputs
                .OrderBy(output => output.HostLocation)
                .Select((output, index) => output.HostLocation == (uint)index)
                .All(isDense => isDense))
        {
            shader = default!;
            error = "pixel output host locations must be dense in the 0..N-1 range";
            return false;
        }

        var context = new CompilationContext(
            Gen5SpirvStage.Pixel,
            state,
            evaluation,
            outputs,
            1,
            1,
            1,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            initialScalarBufferIndex,
            pixelInputEnable: pixelInputEnable,
            pixelInputAddress: pixelInputAddress,
            waveLaneCount: GraphicsWaveLaneCount,
            storageBufferOffsetAlignment: storageBufferOffsetAlignment);
        return context.TryCompile(out shader, out error);
    }

    public static bool TryCompileVertexShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int initialScalarBufferIndex = -1,
        int requiredVertexOutputCount = 0,
        ulong storageBufferOffsetAlignment =
            Gen5GlobalMemoryBinding.PortableDescriptorOffsetAlignment)
    {
        var context = new CompilationContext(
            Gen5SpirvStage.Vertex,
            state,
            evaluation,
            [],
            1,
            1,
            1,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            initialScalarBufferIndex,
            requiredVertexOutputCount: requiredVertexOutputCount,
            waveLaneCount: GraphicsWaveLaneCount,
            storageBufferOffsetAlignment: storageBufferOffsetAlignment);
        return context.TryCompile(out shader, out error);
    }

    public static bool TryCompileComputeShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out Gen5SpirvShader shader,
        out string error,
        uint waveLaneCount = 32,
        int totalGlobalBufferCount = -1,
        int initialScalarBufferIndex = -1)
    {
        if (waveLaneCount is not (32 or 64))
        {
            shader = default!;
            error = "wave lane count must be 32 or 64";
            return false;
        }

        var context = new CompilationContext(
            Gen5SpirvStage.Compute,
            state,
            evaluation,
            [],
            Math.Max(localSizeX, 1),
            Math.Max(localSizeY, 1),
            Math.Max(localSizeZ, 1),
            0,
            totalGlobalBufferCount,
            0,
            initialScalarBufferIndex,
            waveLaneCount: waveLaneCount);
        return context.TryCompile(out shader, out error);
    }

    public static bool TryCompileComputeShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out Gen5SpirvShader shader,
        out string error,
        int totalGlobalBufferCount,
        int initialScalarBufferIndex,
        uint waveLaneCount,
        ulong storageBufferOffsetAlignment)
    {
        if (waveLaneCount is not (32 or 64))
        {
            shader = default!;
            error = "wave lane count must be 32 or 64";
            return false;
        }

        var context = new CompilationContext(
            Gen5SpirvStage.Compute,
            state,
            evaluation,
            [],
            Math.Max(localSizeX, 1),
            Math.Max(localSizeY, 1),
            Math.Max(localSizeZ, 1),
            0,
            totalGlobalBufferCount,
            0,
            initialScalarBufferIndex,
            waveLaneCount: waveLaneCount,
            storageBufferOffsetAlignment: storageBufferOffsetAlignment);
        return context.TryCompile(out shader, out error);
    }

    private sealed partial class CompilationContext
    {
        private readonly SpirvModuleBuilder _module = new();
        private readonly Gen5SpirvStage _stage;
        private readonly Gen5ShaderState _state;
        private readonly Gen5ShaderEvaluation _evaluation;
        private readonly IReadOnlyList<Gen5PixelOutputBinding> _pixelOutputBindings;
        private readonly uint _waveLaneCount;
        private readonly bool _emulateWave64;
        private readonly bool _perInvocationGraphicsMasks;
        private readonly int _requiredVertexOutputCount;
        private readonly uint _localSizeX;
        private readonly uint _localSizeY;
        private readonly uint _localSizeZ;
        private readonly int _globalBufferBase;
        private readonly int _totalGlobalBufferCount;
        private readonly int _imageBindingBase;
        private readonly int _scalarRegisterBufferIndex;
        private readonly uint _pixelInputEnable;
        private readonly uint _pixelInputAddress;
        private readonly ulong _storageBufferOffsetAlignment;
        private readonly List<uint> _interfaces = [];
        private readonly Dictionary<uint, uint> _pixelInputs = [];
        private readonly Dictionary<uint, SpirvPixelOutput> _pixelOutputs = [];
        private readonly Dictionary<uint, uint> _vertexOutputs = [];
        private readonly Dictionary<uint, SpirvVertexInput> _vertexInputsByPc = [];
        private readonly List<SpirvImageResource> _imageResources = [];
        private readonly Dictionary<uint, int> _imageBindingByPc = [];
        private readonly Dictionary<uint, int> _bufferBindingByPc = [];
        private readonly Dictionary<uint, (uint DataFormat, uint NumberFormat)> _formatBindingByPc = [];
        private uint _voidType;
        private uint _boolType;
        private uint _uintType;
        private uint _intType;
        private uint _longType;
        private uint _ulongType;
        private uint _floatType;
        private uint _doubleType;
        private uint _vec2Type;
        private uint _vec3Type;
        private uint _vec4Type;
        private uint _uvec2Type;
        private uint _uvec3Type;
        private uint _uvec4Type;
        private uint _privateUintPointer;
        private uint _privateBoolPointer;
        private uint _scalarRegisters;
        private uint _vectorRegisters;
        private uint _scc;
        private uint _vcc;
        private uint _exec;
        private uint _programCounter;
        private uint _programActive;
        private uint _globalBuffers;
        private uint _storageBlockPointer;
        private uint _storageUintPointer;
        private uint _scratch;
        private uint _lds;
        private uint _workgroupUintPointer;
        private uint _positionOutput;
        private uint _vertexIndexInput;
        private uint _instanceIndexInput;
        private uint _fragCoordInput;
        private uint _localInvocationIdInput;
        private uint _workGroupIdInput;
        private uint _computeDispatchLimit;
        private uint _pushConstantUintPointer;
        private uint _subgroupInvocationIdInput;
        private uint _localInvocationIndexInput;
        private uint _waveScratch;
        private uint _waveScratchElementPointer;
        private uint _glsl;

        private enum ImageComponentKind
        {
            Float,
            Sint,
            Uint,
        }

        private enum VertexInputComponentKind
        {
            Float,
            Sint,
            Uint,
        }

        private readonly record struct SpirvImageResource(
            uint Variable,
            uint ImageType,
            uint ObjectType,
            uint ComponentType,
            uint VectorType,
            SpirvImageFormat Format,
            ImageComponentKind ComponentKind,
            bool IsStorage,
            bool Arrayed);

        private readonly record struct SpirvVertexInput(
            uint Variable,
            uint Type,
            uint ComponentType,
            uint ComponentCount,
            VertexInputComponentKind ComponentKind);

        private readonly record struct SpirvPixelOutput(
            uint Variable,
            uint Type,
            Gen5PixelOutputKind Kind);

        public CompilationContext(
            Gen5SpirvStage stage,
            Gen5ShaderState state,
            Gen5ShaderEvaluation evaluation,
            IReadOnlyList<Gen5PixelOutputBinding> pixelOutputBindings,
            uint localSizeX,
            uint localSizeY,
            uint localSizeZ,
            int globalBufferBase,
            int totalGlobalBufferCount,
            int imageBindingBase,
            int scalarRegisterBufferIndex,
            uint pixelInputEnable = 0,
            uint pixelInputAddress = 0,
            int requiredVertexOutputCount = 0,
            uint waveLaneCount = 32,
            ulong storageBufferOffsetAlignment =
                Gen5GlobalMemoryBinding.PortableDescriptorOffsetAlignment)
        {
            _stage = stage;
            _state = state;
            _evaluation = evaluation;
            _pixelOutputBindings = pixelOutputBindings;
            _waveLaneCount = waveLaneCount;
            // Graphics EXEC/VCC are invocation-local state. Reconstructing a
            // guest wave from the host subgroup couples masks to the host's
            // lane/pixel layout, which is not GCN-compatible on AMD RDNA.
            _perInvocationGraphicsMasks =
                stage is Gen5SpirvStage.Vertex or Gen5SpirvStage.Pixel &&
                waveLaneCount == GraphicsWaveLaneCount;
            _emulateWave64 =
                stage == Gen5SpirvStage.Compute &&
                waveLaneCount == 64 &&
                UsesSubgroupOperations();
            _localSizeX = localSizeX;
            _localSizeY = localSizeY;
            _localSizeZ = localSizeZ;
            _globalBufferBase = globalBufferBase;
            _totalGlobalBufferCount = totalGlobalBufferCount < 0
                ? evaluation.GlobalMemoryBindings.Count
                : totalGlobalBufferCount;
            _imageBindingBase = imageBindingBase;
            _scalarRegisterBufferIndex = scalarRegisterBufferIndex;
            _pixelInputEnable = pixelInputEnable;
            _pixelInputAddress = pixelInputAddress;
            _requiredVertexOutputCount = requiredVertexOutputCount;
            if (storageBufferOffsetAlignment == 0 ||
                (storageBufferOffsetAlignment & (storageBufferOffsetAlignment - 1)) != 0 ||
                storageBufferOffsetAlignment > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(storageBufferOffsetAlignment),
                    storageBufferOffsetAlignment,
                    "storage-buffer offset alignment must be a uint-sized power of two");
            }

            _storageBufferOffsetAlignment = storageBufferOffsetAlignment;
        }

        public bool TryCompile(out Gen5SpirvShader shader, out string error)
        {
            shader = default!;
            error = string.Empty;
            try
            {
                DeclareModule();
                var blocks = BuildBasicBlocks(_state.Program.Instructions);
                if (blocks.Count == 0)
                {
                    error = "shader contains no executable blocks";
                    return false;
                }

                var functionType = _module.TypeFunction(_voidType);
                var main = _module.BeginFunction(_voidType, functionType);
                _module.AddName(main, "main");
                _module.AddLabel();
                EmitInitialState();

                var loopHeader = _module.AllocateId();
                var switchHeader = _module.AllocateId();
                var switchMerge = _module.AllocateId();
                var loopContinue = _module.AllocateId();
                var loopMerge = _module.AllocateId();
                var functionMerge = _module.AllocateId();
                var defaultLabel = _module.AllocateId();
                var caseLabels = new uint[blocks.Count];
                for (var index = 0; index < caseLabels.Length; index++)
                {
                    caseLabels[index] = _module.AllocateId();
                }

                var initiallyActive = Load(_boolType, _programActive);
                _module.AddStatement(SpirvOp.SelectionMerge, functionMerge, 0);
                _module.AddStatement(
                    SpirvOp.BranchConditional,
                    initiallyActive,
                    loopHeader,
                    functionMerge);
                _module.AddLabel(loopHeader);
                _module.AddStatement(SpirvOp.LoopMerge, loopMerge, loopContinue, 0);
                _module.AddStatement(SpirvOp.Branch, switchHeader);

                _module.AddLabel(switchHeader);
                var selector = Load(_uintType, _programCounter);
                _module.AddStatement(SpirvOp.SelectionMerge, switchMerge, 0);
                var switchOperands = new uint[2 + (blocks.Count * 2)];
                switchOperands[0] = selector;
                switchOperands[1] = defaultLabel;
                for (var index = 0; index < blocks.Count; index++)
                {
                    switchOperands[2 + (index * 2)] = (uint)index;
                    switchOperands[3 + (index * 2)] = caseLabels[index];
                }

                _module.AddStatement(SpirvOp.Switch, switchOperands);
                for (var index = 0; index < blocks.Count; index++)
                {
                    _module.AddLabel(caseLabels[index]);
                    if (!TryEmitBlock(blocks, index, out error))
                    {
                        error = $"block=0x{blocks[index].StartPc:X}: {error}";
                        return false;
                    }

                    _module.AddStatement(SpirvOp.Branch, switchMerge);
                }

                _module.AddLabel(defaultLabel);
                Store(_programActive, _module.ConstantBool(false));
                _module.AddStatement(SpirvOp.Branch, switchMerge);

                _module.AddLabel(switchMerge);
                _module.AddStatement(SpirvOp.Branch, loopContinue);
                _module.AddLabel(loopContinue);
                var active = Load(_boolType, _programActive);
                _module.AddStatement(
                    SpirvOp.BranchConditional,
                    active,
                    loopHeader,
                    loopMerge);
                _module.AddLabel(loopMerge);
                _module.AddStatement(SpirvOp.Branch, functionMerge);
                _module.AddLabel(functionMerge);
                _module.AddStatement(SpirvOp.Return);
                _module.EndFunction();

                var model = _stage switch
                {
                    Gen5SpirvStage.Vertex => SpirvExecutionModel.Vertex,
                    Gen5SpirvStage.Pixel => SpirvExecutionModel.Fragment,
                    _ => SpirvExecutionModel.GLCompute,
                };
                _module.AddEntryPoint(model, main, "main", _interfaces);
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    _module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
                }
                else if (_stage == Gen5SpirvStage.Compute)
                {
                    _module.AddExecutionMode(
                        main,
                        SpirvExecutionMode.LocalSize,
                        _localSizeX,
                        _localSizeY,
                        _localSizeZ);
                }

                var attributeCount = _stage == Gen5SpirvStage.Vertex
                    ? (uint)_vertexOutputs.Count
                    : (uint)_pixelInputs.Count;
                shader = new Gen5SpirvShader(
                    _module.Build(),
                    _evaluation.GlobalMemoryBindings,
                    _evaluation.ImageBindings,
                    attributeCount,
                    _stage == Gen5SpirvStage.Vertex
                        ? _evaluation.VertexInputs ?? []
                        : []);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private void DeclareModule()
        {
            var usesFloat64 = _state.Program.Instructions.Any(
                static instruction => instruction.Opcode.Contains(
                    "F64",
                    StringComparison.Ordinal));
            _module.AddCapability(SpirvCapability.Shader);
            _module.AddCapability(SpirvCapability.Int64);
            _module.AddCapability(SpirvCapability.ImageQuery);
            if (usesFloat64)
            {
                _module.AddCapability(SpirvCapability.Float64);
            }
            if (_evaluation.ImageBindings.Any(
                    static binding =>
                        binding.Opcode.EndsWith("O", StringComparison.Ordinal) &&
                        (binding.Opcode.StartsWith(
                             "ImageGather4",
                             StringComparison.Ordinal) ||
                         (binding.Opcode.StartsWith(
                              "ImageSample",
                              StringComparison.Ordinal) &&
                          !binding.PackedOffset.HasValue))))
            {
                _module.AddCapability(SpirvCapability.ImageGatherExtended);
            }

            if (UsesSubgroupOperations() && !_emulateWave64)
            {
                _module.AddCapability(SpirvCapability.GroupNonUniform);
                if (!_perInvocationGraphicsMasks ||
                    UsesReadFirstLane())
                {
                    _module.AddCapability(
                        SpirvCapability.GroupNonUniformBallot);
                }

                if (UsesSubgroupShuffle())
                {
                    _module.AddCapability(SpirvCapability.GroupNonUniformShuffle);
                }

                if (UsesWaveControl() &&
                    !_perInvocationGraphicsMasks)
                {
                    _module.AddCapability(SpirvCapability.GroupNonUniformVote);
                }
            }

            _glsl = _module.ImportExtInst("GLSL.std.450");
            _voidType = _module.TypeVoid();
            _boolType = _module.TypeBool();
            _uintType = _module.TypeInt(32, signed: false);
            _intType = _module.TypeInt(32, signed: true);
            _longType = _module.TypeInt(64, signed: true);
            _ulongType = _module.TypeInt(64, signed: false);
            _floatType = _module.TypeFloat(32);
            if (usesFloat64)
            {
                _doubleType = _module.TypeFloat(64);
            }
            _vec2Type = _module.TypeVector(_floatType, 2);
            _vec3Type = _module.TypeVector(_floatType, 3);
            _vec4Type = _module.TypeVector(_floatType, 4);
            _uvec2Type = _module.TypeVector(_uintType, 2);
            _uvec3Type = _module.TypeVector(_uintType, 3);
            _uvec4Type = _module.TypeVector(_uintType, 4);
            _privateUintPointer =
                _module.TypePointer(SpirvStorageClass.Private, _uintType);
            _privateBoolPointer =
                _module.TypePointer(SpirvStorageClass.Private, _boolType);

            var scalarArrayType = _module.TypeArray(_uintType, ScalarRegisterCount);
            var vectorArrayType = _module.TypeArray(_uintType, VectorRegisterCount);
            var privateScalarArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, scalarArrayType);
            var privateVectorArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, vectorArrayType);
            _scalarRegisters = _module.AddGlobalVariable(
                privateScalarArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(scalarArrayType));
            _vectorRegisters = _module.AddGlobalVariable(
                privateVectorArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(vectorArrayType));
            _scc = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _vcc = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _exec = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(true));
            _programCounter = _module.AddGlobalVariable(
                _privateUintPointer,
                SpirvStorageClass.Private,
                _module.Constant(_uintType, 0));
            _programActive = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(true));
            _interfaces.Add(_scalarRegisters);
            _interfaces.Add(_vectorRegisters);
            _interfaces.Add(_scc);
            _interfaces.Add(_vcc);
            _interfaces.Add(_exec);
            _interfaces.Add(_programCounter);
            _interfaces.Add(_programActive);
            _module.AddName(_scalarRegisters, "sgpr");
            _module.AddName(_vectorRegisters, "vgpr");

            DeclareBuffers();
            DeclareImages();
            DeclareScratch();
            DeclareLds();
            DeclareWave64Scratch();
            DeclareStageInterface();
            DeclareComputeDispatchLimit();
        }

        private void DeclareComputeDispatchLimit()
        {
            if (_stage != Gen5SpirvStage.Compute)
            {
                return;
            }

            // Vulkan launches complete workgroups. The command path provides
            // the guest's exact exclusive thread bounds so invocations in a
            // partially populated final group stop before guest code executes.
            var block = _module.TypeStruct(_uvec3Type);
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            var blockPointer =
                _module.TypePointer(SpirvStorageClass.PushConstant, block);
            _pushConstantUintPointer =
                _module.TypePointer(SpirvStorageClass.PushConstant, _uintType);
            _computeDispatchLimit = _module.AddGlobalVariable(
                blockPointer,
                SpirvStorageClass.PushConstant);
            _module.AddName(_computeDispatchLimit, "dispatchThreadLimit");
            _interfaces.Add(_computeDispatchLimit);
        }

        private void DeclareScratch()
        {
            if (!UsesScratch())
            {
                return;
            }

            var scratchArrayType = _module.TypeArray(_uintType, ScratchDwordCount);
            var scratchPointer =
                _module.TypePointer(SpirvStorageClass.Private, scratchArrayType);
            _scratch = _module.AddGlobalVariable(
                scratchPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(scratchArrayType));
            _module.AddName(_scratch, "scratch");
            _interfaces.Add(_scratch);
        }

        private void DeclareLds()
        {
            if (!UsesLds() ||
                (_stage != Gen5SpirvStage.Compute && !UsesDsAddTid()))
            {
                return;
            }

            var ldsArrayType = _module.TypeArray(_uintType, LdsDwordCount);
            var storageClass = _stage == Gen5SpirvStage.Compute
                ? SpirvStorageClass.Workgroup
                : SpirvStorageClass.Private;
            var ldsPointer =
                _module.TypePointer(storageClass, ldsArrayType);
            _workgroupUintPointer =
                _module.TypePointer(storageClass, _uintType);
            _lds = storageClass == SpirvStorageClass.Private
                ? _module.AddGlobalVariable(
                    ldsPointer,
                    storageClass,
                    _module.ConstantNull(ldsArrayType))
                : _module.AddGlobalVariable(ldsPointer, storageClass);
            _module.AddName(_lds, "lds");
            _interfaces.Add(_lds);
        }

        private void DeclareWave64Scratch()
        {
            if (!_emulateWave64)
            {
                return;
            }

            // Each 64-lane guest wave owns one aligned slice. Workgroup barriers
            // still rendezvous every invocation, but distinct waves must never
            // overwrite one another's ballot or lane-exchange values.
            var localInvocationCount = checked(
                (uint)((ulong)_localSizeX * _localSizeY * _localSizeZ));
            var scratchDwordCount = checked((localInvocationCount + 63u) & ~63u);
            var scratchArrayType = _module.TypeArray(_uintType, scratchDwordCount);
            var scratchArrayPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, scratchArrayType);
            _waveScratchElementPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, _uintType);
            _waveScratch = _module.AddGlobalVariable(
                scratchArrayPointer,
                SpirvStorageClass.Workgroup);
            _module.AddName(_waveScratch, "guestWave64Scratch");
            _interfaces.Add(_waveScratch);
        }

        private void DeclareBuffers()
        {
            for (var index = 0; index < _evaluation.GlobalMemoryBindings.Count; index++)
            {
                foreach (var pc in _evaluation.GlobalMemoryBindings[index].InstructionPcs)
                {
                    _bufferBindingByPc.TryAdd(pc, _globalBufferBase + index);
                }
            }

            if (_evaluation.BufferFormatBindings is { } formatBindings)
            {
                foreach (var binding in formatBindings)
                {
                    _formatBindingByPc.TryAdd(
                        binding.Pc,
                        (binding.DataFormat, binding.NumberFormat));
                }
            }

            if (_totalGlobalBufferCount == 0)
            {
                return;
            }

            var runtimeArray = _module.TypeRuntimeArray(_uintType);
            _module.AddDecoration(runtimeArray, SpirvDecoration.ArrayStride, sizeof(uint));
            var block = _module.TypeStruct(runtimeArray);
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            var descriptors = _module.TypeArray(
                block,
                (uint)_totalGlobalBufferCount);
            var descriptorsPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, descriptors);
            _storageBlockPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, block);
            _storageUintPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, _uintType);
            _globalBuffers = _module.AddGlobalVariable(
                descriptorsPointer,
                SpirvStorageClass.StorageBuffer);
            _module.AddName(_globalBuffers, "guestBuffers");
            _module.AddDecoration(_globalBuffers, SpirvDecoration.DescriptorSet, 0);
            _module.AddDecoration(_globalBuffers, SpirvDecoration.Binding, 0);
            _interfaces.Add(_globalBuffers);
        }

        private void DeclareImages()
        {
            for (var index = 0; index < _evaluation.ImageBindings.Count; index++)
            {
                var binding = _evaluation.ImageBindings[index];
                _imageBindingByPc.TryAdd(binding.Pc, index);
                var isStorage = Gen5ShaderTranslator.RequiresStorageImage(
                    binding,
                    _evaluation.ImageBindings);
                var (format, componentKind) =
                    DecodeImageFormat(binding.ResourceDescriptor);
                var componentType = componentKind switch
                {
                    ImageComponentKind.Sint => _intType,
                    ImageComponentKind.Uint => _uintType,
                    _ => _floatType,
                };
                if (isStorage && format == SpirvImageFormat.Unknown)
                {
                    _module.AddCapability(
                        SpirvCapability.StorageImageReadWithoutFormat);
                    _module.AddCapability(
                        SpirvCapability.StorageImageWriteWithoutFormat);
                }
                else if (isStorage && RequiresExtendedStorageImageFormat(format))
                {
                    _module.AddCapability(
                        SpirvCapability.StorageImageExtendedFormats);
                }

                var isArrayed = !isStorage &&
                    Gen5ShaderTranslator.IsArrayedImageBinding(binding);
                var imageType = _module.TypeImage(
                    componentType,
                    GetImageDimension(binding.Control.Dimension),
                    depth: false,
                    arrayed: isArrayed,
                    multisampled: false,
                    sampled: isStorage ? 2u : 1u,
                    isStorage ? format : SpirvImageFormat.Unknown);
                var objectType = isStorage
                    ? imageType
                    : _module.TypeSampledImage(imageType);
                var pointer = _module.TypePointer(
                    SpirvStorageClass.UniformConstant,
                    objectType);
                var variable = _module.AddGlobalVariable(
                    pointer,
                    SpirvStorageClass.UniformConstant);
                _module.AddName(variable, isStorage ? $"image{index}" : $"tex{index}");
                _module.AddDecoration(variable, SpirvDecoration.DescriptorSet, 0);
                _module.AddDecoration(
                    variable,
                    SpirvDecoration.Binding,
                    (uint)(_imageBindingBase + index + 1));
                _imageResources.Add(
                    new SpirvImageResource(
                        variable,
                        imageType,
                        objectType,
                        componentType,
                        _module.TypeVector(componentType, 4),
                        format,
                        componentKind,
                        isStorage,
                        isArrayed));
                _interfaces.Add(variable);
            }
        }

        private static bool RequiresExtendedStorageImageFormat(
            SpirvImageFormat format) =>
            format is not SpirvImageFormat.Unknown and
                not SpirvImageFormat.Rgba32f and
                not SpirvImageFormat.Rgba32i and
                not SpirvImageFormat.Rgba32ui;

        private static (SpirvImageFormat Format, ImageComponentKind Kind)
            DecodeImageFormat(IReadOnlyList<uint> descriptor)
        {
            if (descriptor.Count < 2)
            {
                return (SpirvImageFormat.Unknown, ImageComponentKind.Float);
            }

            // RDNA2 replaced the legacy DATA_FORMAT/NUM_FORMAT pair with one
            // sparse nine-bit FORMAT field. Its upper bits are not a separate
            // number type; decode the field once before selecting SPIR-V types.
            var unifiedFormat = (descriptor[1] >> 20) & 0x1FFu;
            if (!Gfx10UnifiedFormat.TryDecode(
                    unifiedFormat,
                    out var dataFormat,
                    out var numberType))
            {
                return (
                    SpirvImageFormat.Unknown,
                    ImageComponentKind.Float);
            }

            var kind = numberType switch
            {
                4 => ImageComponentKind.Uint,
                5 => ImageComponentKind.Sint,
                _ => ImageComponentKind.Float,
            };
            return (DecodeStorageImageFormat(dataFormat, numberType), kind);
        }

        private void DeclareStageInterface()
        {
            if (UsesSubgroupOperations())
            {
                var subgroupPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uintType);
                if (_emulateWave64)
                {
                    _localInvocationIndexInput = _module.AddGlobalVariable(
                        subgroupPointer,
                        SpirvStorageClass.Input);
                    _module.AddDecoration(
                        _localInvocationIndexInput,
                        SpirvDecoration.BuiltIn,
                        (uint)SpirvBuiltIn.LocalInvocationIndex);
                    if (RequiresFlatInput(_stage, integerType: true))
                    {
                        _module.AddDecoration(
                            _localInvocationIndexInput,
                            SpirvDecoration.Flat);
                    }
                    _interfaces.Add(_localInvocationIndexInput);
                }
                else
                {
                    _subgroupInvocationIdInput = _module.AddGlobalVariable(
                        subgroupPointer,
                        SpirvStorageClass.Input);
                    _module.AddDecoration(
                        _subgroupInvocationIdInput,
                        SpirvDecoration.BuiltIn,
                        (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
                    if (RequiresFlatInput(_stage, integerType: true))
                    {
                        _module.AddDecoration(
                            _subgroupInvocationIdInput,
                            SpirvDecoration.Flat);
                    }
                    _interfaces.Add(_subgroupInvocationIdInput);
                }
            }

            if (_stage == Gen5SpirvStage.Vertex)
            {
                DeclareVertexInputs();

                var inputPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uintType);
                _vertexIndexInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _vertexIndexInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.VertexIndex);
                _interfaces.Add(_vertexIndexInput);

                _instanceIndexInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _instanceIndexInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.InstanceIndex);
                _interfaces.Add(_instanceIndexInput);

                var outputPointer =
                    _module.TypePointer(SpirvStorageClass.Output, _vec4Type);
                _positionOutput = _module.AddGlobalVariable(
                    outputPointer,
                    SpirvStorageClass.Output);
                _module.AddDecoration(
                    _positionOutput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.Position);
                _interfaces.Add(_positionOutput);

                var parameters = _state.Program.Instructions
                    .Select(instruction => instruction.Control)
                    .OfType<Gen5ExportControl>()
                    .Where(export => export.Target is >= 32 and < 64)
                    .Select(export => export.Target - 32)
                    .Distinct()
                    .Order()
                    .ToHashSet();
                for (uint parameter = 0;
                     parameter < (uint)Math.Max(_requiredVertexOutputCount, 0);
                     parameter++)
                {
                    parameters.Add(parameter);
                }

                foreach (var parameter in parameters)
                {
                    var variable = _module.AddGlobalVariable(
                        outputPointer,
                        SpirvStorageClass.Output);
                    _module.AddDecoration(variable, SpirvDecoration.Location, parameter);
                    _vertexOutputs.Add(parameter, variable);
                    _interfaces.Add(variable);
                }
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var inputVec4Pointer =
                    _module.TypePointer(SpirvStorageClass.Input, _vec4Type);
                var attributes = _state.Program.Instructions
                    .Select(instruction => instruction.Control)
                    .OfType<Gen5InterpolationControl>()
                    .Select(control => control.Attribute)
                    .Distinct()
                    .Order()
                    .ToArray();
                foreach (var attribute in attributes)
                {
                    var variable = _module.AddGlobalVariable(
                        inputVec4Pointer,
                        SpirvStorageClass.Input);
                    _module.AddDecoration(variable, SpirvDecoration.Location, attribute);
                    _pixelInputs.Add(attribute, variable);
                    _interfaces.Add(variable);
                }

                _fragCoordInput = _module.AddGlobalVariable(
                    inputVec4Pointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _fragCoordInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.FragCoord);
                _interfaces.Add(_fragCoordInput);

                foreach (var binding in _pixelOutputBindings)
                {
                    var outputType = GetPixelOutputType(binding.Kind);
                    var outputPointer =
                        _module.TypePointer(SpirvStorageClass.Output, outputType);
                    var variable = _module.AddGlobalVariable(
                        outputPointer,
                        SpirvStorageClass.Output);
                    _module.AddName(variable, $"mrt{binding.GuestSlot}");
                    _module.AddDecoration(
                        variable,
                        SpirvDecoration.Location,
                        binding.HostLocation);
                    _pixelOutputs.Add(
                        binding.GuestSlot,
                        new SpirvPixelOutput(variable, outputType, binding.Kind));
                    _interfaces.Add(variable);
                }
            }
            else
            {
                var inputPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uvec3Type);
                _localInvocationIdInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _localInvocationIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.LocalInvocationId);
                _workGroupIdInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _workGroupIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.WorkgroupId);
                _interfaces.Add(_localInvocationIdInput);
                _interfaces.Add(_workGroupIdInput);
            }
        }

        private void DeclareVertexInputs()
        {
            foreach (var input in _evaluation.VertexInputs ?? [])
            {
                var componentKind = input.NumberFormat switch
                {
                    4 => VertexInputComponentKind.Uint,
                    5 => VertexInputComponentKind.Sint,
                    _ => VertexInputComponentKind.Float,
                };
                var componentType = componentKind switch
                {
                    VertexInputComponentKind.Uint => _uintType,
                    VertexInputComponentKind.Sint => _intType,
                    _ => _floatType,
                };
                var type = input.ComponentCount switch
                {
                    1u => componentType,
                    >= 2u and <= 4u =>
                        _module.TypeVector(componentType, input.ComponentCount),
                    _ => 0u,
                };
                if (type == 0)
                {
                    continue;
                }

                var pointer = _module.TypePointer(SpirvStorageClass.Input, type);
                var variable = _module.AddGlobalVariable(
                    pointer,
                    SpirvStorageClass.Input);
                _module.AddName(variable, $"attr{input.Location}");
                _module.AddDecoration(
                    variable,
                    SpirvDecoration.Location,
                    input.Location);
                _vertexInputsByPc.TryAdd(
                    input.Pc,
                    new SpirvVertexInput(
                        variable,
                        type,
                        componentType,
                        input.ComponentCount,
                        componentKind));
                _interfaces.Add(variable);
            }
        }

        private void EmitInitialState()
        {
            if (_scalarRegisterBufferIndex >= 0)
            {
                for (uint index = 0; index < ScalarRegisterCount; index++)
                {
                    StoreS(index, LoadBufferWord(_scalarRegisterBufferIndex, UInt(index)));
                }
            }
            else
            {
                for (uint index = 0;
                     index < _evaluation.InitialScalarRegisters.Count &&
                     index < ScalarRegisterCount;
                     index++)
                {
                    var value = _evaluation.InitialScalarRegisters[(int)index];
                    if (value != 0)
                    {
                        StoreS(index, UInt(value));
                    }
                }
            }

            Store(_scc, _module.ConstantBool(false));
            if (HasGuestWaveLanes())
            {
                StoreWaveMask(106, _module.ConstantBool(false));
                StoreWaveMask(126, _module.ConstantBool(true));
            }
            else
            {
                Store(_vcc, _module.ConstantBool(false));
                Store(_exec, _module.ConstantBool(true));
            }
            Store(_programCounter, UInt(0));
            Store(_programActive, _module.ConstantBool(true));

            if (_stage == Gen5SpirvStage.Vertex)
            {
                StoreV(5, Load(_uintType, _vertexIndexInput), guardWithExec: false);
                StoreV(8, Load(_uintType, _instanceIndexInput), guardWithExec: false);
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var fragCoord = Load(_vec4Type, _fragCoordInput);
                EmitPixelInputState(fragCoord);
                foreach (var output in _pixelOutputs.Values)
                {
                    Store(output.Variable, _module.ConstantNull(output.Type));
                }
            }
            else
            {
                var localId = Load(_uvec3Type, _localInvocationIdInput);
                var workGroupId = Load(_uvec3Type, _workGroupIdInput);
                var invocationInBounds = _module.ConstantBool(true);
                for (uint component = 0; component < 3; component++)
                {
                    var localComponent = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        localId,
                        component);
                    StoreV(component, localComponent, guardWithExec: false);

                    var groupComponent = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        workGroupId,
                        component);
                    var localSize = component switch
                    {
                        0 => _localSizeX,
                        1 => _localSizeY,
                        _ => _localSizeZ,
                    };
                    var globalComponent = IAdd(
                        _module.AddInstruction(
                            SpirvOp.IMul,
                            _uintType,
                            groupComponent,
                            UInt(localSize)),
                        localComponent);
                    var limitPointer = _module.AddInstruction(
                        SpirvOp.AccessChain,
                        _pushConstantUintPointer,
                        _computeDispatchLimit,
                        UInt(0),
                        UInt(component));
                    var componentInBounds = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        globalComponent,
                        Load(_uintType, limitPointer));
                    invocationInBounds = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        invocationInBounds,
                        componentInBounds);
                }

                // Wave64 emulation uses workgroup barriers to rebuild EXEC/VCC.
                // Vulkan requires every invocation in the workgroup to reach
                // those barriers. Keep padded lanes in the interpreter loop,
                // but mask their guest-visible vector and memory effects out
                // through EXEC.
                if (_emulateWave64)
                {
                    StoreWaveMask(126, invocationInBounds);
                    Store(_programActive, _module.ConstantBool(true));
                }
                else
                {
                    Store(_programActive, invocationInBounds);
                }

                if (_state.ComputeSystemRegisters is { } registers)
                {
                    StoreComputeSystemRegister(
                        registers.WorkGroupXRegister,
                        workGroupId,
                        0);
                    StoreComputeSystemRegister(
                        registers.WorkGroupYRegister,
                        workGroupId,
                        1);
                    StoreComputeSystemRegister(
                        registers.WorkGroupZRegister,
                        workGroupId,
                        2);
                    if (registers.ThreadGroupSizeRegister is { } sizeRegister)
                    {
                        StoreS(
                            sizeRegister,
                            UInt(checked(_localSizeX * _localSizeY * _localSizeZ)));
                    }
                }
            }
        }

        private void EmitPixelInputState(uint fragCoord)
        {
            uint vgpr = 0;
            AdvancePixelInput(0, 2, ref vgpr);
            AdvancePixelInput(1, 2, ref vgpr);
            AdvancePixelInput(2, 2, ref vgpr);
            AdvancePixelInput(3, 3, ref vgpr);
            AdvancePixelInput(4, 2, ref vgpr);
            AdvancePixelInput(5, 2, ref vgpr);
            AdvancePixelInput(6, 2, ref vgpr);
            AdvancePixelInput(7, 1, ref vgpr);
            EmitPixelPositionInput(8, 0, fragCoord, ref vgpr);
            EmitPixelPositionInput(9, 1, fragCoord, ref vgpr);
            EmitPixelPositionInput(10, 2, fragCoord, ref vgpr);
            EmitPixelPositionInput(11, 3, fragCoord, ref vgpr);
            AdvancePixelInput(12, 1, ref vgpr);
            AdvancePixelInput(13, 1, ref vgpr);
            AdvancePixelInput(14, 1, ref vgpr);
            AdvancePixelInput(15, 1, ref vgpr);
        }

        private void AdvancePixelInput(int bit, uint dwordCount, ref uint vgpr)
        {
            if ((_pixelInputAddress & (1u << bit)) != 0)
            {
                vgpr += dwordCount;
            }
        }

        private void EmitPixelPositionInput(
            int bit,
            uint component,
            uint fragCoord,
            ref uint vgpr)
        {
            var mask = 1u << bit;
            if ((_pixelInputAddress & mask) == 0)
            {
                return;
            }

            if ((_pixelInputEnable & mask) != 0)
            {
                var value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    fragCoord,
                    component);
                StoreV(vgpr, Bitcast(_uintType, value), guardWithExec: false);
            }

            vgpr++;
        }

        private void StoreComputeSystemRegister(
            uint? register,
            uint workGroupId,
            uint component)
        {
            if (register is null)
            {
                return;
            }

            var value = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                workGroupId,
                component);
            StoreS(register.Value, value);
        }

        private bool TryEmitBlock(
            IReadOnlyList<ShaderBlock> blocks,
            int blockIndex,
            out string error)
        {
            error = string.Empty;
            var block = blocks[blockIndex];
            for (var index = block.StartIndex; index < block.EndIndex; index++)
            {
                var instruction = _state.Program.Instructions[index];
                if (IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm")
                {
                    continue;
                }

                if (!TryEmitInstruction(instruction, out error))
                {
                    error = $"pc=0x{instruction.Pc:X} {instruction.Opcode}: {error}";
                    return false;
                }
            }

            var terminator = _state.Program.Instructions[block.EndIndex - 1];
            if (terminator.Opcode == "SEndpgm")
            {
                Store(_programActive, _module.ConstantBool(false));
                return true;
            }

            var fallthrough = blockIndex + 1 < blocks.Count
                ? (uint)(blockIndex + 1)
                : uint.MaxValue;
            if (terminator.Opcode == "SBranch")
            {
                if (!TryGetBranchTargetPc(terminator, out var targetPc))
                {
                    error = "invalid scalar branch target";
                    return false;
                }

                if (IsExitBranchTarget(_state.Program.Instructions, targetPc))
                {
                    Store(_programActive, _module.ConstantBool(false));
                    return true;
                }

                if (!TryFindBlock(blocks, targetPc, out var targetBlock))
                {
                    error = $"invalid scalar branch target pc=0x{terminator.Pc:X} target=0x{targetPc:X} blocks={FormatBlockStarts(blocks)}";
                    return false;
                }

                Store(_programCounter, UInt((uint)targetBlock));
                return true;
            }

            if (terminator.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                var hasTarget = TryGetBranchTargetPc(terminator, out var targetPc);
                var targetBlock = -1;
                var hasTargetBlock = hasTarget && TryFindBlock(blocks, targetPc, out targetBlock);
                var targetExits = hasTarget && IsExitBranchTarget(_state.Program.Instructions, targetPc);
                var hasCondition = TryGetBranchCondition(terminator.Opcode, out var condition);
                if (!hasTarget || (!hasTargetBlock && !targetExits) || !hasCondition)
                {
                    error =
                        $"invalid conditional scalar branch opcode={terminator.Opcode} " +
                        $"pc=0x{terminator.Pc:X} " +
                        $"target={(hasTarget ? $"0x{targetPc:X}" : "invalid")} " +
                        $"target_block={(hasTargetBlock ? targetBlock.ToString() : targetExits ? "exit" : "missing")} " +
                        $"fallthrough={(fallthrough == uint.MaxValue ? "end" : fallthrough.ToString())} " +
                        $"condition={hasCondition} " +
                        $"blocks={FormatBlockStarts(blocks)}";
                    return false;
                }

                var takenBlock = targetExits ? uint.MaxValue : (uint)targetBlock;
                var selected = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    condition,
                    UInt(takenBlock),
                    UInt(fallthrough));
                Store(_programCounter, selected);
                return true;
            }

            if (fallthrough == uint.MaxValue)
            {
                Store(_programActive, _module.ConstantBool(false));
            }
            else
            {
                Store(_programCounter, UInt(fallthrough));
            }

            return true;
        }

        private static string FormatBlockStarts(IReadOnlyList<ShaderBlock> blocks)
        {
            const int maxBlocks = 32;
            var count = Math.Min(blocks.Count, maxBlocks);
            var starts = new string[count];
            for (var index = 0; index < count; index++)
            {
                starts[index] = $"0x{blocks[index].StartPc:X}";
            }

            return blocks.Count <= maxBlocks
                ? string.Join(",", starts)
                : string.Join(",", starts) + $",...({blocks.Count})";
        }

        private static bool IsExitBranchTarget(
            IReadOnlyList<Gen5ShaderInstruction> instructions,
            uint targetPc)
        {
            if (instructions.Count == 0)
            {
                return false;
            }

            var last = instructions[^1];
            var lastEndPc = last.Pc + (uint)(last.Words.Count * sizeof(uint));
            return targetPc >= lastEndPc;
        }

        private bool TryGetBranchCondition(string opcode, out uint condition)
        {
            condition = opcode switch
            {
                "SCbranchScc0" => LogicalNot(Load(_boolType, _scc)),
                "SCbranchScc1" => Load(_boolType, _scc),
                "SCbranchVccz" => LogicalNot(SubgroupAny(Load(_boolType, _vcc))),
                "SCbranchVccnz" => SubgroupAny(Load(_boolType, _vcc)),
                "SCbranchExecz" => LogicalNot(SubgroupAny(Load(_boolType, _exec))),
                "SCbranchExecnz" => SubgroupAny(Load(_boolType, _exec)),
                _ => 0,
            };
            return condition != 0;
        }

        private bool TryEmitInstruction(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode is
                "SNop" or
                "SWaitcnt" or
                "SInstPrefetch" or
                "STtraceData")
            {
                return true;
            }

            if (instruction.Opcode == "SBarrier")
            {
                var workgroup = UInt(2);
                var semantics = UInt(0x108);
                _module.AddStatement(
                    SpirvOp.ControlBarrier,
                    workgroup,
                    workgroup,
                    semantics);
                return true;
            }

            if (instruction.Control is Gen5ScalarMemoryControl scalarMemory)
            {
                return TryEmitScalarMemory(instruction, scalarMemory, out error);
            }

            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                return TryEmitInterpolation(instruction, interpolation, out error);
            }

            if (instruction.Control is Gen5ImageControl image)
            {
                return TryEmitImage(instruction, image, out error);
            }

            if (instruction.Control is Gen5GlobalMemoryControl globalMemory)
            {
                if (instruction.Opcode.StartsWith("Scratch", StringComparison.Ordinal))
                {
                    return TryEmitScratchMemory(instruction, globalMemory, out error);
                }

                return TryEmitGlobalMemory(instruction, globalMemory, out error);
            }

            if (instruction.Control is Gen5BufferMemoryControl bufferMemory)
            {
                return TryEmitBufferMemory(instruction, bufferMemory, out error);
            }

            if (instruction.Control is Gen5ExportControl export)
            {
                return TryEmitExport(instruction, export, out error);
            }

            if (instruction.Control is Gen5DataShareControl)
            {
                return TryEmitDataShare(instruction, out error);
            }

            if (instruction.Encoding is
                Gen5ShaderEncoding.Sop1 or
                Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopc or
                Gen5ShaderEncoding.Sopk)
            {
                return TryEmitScalarAlu(instruction, out error);
            }

            if (instruction.Encoding is
                Gen5ShaderEncoding.Sopp or
                Gen5ShaderEncoding.Smrd or
                Gen5ShaderEncoding.Smem)
            {
                return true;
            }

            return TryEmitVectorAlu(instruction, out error);
        }

        private bool TryEmitDataShare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Control is not Gen5DataShareControl control ||
                (_stage != Gen5SpirvStage.Compute &&
                 instruction.Opcode is not
                     ("DsWriteAddtidB32" or "DsReadAddtidB32")))
            {
                error = "invalid LDS instruction";
                return false;
            }

            if (control.Gds)
            {
                error = "GDS data share is not implemented";
                return false;
            }

            if (instruction.Opcode == "DsPermuteB32")
            {
                if (instruction.Sources.Count < 2 ||
                    instruction.Destinations.Count < 1)
                {
                    error = "missing DS forward permute operand";
                    return false;
                }

                var targetLane = EmitDsPermuteLane(instruction, control);
                var activeTargetLane = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    Load(_boolType, _exec),
                    targetLane,
                    UInt(_waveLaneCount));
                var destinationLane = GuestWaveLane();
                var winnerLane = UInt(_waveLaneCount);
                // Scan source lanes from low to high so a later matching lane
                // reproduces the LDS rule that the highest lane wins a collision.
                for (uint sourceLane = 0; sourceLane < _waveLaneCount; sourceLane++)
                {
                    var candidateTarget = WaveBroadcast(
                        activeTargetLane,
                        UInt(sourceLane));
                    var matches = _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        candidateTarget,
                        destinationLane);
                    winnerLane = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        matches,
                        UInt(sourceLane),
                        winnerLane);
                }

                var unwritten = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    winnerLane,
                    UInt(_waveLaneCount));
                var safeWinnerLane = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    unwritten,
                    UInt(0),
                    winnerLane);
                var winnerValue = WaveBroadcast(
                    GetRawSource(instruction, 1),
                    safeWinnerLane);
                var value = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    unwritten,
                    UInt(0),
                    winnerValue);
                StoreV(instruction.Destinations[0].Value, value);
                return true;
            }

            if (instruction.Opcode is "DsBpermuteB32" or "DsSwizzleB32")
            {
                var sourceIndex = instruction.Opcode == "DsBpermuteB32" ? 1 : 0;
                if (instruction.Sources.Count <= sourceIndex ||
                    instruction.Destinations.Count < 1)
                {
                    error = "missing DS lane operation operand";
                    return false;
                }

                var sourceLane = instruction.Opcode == "DsBpermuteB32"
                    ? EmitDsPermuteLane(instruction, control)
                    : EmitDsSwizzleSourceLane(control);
                var activeSource = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    Load(_boolType, _exec),
                    GetRawSource(instruction, sourceIndex),
                    UInt(0));
                var value = WaveBroadcast(
                    activeSource,
                    sourceLane);
                StoreV(instruction.Destinations[0].Value, value);
                return true;
            }

            if (_lds == 0 || _workgroupUintPointer == 0)
            {
                error = "invalid LDS memory instruction";
                return false;
            }

            switch (instruction.Opcode)
            {
                case "DsAddU32":
                case "DsSubU32":
                case "DsMinI32":
                case "DsMaxI32":
                case "DsMinU32":
                case "DsMaxU32":
                case "DsAndB32":
                case "DsOrB32":
                case "DsXorB32":
                case "DsAddRtnU32":
                case "DsSubRtnU32":
                case "DsMinRtnI32":
                case "DsMaxRtnI32":
                case "DsMinRtnU32":
                case "DsMaxRtnU32":
                case "DsAndRtnB32":
                case "DsOrRtnB32":
                case "DsXorRtnB32":
                case "DsWrxchgRtnB32":
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = "missing LDS atomic source";
                        return false;
                    }

                    var operation = instruction.Opcode switch
                    {
                        "DsAddU32" or "DsAddRtnU32" => SpirvOp.AtomicIAdd,
                        "DsSubU32" or "DsSubRtnU32" => SpirvOp.AtomicISub,
                        "DsMinI32" or "DsMinRtnI32" => SpirvOp.AtomicSMin,
                        "DsMaxI32" or "DsMaxRtnI32" => SpirvOp.AtomicSMax,
                        "DsMinU32" or "DsMinRtnU32" => SpirvOp.AtomicUMin,
                        "DsMaxU32" or "DsMaxRtnU32" => SpirvOp.AtomicUMax,
                        "DsAndB32" or "DsAndRtnB32" => SpirvOp.AtomicAnd,
                        "DsOrB32" or "DsOrRtnB32" => SpirvOp.AtomicOr,
                        "DsXorB32" or "DsXorRtnB32" => SpirvOp.AtomicXor,
                        _ => SpirvOp.AtomicExchange,
                    };
                    var returnsValue = instruction.Opcode.Contains("Rtn", StringComparison.Ordinal);
                    if (returnsValue && instruction.Destinations.Count < 1)
                    {
                        error = "missing LDS atomic destination";
                        return false;
                    }

                    EmitExecConditional(() =>
                    {
                        var original = _module.AddInstruction(
                            operation,
                            _uintType,
                            LdsPointer(
                                GetRawSource(instruction, 0),
                                EffectiveDsSingleOffsetBytes(control)),
                            UInt(2),
                            UInt(0x108),
                            GetRawSource(instruction, 1));
                        if (returnsValue)
                        {
                            StoreV(instruction.Destinations[0].Value, original);
                        }
                    });
                    return true;
                }
                case "DsCmpstB32":
                case "DsCmpstRtnB32":
                {
                    if (instruction.Sources.Count < 3)
                    {
                        error = "missing LDS compare-store source";
                        return false;
                    }

                    var returnsValue = instruction.Opcode == "DsCmpstRtnB32";
                    if (returnsValue && instruction.Destinations.Count < 1)
                    {
                        error = "missing LDS compare-store destination";
                        return false;
                    }

                    EmitExecConditional(() =>
                    {
                        var original = _module.AddInstruction(
                            SpirvOp.AtomicCompareExchange,
                            _uintType,
                            LdsPointer(
                                GetRawSource(instruction, 0),
                                EffectiveDsSingleOffsetBytes(control)),
                            UInt(2),
                            UInt(0x108),
                            UInt(0x102),
                            GetRawSource(instruction, 2),
                            GetRawSource(instruction, 1));
                        if (returnsValue)
                        {
                            StoreV(instruction.Destinations[0].Value, original);
                        }
                    });
                    return true;
                }
                case "DsRsubU32":
                case "DsIncU32":
                case "DsDecU32":
                case "DsMskorB32":
                case "DsRsubRtnU32":
                case "DsIncRtnU32":
                case "DsDecRtnU32":
                case "DsMskorRtnB32":
                case "DsWrapRtnB32":
                case "DsCmpstF32":
                case "DsMinF32":
                case "DsMaxF32":
                case "DsAddF32":
                case "DsCmpstRtnF32":
                case "DsMinRtnF32":
                case "DsMaxRtnF32":
                case "DsAddRtnF32":
                {
                    var usesData1 = instruction.Opcode.Contains(
                        "Mskor",
                        StringComparison.Ordinal) ||
                        instruction.Opcode == "DsWrapRtnB32" ||
                        instruction.Opcode.Contains("Cmpst", StringComparison.Ordinal);
                    if (instruction.Sources.Count < (usesData1 ? 3 : 2))
                    {
                        error = "missing compare/exchange LDS atomic source";
                        return false;
                    }

                    var returnsValue = instruction.Opcode.Contains(
                        "Rtn",
                        StringComparison.Ordinal);
                    if (returnsValue && instruction.Destinations.Count < 1)
                    {
                        error = "missing compare/exchange LDS atomic destination";
                        return false;
                    }

                    EmitExecConditional(() =>
                    {
                        var original = EmitAtomicCompareExchangeLoop(
                            LdsPointer(
                                GetRawSource(instruction, 0),
                                EffectiveDsSingleOffsetBytes(control)),
                            _uintType,
                            UInt(2),
                            UInt(0x102),
                            UInt(0x108),
                            UInt(0x102),
                            expected => EmitLdsCasDesiredValue(instruction, expected));
                        if (returnsValue)
                        {
                            StoreV(instruction.Destinations[0].Value, original);
                        }
                    });
                    return true;
                }
                case "DsWrxchg2RtnB32":
                case "DsWrxchg2St64RtnB32":
                {
                    if (instruction.Sources.Count < 3 ||
                        instruction.Destinations.Count < 2)
                    {
                        error = "missing paired LDS exchange operand";
                        return false;
                    }

                    var st64 = instruction.Opcode == "DsWrxchg2St64RtnB32";
                    EmitExecConditional(() =>
                    {
                        for (var pair = 0; pair < 2; pair++)
                        {
                            var offset = EffectiveDsPairOffsetBytes(
                                pair == 0 ? control.Offset0 : control.Offset1,
                                st64,
                                sizeof(uint));
                            var original = _module.AddInstruction(
                                SpirvOp.AtomicExchange,
                                _uintType,
                                LdsPointer(GetRawSource(instruction, 0), offset),
                                UInt(2),
                                UInt(0x108),
                                GetRawSource(instruction, pair + 1));
                            StoreV(instruction.Destinations[pair].Value, original);
                        }
                    });
                    return true;
                }
                case "DsWriteB8":
                case "DsWriteB16":
                case "DsWriteB8D16Hi":
                case "DsWriteB16D16Hi":
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = "missing LDS subword write source";
                        return false;
                    }

                    var componentBits = instruction.Opcode.StartsWith(
                        "DsWriteB8",
                        StringComparison.Ordinal) ? 8u : 16u;
                    var byteAddress = LdsByteAddress(
                        GetRawSource(instruction, 0),
                        EffectiveDsSingleOffsetBytes(control));
                    var pointer = LdsPointerFromByteAddress(byteAddress);
                    var bitOffset = ShiftLeftLogical(
                        BitwiseAnd(byteAddress, UInt(3)),
                        UInt(3));
                    var source = GetRawSource(instruction, 1);
                    if (instruction.Opcode.EndsWith("D16Hi", StringComparison.Ordinal))
                    {
                        source = ShiftRightLogical(source, UInt(16));
                    }

                    var inserted = _module.AddInstruction(
                        SpirvOp.BitFieldInsert,
                        _uintType,
                        Load(_uintType, pointer),
                        source,
                        bitOffset,
                        UInt(componentBits));
                    StoreLds(pointer, inserted);
                    return true;
                }
                case "DsWriteB32":
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = "missing LDS write source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    StoreLds(
                        LdsPointer(address, EffectiveDsSingleOffsetBytes(control)),
                        GetRawSource(instruction, 1));
                    return true;
                }
                case "DsWriteAddtidB32":
                {
                    if (instruction.Sources.Count < 1)
                    {
                        error = "missing LDS add-TID write source";
                        return false;
                    }

                    StoreLds(
                        LdsPointerFromByteAddress(EmitDsAddTidByteAddress(control)),
                        GetRawSource(instruction, 0));
                    return true;
                }
                case "DsWriteB64":
                case "DsWriteB96":
                case "DsWriteB128":
                {
                    var dwordCount = instruction.Opcode switch
                    {
                        "DsWriteB96" => 3,
                        "DsWriteB128" => 4,
                        _ => 2,
                    };
                    if (instruction.Sources.Count < dwordCount + 1)
                    {
                        error = "missing wide LDS write source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = EffectiveDsSingleOffsetBytes(control);
                    for (var component = 0; component < dwordCount; component++)
                    {
                        StoreLds(
                            LdsPointer(
                                address,
                                offset + ((uint)component * sizeof(uint))),
                            GetRawSource(instruction, component + 1));
                    }

                    return true;
                }
                case "DsWrite2B32":
                case "DsWrite2St64B32":
                case "DsWrite2B64":
                case "DsWrite2St64B64":
                {
                    var componentCount = instruction.Opcode.EndsWith(
                        "B64",
                        StringComparison.Ordinal) ? 2 : 1;
                    if (instruction.Sources.Count < 1 + (2 * componentCount))
                    {
                        error = "missing LDS write2 source";
                        return false;
                    }

                    var st64 = instruction.Opcode.Contains(
                        "St64",
                        StringComparison.Ordinal);
                    var elementBytes = (uint)componentCount * sizeof(uint);
                    var address = GetRawSource(instruction, 0);
                    for (var pair = 0; pair < 2; pair++)
                    {
                        var baseOffset = EffectiveDsPairOffsetBytes(
                            pair == 0 ? control.Offset0 : control.Offset1,
                            st64,
                            elementBytes);
                        for (var component = 0; component < componentCount; component++)
                        {
                            StoreLds(
                                LdsPointer(
                                    address,
                                    baseOffset + ((uint)component * sizeof(uint))),
                                GetRawSource(
                                    instruction,
                                    1 + (pair * componentCount) + component));
                        }
                    }

                    return true;
                }
                case "DsReadB32":
                {
                    if (instruction.Destinations.Count < 1 ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read operand";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var value = Load(
                        _uintType,
                        LdsPointer(address, EffectiveDsSingleOffsetBytes(control)));
                    StoreV(instruction.Destinations[0].Value, value);
                    return true;
                }
                case "DsReadAddtidB32":
                {
                    if (instruction.Destinations.Count < 1)
                    {
                        error = "missing LDS add-TID read destination";
                        return false;
                    }

                    var value = Load(
                        _uintType,
                        LdsPointerFromByteAddress(EmitDsAddTidByteAddress(control)));
                    StoreV(instruction.Destinations[0].Value, value);
                    return true;
                }
                case "DsReadB64":
                case "DsReadB96":
                case "DsReadB128":
                {
                    var dwordCount = instruction.Opcode switch
                    {
                        "DsReadB96" => 3,
                        "DsReadB128" => 4,
                        _ => 2,
                    };
                    if (instruction.Destinations.Count < dwordCount ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing wide LDS read operand";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = EffectiveDsSingleOffsetBytes(control);
                    for (var component = 0; component < dwordCount; component++)
                    {
                        StoreV(
                            instruction.Destinations[component].Value,
                            Load(
                                _uintType,
                                LdsPointer(
                                    address,
                                    offset + ((uint)component * sizeof(uint)))));
                    }

                    return true;
                }
                case "DsReadI8":
                case "DsReadU8":
                case "DsReadI16":
                case "DsReadU16":
                case "DsReadU8D16":
                case "DsReadU8D16Hi":
                case "DsReadI8D16":
                case "DsReadI8D16Hi":
                case "DsReadU16D16":
                case "DsReadU16D16Hi":
                {
                    if (instruction.Destinations.Count < 1 ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS subword read operand";
                        return false;
                    }

                    var componentBits = instruction.Opcode.StartsWith(
                            "DsReadI16",
                            StringComparison.Ordinal) ||
                        instruction.Opcode.StartsWith(
                            "DsReadU16",
                            StringComparison.Ordinal)
                        ? 16u
                        : 8u;
                    var signed = instruction.Opcode.StartsWith(
                        "DsReadI",
                        StringComparison.Ordinal);
                    var byteAddress = LdsByteAddress(
                        GetRawSource(instruction, 0),
                        EffectiveDsSingleOffsetBytes(control));
                    var word = Load(_uintType, LdsPointerFromByteAddress(byteAddress));
                    var bitOffset = ShiftLeftLogical(
                        BitwiseAnd(byteAddress, UInt(3)),
                        UInt(3));
                    var value = _module.AddInstruction(
                        signed ? SpirvOp.BitFieldSExtract : SpirvOp.BitFieldUExtract,
                        signed ? _intType : _uintType,
                        signed ? Bitcast(_intType, word) : word,
                        bitOffset,
                        UInt(componentBits));
                    value = signed ? Bitcast(_uintType, value) : value;
                    if (instruction.Opcode.Contains("D16", StringComparison.Ordinal))
                    {
                        value = _module.AddInstruction(
                            SpirvOp.BitFieldInsert,
                            _uintType,
                            LoadV(instruction.Destinations[0].Value),
                            value,
                            UInt(instruction.Opcode.EndsWith(
                                "Hi",
                                StringComparison.Ordinal) ? 16u : 0u),
                            UInt(16));
                    }

                    StoreV(instruction.Destinations[0].Value, value);
                    return true;
                }
                case "DsRead2B32":
                case "DsRead2St64B32":
                case "DsRead2B64":
                case "DsRead2St64B64":
                {
                    var componentCount = instruction.Opcode.EndsWith(
                        "B64",
                        StringComparison.Ordinal) ? 2 : 1;
                    if (instruction.Destinations.Count < 2 * componentCount ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read2 operand";
                        return false;
                    }

                    var st64 = instruction.Opcode.Contains(
                        "St64",
                        StringComparison.Ordinal);
                    var elementBytes = (uint)componentCount * sizeof(uint);
                    var address = GetRawSource(instruction, 0);
                    for (var pair = 0; pair < 2; pair++)
                    {
                        var baseOffset = EffectiveDsPairOffsetBytes(
                            pair == 0 ? control.Offset0 : control.Offset1,
                            st64,
                            elementBytes);
                        for (var component = 0; component < componentCount; component++)
                        {
                            StoreV(
                                instruction.Destinations[
                                    (pair * componentCount) + component].Value,
                                Load(
                                    _uintType,
                                    LdsPointer(
                                        address,
                                        baseOffset +
                                            ((uint)component * sizeof(uint)))));
                        }
                    }

                    return true;
                }
                default:
                    error = $"unsupported LDS opcode {instruction.Opcode}";
                    return false;
            }
        }

        private uint EmitDsPermuteLane(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control)
        {
            var byteIndex = LdsByteAddress(
                GetRawSource(instruction, 0),
                EffectiveDsSingleOffsetBytes(control));
            return BitwiseAnd(
                ShiftRightLogical(byteIndex, UInt(2)),
                UInt(_waveLaneCount - 1));
        }

        private uint EmitAtomicCompareExchangeLoop(
            uint pointer,
            uint valueType,
            uint scope,
            uint loadMemorySemantics,
            uint equalMemorySemantics,
            uint unequalMemorySemantics,
            Func<uint, uint> emitDesiredValue)
        {
            var loopHeader = _module.AllocateId();
            var loopBody = _module.AllocateId();
            var loopContinue = _module.AllocateId();
            var loopMerge = _module.AllocateId();
            _module.AddStatement(SpirvOp.Branch, loopHeader);
            _module.AddLabel(loopHeader);
            _module.AddStatement(
                SpirvOp.LoopMerge,
                loopMerge,
                loopContinue,
                0);
            _module.AddStatement(SpirvOp.Branch, loopBody);
            _module.AddLabel(loopBody);
            var expected = _module.AddInstruction(
                SpirvOp.AtomicLoad,
                valueType,
                pointer,
                scope,
                loadMemorySemantics);
            var desired = emitDesiredValue(expected);
            var observed = _module.AddInstruction(
                SpirvOp.AtomicCompareExchange,
                valueType,
                pointer,
                scope,
                equalMemorySemantics,
                unequalMemorySemantics,
                desired,
                expected);
            var succeeded = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                observed,
                expected);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                succeeded,
                loopMerge,
                loopContinue);
            _module.AddLabel(loopContinue);
            _module.AddStatement(SpirvOp.Branch, loopHeader);
            _module.AddLabel(loopMerge);
            return observed;
        }

        private uint EmitLdsCasDesiredValue(
            Gen5ShaderInstruction instruction,
            uint expected)
        {
            var data0 = GetRawSource(instruction, 1);
            switch (instruction.Opcode)
            {
                case "DsRsubU32":
                case "DsRsubRtnU32":
                    return _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        data0,
                        expected);
                case "DsIncU32":
                case "DsIncRtnU32":
                {
                    var wraps = _module.AddInstruction(
                        SpirvOp.UGreaterThanEqual,
                        _boolType,
                        expected,
                        data0);
                    return _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        wraps,
                        UInt(0),
                        IAdd(expected, UInt(1)));
                }
                case "DsDecU32":
                case "DsDecRtnU32":
                {
                    var isZero = _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        expected,
                        UInt(0));
                    var aboveLimit = _module.AddInstruction(
                        SpirvOp.UGreaterThan,
                        _boolType,
                        expected,
                        data0);
                    var wraps = _module.AddInstruction(
                        SpirvOp.LogicalOr,
                        _boolType,
                        isZero,
                        aboveLimit);
                    return _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        wraps,
                        data0,
                        _module.AddInstruction(
                            SpirvOp.ISub,
                            _uintType,
                            expected,
                            UInt(1)));
                }
                case "DsWrapRtnB32":
                {
                    var subtracts = _module.AddInstruction(
                        SpirvOp.UGreaterThanEqual,
                        _boolType,
                        expected,
                        data0);
                    return _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        subtracts,
                        _module.AddInstruction(
                            SpirvOp.ISub,
                            _uintType,
                            expected,
                            data0),
                        IAdd(expected, GetRawSource(instruction, 2)));
                }
                case "DsCmpstF32":
                case "DsCmpstRtnF32":
                {
                    var matches = _module.AddInstruction(
                        SpirvOp.FOrdEqual,
                        _boolType,
                        Bitcast(_floatType, expected),
                        Bitcast(_floatType, data0));
                    return _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        matches,
                        GetRawSource(instruction, 2),
                        expected);
                }
                case "DsMinF32":
                case "DsMinRtnF32":
                case "DsMaxF32":
                case "DsMaxRtnF32":
                {
                    var comparison = _module.AddInstruction(
                        instruction.Opcode.Contains("Min", StringComparison.Ordinal)
                            ? SpirvOp.FOrdLessThan
                            : SpirvOp.FOrdGreaterThan,
                        _boolType,
                        Bitcast(_floatType, data0),
                        Bitcast(_floatType, expected));
                    return _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        comparison,
                        data0,
                        expected);
                }
                case "DsAddF32":
                case "DsAddRtnF32":
                    return Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.FAdd,
                            _floatType,
                            Bitcast(_floatType, expected),
                            Bitcast(_floatType, data0)));
                default:
                    return BitwiseOr(
                        BitwiseAnd(
                            expected,
                            _module.AddInstruction(SpirvOp.Not, _uintType, data0)),
                        GetRawSource(instruction, 2));
            }
        }

        private uint EmitDsAddTidByteAddress(Gen5DataShareControl control)
        {
            var laneOffset = ShiftLeftLogical(
                GuestWaveLane(),
                UInt(2));
            return LdsByteAddress(
                IAdd(LoadS(M0Register), laneOffset),
                EffectiveDsSingleOffsetBytes(control));
        }

        private uint EmitDsSwizzleSourceLane(Gen5DataShareControl control)
        {
            var offset = EffectiveDsSingleOffsetBytes(control);
            var lane = GuestWaveLane();
            var waveHalf = BitwiseAnd(lane, UInt(32));
            var laneInHalf = BitwiseAnd(lane, UInt(31));
            if (offset >= 0xE000)
            {
                var mask = offset & 0x1F;
                var reversedLane = ShiftRightLogical(
                    _module.AddInstruction(
                        SpirvOp.BitReverse,
                        _uintType,
                        laneInHalf),
                    UInt(27));
                var compactedLane = ShiftRightLogical(
                    reversedLane,
                    UInt((uint)System.Numerics.BitOperations.PopCount(mask)));
                return BitwiseOr(
                    waveHalf,
                    BitwiseOr(
                        compactedLane,
                        BitwiseAnd(laneInHalf, UInt(mask))));
            }

            if (offset >= 0xC000)
            {
                var mask = offset & 0x1F;
                var rotate = (offset >> 5) & 0x1F;
                var rotatedLane = (offset & 0x400) == 0
                    ? IAdd(laneInHalf, UInt(rotate))
                    : _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        laneInHalf,
                        UInt(rotate));
                return BitwiseOr(
                    waveHalf,
                    BitwiseOr(
                        BitwiseAnd(laneInHalf, UInt(mask)),
                        BitwiseAnd(
                            rotatedLane,
                            UInt(31u & ~mask))));
            }

            if ((offset & 0x8000) != 0)
            {
                var localLane = BitwiseAnd(laneInHalf, UInt(3));
                var selector = BitwiseAnd(
                    ShiftRightLogical(
                        UInt(offset),
                        ShiftLeftLogical(localLane, UInt(1))),
                    UInt(3));
                return BitwiseOr(
                    waveHalf,
                    BitwiseOr(
                        BitwiseAnd(laneInHalf, UInt(28)),
                        selector));
            }

            var andMask = offset & 0x1F;
            var orMask = (offset >> 5) & 0x1F;
            var xorMask = (offset >> 10) & 0x1F;
            return BitwiseOr(
                waveHalf,
                BitwiseXor(
                    BitwiseOr(
                        BitwiseAnd(laneInHalf, UInt(andMask)),
                        UInt(orMask)),
                    UInt(xorMask)));
        }

        // Regular DS offsets are bytes. The read2/write2 families instead
        // scale each offset by the element width (and ST64 adds a 64x stride).
        private static uint EffectiveDsSingleOffsetBytes(Gen5DataShareControl control) =>
            control.Offset0 | (control.Offset1 << 8);

        private static uint EffectiveDsPairOffsetBytes(
            uint offset,
            bool st64,
            uint elementBytes) =>
            offset * (st64 ? 64u * elementBytes : elementBytes);

        private uint LdsByteAddress(uint address, uint offsetBytes) =>
            offsetBytes == 0 ? address : IAdd(address, UInt(offsetBytes));

        private uint LdsPointer(uint address, uint offsetBytes) =>
            LdsPointerFromByteAddress(LdsByteAddress(address, offsetBytes));

        private uint LdsPointerFromByteAddress(uint byteAddress)
        {
            var index = ShiftRightLogical(byteAddress, UInt(2));
            return _module.AddInstruction(
                SpirvOp.AccessChain,
                _workgroupUintPointer,
                _lds,
                index);
        }

        private void StoreLds(uint pointer, uint value)
        {
            var active = Load(_boolType, _exec);
            var oldValue = Load(_uintType, pointer);
            var selected = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                active,
                value,
                oldValue);
            Store(pointer, selected);
        }

        private bool TryEmitInterpolation(
            Gen5ShaderInstruction instruction,
            Gen5InterpolationControl interpolation,
            out string error)
        {
            error = string.Empty;
            if (_stage != Gen5SpirvStage.Pixel ||
                !_pixelInputs.TryGetValue(interpolation.Attribute, out var input) ||
                !TryGetVectorDestination(instruction, out var destination))
            {
                error = "invalid interpolated attribute";
                return false;
            }

            var vector = Load(_vec4Type, input);
            var component = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                vector,
                interpolation.Channel);
            var result = instruction.Opcode == "VInterpP2F16"
                ? BitwiseAnd(PackHalf2(component, Float(0)), UInt(0xFFFF))
                : Bitcast(_uintType, component);
            StoreV(destination, result);
            return true;
        }

        private bool TryEmitScalarMemory(
            Gen5ShaderInstruction instruction,
            Gen5ScalarMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (!_bufferBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex))
            {
                foreach (var destination in instruction.Destinations)
                {
                    if (destination.Kind == Gen5OperandKind.ScalarRegister)
                    {
                        StoreS(destination.Value, UInt(0));
                    }
                }

                return true;
            }

            var dynamicOffset = control.DynamicOffsetRegister is { } register
                ? LoadS(register)
                : UInt(0);
            var byteAddress = IAdd(
                dynamicOffset,
                UInt(unchecked((uint)control.ImmediateOffsetBytes)));
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
            for (var index = 0; index < instruction.Destinations.Count; index++)
            {
                var destination = instruction.Destinations[index];
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    error = "invalid scalar-memory destination";
                    return false;
                }

                var address = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt((uint)index));
                StoreS(destination.Value, LoadBufferWord(bindingIndex, address));
            }

            return true;
        }

        private bool TryEmitScratchMemory(
            Gen5ShaderInstruction instruction,
            Gen5GlobalMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (_scratch == 0)
            {
                error = "invalid scratch-memory instruction";
                return false;
            }

            var dynamicOffset = control.ScalarAddress == 0x7Fu
                ? LoadV(control.VectorAddress)
                : LoadS(control.ScalarAddress);
            var byteAddress = IAdd(
                dynamicOffset,
                UInt(unchecked((uint)control.OffsetBytes)));

            if (instruction.Opcode.StartsWith(
                    "ScratchAtomic",
                    StringComparison.Ordinal))
            {
                var original = LoadScratchDword(byteAddress);
                StoreScratchDword(
                    byteAddress,
                    EmitScratchAtomic32(
                        instruction.Opcode,
                        original,
                        control.VectorData));
                if (control.Glc)
                {
                    StoreV(control.VectorDestination, original);
                }

                return true;
            }

            if (instruction.Opcode is
                "ScratchLoadUbyteD16" or "ScratchLoadUbyteD16Hi" or
                "ScratchLoadSbyteD16" or "ScratchLoadSbyteD16Hi" or
                "ScratchLoadShortD16" or "ScratchLoadShortD16Hi")
            {
                StoreV(
                    control.VectorDestination,
                    InsertD16RegisterHalf(
                        control.VectorDestination,
                        LoadScratchSubword(
                            byteAddress,
                            instruction.Opcode.Contains("byte", StringComparison.Ordinal)
                                ? 8u
                                : 16u,
                            instruction.Opcode.Contains("Sbyte", StringComparison.Ordinal)),
                        instruction.Opcode.EndsWith("Hi", StringComparison.Ordinal)));
                return true;
            }

            if (instruction.Opcode is
                "ScratchLoadUbyte" or "ScratchLoadSbyte" or
                "ScratchLoadUshort" or "ScratchLoadSshort")
            {
                StoreV(
                    control.VectorDestination,
                    LoadScratchSubword(
                        byteAddress,
                        instruction.Opcode.EndsWith(
                            "short",
                            StringComparison.OrdinalIgnoreCase)
                            ? 16u
                            : 8u,
                        instruction.Opcode.Contains("LoadS", StringComparison.Ordinal)));
                return true;
            }

            if (instruction.Opcode is
                "ScratchStoreByteD16Hi" or "ScratchStoreShortD16Hi")
            {
                StoreScratchSubword(
                    byteAddress,
                    ShiftRightLogical(LoadV(control.VectorData), UInt(16)),
                    instruction.Opcode == "ScratchStoreShortD16Hi" ? 16u : 8u);
                return true;
            }

            if (instruction.Opcode is "ScratchStoreByte" or "ScratchStoreShort")
            {
                StoreScratchSubword(
                    byteAddress,
                    LoadV(control.VectorData),
                    instruction.Opcode == "ScratchStoreShort" ? 16u : 8u);
                return true;
            }

            if (instruction.Opcode.StartsWith(
                    "ScratchStoreDword",
                    StringComparison.Ordinal))
            {
                for (uint index = 0; index < control.DwordCount; index++)
                {
                    StoreScratchDword(
                        index == 0
                            ? byteAddress
                            : IAdd(byteAddress, UInt(index * sizeof(uint))),
                        LoadV(control.VectorData + index));
                }

                return true;
            }

            if (instruction.Opcode.StartsWith(
                    "ScratchLoadDword",
                    StringComparison.Ordinal))
            {
                for (uint index = 0; index < control.DwordCount; index++)
                {
                    StoreV(
                        control.VectorDestination + index,
                        LoadScratchDword(
                            index == 0
                                ? byteAddress
                                : IAdd(byteAddress, UInt(index * sizeof(uint)))));
                }

                return true;
            }

            error = $"unsupported scratch-memory opcode {instruction.Opcode}";
            return false;
        }

        private uint EmitScratchAtomic32(
            string opcode,
            uint original,
            uint vectorData)
        {
            var operation = opcode["ScratchAtomic".Length..];
            var data = LoadV(vectorData);
            return operation switch
            {
                "Swap" => data,
                "Cmpswap" => _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        original,
                        LoadV(vectorData + 1)),
                    data,
                    original),
                "Add" => IAdd(original, data),
                "Sub" => _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    original,
                    data),
                "Smin" => SelectScratchAtomicMinMax(
                    SpirvOp.SLessThan,
                    original,
                    data),
                "Umin" => SelectScratchAtomicMinMax(
                    SpirvOp.ULessThan,
                    original,
                    data),
                "Smax" => SelectScratchAtomicMinMax(
                    SpirvOp.SGreaterThan,
                    original,
                    data),
                "Umax" => SelectScratchAtomicMinMax(
                    SpirvOp.UGreaterThan,
                    original,
                    data),
                "And" => BitwiseAnd(original, data),
                "Or" => BitwiseOr(original, data),
                "Xor" => BitwiseXor(original, data),
                "Inc" or "Dec" => EmitAtomicIncDecDesiredValue(
                    operation == "Inc",
                    vectorData,
                    original),
                _ => throw new InvalidOperationException(
                    $"unsupported scratch-memory atomic {opcode}"),
            };
        }

        private uint SelectScratchAtomicMinMax(
            SpirvOp comparison,
            uint original,
            uint data) =>
            _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    comparison,
                    _boolType,
                    data,
                    original),
                data,
                original);

        private uint LoadScratchSubword(
            uint byteAddress,
            uint componentBits,
            bool signed) =>
            ExtractBufferSubword(
                LoadScratchDword(byteAddress),
                UInt(0),
                componentBits,
                signed);

        private void StoreScratchSubword(
            uint byteAddress,
            uint value,
            uint componentBits)
        {
            var existing = LoadScratchDword(byteAddress);
            StoreScratchDword(
                byteAddress,
                _module.AddInstruction(
                    SpirvOp.BitFieldInsert,
                    _uintType,
                    existing,
                    value,
                    UInt(0),
                    UInt(componentBits)));
        }

        private uint LoadScratchDword(uint byteAddress)
        {
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
            var byteOffset = BitwiseAnd(byteAddress, UInt(3));
            var bitOffset = ShiftLeftLogical(byteOffset, UInt(3));
            var alignedLabel = _module.AllocateId();
            var unalignedLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var aligned = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                byteOffset,
                UInt(0));
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                aligned,
                alignedLabel,
                unalignedLabel);

            _module.AddLabel(alignedLabel);
            var alignedValue = LoadScratchWord(dwordAddress);
            _module.AddStatement(SpirvOp.Branch, mergeLabel);

            _module.AddLabel(unalignedLabel);
            var firstWord = LoadScratchWord(dwordAddress);
            var secondWord = LoadScratchWord(IAdd(dwordAddress, UInt(1)));
            var firstBits = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                UInt(32),
                bitOffset);
            var unalignedValue = BitwiseOr(
                ShiftRightLogical(firstWord, bitOffset),
                ShiftLeftLogical(secondWord, firstBits));
            _module.AddStatement(SpirvOp.Branch, mergeLabel);

            _module.AddLabel(mergeLabel);
            return _module.AddInstruction(
                SpirvOp.Phi,
                _uintType,
                alignedValue,
                alignedLabel,
                unalignedValue,
                unalignedLabel);
        }

        private void StoreScratchDword(uint byteAddress, uint value)
        {
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
            var byteOffset = BitwiseAnd(byteAddress, UInt(3));
            var bitOffset = ShiftLeftLogical(byteOffset, UInt(3));
            var alignedLabel = _module.AllocateId();
            var unalignedLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var aligned = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                byteOffset,
                UInt(0));
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                aligned,
                alignedLabel,
                unalignedLabel);

            _module.AddLabel(alignedLabel);
            StoreScratchWord(dwordAddress, value);
            _module.AddStatement(SpirvOp.Branch, mergeLabel);

            _module.AddLabel(unalignedLabel);
            var firstBits = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                UInt(32),
                bitOffset);
            var firstWord = LoadScratchWord(dwordAddress);
            StoreScratchWord(
                dwordAddress,
                _module.AddInstruction(
                    SpirvOp.BitFieldInsert,
                    _uintType,
                    firstWord,
                    value,
                    bitOffset,
                    firstBits));
            var secondAddress = IAdd(dwordAddress, UInt(1));
            var secondWord = LoadScratchWord(secondAddress);
            StoreScratchWord(
                secondAddress,
                _module.AddInstruction(
                    SpirvOp.BitFieldInsert,
                    _uintType,
                    secondWord,
                    ShiftRightLogical(value, firstBits),
                    UInt(0),
                    bitOffset));
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private uint LoadScratchWord(uint dwordAddress)
        {
            var inRange = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                dwordAddress,
                UInt(ScratchDwordCount));
            var safeAddress = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                inRange,
                dwordAddress,
                UInt(0));
            var value = Load(_uintType, ScratchPointer(safeAddress));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                inRange,
                value,
                UInt(0));
        }

        private void StoreScratchWord(uint dwordAddress, uint value)
        {
            var inRange = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                dwordAddress,
                UInt(ScratchDwordCount));
            var safeAddress = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                inRange,
                dwordAddress,
                UInt(0));
            var pointer = ScratchPointer(safeAddress);
            var activeAndInRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                Load(_boolType, _exec),
                inRange);
            Store(
                pointer,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    activeAndInRange,
                    value,
                    Load(_uintType, pointer)));
        }

        private uint ScratchPointer(uint dwordAddress) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _scratch,
                dwordAddress);

        private bool TryEmitGlobalMemory(
            Gen5ShaderInstruction instruction,
            Gen5GlobalMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (!_bufferBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex))
            {
                error = "missing global-memory binding";
                return false;
            }

            var byteAddress = IAdd(
                LoadV(control.VectorAddress),
                UInt(unchecked((uint)control.OffsetBytes)));
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));

            if (instruction.Opcode is
                "GlobalLoadUbyteD16" or "GlobalLoadUbyteD16Hi" or
                "GlobalLoadSbyteD16" or "GlobalLoadSbyteD16Hi" or
                "GlobalLoadShortD16" or "GlobalLoadShortD16Hi")
            {
                StoreV(
                    control.VectorDestination,
                    InsertD16RegisterHalf(
                        control.VectorDestination,
                        EmitBufferSubwordLoad(
                            bindingIndex,
                            byteAddress,
                            instruction.Opcode.Contains("byte", StringComparison.Ordinal)
                                ? 8u
                                : 16u,
                            instruction.Opcode.Contains("Sbyte", StringComparison.Ordinal)),
                        instruction.Opcode.EndsWith("Hi", StringComparison.Ordinal)));
                return true;
            }

            if (instruction.Opcode is
                "GlobalLoadUbyte" or "GlobalLoadSbyte" or
                "GlobalLoadUshort" or "GlobalLoadSshort")
            {
                StoreV(
                    control.VectorDestination,
                    EmitBufferSubwordLoad(
                        bindingIndex,
                        byteAddress,
                        instruction.Opcode.EndsWith("short", StringComparison.OrdinalIgnoreCase)
                            ? 16u
                            : 8u,
                        instruction.Opcode.Contains("LoadS", StringComparison.Ordinal)));
                return true;
            }

            if (instruction.Opcode.StartsWith("GlobalAtomic", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    var original = EmitStorageBufferAtomic32(
                        instruction.Opcode,
                        BufferWordPointer(bindingIndex, dwordAddress),
                        control.VectorData);
                    if (control.Glc)
                    {
                        StoreV(control.VectorDestination, original);
                    }
                });

                return true;
            }

            if (instruction.Opcode is
                "GlobalStoreByteD16Hi" or "GlobalStoreShortD16Hi")
            {
                EmitExecConditional(() =>
                    EmitBufferSubwordStore(
                        bindingIndex,
                        byteAddress,
                        ShiftRightLogical(LoadV(control.VectorData), UInt(16)),
                        instruction.Opcode == "GlobalStoreShortD16Hi" ? 16u : 8u));
                return true;
            }

            if (instruction.Opcode is "GlobalStoreByte" or "GlobalStoreShort")
            {
                EmitExecConditional(() =>
                    EmitBufferSubwordStore(
                        bindingIndex,
                        byteAddress,
                        LoadV(control.VectorData),
                        instruction.Opcode == "GlobalStoreShort" ? 16u : 8u));
                return true;
            }

            if (instruction.Opcode.StartsWith("GlobalStoreDword", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        var address = index == 0
                            ? dwordAddress
                            : IAdd(dwordAddress, UInt(index));
                        StoreBufferWord(
                            bindingIndex,
                            address,
                            LoadV(control.VectorData + index));
                    }
                });
                return true;
            }

            if (!instruction.Opcode.StartsWith("GlobalLoadDword", StringComparison.Ordinal))
            {
                error = $"unsupported global-memory opcode {instruction.Opcode}";
                return false;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                var address = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt(index));
                StoreV(
                    control.VectorDestination + index,
                    LoadBufferWord(bindingIndex, address));
            }

            return true;
        }

        private bool TryEmitBufferMemory(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (_stage == Gen5SpirvStage.Vertex &&
                _vertexInputsByPc.TryGetValue(instruction.Pc, out var vertexInput))
            {
                return TryEmitVertexInputFetch(control, vertexInput, out error);
            }

            if (_stage == Gen5SpirvStage.Vertex &&
                IsFormatBufferLoad(instruction.Opcode) &&
                !_bufferBindingByPc.ContainsKey(instruction.Pc))
            {
                error = $"missing vertex input for {instruction.Opcode} pc=0x{instruction.Pc:X}";
                return false;
            }

            if (!_bufferBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex))
            {
                error = "missing buffer-memory binding";
                return false;
            }

            var scalarOffset = instruction.Sources.Count > 2
                ? GetRawSource(instruction, 2)
                : UInt(0);
            var stride = ShiftRightLogical(LoadS(control.ScalarResource + 1), UInt(16));
            stride = BitwiseAnd(stride, UInt(0x3FFF));
            var vectorIndex = control.IndexEnabled
                ? LoadV(control.VectorAddress)
                : UInt(0);
            var vectorOffset = control.OffsetEnabled
                ? LoadV(control.VectorAddress + (control.IndexEnabled ? 1u : 0u))
                : UInt(0);
            var byteAddress = IAdd(
                UInt(unchecked((uint)control.OffsetBytes)),
                scalarOffset);
            byteAddress = IAdd(byteAddress, vectorOffset);
            byteAddress = IAdd(
                byteAddress,
                _module.AddInstruction(SpirvOp.IMul, _uintType, vectorIndex, stride));
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));

            if (instruction.Opcode is
                "BufferLoadUbyteD16" or "BufferLoadUbyteD16Hi" or
                "BufferLoadSbyteD16" or "BufferLoadSbyteD16Hi" or
                "BufferLoadShortD16" or "BufferLoadShortD16Hi")
            {
                StoreV(
                    control.VectorData,
                    InsertD16RegisterHalf(
                        control.VectorData,
                        EmitBufferSubwordLoad(
                            bindingIndex,
                            byteAddress,
                            instruction.Opcode.Contains("byte", StringComparison.Ordinal)
                                ? 8u
                                : 16u,
                            instruction.Opcode.Contains("Sbyte", StringComparison.Ordinal)),
                        instruction.Opcode.EndsWith("Hi", StringComparison.Ordinal)));
                return true;
            }

            if (instruction.Opcode is
                "BufferLoadUbyte" or "BufferLoadSbyte" or
                "BufferLoadUshort" or "BufferLoadSshort")
            {
                StoreV(
                    control.VectorData,
                    EmitBufferSubwordLoad(
                        bindingIndex,
                        byteAddress,
                        instruction.Opcode.EndsWith("short", StringComparison.OrdinalIgnoreCase)
                            ? 16u
                            : 8u,
                        instruction.Opcode.Contains("LoadS", StringComparison.Ordinal)));
                return true;
            }

            if (instruction.Opcode is
                "BufferStoreByteD16Hi" or "BufferStoreShortD16Hi")
            {
                EmitExecConditional(() =>
                    EmitBufferSubwordStore(
                        bindingIndex,
                        byteAddress,
                        ShiftRightLogical(LoadV(control.VectorData), UInt(16)),
                        instruction.Opcode == "BufferStoreShortD16Hi" ? 16u : 8u));
                return true;
            }

            if (instruction.Opcode is "BufferStoreByte" or "BufferStoreShort")
            {
                EmitExecConditional(() =>
                    EmitBufferSubwordStore(
                        bindingIndex,
                        byteAddress,
                        LoadV(control.VectorData),
                        instruction.Opcode == "BufferStoreShort" ? 16u : 8u));
                return true;
            }

            if (instruction.Opcode.StartsWith("BufferAtomic", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    EmitConditional(IsBufferWordInRange(bindingIndex, dwordAddress), () =>
                    {
                        var original = EmitStorageBufferAtomic32(
                            instruction.Opcode,
                            BufferWordPointer(bindingIndex, dwordAddress),
                            control.VectorData);
                        if (control.Glc)
                        {
                            StoreV(control.VectorData, original);
                        }
                    });
                });

                return true;
            }

            if (instruction.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal) ||
                IsFormatBufferStore(instruction.Opcode))
            {
                if (IsFormatBufferStore(instruction.Opcode) &&
                    _formatBindingByPc.TryGetValue(instruction.Pc, out var formatInfo) &&
                    !IsPassthroughFormat(formatInfo.DataFormat, formatInfo.NumberFormat))
                {
                    EmitExecConditional(() =>
                    {
                        var rawDwordCount = FormatRawDwordCount(
                            formatInfo.DataFormat,
                            formatInfo.NumberFormat,
                            control.DwordCount);
                        for (uint rawIndex = 0; rawIndex < rawDwordCount; rawIndex++)
                        {
                            var rawValue = UInt(0);
                            var cpd = FormatComponentsPerDword(formatInfo.DataFormat);
                            for (uint ci = 0; ci < cpd; ci++)
                            {
                                var componentIndex = rawIndex * cpd + ci;
                                if (componentIndex >= control.DwordCount)
                                {
                                    break;
                                }

                                var componentVal = LoadV(control.VectorData + componentIndex);
                                var packed = PackComponentForStore(
                                    componentVal,
                                    ci,
                                    formatInfo.DataFormat,
                                    formatInfo.NumberFormat);
                                rawValue = BitwiseOr(rawValue, packed);
                            }

                            var address = rawIndex == 0
                                ? dwordAddress
                                : IAdd(dwordAddress, UInt(rawIndex));
                            StoreBufferWord(bindingIndex, address, rawValue);
                        }
                    });
                }
                else
                {
                    EmitExecConditional(() =>
                    {
                        for (uint index = 0; index < control.DwordCount; index++)
                        {
                            var address = index == 0
                                ? dwordAddress
                                : IAdd(dwordAddress, UInt(index));
                            StoreBufferWord(
                                bindingIndex,
                                address,
                                LoadV(control.VectorData + index));
                        }
                    });
                }

                return true;
            }

            if (!instruction.Opcode.StartsWith("BufferLoad", StringComparison.Ordinal) &&
                !instruction.Opcode.StartsWith("TBufferLoad", StringComparison.Ordinal))
            {
                error = $"unsupported buffer opcode {instruction.Opcode}";
                return false;
            }

            if (IsFormatBufferLoad(instruction.Opcode) &&
                _formatBindingByPc.TryGetValue(instruction.Pc, out var loadFormatInfo) &&
                !IsPassthroughFormat(loadFormatInfo.DataFormat, loadFormatInfo.NumberFormat))
            {
                var rawDwordCount = FormatRawDwordCount(
                    loadFormatInfo.DataFormat,
                    loadFormatInfo.NumberFormat,
                    control.DwordCount);
                for (uint rawIndex = 0; rawIndex < rawDwordCount; rawIndex++)
                {
                    var address = rawIndex == 0
                        ? dwordAddress
                        : IAdd(dwordAddress, UInt(rawIndex));
                    var rawDword = LoadBufferWord(bindingIndex, address);
                    var cpd = FormatComponentsPerDword(loadFormatInfo.DataFormat);
                    for (uint ci = 0; ci < cpd; ci++)
                    {
                        var componentIndex = rawIndex * cpd + ci;
                        if (componentIndex >= control.DwordCount)
                        {
                            break;
                        }

                        StoreV(
                            control.VectorData + componentIndex,
                            ConvertComponent(
                                rawDword,
                                ci,
                                loadFormatInfo.DataFormat,
                                loadFormatInfo.NumberFormat));
                    }
                }

                return true;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                var address = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt(index));
                StoreV(
                    control.VectorData + index,
                    LoadBufferWord(bindingIndex, address));
            }

            return true;
        }

        private uint EmitBufferSubwordLoad(
            int binding,
            uint byteAddress,
            uint componentBits,
            bool signed)
        {
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
            var byteOffset = BitwiseAnd(byteAddress, UInt(3));
            var bitOffset = ShiftLeftLogical(byteOffset, UInt(3));
            var word = LoadBufferWord(binding, dwordAddress);
            if (componentBits == 8)
            {
                return ExtractBufferSubword(word, bitOffset, componentBits, signed);
            }

            var regularLabel = _module.AllocateId();
            var crossingLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var crossesWord = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                byteOffset,
                UInt(3));
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                crossesWord,
                crossingLabel,
                regularLabel);

            _module.AddLabel(regularLabel);
            var regularValue = ExtractBufferSubword(
                word,
                bitOffset,
                componentBits,
                signed);
            _module.AddStatement(SpirvOp.Branch, mergeLabel);

            _module.AddLabel(crossingLabel);
            var nextWord = LoadBufferWord(binding, IAdd(dwordAddress, UInt(1)));
            var crossingWord = BitwiseOr(
                ShiftRightLogical(word, UInt(24)),
                ShiftLeftLogical(nextWord, UInt(8)));
            var crossingValue = ExtractBufferSubword(
                crossingWord,
                UInt(0),
                componentBits,
                signed);
            _module.AddStatement(SpirvOp.Branch, mergeLabel);

            _module.AddLabel(mergeLabel);
            return _module.AddInstruction(
                SpirvOp.Phi,
                _uintType,
                regularValue,
                regularLabel,
                crossingValue,
                crossingLabel);
        }

        private uint ExtractBufferSubword(
            uint word,
            uint bitOffset,
            uint componentBits,
            bool signed)
        {
            var value = _module.AddInstruction(
                signed ? SpirvOp.BitFieldSExtract : SpirvOp.BitFieldUExtract,
                signed ? _intType : _uintType,
                signed ? Bitcast(_intType, word) : word,
                bitOffset,
                UInt(componentBits));
            return signed ? Bitcast(_uintType, value) : value;
        }

        private uint InsertD16RegisterHalf(
            uint vectorDestination,
            uint value,
            bool high)
        {
            return _module.AddInstruction(
                SpirvOp.BitFieldInsert,
                _uintType,
                LoadV(vectorDestination),
                value,
                UInt(high ? 16u : 0u),
                UInt(16));
        }

        private void EmitBufferSubwordStore(
            int binding,
            uint byteAddress,
            uint value,
            uint componentBits)
        {
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
            var byteOffset = BitwiseAnd(byteAddress, UInt(3));
            var bitOffset = ShiftLeftLogical(byteOffset, UInt(3));
            var word = LoadBufferWord(binding, dwordAddress);
            if (componentBits == 8)
            {
                StoreBufferWord(
                    binding,
                    dwordAddress,
                    _module.AddInstruction(
                        SpirvOp.BitFieldInsert,
                        _uintType,
                        word,
                        value,
                        bitOffset,
                        UInt(componentBits)));
                return;
            }

            var regularLabel = _module.AllocateId();
            var crossingLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var crossesWord = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                byteOffset,
                UInt(3));
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                crossesWord,
                crossingLabel,
                regularLabel);

            _module.AddLabel(regularLabel);
            StoreBufferWord(
                binding,
                dwordAddress,
                _module.AddInstruction(
                    SpirvOp.BitFieldInsert,
                    _uintType,
                    word,
                    value,
                    bitOffset,
                    UInt(componentBits)));
            _module.AddStatement(SpirvOp.Branch, mergeLabel);

            _module.AddLabel(crossingLabel);
            StoreBufferWord(
                binding,
                dwordAddress,
                _module.AddInstruction(
                    SpirvOp.BitFieldInsert,
                    _uintType,
                    word,
                    value,
                    UInt(24),
                    UInt(8)));
            var nextAddress = IAdd(dwordAddress, UInt(1));
            var nextWord = LoadBufferWord(binding, nextAddress);
            StoreBufferWord(
                binding,
                nextAddress,
                _module.AddInstruction(
                    SpirvOp.BitFieldInsert,
                    _uintType,
                    nextWord,
                    ShiftRightLogical(value, UInt(8)),
                    UInt(0),
                    UInt(8)));
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private uint EmitStorageBufferAtomic32(
            string opcode,
            uint pointer,
            uint vectorData)
        {
            var operation = opcode[(opcode.IndexOf("Atomic", StringComparison.Ordinal) + 6)..];
            return operation switch
            {
                "Cmpswap" => _module.AddInstruction(
                    SpirvOp.AtomicCompareExchange,
                    _uintType,
                    pointer,
                    UInt(1),
                    UInt(0x48),
                    UInt(0x42),
                    LoadV(vectorData),
                    LoadV(vectorData + 1)),
                "Inc" or "Dec" => EmitAtomicCompareExchangeLoop(
                    pointer,
                    _uintType,
                    UInt(1),
                    UInt(0x42),
                    UInt(0x48),
                    UInt(0x42),
                    expected => EmitAtomicIncDecDesiredValue(
                        operation == "Inc",
                        vectorData,
                        expected)),
                _ => _module.AddInstruction(
                    operation switch
                    {
                        "Swap" => SpirvOp.AtomicExchange,
                        "Add" => SpirvOp.AtomicIAdd,
                        "Sub" => SpirvOp.AtomicISub,
                        "Smin" => SpirvOp.AtomicSMin,
                        "Umin" => SpirvOp.AtomicUMin,
                        "Smax" => SpirvOp.AtomicSMax,
                        "Umax" => SpirvOp.AtomicUMax,
                        "And" => SpirvOp.AtomicAnd,
                        "Or" => SpirvOp.AtomicOr,
                        "Xor" => SpirvOp.AtomicXor,
                        _ => throw new InvalidOperationException(
                            $"unsupported storage-buffer atomic {opcode}"),
                    },
                    _uintType,
                    pointer,
                    UInt(1),
                    UInt(0x48),
                    LoadV(vectorData)),
            };
        }

        private uint EmitAtomicIncDecDesiredValue(
            bool increment,
            uint vectorData,
            uint expected)
        {
            var data = LoadV(vectorData);
            if (increment)
            {
                var incrementWraps = _module.AddInstruction(
                    SpirvOp.UGreaterThanEqual,
                    _boolType,
                    expected,
                    data);
                return _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    incrementWraps,
                    UInt(0),
                    IAdd(expected, UInt(1)));
            }

            var isZero = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                expected,
                UInt(0));
            var aboveLimit = _module.AddInstruction(
                SpirvOp.UGreaterThan,
                _boolType,
                expected,
                data);
            var decrementWraps = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                isZero,
                aboveLimit);
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                decrementWraps,
                data,
                _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    expected,
                    UInt(1)));
        }

        private static bool IsFormatBufferLoad(string opcode) =>
            opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal) ||
            opcode.StartsWith("TBufferLoadFormat", StringComparison.Ordinal);

        private static bool IsFormatBufferStore(string opcode) =>
            opcode.StartsWith("BufferStoreFormat", StringComparison.Ordinal) ||
            opcode.StartsWith("TBufferStoreFormat", StringComparison.Ordinal);

        private static uint FormatComponentsPerDword(uint dataFormat) =>
            dataFormat switch
            {
                1 => 4,
                2 => 2,
                3 => 2,
                4 => 1,
                5 => 2,
                7 => 3,
                8 or 9 => 4,
                10 => 4,
                11 or 13 => 1,
                12 => 2,
                14 => 1,
                16 or 17 or 18 or 19 => 4,
                34 => 3,
                _ => 1,
            };

        private static uint FormatByteCount(
            uint dataFormat,
            uint numberFormat) =>
            dataFormat switch
            {
                1 => 1,
                2 => 2,
                3 => 1,
                4 => 4,
                5 => 2,
                6 or 7 => 4,
                8 or 9 => 4,
                10 => 1,
                11 => 4,
                12 => 2,
                13 => 4,
                14 => 4,
                16 or 17 or 18 or 19 => 2,
                34 => 4,
                _ => 4,
            };

        private static uint FormatRawDwordCount(
            uint dataFormat,
            uint numberFormat,
            uint componentCount)
        {
            if (dataFormat is 4 or 11 or 13 or 14 or 6 or 7 or 34)
            {
                return componentCount;
            }

            var bytesPerComponent = FormatByteCount(dataFormat, numberFormat);
            var totalBytes = componentCount * bytesPerComponent;
            return (totalBytes + 3) / 4;
        }

        private bool IsPassthroughFormat(
            uint dataFormat,
            uint numberFormat) =>
            dataFormat is 4 && numberFormat is 4 or 5 or 7;

        private bool IsFloat16Format(uint dataFormat, uint numberFormat) =>
            numberFormat == 7 &&
            dataFormat is 2 or 5 or 12;

        private bool IsUintFormat(uint dataFormat, uint numberFormat) =>
            numberFormat == 4 ||
            (dataFormat is 4 or 11 or 13 or 14 && numberFormat is 4);

        private bool IsSintFormat(uint dataFormat, uint numberFormat) =>
            numberFormat == 5;

        private uint ExtractByteComponent(uint rawDword, uint byteIndex) =>
            BitwiseAnd(
                ShiftRightLogical(rawDword, UInt(byteIndex * 8)),
                UInt(0xFF));

        private uint ExtractShortComponent(uint rawDword, uint shortIndex) =>
            BitwiseAnd(
                ShiftRightLogical(rawDword, UInt(shortIndex * 16)),
                UInt(0xFFFF));

        private uint ConvertByteToUnorm(uint byteValue)
        {
            var floatValue = _module.AddInstruction(
                SpirvOp.ConvertUToF,
                _floatType,
                byteValue);
            var divisor = Float(255.0f);
            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.FDiv,
                    _floatType,
                    floatValue,
                    divisor));
        }

        private uint ConvertByteToSnorm(uint byteValue)
        {
            var signedValue = _module.AddInstruction(
                SpirvOp.SConvert,
                _intType,
                _module.AddInstruction(
                    SpirvOp.BitFieldSExtract,
                    _intType,
                    Bitcast(_intType, byteValue),
                    UInt(0),
                    UInt(8)));
            var floatValue = _module.AddInstruction(
                SpirvOp.SConvert,
                    _floatType,
                    signedValue);
            var divisor = Float(127.0f);
            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.FDiv,
                    _floatType,
                    floatValue,
                    divisor));
        }

        private uint ConvertShortToHalfFloat(uint shortValue)
        {
            var unpacked = Ext(62, _vec2Type, shortValue);
            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    unpacked,
                    0));
        }

        private uint ConvertShortToUnorm(uint shortValue)
        {
            var floatValue = _module.AddInstruction(
                SpirvOp.ConvertUToF,
                _floatType,
                shortValue);
            var divisor = Float(65535.0f);
            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.FDiv,
                    _floatType,
                    floatValue,
                    divisor));
        }

        private uint ConvertShortToSnorm(uint shortValue)
        {
            var signedValue = Bitcast(
                _intType,
                _module.AddInstruction(
                    SpirvOp.BitFieldSExtract,
                    _intType,
                    Bitcast(_intType, shortValue),
                    UInt(0),
                    UInt(16)));
            var floatValue = _module.AddInstruction(
                SpirvOp.SConvert,
                    _floatType,
                    signedValue);
            var divisor = Float(32767.0f);
            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.FDiv,
                    _floatType,
                    floatValue,
                    divisor));
        }

        private uint ConvertComponent(
            uint rawDword,
            uint componentIndex,
            uint dataFormat,
            uint numberFormat)
        {
            if (IsPassthroughFormat(dataFormat, numberFormat))
            {
                return rawDword;
            }

            if (dataFormat is 1 or 3 or 10)
            {
                var byteIndex = componentIndex % FormatComponentsPerDword(dataFormat);
                var byteVal = ExtractByteComponent(rawDword, byteIndex);
                return numberFormat switch
                {
                    0 or 6 => ConvertByteToUnorm(byteVal),
                    1 => ConvertByteToSnorm(byteVal),
                    4 => byteVal,
                    5 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.BitFieldSExtract,
                            _intType,
                            Bitcast(_intType, byteVal),
                            UInt(8))),
                    9 => ConvertByteToUnorm(byteVal),
                    _ => byteVal,
                };
            }

            if (dataFormat is 2 && numberFormat is 7)
            {
                return ConvertShortToHalfFloat(rawDword);
            }

            if (dataFormat is 2)
            {
                var shortVal = ExtractShortComponent(rawDword, componentIndex);
                return numberFormat switch
                {
                    0 or 6 => ConvertShortToUnorm(shortVal),
                    1 => ConvertShortToSnorm(shortVal),
                    4 => shortVal,
                    5 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.BitFieldSExtract,
                            _intType,
                            Bitcast(_intType, shortVal),
                            UInt(16))),
                    _ => shortVal,
                };
            }

            if (dataFormat is 5)
            {
                var shortVal = ExtractShortComponent(rawDword, componentIndex % 2);
                return numberFormat switch
                {
                    7 => ConvertShortToHalfFloat(shortVal),
                    0 or 6 => ConvertShortToUnorm(shortVal),
                    1 => ConvertShortToSnorm(shortVal),
                    2 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.ConvertUToF,
                            _floatType,
                            shortVal)),
                    3 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.SConvert,
                            _floatType,
                            Bitcast(_intType, shortVal))),
                    4 => shortVal,
                    5 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.BitFieldSExtract,
                            _intType,
                            Bitcast(_intType, shortVal),
                            UInt(16))),
                    _ => shortVal,
                };
            }

            if (dataFormat is 12)
            {
                var shortIndex = componentIndex / 2;
                var shortInDword = componentIndex % 2;
                var localRaw = shortIndex == 0
                    ? rawDword
                    : rawDword;
                var shortVal = ExtractShortComponent(localRaw, shortInDword);
                return numberFormat switch
                {
                    7 => ConvertShortToHalfFloat(shortVal),
                    0 or 6 => ConvertShortToUnorm(shortVal),
                    1 => ConvertShortToSnorm(shortVal),
                    4 => shortVal,
                    5 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.BitFieldSExtract,
                            _intType,
                            Bitcast(_intType, shortVal),
                            UInt(16))),
                    _ => shortVal,
                };
            }

            if (dataFormat is 11)
            {
                return numberFormat switch
                {
                    7 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.Bitcast,
                            _floatType,
                            rawDword)),
                    4 => rawDword,
                    5 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.SConvert,
                            _intType,
                            Bitcast(_intType, rawDword))),
                    _ => rawDword,
                };
            }

            if (dataFormat is 13)
            {
                return numberFormat switch
                {
                    7 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.Bitcast,
                            _floatType,
                            rawDword)),
                    4 => rawDword,
                    5 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.SConvert,
                            _intType,
                            Bitcast(_intType, rawDword))),
                    _ => rawDword,
                };
            }

            if (dataFormat is 14)
            {
                return numberFormat switch
                {
                    7 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.Bitcast,
                            _floatType,
                            rawDword)),
                    4 => rawDword,
                    5 => Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.SConvert,
                            _intType,
                            Bitcast(_intType, rawDword))),
                    _ => rawDword,
                };
            }

            if (dataFormat is 8)
            {
                return componentIndex switch
                {
                    0 => ExtractAndConvert10Bit(rawDword, 0, numberFormat),
                    1 => ExtractAndConvert10Bit(rawDword, 10, numberFormat),
                    2 => ExtractAndConvert10Bit(rawDword, 20, numberFormat),
                    3 => ExtractAndConvert2Bit(rawDword, 30, numberFormat),
                    _ => rawDword,
                };
            }

            if (dataFormat is 9)
            {
                return componentIndex switch
                {
                    0 => ExtractAndConvert10Bit(rawDword, 20, numberFormat),
                    1 => ExtractAndConvert10Bit(rawDword, 10, numberFormat),
                    2 => ExtractAndConvert10Bit(rawDword, 0, numberFormat),
                    3 => ExtractAndConvert2Bit(rawDword, 30, numberFormat),
                    _ => rawDword,
                };
            }

            if (dataFormat is 7)
            {
                return componentIndex switch
                {
                    0 => ExtractAndConvertUnsignedFloat(rawDword, 0, 11),
                    1 => ExtractAndConvertUnsignedFloat(rawDword, 11, 11),
                    2 => ExtractAndConvertUnsignedFloat(rawDword, 22, 10),
                    _ => rawDword,
                };
            }

            if (dataFormat is 16)
            {
                return componentIndex switch
                {
                    0 => ExtractAndConvert565Component(rawDword, 11, 5, numberFormat),
                    1 => ExtractAndConvert565Component(rawDword, 5, 6, numberFormat),
                    2 => ExtractAndConvert565Component(rawDword, 0, 5, numberFormat),
                    _ => rawDword,
                };
            }

            if (dataFormat is 17)
            {
                return componentIndex switch
                {
                    0 => ExtractAndConvert5551Component(rawDword, 11, 5, numberFormat),
                    1 => ExtractAndConvert5551Component(rawDword, 6, 5, numberFormat),
                    2 => ExtractAndConvert5551Component(rawDword, 1, 5, numberFormat),
                    3 => ExtractAndConvert1Bit(rawDword, 0),
                    _ => rawDword,
                };
            }

            return rawDword;
        }

        private uint ExtractAndConvert10Bit(
            uint rawDword,
            uint bitOffset,
            uint numberFormat)
        {
            var raw10 = BitwiseAnd(
                ShiftRightLogical(rawDword, UInt(bitOffset)),
                UInt(0x3FF));
            return numberFormat switch
            {
                0 or 6 =>
                    Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.ConvertUToF,
                                _floatType,
                                raw10),
                            Float(1023.0f))),
                1 =>
                    Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.SConvert,
                                _floatType,
                                Bitcast(
                                    _intType,
                                    _module.AddInstruction(
                                        SpirvOp.BitFieldSExtract,
                                        _intType,
                                        Bitcast(_intType, raw10),
                                        UInt(0),
                                        UInt(10)))),
                            Float(511.0f))),
                2 =>
                    Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.ConvertUToF,
                            _floatType,
                            raw10)),
                3 =>
                    Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.SConvert,
                            _floatType,
                            Bitcast(_intType, raw10))),
                4 => raw10,
                5 => Bitcast(
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _intType,
                        Bitcast(_intType, raw10),
                        UInt(0),
                        UInt(10))),
                _ => raw10,
            };
        }

        private uint ExtractAndConvertUnsignedFloat(
            uint rawDword,
            uint bitOffset,
            uint bitCount)
        {
            var raw = BitwiseAnd(
                ShiftRightLogical(rawDword, UInt(bitOffset)),
                UInt((1u << checked((int)bitCount)) - 1));
            var halfBits = ShiftLeftLogical(raw, UInt(15 - bitCount));
            var unpacked = Ext(62, _vec2Type, halfBits);
            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    unpacked,
                    0));
        }

        private uint ExtractAndConvert2Bit(
            uint rawDword,
            uint bitOffset,
            uint numberFormat)
        {
            var raw2 = BitwiseAnd(
                ShiftRightLogical(rawDword, UInt(bitOffset)),
                UInt(0x3));
            return numberFormat switch
            {
                0 or 6 =>
                    Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.ConvertUToF,
                                _floatType,
                                raw2),
                            Float(3.0f))),
                4 => raw2,
                _ =>
                    Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.ConvertUToF,
                                _floatType,
                                raw2),
                            Float(3.0f))),
            };
        }

        private uint ExtractAndConvert1Bit(uint rawDword, uint bitOffset)
        {
            var raw1 = BitwiseAnd(
                ShiftRightLogical(rawDword, UInt(bitOffset)),
                UInt(1));
            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.FDiv,
                    _floatType,
                    _module.AddInstruction(
                        SpirvOp.ConvertUToF,
                        _floatType,
                        raw1),
                    Float(1.0f)));
        }

        private uint ExtractAndConvert565Component(
            uint rawDword,
            uint bitOffset,
            uint bitCount,
            uint numberFormat)
        {
            var rawVal = BitwiseAnd(
                ShiftRightLogical(rawDword, UInt(bitOffset)),
                bitCount == 6 ? UInt(0x3F) : UInt(0x1F));
            var maxVal = bitCount == 6 ? 63.0f : 31.0f;
            return numberFormat switch
            {
                0 or 6 =>
                    Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.ConvertUToF,
                                _floatType,
                                rawVal),
                            Float(maxVal))),
                4 => rawVal,
                _ =>
                    Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.ConvertUToF,
                                _floatType,
                                rawVal),
                            Float(maxVal))),
            };
        }

        private uint ExtractAndConvert5551Component(
            uint rawDword,
            uint bitOffset,
            uint bitCount,
            uint numberFormat)
        {
            var rawVal = BitwiseAnd(
                ShiftRightLogical(rawDword, UInt(bitOffset)),
                UInt(0x1F));
            return numberFormat switch
            {
                0 or 6 =>
                    Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.ConvertUToF,
                                _floatType,
                                rawVal),
                            Float(31.0f))),
                4 => rawVal,
                _ =>
                    Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.ConvertUToF,
                                _floatType,
                                rawVal),
                            Float(31.0f))),
            };
        }

        private uint PackComponentForStore(
            uint componentValue,
            uint componentIndex,
            uint dataFormat,
            uint numberFormat)
        {
            if (IsPassthroughFormat(dataFormat, numberFormat))
            {
                return componentValue;
            }

            if (dataFormat is 1 or 3 or 10)
            {
                var byteIndex = componentIndex % FormatComponentsPerDword(dataFormat);
                var packed = PackByteForStore(componentValue, numberFormat);
                return ShiftLeftLogical(packed, UInt(byteIndex * 8));
            }

            if (dataFormat is 2)
            {
                var shortValue = PackShortForStore(componentValue, numberFormat);
                return ShiftLeftLogical(shortValue, UInt(componentIndex * 16));
            }

            if (dataFormat is 5)
            {
                var shortValue = PackShortForStore(componentValue, numberFormat);
                return ShiftLeftLogical(shortValue, UInt((componentIndex % 2) * 16));
            }

            if (dataFormat is 12)
            {
                var shortValue = PackShortForStore(componentValue, numberFormat);
                return ShiftLeftLogical(
                    shortValue,
                    UInt((componentIndex % 2) * 16));
            }

            if (dataFormat is 11 or 13 or 14)
            {
                return PackDwordForStore(componentValue, numberFormat);
            }

            if (dataFormat is 8)
            {
                return componentIndex switch
                {
                    0 => ShiftLeftLogical(
                        Pack10BitForStore(componentValue, numberFormat),
                        UInt(0)),
                    1 => ShiftLeftLogical(
                        Pack10BitForStore(componentValue, numberFormat),
                        UInt(10)),
                    2 => ShiftLeftLogical(
                        Pack10BitForStore(componentValue, numberFormat),
                        UInt(20)),
                    3 => ShiftLeftLogical(
                        Pack2BitForStore(componentValue, numberFormat),
                        UInt(30)),
                    _ => componentValue,
                };
            }

            if (dataFormat is 9)
            {
                return componentIndex switch
                {
                    0 => ShiftLeftLogical(
                        Pack10BitForStore(componentValue, numberFormat),
                        UInt(20)),
                    1 => ShiftLeftLogical(
                        Pack10BitForStore(componentValue, numberFormat),
                        UInt(10)),
                    2 => ShiftLeftLogical(
                        Pack10BitForStore(componentValue, numberFormat),
                        UInt(0)),
                    3 => ShiftLeftLogical(
                        Pack2BitForStore(componentValue, numberFormat),
                        UInt(30)),
                    _ => componentValue,
                };
            }

            if (dataFormat is 7)
            {
                return componentIndex switch
                {
                    0 => ShiftLeftLogical(
                        PackUnsignedFloatForStore(componentValue, 11),
                        UInt(0)),
                    1 => ShiftLeftLogical(
                        PackUnsignedFloatForStore(componentValue, 11),
                        UInt(11)),
                    2 => ShiftLeftLogical(
                        PackUnsignedFloatForStore(componentValue, 10),
                        UInt(22)),
                    _ => componentValue,
                };
            }

            if (dataFormat is 16)
            {
                return componentIndex switch
                {
                    0 => ShiftLeftLogical(
                        Pack565ComponentForStore(componentValue, 5, numberFormat),
                        UInt(11)),
                    1 => ShiftLeftLogical(
                        Pack565ComponentForStore(componentValue, 6, numberFormat),
                        UInt(5)),
                    2 => ShiftLeftLogical(
                        Pack565ComponentForStore(componentValue, 5, numberFormat),
                        UInt(0)),
                    _ => componentValue,
                };
            }

            if (dataFormat is 17)
            {
                return componentIndex switch
                {
                    0 => ShiftLeftLogical(
                        Pack5551ComponentForStore(componentValue, numberFormat),
                        UInt(11)),
                    1 => ShiftLeftLogical(
                        Pack5551ComponentForStore(componentValue, numberFormat),
                        UInt(6)),
                    2 => ShiftLeftLogical(
                        Pack5551ComponentForStore(componentValue, numberFormat),
                        UInt(1)),
                    3 => ShiftLeftLogical(
                        Pack1BitForStore(componentValue),
                        UInt(0)),
                    _ => componentValue,
                };
            }

            return componentValue;
        }

        private uint PackByteForStore(uint componentValue, uint numberFormat)
        {
            var floatVal = Bitcast(_floatType, componentValue);
            return numberFormat switch
            {
                0 or 6 =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                ClampFloat01(floatVal))),
                        UInt(0xFF)),
                1 =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.BitFieldSExtract,
                            _intType,
                            _module.AddInstruction(
                                SpirvOp.SConvert,
                                _intType,
                                _module.AddInstruction(
                                    SpirvOp.ConvertFToS,
                                    _intType,
                                    ClampFloatN11(floatVal))),
                            UInt(0),
                            UInt(8)),
                        UInt(0xFF)),
                4 => BitwiseAnd(componentValue, UInt(0xFF)),
                5 => BitwiseAnd(
                    _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _intType,
                        Bitcast(_intType, componentValue),
                        UInt(0),
                        UInt(8)),
                    UInt(0xFF)),
                _ => BitwiseAnd(componentValue, UInt(0xFF)),
            };
        }

        private uint PackShortForStore(uint componentValue, uint numberFormat)
        {
            var floatVal = Bitcast(_floatType, componentValue);
            return numberFormat switch
            {
                7 => BitwiseAnd(
                    PackHalf2(floatVal, Float(0)),
                    UInt(0xFFFF)),
                0 or 6 =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                ClampFloat01(floatVal))),
                        UInt(0xFFFF)),
                1 =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.BitFieldSExtract,
                            _intType,
                            _module.AddInstruction(
                                SpirvOp.SConvert,
                                _intType,
                                _module.AddInstruction(
                                    SpirvOp.ConvertFToS,
                                    _intType,
                                    ClampFloatN11(floatVal))),
                            UInt(0),
                            UInt(16)),
                        UInt(0xFFFF)),
                4 => BitwiseAnd(componentValue, UInt(0xFFFF)),
                5 => BitwiseAnd(
                    _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _intType,
                        Bitcast(_intType, componentValue),
                        UInt(0),
                        UInt(16)),
                    UInt(0xFFFF)),
                _ => BitwiseAnd(componentValue, UInt(0xFFFF)),
            };
        }

        private uint PackDwordForStore(uint componentValue, uint numberFormat) =>
            numberFormat switch
            {
                7 => Bitcast(
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.Bitcast,
                        _floatType,
                        componentValue)),
                4 => componentValue,
                5 => Bitcast(
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.SConvert,
                        _intType,
                        Bitcast(_intType, componentValue))),
                _ => componentValue,
            };

        private uint Pack10BitForStore(uint componentValue, uint numberFormat)
        {
            var floatVal = Bitcast(_floatType, componentValue);
            return numberFormat switch
            {
                0 or 6 =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.FMul,
                                    _floatType,
                                    ClampFloat01(floatVal),
                                    Float(1023.0f)))),
                        UInt(0x3FF)),
                1 =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.SConvert,
                                _intType,
                                _module.AddInstruction(
                                    SpirvOp.ConvertFToS,
                                    _intType,
                                    _module.AddInstruction(
                                        SpirvOp.FMul,
                                        _floatType,
                                        ClampFloatN11(floatVal),
                                        Float(511.0f))))),
                        UInt(0x3FF)),
                4 => BitwiseAnd(componentValue, UInt(0x3FF)),
                5 => BitwiseAnd(
                    _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _intType,
                        Bitcast(_intType, componentValue),
                        UInt(0),
                        UInt(10)),
                    UInt(0x3FF)),
                _ => BitwiseAnd(componentValue, UInt(0x3FF)),
            };
        }

        private uint Pack2BitForStore(uint componentValue, uint numberFormat)
        {
            var floatVal = Bitcast(_floatType, componentValue);
            return numberFormat switch
            {
                0 or 6 =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.FMul,
                                    _floatType,
                                    ClampFloat01(floatVal),
                                    Float(3.0f)))),
                        UInt(0x3)),
                4 => BitwiseAnd(componentValue, UInt(0x3)),
                _ =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.FMul,
                                    _floatType,
                                    ClampFloat01(floatVal),
                                    Float(3.0f)))),
                        UInt(0x3)),
            };
        }

        private uint PackUnsignedFloatForStore(
            uint componentValue,
            uint bitCount)
        {
            var floatVal = Bitcast(_floatType, componentValue);
            var halfBits = BitwiseAnd(
                PackHalf2(floatVal, Float(0)),
                UInt(0xFFFF));
            return BitwiseAnd(
                ShiftRightLogical(halfBits, UInt(15 - bitCount)),
                UInt((1u << checked((int)bitCount)) - 1));
        }

        private uint Pack565ComponentForStore(
            uint componentValue,
            uint bitCount,
            uint numberFormat)
        {
            var floatVal = Bitcast(_floatType, componentValue);
            var maxVal = bitCount == 6 ? 63.0f : 31.0f;
            var mask = bitCount == 6 ? 0x3Fu : 0x1Fu;
            return numberFormat switch
            {
                0 or 6 =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.FMul,
                                    _floatType,
                                    ClampFloat01(floatVal),
                                    Float(maxVal)))),
                        UInt(mask)),
                4 => BitwiseAnd(componentValue, UInt(mask)),
                _ =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.FMul,
                                    _floatType,
                                    ClampFloat01(floatVal),
                                    Float(maxVal)))),
                        UInt(mask)),
            };
        }

        private uint Pack5551ComponentForStore(
            uint componentValue,
            uint numberFormat)
        {
            var floatVal = Bitcast(_floatType, componentValue);
            return numberFormat switch
            {
                0 or 6 =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.FMul,
                                    _floatType,
                                    ClampFloat01(floatVal),
                                    Float(31.0f)))),
                        UInt(0x1F)),
                4 => BitwiseAnd(componentValue, UInt(0x1F)),
                _ =>
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.FMul,
                                    _floatType,
                                    ClampFloat01(floatVal),
                                    Float(31.0f)))),
                        UInt(0x1F)),
            };
        }

        private uint Pack1BitForStore(uint componentValue)
        {
            var floatVal = Bitcast(_floatType, componentValue);
            return BitwiseAnd(
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.ConvertFToU,
                        _uintType,
                        ClampFloat01(floatVal))),
                UInt(1));
        }

        private bool TryEmitVertexInputFetch(
            Gen5BufferMemoryControl control,
            SpirvVertexInput input,
            out string error)
        {
            error = string.Empty;
            if (control.DwordCount == 0 ||
                control.DwordCount > input.ComponentCount)
            {
                error =
                    $"invalid vertex input fetch components={control.DwordCount} " +
                    $"input={input.ComponentCount}";
                return false;
            }

            var loaded = Load(input.Type, input.Variable);
            for (uint component = 0; component < control.DwordCount; component++)
            {
                var value = input.ComponentCount == 1
                    ? loaded
                    : _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        input.ComponentType,
                        loaded,
                        component);
                var raw = input.ComponentKind == VertexInputComponentKind.Uint
                    ? value
                    : Bitcast(_uintType, value);
                StoreV(control.VectorData + component, raw);
            }

            return true;
        }

        private bool TryEmitImage(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            out string error)
        {
            error = string.Empty;
            if (!_imageBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex) ||
                bindingIndex >= _imageResources.Count)
            {
                error = "unresolved image binding";
                return false;
            }

            var resource = _imageResources[bindingIndex];
            var imageObject = Load(resource.ObjectType, resource.Variable);
            if (instruction.Opcode == "ImageGetLod")
            {
                if (_stage != Gen5SpirvStage.Pixel)
                {
                    error = "image_get_lod requires a pixel shader";
                    return false;
                }

                var lod = _module.AddInstruction(
                    SpirvOp.ImageQueryLod,
                    _vec2Type,
                    imageObject,
                    resource.Arrayed
                        ? BuildFloatArrayCoordinates(image, 0)
                        : BuildFloatCoordinates(image, 0));
                for (var component = 0u; component < 2; component++)
                {
                    StoreV(
                        image.VectorData + component,
                        Bitcast(
                            _uintType,
                            _module.AddInstruction(
                                SpirvOp.CompositeExtract,
                                _floatType,
                                lod,
                                component)));
                }

                return true;
            }

            if (instruction.Opcode == "ImageGetResinfo")
            {
                var queryImage = resource.IsStorage
                    ? imageObject
                    : _module.AddInstruction(
                        SpirvOp.Image,
                        resource.ImageType,
                        imageObject);
                var sizeType = _module.TypeVector(
                    _intType,
                    resource.Arrayed ? 3u : 2u);
                var size = _module.AddInstruction(
                    resource.IsStorage
                        ? SpirvOp.ImageQuerySize
                        : SpirvOp.ImageQuerySizeLod,
                    sizeType,
                    resource.IsStorage
                        ? [queryImage]
                        :
                        [
                            queryImage,
                            Bitcast(
                                _intType,
                                LoadV(image.GetAddressRegister(0))),
                        ]);
                var levels = resource.IsStorage
                    ? UInt(1)
                    : Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.ImageQueryLevels,
                            _intType,
                            queryImage));
                uint outputIndex = 0;
                for (uint component = 0; component < 4; component++)
                {
                    if ((image.Dmask & (1u << (int)component)) == 0)
                    {
                        continue;
                    }

                    uint value;
                    if (component < 2 || (component == 2 && resource.Arrayed))
                    {
                        var signedValue = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _intType,
                            size,
                            component);
                        value = Bitcast(_uintType, signedValue);
                    }
                    else
                    {
                        value = component == 2 ? UInt(1) : levels;
                    }

                    StoreV(image.VectorData + outputIndex++, value);
                }

                return true;
            }

            if (instruction.Opcode is "ImageStore" or "ImageStoreMip")
            {
                if (!resource.IsStorage)
                {
                    error = "image store is not bound as storage";
                    return false;
                }

                uint mipLevel = 0;
                if (instruction.Opcode == "ImageStoreMip")
                {
                    var resolvedMipLevel =
                        _evaluation.ImageBindings[bindingIndex].MipLevel;
                    if (!resolvedMipLevel.HasValue)
                    {
                        error = "dynamic image_store_mip is unsupported";
                        return false;
                    }

                    mipLevel = resolvedMipLevel.Value;
                }

                var coordinates = BuildIntegerCoordinates(
                    image,
                    0,
                    resource.Arrayed);
                var components = new uint[4];
                uint sourceIndex = 0;
                for (var component = 0; component < components.Length; component++)
                {
                    if ((image.Dmask & (1u << component)) != 0)
                    {
                        var raw = LoadV(image.VectorData + sourceIndex++);
                        components[component] = resource.ComponentKind switch
                        {
                            ImageComponentKind.Sint => Bitcast(_intType, raw),
                            ImageComponentKind.Uint => raw,
                            _ => Bitcast(_floatType, raw),
                        };
                    }
                    else
                    {
                        components[component] = resource.ComponentKind switch
                        {
                            ImageComponentKind.Sint =>
                                _module.Constant(_intType, 0),
                            ImageComponentKind.Uint => UInt(0),
                            _ => Float(0),
                        };
                    }
                }

                var texel = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    resource.VectorType,
                    components);
                if (TryGetImageExtents(
                        _evaluation.ImageBindings[bindingIndex].ResourceDescriptor,
                        image.Dimension,
                        out var width,
                        out var height,
                        out var depth))
                {
                    if (mipLevel >= 32)
                    {
                        width = 1;
                        height = 1;
                        depth = 1;
                    }
                    else
                    {
                        width = Math.Max(width >> (int)mipLevel, 1);
                        height = Math.Max(height >> (int)mipLevel, 1);
                        depth = Math.Max(depth >> (int)mipLevel, 1);
                    }

                    EmitBoundsCheckedImageWrite(
                        coordinates,
                        image.Dimension == 2
                            ? [width, height, depth]
                            : [width, height],
                        imageObject,
                        texel);
                }
                else
                {
                    EmitExecConditional(
                        () => _module.AddStatement(
                            SpirvOp.ImageWrite,
                            imageObject,
                            coordinates,
                            texel));
                }

                return true;
            }

            if (instruction.Opcode.StartsWith("ImageAtomic", StringComparison.Ordinal))
            {
                if (!resource.IsStorage)
                {
                    error = "image atomic is not bound as storage";
                    return false;
                }

                var hasAtomicFormat = resource.Format is
                    SpirvImageFormat.R32ui or SpirvImageFormat.R32i;
                var hasIntegerComponent = resource.ComponentKind is
                    ImageComponentKind.Uint or ImageComponentKind.Sint;
                if (!hasAtomicFormat || !hasIntegerComponent)
                {
                    error =
                        $"image atomic requires an R32ui or R32i resource, got {resource.Format}";
                    return false;
                }

                var atomicImageSize = _module.AddInstruction(
                    SpirvOp.ImageQuerySize,
                    GetIntegerCoordinateType(
                        GetImageCoordinateComponentCount(
                            image.Dimension,
                            resource.Arrayed)),
                    imageObject);
                var coordinates = BuildClampedIntegerCoordinates(
                    image,
                    0,
                    atomicImageSize,
                    resource.Arrayed);
                var pointerType = _module.TypePointer(
                    SpirvStorageClass.Image,
                    resource.ComponentType);
                var pointer = _module.AddInstruction(
                    SpirvOp.ImageTexelPointer,
                    pointerType,
                    resource.Variable,
                    coordinates,
                    UInt(0));
                EmitExecConditional(
                    () =>
                    {
                        var original = EmitImageAtomic32(
                            instruction.Opcode,
                            pointer,
                            image.VectorData,
                            resource);
                        if (image.Glc)
                        {
                            StoreV(
                                image.VectorData,
                                resource.ComponentKind == ImageComponentKind.Uint
                                    ? original
                                    : Bitcast(_uintType, original));
                        }
                    });
                return true;
            }

            if (resource.IsStorage &&
                instruction.Opcode is "ImageLoad" or "ImageLoadMip")
            {
                if (instruction.Opcode == "ImageLoadMip" &&
                    !_evaluation.ImageBindings[bindingIndex].MipLevel.HasValue)
                {
                    error = "dynamic image_load_mip is unsupported for storage images";
                    return false;
                }

                var imageSize = _module.AddInstruction(
                    SpirvOp.ImageQuerySize,
                    GetIntegerCoordinateType(
                        GetImageCoordinateComponentCount(
                            image.Dimension,
                            resource.Arrayed)),
                    imageObject);
                var coordinates = BuildClampedIntegerCoordinates(
                    image,
                    0,
                    imageSize,
                    resource.Arrayed);
                var texel = _module.AddInstruction(
                    SpirvOp.ImageRead,
                    resource.VectorType,
                    imageObject,
                    coordinates);
                StoreImageComponents(image, resource, texel);
                return true;
            }

            if (resource.IsStorage)
            {
                error = $"unsupported storage image opcode {instruction.Opcode}";
                return false;
            }

            uint sampled;
            var writeAllComponents = false;
            if (instruction.Opcode is "ImageLoad" or "ImageLoadMip")
            {
                var mipLevel = instruction.Opcode == "ImageLoadMip"
                    ? Bitcast(
                        _intType,
                        LoadV(image.GetAddressRegister(2)))
                    : _module.Constant(_intType, 0);
                var fetchedImage = _module.AddInstruction(
                    SpirvOp.Image,
                    resource.ImageType,
                    imageObject);
                uint coordinates;
                if (instruction.Opcode == "ImageLoadMip")
                {
                    var mipSize = _module.AddInstruction(
                        SpirvOp.ImageQuerySizeLod,
                        GetIntegerCoordinateType(
                            GetImageCoordinateComponentCount(
                                image.Dimension,
                                resource.Arrayed)),
                        fetchedImage,
                        mipLevel);
                    coordinates = BuildClampedIntegerCoordinates(
                        image,
                        0,
                        mipSize,
                        resource.Arrayed);
                }
                else
                {
                    coordinates = TryGetImageExtents(
                            _evaluation.ImageBindings[bindingIndex].ResourceDescriptor,
                            image.Dimension,
                            out var width,
                            out var height,
                            out var depth)
                        ? BuildClampedIntegerCoordinates(
                            image,
                            0,
                            GetStaticImageCoordinateExtents(
                                image.Dimension,
                                resource.Arrayed,
                                width,
                                height,
                                depth))
                        : BuildIntegerCoordinates(image, 0, resource.Arrayed);
                }

                sampled = _module.AddInstruction(
                    SpirvOp.ImageFetch,
                    resource.VectorType,
                    fetchedImage,
                    coordinates,
                    2,
                    mipLevel);
            }
            else if (instruction.Opcode.StartsWith(
                         "ImageSample",
                         StringComparison.Ordinal))
            {
                var sampleSuffix = instruction.Opcode["ImageSample".Length..];
                var hasOffset = sampleSuffix.EndsWith("O", StringComparison.Ordinal);
                if (hasOffset)
                {
                    sampleSuffix = sampleSuffix[..^1];
                }

                var hasCompare = sampleSuffix.StartsWith("C", StringComparison.Ordinal);
                if (hasCompare)
                {
                    sampleSuffix = sampleSuffix[1..];
                }

                var hasBias = sampleSuffix == "B";
                var hasGradients = sampleSuffix == "D";
                var isZeroLod = sampleSuffix == "Lz";
                var biasIndex = hasOffset ? 1 : 0;
                var compareIndex = biasIndex + (hasBias ? 1 : 0);
                var gradientIndex = compareIndex + (hasCompare ? 1 : 0);
                var spatialCoordinateCount = checked((int)
                    GetImageCoordinateComponentCount(
                        image.Dimension,
                        arrayed: false));
                var coordinateCount = checked((int)
                    GetImageCoordinateComponentCount(
                        image.Dimension,
                        resource.Arrayed));
                var start = gradientIndex +
                    (hasGradients ? spatialCoordinateCount * 2 : 0);
                var coordinates = resource.Arrayed
                    ? BuildFloatArrayCoordinates(image, start)
                    : BuildFloatCoordinates(image, start);
                var explicitLod = sampleSuffix == "L" || isZeroLod || hasGradients;
                uint offset = 0;
                uint offsetOperand = 0;
                if (hasOffset)
                {
                    var packedOffset =
                        _evaluation.ImageBindings[bindingIndex].PackedOffset;
                    if (packedOffset.HasValue)
                    {
                        offset = BuildConstantImageOffset(packedOffset.Value);
                        offsetOperand = 0x8u;
                    }
                    else
                    {
                        offset = BuildImageOffset(image, 0);
                        offsetOperand = 0x10u;
                    }
                }

                var imageOperands = (hasBias ? 1u : 0u) |
                    (hasGradients ? 4u : explicitLod ? 2u : 0u) |
                    offsetOperand;
                var reference = hasCompare
                    ? Bitcast(
                        _floatType,
                        LoadV(image.GetAddressRegister(compareIndex)))
                    : 0u;
                var operands = new List<uint>
                {
                    imageObject,
                    coordinates,
                };

                if (imageOperands != 0)
                {
                    operands.Add(imageOperands);
                    if (hasBias)
                    {
                        operands.Add(
                            Bitcast(
                                _floatType,
                                LoadV(image.GetAddressRegister(biasIndex))));
                    }

                    if (hasGradients)
                    {
                        operands.Add(BuildFloatCoordinates(image, gradientIndex));
                        operands.Add(BuildFloatCoordinates(
                            image,
                            gradientIndex + spatialCoordinateCount));
                    }
                    else if (explicitLod)
                    {
                        operands.Add(
                            isZeroLod
                                ? Float(0)
                                : Bitcast(
                                    _floatType,
                                    LoadV(image.GetAddressRegister(
                                        start + coordinateCount))));
                    }

                    if (hasOffset)
                    {
                        operands.Add(offset);
                    }
                }

                sampled = _module.AddInstruction(
                    explicitLod
                        ? SpirvOp.ImageSampleExplicitLod
                        : SpirvOp.ImageSampleImplicitLod,
                    resource.VectorType,
                    [.. operands]);
                if (hasCompare)
                {
                    sampled = EmitManualDepthCompare(resource, sampled, reference);
                }
            }
            else if (instruction.Opcode.StartsWith(
                         "ImageGather4",
                         StringComparison.Ordinal))
            {
                var gatherSuffix = instruction.Opcode["ImageGather4".Length..];
                var hasOffset = gatherSuffix.EndsWith("O", StringComparison.Ordinal);
                if (hasOffset)
                {
                    gatherSuffix = gatherSuffix[..^1];
                }

                var hasClamp = gatherSuffix.Contains("Cl", StringComparison.Ordinal);
                if (hasClamp)
                {
                    gatherSuffix = gatherSuffix.Replace("Cl", "", StringComparison.Ordinal);
                }

                var hasCompare = gatherSuffix.StartsWith("C", StringComparison.Ordinal);
                if (hasCompare)
                {
                    gatherSuffix = gatherSuffix[1..];
                }

                var hasBias = gatherSuffix == "B";
                var hasLod = gatherSuffix == "L";
                if (gatherSuffix is not ("" or "B" or "L" or "Lz"))
                {
                    error = $"unsupported image gather variant {instruction.Opcode}";
                    return false;
                }

                if (hasClamp)
                {
                    error = $"image gather LOD clamp is unsupported for {instruction.Opcode}";
                    return false;
                }

                var biasIndex = hasOffset ? 1 : 0;
                var compareIndex = biasIndex + (hasBias ? 1 : 0);
                var start = compareIndex + (hasCompare ? 1 : 0);
                var coordinates = resource.Arrayed
                    ? BuildFloatArrayCoordinates(image, start)
                    : BuildFloatCoordinates(image, start);
                var offset = hasOffset ? BuildImageOffset(image, 0) : 0u;
                var reference = hasCompare
                    ? Bitcast(
                        _floatType,
                        LoadV(image.GetAddressRegister(compareIndex)))
                    : 0u;
                var operands = new List<uint>
                {
                    imageObject,
                    coordinates,
                };
                if (hasCompare)
                {
                    operands.Add(UInt(0));
                }
                else
                {
                    uint component = 0;
                    while (component < 3 &&
                           (image.Dmask & (1u << (int)component)) == 0)
                    {
                        component++;
                    }

                    operands.Add(UInt(component));
                }

                var imageOperands = (hasBias ? 0x01u : 0u) |
                    (hasLod ? 0x02u : 0u) |
                    (hasOffset ? 0x10u : 0u);
                if (imageOperands != 0)
                {
                    operands.Add(imageOperands);
                    if (hasBias)
                    {
                        operands.Add(
                            Bitcast(
                                _floatType,
                                LoadV(image.GetAddressRegister(biasIndex))));
                    }

                    if (hasLod)
                    {
                        operands.Add(
                            Bitcast(
                                _floatType,
                                LoadV(image.GetAddressRegister(
                                    start + (resource.Arrayed ? 3 : 2)))));
                    }

                    if (hasOffset)
                    {
                        operands.Add(offset);
                    }
                }

                sampled = _module.AddInstruction(
                    SpirvOp.ImageGather,
                    resource.VectorType,
                    [.. operands]);
                if (hasCompare)
                {
                    var compared = new uint[4];
                    for (var component = 0u; component < 4; component++)
                    {
                        var texel = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            resource.ComponentType,
                            sampled,
                            component);
                        compared[component] = EmitDepthCompareScalar(resource, texel, reference);
                    }

                    sampled = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        resource.VectorType,
                        compared);
                }

                writeAllComponents = true;
            }
            else
            {
                error = $"unsupported image opcode {instruction.Opcode}";
                return false;
            }

            StoreImageComponents(image, resource, sampled, writeAllComponents);
            return true;
        }

        private void StoreImageComponents(
            Gen5ImageControl image,
            SpirvImageResource resource,
            uint texel,
            bool writeAllComponents = false)
        {
            uint output = 0;
            for (uint component = 0; component < 4; component++)
            {
                if (!writeAllComponents &&
                    (image.Dmask & (1u << (int)component)) == 0)
                {
                    continue;
                }

                var value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    resource.ComponentType,
                    texel,
                    component);
                var raw = resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => value,
                    _ => Bitcast(_uintType, value),
                };
                StoreV(image.VectorData + output++, raw);
            }
        }

        private uint EmitImageAtomic32(
            string opcode,
            uint pointer,
            uint vectorData,
            SpirvImageResource resource)
        {
            const uint imageAcquire = 0x802;
            const uint imageAcquireRelease = 0x808;
            var operation = opcode[(opcode.IndexOf("Atomic", StringComparison.Ordinal) + 6)..];
            uint LoadData(uint register)
            {
                var raw = LoadV(register);
                return resource.ComponentKind == ImageComponentKind.Uint
                    ? raw
                    : Bitcast(_intType, raw);
            }

            return operation switch
            {
                "Cmpswap" => _module.AddInstruction(
                    SpirvOp.AtomicCompareExchange,
                    resource.ComponentType,
                    pointer,
                    UInt(1),
                    UInt(imageAcquireRelease),
                    UInt(imageAcquire),
                    LoadData(vectorData),
                    LoadData(vectorData + 1)),
                "Inc" or "Dec" => EmitAtomicCompareExchangeLoop(
                    pointer,
                    resource.ComponentType,
                    UInt(1),
                    UInt(imageAcquire),
                    UInt(imageAcquireRelease),
                    UInt(imageAcquire),
                    expected =>
                    {
                        var expectedRaw = resource.ComponentKind == ImageComponentKind.Uint
                            ? expected
                            : Bitcast(_uintType, expected);
                        var desiredRaw = EmitAtomicIncDecDesiredValue(
                            operation == "Inc",
                            vectorData,
                            expectedRaw);
                        return resource.ComponentKind == ImageComponentKind.Uint
                            ? desiredRaw
                            : Bitcast(_intType, desiredRaw);
                    }),
                _ => _module.AddInstruction(
                    operation switch
                    {
                        "Swap" => SpirvOp.AtomicExchange,
                        "Add" => SpirvOp.AtomicIAdd,
                        "Sub" => SpirvOp.AtomicISub,
                        "Smin" => SpirvOp.AtomicSMin,
                        "Umin" => SpirvOp.AtomicUMin,
                        "Smax" => SpirvOp.AtomicSMax,
                        "Umax" => SpirvOp.AtomicUMax,
                        "And" => SpirvOp.AtomicAnd,
                        "Or" => SpirvOp.AtomicOr,
                        "Xor" => SpirvOp.AtomicXor,
                        _ => throw new InvalidOperationException(
                            $"unsupported image atomic {opcode}"),
                    },
                    resource.ComponentType,
                    pointer,
                    UInt(1),
                    UInt(imageAcquireRelease),
                    LoadData(vectorData)),
            };
        }

        private uint EmitDepthCompareScalar(
            SpirvImageResource resource,
            uint texel,
            uint reference)
        {
            var texelAsFloat = resource.ComponentKind switch
            {
                ImageComponentKind.Uint => _module.AddInstruction(
                    SpirvOp.ConvertUToF, _floatType, texel),
                ImageComponentKind.Sint => _module.AddInstruction(
                    SpirvOp.ConvertSToF, _floatType, texel),
                _ => texel,
            };
            var passes = _module.AddInstruction(
                SpirvOp.FOrdLessThanEqual,
                _boolType,
                reference,
                texelAsFloat);
            return _module.AddInstruction(
                SpirvOp.Select,
                resource.ComponentType,
                passes,
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(1),
                    ImageComponentKind.Sint => _module.Constant(_intType, 1),
                    _ => Float(1),
                },
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(0),
                    ImageComponentKind.Sint => _module.Constant(_intType, 0),
                    _ => Float(0),
                });
        }

        private uint EmitManualDepthCompare(
            SpirvImageResource resource,
            uint sampledVector,
            uint reference)
        {
            var texel = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                resource.ComponentType,
                sampledVector,
                0u);
            var scalar = EmitDepthCompareScalar(resource, texel, reference);
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                resource.VectorType,
                scalar,
                scalar,
                scalar,
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(1),
                    ImageComponentKind.Sint => _module.Constant(_intType, 1),
                    _ => Float(1),
                });
        }

        private uint BuildFloatCoordinates(Gen5ImageControl image, int start)
        {
            var x = Bitcast(
                _floatType,
                LoadV(image.GetAddressRegister(start)));
            var componentCount = GetImageCoordinateComponentCount(
                image.Dimension,
                arrayed: false);
            if (componentCount == 1)
            {
                return x;
            }

            var y = Bitcast(
                _floatType,
                LoadV(image.GetAddressRegister(start + 1)));
            if (componentCount == 3)
            {
                var z = Bitcast(
                    _floatType,
                    LoadV(image.GetAddressRegister(start + 2)));
                return _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    _vec3Type,
                    x,
                    y,
                    z);
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec2Type,
                x,
                y);
        }

        private uint BuildFloatArrayCoordinates(Gen5ImageControl image, int start)
        {
            var x = Bitcast(
                _floatType,
                LoadV(image.GetAddressRegister(start)));
            var y = Bitcast(
                _floatType,
                LoadV(image.GetAddressRegister(start + 1)));
            if (GetImageCoordinateComponentCount(image.Dimension, arrayed: true) == 2)
            {
                return _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    _vec2Type,
                    x,
                    y);
            }

            var layer = Bitcast(
                _floatType,
                LoadV(image.GetAddressRegister(start + 2)));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec3Type,
                x,
                y,
                layer);
        }

        private uint BuildClampedIntegerCoordinates(
            Gen5ImageControl image,
            int start,
            uint extents,
            bool arrayed)
        {
            var componentCount = GetImageCoordinateComponentCount(
                image.Dimension,
                arrayed);
            var components = new uint[componentCount];
            for (var component = 0; component < components.Length; component++)
            {
                var extent = componentCount == 1
                    ? extents
                    : _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _intType,
                        extents,
                        (uint)component);
                components[component] = ClampSignedCoordinateToDynamicExtent(
                    Bitcast(
                        _intType,
                        LoadV(image.GetAddressRegister(start + component))),
                    extent);
            }

            if (components.Length == 1)
            {
                return components[0];
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                GetIntegerCoordinateType(componentCount),
                components);
        }

        private uint BuildIntegerCoordinates(
            Gen5ImageControl image,
            int start,
            bool arrayed = false)
        {
            var componentCount = GetImageCoordinateComponentCount(
                image.Dimension,
                arrayed);
            var components = new uint[componentCount];
            for (var component = 0; component < components.Length; component++)
            {
                components[component] = Bitcast(
                    _intType,
                    LoadV(image.GetAddressRegister(start + component)));
            }

            if (components.Length == 1)
            {
                return components[0];
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                GetIntegerCoordinateType(componentCount),
                components);
        }

        private uint BuildClampedIntegerCoordinates(
            Gen5ImageControl image,
            int start,
            IReadOnlyList<uint> extents)
        {
            var components = new uint[extents.Count];
            for (var component = 0; component < components.Length; component++)
            {
                components[component] = ClampSignedCoordinate(
                    Bitcast(
                        _intType,
                        LoadV(image.GetAddressRegister(start + component))),
                    extents[component]);
            }

            if (components.Length == 1)
            {
                return components[0];
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                GetIntegerCoordinateType((uint)components.Length),
                components);
        }

        private uint GetIntegerCoordinateType(uint componentCount) =>
            componentCount == 1
                ? _intType
                : _module.TypeVector(_intType, componentCount);

        private static uint[] GetStaticImageCoordinateExtents(
            uint dimension,
            bool arrayed,
            uint width,
            uint height,
            uint depth) =>
            GetImageCoordinateComponentCount(dimension, arrayed) switch
            {
                1 => [width],
                2 when dimension == 4 => [width, depth],
                2 => [width, height],
                _ => [width, height, depth],
            };

        private uint ClampSignedCoordinate(uint value, uint extent)
        {
            var zero = _module.Constant(_intType, 0);
            var max = _module.Constant(_intType, Math.Max(extent, 1) - 1);
            var belowZero = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                value,
                zero);
            var atLeastZero = _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                belowZero,
                zero,
                value);
            var aboveMax = _module.AddInstruction(
                SpirvOp.SGreaterThan,
                _boolType,
                atLeastZero,
                max);
            return _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                aboveMax,
                max,
                atLeastZero);
        }

        private uint ClampSignedCoordinateToDynamicExtent(
            uint value,
            uint extent)
        {
            var zero = _module.Constant(_intType, 0);
            var one = _module.Constant(_intType, 1);
            var hasPositiveExtent = _module.AddInstruction(
                SpirvOp.SGreaterThan,
                _boolType,
                extent,
                zero);
            var safeExtent = _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                hasPositiveExtent,
                extent,
                one);
            var max = _module.AddInstruction(
                SpirvOp.ISub,
                _intType,
                safeExtent,
                one);
            var belowZero = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                value,
                zero);
            var atLeastZero = _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                belowZero,
                zero,
                value);
            var aboveMax = _module.AddInstruction(
                SpirvOp.SGreaterThan,
                _boolType,
                atLeastZero,
                max);
            return _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                aboveMax,
                max,
                atLeastZero);
        }

        private void EmitBoundsCheckedImageWrite(
            uint coordinates,
            IReadOnlyList<uint> extents,
            uint imageObject,
            uint texel)
        {
            var zero = _module.Constant(_intType, 0);
            var inRange = Load(_boolType, _exec);
            for (var component = 0; component < extents.Count; component++)
            {
                var coordinate = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _intType,
                    coordinates,
                    (uint)component);
                var nonNegative = _module.AddInstruction(
                    SpirvOp.SGreaterThanEqual,
                    _boolType,
                    coordinate,
                    zero);
                var belowExtent = _module.AddInstruction(
                    SpirvOp.SLessThan,
                    _boolType,
                    coordinate,
                    _module.Constant(_intType, extents[component]));
                var componentInRange = _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    nonNegative,
                    belowExtent);
                inRange = _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    inRange,
                    componentInRange);
            }
            var writeLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                inRange,
                writeLabel,
                mergeLabel);
            _module.AddLabel(writeLabel);
            _module.AddStatement(
                SpirvOp.ImageWrite,
                imageObject,
                coordinates,
                texel);
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private static bool TryGetImageBounds(
            IReadOnlyList<uint> descriptor,
            out uint width,
            out uint height)
        {
            width = 0;
            height = 0;
            if (descriptor.Count < 3)
            {
                return false;
            }

            width = (((descriptor[1] >> 30) & 0x3u) |
                     ((descriptor[2] & 0xFFFu) << 2)) + 1;
            height = ((descriptor[2] >> 14) & 0x3FFFu) + 1;
            return width != 0 && height != 0 && width <= 16384 && height <= 16384;
        }

        private static bool TryGetImageExtents(
            IReadOnlyList<uint> descriptor,
            uint dimension,
            out uint width,
            out uint height,
            out uint depth)
        {
            depth = 1;
            if (!TryGetImageBounds(descriptor, out width, out height))
            {
                return false;
            }

            if (GetImageCoordinateComponentCount(
                    dimension,
                    arrayed: dimension is 4 or 5 or 7) >= 3 ||
                dimension == 4)
            {
                if (descriptor.Count < 5)
                {
                    return false;
                }

                depth = (descriptor[4] & 0x1FFFu) + 1;
            }

            return depth is > 0 and <= 8192;
        }

        private uint BuildImageOffset(Gen5ImageControl image, int component)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var packed = Bitcast(
                _intType,
                LoadV(image.GetAddressRegister(component)));
            var x = _module.AddInstruction(
                SpirvOp.BitFieldSExtract,
                _intType,
                packed,
                UInt(0),
                UInt(6));
            var y = _module.AddInstruction(
                SpirvOp.BitFieldSExtract,
                _intType,
                packed,
                UInt(8),
                UInt(6));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private uint BuildConstantImageOffset(uint packed)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            static int SignExtend6(uint value) => (int)(value << 26) >> 26;
            var x = _module.Constant(
                _intType,
                unchecked((uint)SignExtend6(packed & 0x3Fu)));
            var y = _module.Constant(
                _intType,
                unchecked((uint)SignExtend6((packed >> 8) & 0x3Fu)));
            return _module.ConstantComposite(ivec2, x, y);
        }

        private bool TryEmitExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 4)
            {
                error = "missing export sources";
                return false;
            }

            if (_stage == Gen5SpirvStage.Pixel)
            {
                if (!_pixelOutputs.TryGetValue(export.Target, out var output))
                {
                    return true;
                }

                var values = new uint[4];
                for (var component = 0; component < 4; component++)
                {
                    var enabled = (export.EnableMask & (1u << component)) != 0;
                    if (!enabled)
                    {
                        values[component] = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            output.Kind switch
                            {
                                Gen5PixelOutputKind.Uint => _uintType,
                                Gen5PixelOutputKind.Sint => _intType,
                                _ => _floatType,
                            },
                            Load(output.Type, output.Variable),
                            (uint)component);
                        continue;
                    }

                    if (export.Compressed)
                    {
                        var value = LoadCompressedExportComponent(
                            instruction,
                            component);
                        values[component] = output.Kind switch
                        {
                            Gen5PixelOutputKind.Uint =>
                                EmitSaturatingFloatToInteger(
                                    value,
                                    _floatType,
                                    signed: false),
                            Gen5PixelOutputKind.Sint => Bitcast(
                                _intType,
                                EmitSaturatingFloatToInteger(
                                    value,
                                    _floatType,
                                    signed: true)),
                            _ => value,
                        };
                        continue;
                    }

                    var raw = LoadV(instruction.Sources[component].Value);
                    values[component] = output.Kind switch
                    {
                        Gen5PixelOutputKind.Uint => raw,
                        Gen5PixelOutputKind.Sint => Bitcast(_intType, raw),
                        _ => Bitcast(_floatType, raw),
                    };
                }

                var vector = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    output.Type,
                    values);
                vector = _module.AddInstruction(
                    SpirvOp.Select,
                    output.Type,
                    Load(_boolType, _exec),
                    vector,
                    Load(output.Type, output.Variable));
                Store(output.Variable, vector);
                return true;
            }

            if (_stage != Gen5SpirvStage.Vertex)
            {
                return true;
            }

            uint outputVariable;
            if (export.Target is >= 12 and < 16)
            {
                if (export.Target != 12)
                {
                    return true;
                }

                outputVariable = _positionOutput;
            }
            else if (export.Target is >= 32 and < 64 &&
                     _vertexOutputs.TryGetValue(export.Target - 32, out var parameter))
            {
                outputVariable = parameter;
            }
            else
            {
                return true;
            }

            var components = new uint[4];
            for (var component = 0; component < 4; component++)
            {
                components[component] = (export.EnableMask & (1u << component)) != 0
                    ? export.Compressed
                        ? LoadCompressedExportComponent(instruction, component)
                        : Bitcast(
                            _floatType,
                            LoadV(instruction.Sources[component].Value))
                    : Float(component == 3 ? 1f : 0f);
            }

            var outputValue = _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec4Type,
                components);
            outputValue = _module.AddInstruction(
                SpirvOp.Select,
                _vec4Type,
                Load(_boolType, _exec),
                outputValue,
                Load(_vec4Type, outputVariable));
            Store(outputVariable, outputValue);
            return true;
        }

        private uint LoadCompressedExportComponent(
            Gen5ShaderInstruction instruction,
            int component)
        {
            var packed = LoadV(instruction.Sources[component >> 1].Value);
            var unpacked = Ext(62, _vec2Type, packed);
            return _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                unpacked,
                (uint)(component & 1));
        }

        private uint GetPixelOutputType(Gen5PixelOutputKind kind) =>
            kind switch
            {
                Gen5PixelOutputKind.Uint => _uvec4Type,
                Gen5PixelOutputKind.Sint => _module.TypeVector(_intType, 4),
                _ => _vec4Type,
            };

        private uint LoadBufferWord(int binding, uint dwordAddress)
        {
            var pointer = BufferWordPointer(binding, dwordAddress);
            return Load(_uintType, pointer);
        }

        private void StoreBufferWord(int binding, uint dwordAddress, uint value)
        {
            var pointer = BufferWordPointer(binding, dwordAddress);
            Store(pointer, value);
        }

        private uint BufferWordPointer(int binding, uint dwordAddress)
        {
            var addressedDword = ApplyGuestBufferWordBias(
                binding,
                dwordAddress);
            return _module.AddInstruction(
                SpirvOp.AccessChain,
                _storageUintPointer,
                _globalBuffers,
                UInt((uint)binding),
                UInt(0),
                addressedDword);
        }

        private uint IsBufferWordInRange(int binding, uint dwordAddress)
        {
            var buffer = _module.AddInstruction(
                SpirvOp.AccessChain,
                _storageBlockPointer,
                _globalBuffers,
                UInt((uint)binding));
            var length = _module.AddInstruction(
                SpirvOp.ArrayLength,
                _uintType,
                buffer,
                0);
            return _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                ApplyGuestBufferWordBias(binding, dwordAddress),
                length);
        }

        private uint ApplyGuestBufferWordBias(
            int binding,
            uint dwordAddress)
        {
            var guestBindingIndex = binding - _globalBufferBase;
            if ((uint)guestBindingIndex >=
                (uint)_evaluation.GlobalMemoryBindings.Count)
            {
                return dwordAddress;
            }

            var byteBias =
                _evaluation.GlobalMemoryBindings[guestBindingIndex]
                    .BaseAddress &
                (_storageBufferOffsetAlignment - 1);
            if ((byteBias & (sizeof(uint) - 1)) != 0)
            {
                throw new InvalidOperationException(
                    $"guest storage-buffer address " +
                    $"0x{_evaluation.GlobalMemoryBindings[guestBindingIndex].BaseAddress:X16} " +
                    $"is not dword aligned");
            }

            if (_scalarRegisterBufferIndex >= 0)
            {
                var scalarAddress =
                    _evaluation.GlobalMemoryBindings[guestBindingIndex]
                        .ScalarAddress;
                var runtimeBaseAddress = LoadS(scalarAddress);
                var runtimeDwordBias = ShiftRightLogical(
                    BitwiseAnd(
                        runtimeBaseAddress,
                        UInt(checked((uint)(_storageBufferOffsetAlignment - 1)))),
                    UInt(2));
                return IAdd(dwordAddress, runtimeDwordBias);
            }

            var dwordBias = checked((uint)(byteBias / sizeof(uint)));
            return dwordBias == 0
                ? dwordAddress
                : IAdd(dwordAddress, UInt(dwordBias));
        }

        private uint ScalarPointer(uint register) => ScalarPointerAt(UInt(register));

        private uint ScalarPointerAt(uint index) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _scalarRegisters,
                index);

        private uint VectorPointer(uint register) => VectorPointerAt(UInt(register));

        private uint VectorPointerAt(uint index) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _vectorRegisters,
                index);

        private uint LoadS(uint register) => Load(_uintType, ScalarPointer(register));

        private uint LoadSAt(uint index) => Load(_uintType, ScalarPointerAt(index));

        private uint LoadV(uint register) => Load(_uintType, VectorPointer(register));

        private uint LoadVAt(uint index) => Load(_uintType, VectorPointerAt(index));

        private void StoreS(uint register, uint value)
        {
            Store(ScalarPointer(register), value);
            if (register is 106 or 107)
            {
                Store(_vcc, IsWaveMaskActive(LoadS64(106)));
            }
            else if (register is 126 or 127)
            {
                Store(_exec, IsWaveMaskActive(LoadS64(126)));
            }
        }

        private void StoreSAt(uint index, uint value)
        {
            Store(ScalarPointerAt(index), value);
            Store(_vcc, IsWaveMaskActive(LoadS64(106)));
            Store(_exec, IsWaveMaskActive(LoadS64(126)));
        }

        private void StoreV(uint register, uint value, bool guardWithExec = true)
        {
            StoreVAt(UInt(register), value, guardWithExec);
        }

        private void StoreVAt(uint index, uint value, bool guardWithExec = true)
        {
            if (guardWithExec)
            {
                var active = Load(_boolType, _exec);
                var oldValue = LoadVAt(index);
                value = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    active,
                    value,
                    oldValue);
            }

            Store(VectorPointerAt(index), value);
        }

        private uint Load(uint type, uint pointer) =>
            _module.AddInstruction(SpirvOp.Load, type, pointer);

        private void Store(uint pointer, uint value) =>
            _module.AddStatement(SpirvOp.Store, pointer, value);

        private uint UInt(uint value) => _module.Constant(_uintType, value);

        private uint Float(float value) => _module.ConstantFloat(_floatType, value);

        private uint ClampFloat01(uint value) =>
            Ext(43, _floatType, value, Float(0.0f), Float(1.0f));

        private uint ClampFloatN11(uint value) =>
            Ext(43, _floatType, value, Float(-1.0f), Float(1.0f));

        private uint Bitcast(uint type, uint value) =>
            _module.AddInstruction(SpirvOp.Bitcast, type, value);

        private uint IAdd(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.IAdd, _uintType, left, right);

        private uint ShiftLeftLogical(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftLeftLogical,
                _uintType,
                left,
                BitwiseAnd(right, UInt(31)));

        private uint ShiftRightLogical(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftRightLogical,
                _uintType,
                left,
                BitwiseAnd(right, UInt(31)));

        private uint ShiftRightArithmetic(uint left, uint right) =>
            Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.ShiftRightArithmetic,
                    _intType,
                    Bitcast(_intType, left),
                    BitwiseAnd(right, UInt(31))));

        private uint ShiftLeftLogical64(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftLeftLogical,
                _ulongType,
                left,
                BitwiseAnd64(right, _module.Constant64(_ulongType, 63)));

        private uint ShiftRightLogical64(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftRightLogical,
                _ulongType,
                left,
                BitwiseAnd64(right, _module.Constant64(_ulongType, 63)));

        private uint ShiftRightArithmetic64(uint left, uint right) =>
            Bitcast(
                _ulongType,
                _module.AddInstruction(
                    SpirvOp.ShiftRightArithmetic,
                    _longType,
                    Bitcast(_longType, left),
                    BitwiseAnd64(right, _module.Constant64(_ulongType, 63))));

        private uint BitwiseAnd(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseAnd, _uintType, left, right);

        private uint BitwiseAnd64(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseAnd, _ulongType, left, right);

        private uint BitwiseOr64(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseOr, _ulongType, left, right);

        private uint BitwiseOr(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseOr, _uintType, left, right);

        private uint BitwiseXor(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseXor, _uintType, left, right);

        private uint LogicalNot(uint value) =>
            _module.AddInstruction(SpirvOp.LogicalNot, _boolType, value);

        private uint SubgroupAny(uint condition) =>
            _perInvocationGraphicsMasks ||
            !HasGuestWaveLanes()
                ? condition
                : _emulateWave64
                    ? IsNotZero64(BooleanToWaveMask(condition))
                    : _module.AddInstruction(
                        SpirvOp.GroupNonUniformAny,
                        _boolType,
                        UInt(3),
                        condition);

        private uint GuestWaveLane()
        {
            if (_emulateWave64)
            {
                return BitwiseAnd(
                    Load(_uintType, _localInvocationIndexInput),
                    UInt(63));
            }

            if (_subgroupInvocationIdInput != 0)
            {
                return BitwiseAnd(
                    Load(_uintType, _subgroupInvocationIdInput),
                    UInt(_waveLaneCount - 1));
            }

            return UInt(0);
        }

        private bool HasGuestWaveLanes() =>
            _emulateWave64 || _subgroupInvocationIdInput != 0;

        private uint CurrentLaneBit()
        {
            if (!HasGuestWaveLanes())
            {
                return _module.Constant64(_ulongType, 1);
            }

            var maskedLane = GuestWaveLane();
            var shifted = ShiftLeftLogical64(
                _module.Constant64(_ulongType, 1),
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    maskedLane));
            if (_emulateWave64)
            {
                return shifted;
            }

            return _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                IsCurrentLaneInRdnaWave(),
                shifted,
                _module.Constant64(_ulongType, 0));
        }

        private uint IsCurrentLaneInRdnaWave() =>
            _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                Load(_uintType, _subgroupInvocationIdInput),
                UInt(RdnaWaveLaneCount));

        private uint BooleanToLaneMask(uint condition) =>
            _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                condition,
                CurrentLaneBit(),
                _module.Constant64(_ulongType, 0));

        private uint BooleanToWaveMask(uint condition)
        {
            if (_perInvocationGraphicsMasks)
            {
                return _module.AddInstruction(
                    SpirvOp.Select,
                    _ulongType,
                    condition,
                    _module.Constant64(_ulongType, 1),
                    _module.Constant64(_ulongType, 0));
            }

            if (!HasGuestWaveLanes())
            {
                return BooleanToLaneMask(condition);
            }

            if (!_emulateWave64)
            {
                var ballot = _module.AddInstruction(
                    SpirvOp.GroupNonUniformBallot,
                    _uvec4Type,
                    UInt(3),
                    condition);
                var low = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _uintType,
                    ballot,
                    0);
                return _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    low);
            }

            var lane = GuestWaveLane();
            var firstLane = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                lane,
                UInt(0));
            EmitConditional(firstLane, () =>
            {
                Store(WaveScratchPointer(UInt(0)), UInt(0));
                Store(WaveScratchPointer(UInt(1)), UInt(0));
            });
            EmitWave64Barrier();

            var half = ShiftRightLogical(lane, UInt(5));
            var bit = ShiftLeftLogical(UInt(1), lane);
            EmitConditional(condition, () =>
            {
                _module.AddInstruction(
                    SpirvOp.AtomicOr,
                    _uintType,
                    WaveScratchPointer(half),
                    UInt(2),
                    UInt(0x108),
                    bit);
            });
            EmitWave64Barrier();

            var lowMask = Load(_uintType, WaveScratchPointer(UInt(0)));
            var highMask = Load(_uintType, WaveScratchPointer(UInt(1)));
            var combined = BitwiseOr64(
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    lowMask),
                ShiftLeftLogical64(
                    _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        highMask),
                    _module.Constant64(_ulongType, 32)));
            EmitWave64Barrier();
            return combined;
        }

        private uint WaveBroadcast(uint value, uint lane)
        {
            if (!_emulateWave64)
            {
                return _module.AddInstruction(
                    SpirvOp.GroupNonUniformShuffle,
                    _uintType,
                    UInt(3),
                    value,
                    BitwiseAnd(lane, UInt(31)));
            }

            InitializeInactiveWave64Lanes();
            Store(WaveScratchPointer(GuestWaveLane()), value);
            EmitWave64Barrier();
            var result = Load(
                _uintType,
                WaveScratchPointer(BitwiseAnd(lane, UInt(63))));
            EmitWave64Barrier();
            return result;
        }

        private void InitializeInactiveWave64Lanes()
        {
            var localInvocationCount = checked(
                (uint)((ulong)_localSizeX * _localSizeY * _localSizeZ));
            var partialWaveLaneCount = localInvocationCount & 63u;
            if (partialWaveLaneCount == 0)
            {
                return;
            }

            var currentLane = GuestWaveLane();
            var localInvocationIndex = Load(_uintType, _localInvocationIndexInput);
            var currentWaveBase = BitwiseAnd(localInvocationIndex, UInt(~63u));
            var partialWaveBase = UInt(localInvocationCount & ~63u);
            var isPartialWave = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                currentWaveBase,
                partialWaveBase);
            for (var offset = partialWaveLaneCount;
                 offset < 64;
                 offset += partialWaveLaneCount)
            {
                var inactiveLane = IAdd(currentLane, UInt(offset));
                var isInsideWave = _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    inactiveLane,
                    UInt(64));
                var shouldInitialize = _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    isPartialWave,
                    isInsideWave);
                EmitConditional(
                    shouldInitialize,
                    () => Store(WaveScratchPointer(inactiveLane), UInt(0)));
            }

            EmitWave64Barrier();
        }

        private uint WaveScratchPointer(uint index)
        {
            var waveBase = BitwiseAnd(
                Load(_uintType, _localInvocationIndexInput),
                UInt(~63u));
            return _module.AddInstruction(
                SpirvOp.AccessChain,
                _waveScratchElementPointer,
                _waveScratch,
                IAdd(waveBase, index));
        }

        private void EmitWave64Barrier()
        {
            _module.AddStatement(
                SpirvOp.ControlBarrier,
                UInt(2),
                UInt(2),
                UInt(0x108));
        }

        // A wave-mask SGPR (VCC/EXEC) consumed as a per-lane predicate — the
        // condition of VCndmask, a VCC/EXEC branch, or the derived _vcc/_exec
        // bool — must be tested at the CURRENT lane's bit, exactly as the
        // hardware does, not as "the 64-bit value is non-zero". The two coincide
        // for comparison results (only the lane's own bit is ever set), so the
        // single-lane path historically used a cheaper whole-word non-zero test.
        // But bitwise-complement wave-mask idioms (S_NOT/S_ORN2/S_ANDN2/S_NAND/
        // S_NOR on a 64-bit mask) set the unused upper 63 bits; a whole-word test
        // then reports "lane active" even when this lane's bit is clear. Unity's
        // PostProcessing NaN killer does exactly this (`anyNaN | ~allFinite`),
        // which made every valid pixel read as NaN and get replaced with 0 —
        // zeroing the whole scene before tonemap. Extract the lane bit always.
        private uint IsWaveMaskActive(uint mask) =>
            IsCurrentLaneSet(mask);

        private uint IsCurrentLaneSet(uint mask) =>
            IsNotZero64(
                _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    mask,
                    CurrentLaneBit()));

        private void StoreWaveMask(uint register, uint condition) =>
            StoreS64(register, BooleanToWaveMask(condition));

        private void EmitExecConditional(Action emit)
        {
            var activeLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var active = Load(_boolType, _exec);
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                active,
                activeLabel,
                mergeLabel);
            _module.AddLabel(activeLabel);
            emit();
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private void EmitConditional(uint condition, Action emit)
        {
            var activeLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                condition,
                activeLabel,
                mergeLabel);
            _module.AddLabel(activeLabel);
            emit();
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private bool UsesLds() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Control is Gen5DataShareControl &&
                instruction.Opcode is not (
                    "DsPermuteB32" or "DsBpermuteB32" or "DsSwizzleB32"));

        private bool UsesScratch() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode.StartsWith("Scratch", StringComparison.Ordinal));

        private bool UsesSubgroupShuffle() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode is
                    "VReadlaneB32" or
                    "VReadfirstlaneB32" or
                    "VPermlane16B32" or
                    "VPermlanex16B32" or
                    "DsPermuteB32" or
                    "DsBpermuteB32" or
                    "DsSwizzleB32");

        private bool UsesReadFirstLane()
        {
            foreach (var instruction in _state.Program.Instructions)
            {
                if (instruction.Opcode == "VReadfirstlaneB32")
                {
                    return true;
                }
            }

            return false;
        }

        private bool UsesLaneOperations() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode is
                    "VReadfirstlaneB32" or "VReadlaneB32" or "VWritelaneB32");

        private bool UsesDsAddTid() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode is "DsWriteAddtidB32" or "DsReadAddtidB32");

        private bool UsesWaveControl() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                instruction.Opcode.Contains("Wrexec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("SCbranchExec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("SCbranchVcc", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) ||
                instruction.Sources.Any(IsWaveMaskOperand) ||
                instruction.Destinations.Any(IsWaveMaskOperand));

        private bool UsesSubgroupOperations() =>
            UsesLaneOperations() ||
            UsesDsAddTid() ||
            (_stage == Gen5SpirvStage.Compute &&
             (UsesSubgroupShuffle() || UsesWaveControl()));

        private static bool IsWaveMaskOperand(Gen5Operand operand) =>
            operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value is 106 or 107 or 126 or 127;

        private static bool TryGetVectorDestination(
            Gen5ShaderInstruction instruction,
            out uint destination)
        {
            if (instruction.Destinations.Count != 0 &&
                instruction.Destinations[0].Kind == Gen5OperandKind.VectorRegister)
            {
                destination = instruction.Destinations[0].Value;
                return true;
            }

            destination = 0;
            return false;
        }

        private static bool IsBranch(string opcode) =>
            opcode == "SBranch" ||
            opcode.StartsWith("SCbranch", StringComparison.Ordinal);

        private static bool TryGetBranchTargetPc(
            Gen5ShaderInstruction instruction,
            out uint targetPc)
        {
            targetPc = 0;
            if (instruction.Encoding != Gen5ShaderEncoding.Sopp ||
                instruction.Words.Count == 0)
            {
                return false;
            }

            var offset = unchecked((short)(instruction.Words[0] & 0xFFFF));
            var nextPc = (long)instruction.Pc +
                (instruction.Words.Count * sizeof(uint));
            var target = nextPc + (offset * sizeof(uint));
            if (target < 0 || target > uint.MaxValue)
            {
                return false;
            }

            targetPc = (uint)target;
            return true;
        }

        private static IReadOnlyList<ShaderBlock> BuildBasicBlocks(
            IReadOnlyList<Gen5ShaderInstruction> instructions)
        {
            if (instructions.Count == 0)
            {
                return [];
            }

            var leaders = new SortedSet<uint> { instructions[0].Pc };
            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (IsBranch(instruction.Opcode) &&
                    TryGetBranchTargetPc(instruction, out var targetPc))
                {
                    leaders.Add(targetPc);
                }

                if ((IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm") &&
                    index + 1 < instructions.Count)
                {
                    leaders.Add(instructions[index + 1].Pc);
                }
            }

            var starts = leaders
                .Where(pc => instructions.Any(instruction => instruction.Pc == pc))
                .ToArray();
            var blocks = new List<ShaderBlock>(starts.Length);
            for (var index = 0; index < starts.Length; index++)
            {
                var startIndex = FindInstructionIndex(instructions, starts[index]);
                var endIndex = index + 1 < starts.Length
                    ? FindInstructionIndex(instructions, starts[index + 1])
                    : instructions.Count;
                if (startIndex >= 0 && endIndex > startIndex)
                {
                    blocks.Add(new ShaderBlock(starts[index], startIndex, endIndex));
                }
            }

            return blocks;
        }

        private static int FindInstructionIndex(
            IReadOnlyList<Gen5ShaderInstruction> instructions,
            uint pc)
        {
            for (var index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].Pc == pc)
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool TryFindBlock(
            IReadOnlyList<ShaderBlock> blocks,
            uint pc,
            out int block)
        {
            for (var index = 0; index < blocks.Count; index++)
            {
                if (blocks[index].StartPc == pc)
                {
                    block = index;
                    return true;
                }
            }

            block = -1;
            return false;
        }

        private readonly record struct ShaderBlock(
            uint StartPc,
            int StartIndex,
            int EndIndex);
    }
}
