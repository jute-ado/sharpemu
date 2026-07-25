// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static class SpirvFixedShaders
{
    public static byte[] CreateFullscreenVertex(uint attributeCount)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var boolType = module.TypeBool();
        var uintType = module.TypeInt(32, signed: false);
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputUintPointer = module.TypePointer(SpirvStorageClass.Input, uintType);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);

        var vertexIndex = module.AddGlobalVariable(inputUintPointer, SpirvStorageClass.Input);
        module.AddName(vertexIndex, "vertexIndex");
        module.AddDecoration(
            vertexIndex,
            SpirvDecoration.BuiltIn,
            (uint)SpirvBuiltIn.VertexIndex);

        var position = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(position, "position");
        module.AddDecoration(position, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.Position);

        var attributes = new uint[attributeCount];
        for (uint index = 0; index < attributeCount; index++)
        {
            attributes[index] =
                module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
            module.AddName(attributes[index], $"attr{index}");
            module.AddDecoration(attributes[index], SpirvDecoration.Location, index);
            module.AddDecoration(attributes[index], SpirvDecoration.NoPerspective);
        }

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var indexValue = module.AddInstruction(SpirvOp.Load, uintType, vertexIndex);
        var one = module.Constant(uintType, 1);
        var two = module.Constant(uintType, 2);
        var shifted = module.AddInstruction(SpirvOp.ShiftLeftLogical, uintType, indexValue, one);
        var xBits = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, shifted, two);
        var yBits = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, indexValue, two);
        var x = module.AddInstruction(SpirvOp.ConvertUToF, floatType, xBits);
        var y = module.AddInstruction(SpirvOp.ConvertUToF, floatType, yBits);
        var zero = module.ConstantFloat(floatType, 0f);
        var oneFloat = module.ConstantFloat(floatType, 1f);
        var twoFloat = module.ConstantFloat(floatType, 2f);
        var xPosition = module.AddInstruction(SpirvOp.FMul, floatType, x, twoFloat);
        xPosition = module.AddInstruction(SpirvOp.FSub, floatType, xPosition, oneFloat);
        var yPosition = module.AddInstruction(SpirvOp.FMul, floatType, y, twoFloat);
        yPosition = module.AddInstruction(SpirvOp.FSub, floatType, yPosition, oneFloat);
        var positionValue = module.AddInstruction(
            SpirvOp.CompositeConstruct,
            vec4Type,
            xPosition,
            yPosition,
            zero,
            oneFloat);
        module.AddStatement(SpirvOp.Store, position, positionValue);

        var attributeValue = module.AddInstruction(
            SpirvOp.CompositeConstruct,
            vec4Type,
            x,
            y,
            zero,
            oneFloat);
        foreach (var attribute in attributes)
        {
            module.AddStatement(SpirvOp.Store, attribute, attributeValue);
        }

        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        var interfaces = new uint[2 + attributes.Length];
        interfaces[0] = vertexIndex;
        interfaces[1] = position;
        attributes.CopyTo(interfaces, 2);
        module.AddEntryPoint(SpirvExecutionModel.Vertex, main, "main", interfaces);
        _ = boolType;
        return module.Build();
    }

    public static byte[] CreateCopyFragment() =>
        CreateCopyFragment(binding: 1, arrayed: false);

    public static byte[] CreateArrayCopyFragment(uint binding) =>
        CreateCopyFragment(binding, arrayed: true);

    public static byte[] CreateArrayFetchFragment(uint binding)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var intType = module.TypeInt(32, signed: true);
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var ivec3Type = module.TypeVector(intType, 3);
        var inputPointer = module.TypePointer(SpirvStorageClass.Input, vec4Type);
        var outputPointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var imageType = module.TypeImage(
            floatType,
            SpirvImageDim.Dim2D,
            depth: false,
            arrayed: true,
            multisampled: false,
            sampled: 1,
            SpirvImageFormat.Unknown);
        var sampledImageType = module.TypeSampledImage(imageType);
        var sampledImagePointer =
            module.TypePointer(SpirvStorageClass.UniformConstant, sampledImageType);

        var fragmentCoordinate = module.AddGlobalVariable(
            inputPointer,
            SpirvStorageClass.Input);
        module.AddName(fragmentCoordinate, "fragCoord");
        module.AddDecoration(
            fragmentCoordinate,
            SpirvDecoration.BuiltIn,
            (uint)SpirvBuiltIn.FragCoord);
        var texture = module.AddGlobalVariable(
            sampledImagePointer,
            SpirvStorageClass.UniformConstant);
        module.AddName(texture, "tex");
        module.AddDecoration(texture, SpirvDecoration.DescriptorSet, 0);
        module.AddDecoration(texture, SpirvDecoration.Binding, binding);
        var output = module.AddGlobalVariable(outputPointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        var fragCoordValue = module.AddInstruction(
            SpirvOp.Load,
            vec4Type,
            fragmentCoordinate);
        var x = module.AddInstruction(
            SpirvOp.ConvertFToS,
            intType,
            module.AddInstruction(
                SpirvOp.CompositeExtract,
                floatType,
                fragCoordValue,
                0));
        var y = module.AddInstruction(
            SpirvOp.ConvertFToS,
            intType,
            module.AddInstruction(
                SpirvOp.CompositeExtract,
                floatType,
                fragCoordValue,
                1));
        var coordinates = module.AddInstruction(
            SpirvOp.CompositeConstruct,
            ivec3Type,
            x,
            y,
            module.Constant(intType, 0));
        var sampledImage = module.AddInstruction(
            SpirvOp.Load,
            sampledImageType,
            texture);
        var image = module.AddInstruction(SpirvOp.Image, imageType, sampledImage);
        var color = module.AddInstruction(
            SpirvOp.ImageFetch,
            vec4Type,
            image,
            coordinates,
            2,
            module.Constant(intType, 0));
        module.AddStatement(SpirvOp.Store, output, color);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(
            SpirvExecutionModel.Fragment,
            main,
            "main",
            [fragmentCoordinate, texture, output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    private static byte[] CreateCopyFragment(uint binding, bool arrayed)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec2Type = module.TypeVector(floatType, 2);
        var vec3Type = module.TypeVector(floatType, 3);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputVec4Pointer = module.TypePointer(SpirvStorageClass.Input, vec4Type);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var imageType = module.TypeImage(
            floatType,
            SpirvImageDim.Dim2D,
            depth: false,
            arrayed,
            multisampled: false,
            sampled: 1,
            SpirvImageFormat.Unknown);
        var sampledImageType = module.TypeSampledImage(imageType);
        var sampledImagePointer =
            module.TypePointer(SpirvStorageClass.UniformConstant, sampledImageType);

        var attribute = module.AddGlobalVariable(inputVec4Pointer, SpirvStorageClass.Input);
        module.AddName(attribute, "attr0");
        module.AddDecoration(attribute, SpirvDecoration.Location, 0);

        var texture = module.AddGlobalVariable(
            sampledImagePointer,
            SpirvStorageClass.UniformConstant);
        module.AddName(texture, "tex0");
        module.AddDecoration(texture, SpirvDecoration.DescriptorSet, 0);
        module.AddDecoration(texture, SpirvDecoration.Binding, binding);

        var output = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var attributeValue = module.AddInstruction(SpirvOp.Load, vec4Type, attribute);
        var twoDimensionalCoordinates = module.AddInstruction(
            SpirvOp.VectorShuffle,
            vec2Type,
            attributeValue,
            attributeValue,
            0,
            1);
        var coordinates = arrayed
            ? module.AddInstruction(
                SpirvOp.CompositeConstruct,
                vec3Type,
                module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    floatType,
                    twoDimensionalCoordinates,
                    0),
                module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    floatType,
                    twoDimensionalCoordinates,
                    1),
                module.ConstantFloat(floatType, 0f))
            : twoDimensionalCoordinates;
        var sampledImage = module.AddInstruction(SpirvOp.Load, sampledImageType, texture);
        var lod = module.ConstantFloat(floatType, 0f);
        var color = module.AddInstruction(
            SpirvOp.ImageSampleExplicitLod,
            vec4Type,
            sampledImage,
            coordinates,
            2,
            lod);
        module.AddStatement(SpirvOp.Store, output, color);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(
            SpirvExecutionModel.Fragment,
            main,
            "main",
            [attribute, texture, output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    public static byte[] CreateSolidFragment(float red, float green, float blue, float alpha)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var output = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        var color = module.ConstantComposite(
            vec4Type,
            module.ConstantFloat(floatType, red),
            module.ConstantFloat(floatType, green),
            module.ConstantFloat(floatType, blue),
            module.ConstantFloat(floatType, alpha));
        module.AddStatement(SpirvOp.Store, output, color);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", [output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    /// <summary>
    /// Diagnostic fragment stage that exposes one interpolated vertex output
    /// directly as color. This keeps the real guest vertex/index/depth path
    /// intact while isolating fragment-shader translation from interface data.
    /// </summary>
    public static byte[] CreateAttributeFragment(uint location)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputPointer = module.TypePointer(SpirvStorageClass.Input, vec4Type);
        var outputPointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var input = module.AddGlobalVariable(inputPointer, SpirvStorageClass.Input);
        module.AddName(input, $"attr{location}");
        module.AddDecoration(input, SpirvDecoration.Location, location);
        var output = module.AddGlobalVariable(outputPointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        var value = module.AddInstruction(SpirvOp.Load, vec4Type, input);
        module.AddStatement(SpirvOp.Store, output, value);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(
            SpirvExecutionModel.Fragment,
            main,
            "main",
            [input, output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    /// <summary>
    /// Minimal fragment stage for fixed-function depth-only passes.  The
    /// guest has no pixel shader and therefore cannot export colour; keeping
    /// this stage output-free preserves that contract while allowing Vulkan
    /// to run early/late depth tests for the translated vertex shader.
    /// </summary>
    public static byte[] CreateDepthOnlyFragment()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", []);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    /// <summary>
    /// Creates a compute kernel that deswizzles RDNA2 tiled surfaces into a
    /// layer-major linear buffer. The kernel supports the exact-XOR and block-table
    /// equations exposed by <c>GnmTiling.GetDetileParams</c>.
    ///
    /// Width and height are element dimensions. Each element occupies
    /// <c>uintsPerElement</c> 32-bit words, allowing the same kernel to copy
    /// 4-, 8-, and 16-byte elements without interpreting their contents.
    ///
    /// Descriptor set 0 uses binding 0 for tiled input, binding 1 for the X-term
    /// table or block table, binding 2 for the Y-term table, and binding 3 for
    /// linear output. Push constants are eleven consecutive uints:
    /// width, height, blockWidth, blockHeight, blockElements, blocksPerRow,
    /// xMask, yMask, srcSliceElements, equation, and uintsPerElement.
    /// </summary>
    public static byte[] CreateDetileCompute()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var boolType = module.TypeBool();
        var uintType = module.TypeInt(32, signed: false);
        var uvec3Type = module.TypeVector(uintType, 3);

        var runtimeArray = module.TypeRuntimeArray(uintType);
        module.AddDecoration(runtimeArray, SpirvDecoration.ArrayStride, 4);
        var bufferStruct = module.TypeStruct(runtimeArray);
        module.AddDecoration(bufferStruct, SpirvDecoration.Block);
        module.AddMemberDecoration(bufferStruct, 0, SpirvDecoration.Offset, 0);
        var bufferPointer = module.TypePointer(
            SpirvStorageClass.StorageBuffer,
            bufferStruct);
        var uintStoragePointer = module.TypePointer(
            SpirvStorageClass.StorageBuffer,
            uintType);

        uint MakeBuffer(uint binding, string name)
        {
            var variable = module.AddGlobalVariable(
                bufferPointer,
                SpirvStorageClass.StorageBuffer);
            module.AddName(variable, name);
            module.AddDecoration(variable, SpirvDecoration.DescriptorSet, 0);
            module.AddDecoration(variable, SpirvDecoration.Binding, binding);
            return variable;
        }

        var tiled = MakeBuffer(0, "tiled");
        var xTerm = MakeBuffer(1, "xTerm");
        var yTerm = MakeBuffer(2, "yTerm");
        var linear = MakeBuffer(3, "outLinear");

        var pushStruct = module.TypeStruct(
            uintType,
            uintType,
            uintType,
            uintType,
            uintType,
            uintType,
            uintType,
            uintType,
            uintType,
            uintType,
            uintType);
        module.AddDecoration(pushStruct, SpirvDecoration.Block);
        for (uint member = 0; member < 11; member++)
        {
            module.AddMemberDecoration(
                pushStruct,
                member,
                SpirvDecoration.Offset,
                member * 4);
        }

        var pushPointer = module.TypePointer(
            SpirvStorageClass.PushConstant,
            pushStruct);
        var pushMemberPointer = module.TypePointer(
            SpirvStorageClass.PushConstant,
            uintType);
        var pushConstants = module.AddGlobalVariable(
            pushPointer,
            SpirvStorageClass.PushConstant);
        module.AddName(pushConstants, "pc");

        var inputUvec3Pointer = module.TypePointer(
            SpirvStorageClass.Input,
            uvec3Type);
        var globalInvocationId = module.AddGlobalVariable(
            inputUvec3Pointer,
            SpirvStorageClass.Input);
        module.AddName(globalInvocationId, "gid");
        module.AddDecoration(
            globalInvocationId,
            SpirvDecoration.BuiltIn,
            (uint)SpirvBuiltIn.GlobalInvocationId);

        var uintConstants = new uint[11];
        for (uint value = 0; value < uintConstants.Length; value++)
        {
            uintConstants[value] = module.Constant(uintType, value);
        }

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var invocation = module.AddInstruction(
            SpirvOp.Load,
            uvec3Type,
            globalInvocationId);
        var invocationX = module.AddInstruction(
            SpirvOp.CompositeExtract,
            uintType,
            invocation,
            0);
        var y = module.AddInstruction(
            SpirvOp.CompositeExtract,
            uintType,
            invocation,
            1);
        var layer = module.AddInstruction(
            SpirvOp.CompositeExtract,
            uintType,
            invocation,
            2);

        uint PushField(uint index)
        {
            var pointer = module.AddInstruction(
                SpirvOp.AccessChain,
                pushMemberPointer,
                pushConstants,
                uintConstants[index]);
            return module.AddInstruction(SpirvOp.Load, uintType, pointer);
        }

        var width = PushField(0);
        var height = PushField(1);
        var blockWidth = PushField(2);
        var blockHeight = PushField(3);
        var blockElements = PushField(4);
        var blocksPerRow = PushField(5);
        var xMask = PushField(6);
        var yMask = PushField(7);
        var sourceSliceElements = PushField(8);
        var equation = PushField(9);
        var uintsPerElement = PushField(10);

        var x = module.AddInstruction(
            SpirvOp.UDiv,
            uintType,
            invocationX,
            uintsPerElement);
        var xWordBase = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            x,
            uintsPerElement);
        var wordInElement = module.AddInstruction(
            SpirvOp.ISub,
            uintType,
            invocationX,
            xWordBase);

        var xInRange = module.AddInstruction(
            SpirvOp.ULessThan,
            boolType,
            x,
            width);
        var yInRange = module.AddInstruction(
            SpirvOp.ULessThan,
            boolType,
            y,
            height);
        var inRange = module.AddInstruction(
            SpirvOp.LogicalAnd,
            boolType,
            xInRange,
            yInRange);

        var bodyLabel = module.AllocateId();
        var mergeLabel = module.AllocateId();
        module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
        module.AddStatement(
            SpirvOp.BranchConditional,
            inRange,
            bodyLabel,
            mergeLabel);
        module.AddLabel(bodyLabel);

        var blockY = module.AddInstruction(
            SpirvOp.UDiv,
            uintType,
            y,
            blockHeight);
        var blockRow = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            blockY,
            blocksPerRow);
        var blockX = module.AddInstruction(
            SpirvOp.UDiv,
            uintType,
            x,
            blockWidth);
        var blockIndex = module.AddInstruction(
            SpirvOp.IAdd,
            uintType,
            blockRow,
            blockX);

        var isBlockTable = module.AddInstruction(
            SpirvOp.INotEqual,
            boolType,
            equation,
            uintConstants[0]);
        var exactXorLabel = module.AllocateId();
        var blockTableLabel = module.AllocateId();
        var equationMergeLabel = module.AllocateId();
        module.AddStatement(
            SpirvOp.SelectionMerge,
            equationMergeLabel,
            0);
        module.AddStatement(
            SpirvOp.BranchConditional,
            isBlockTable,
            blockTableLabel,
            exactXorLabel);

        module.AddLabel(exactXorLabel);
        var xIndex = module.AddInstruction(
            SpirvOp.BitwiseAnd,
            uintType,
            x,
            xMask);
        var xPointer = module.AddInstruction(
            SpirvOp.AccessChain,
            uintStoragePointer,
            xTerm,
            uintConstants[0],
            xIndex);
        var xOffset = module.AddInstruction(
            SpirvOp.Load,
            uintType,
            xPointer);
        var yIndex = module.AddInstruction(
            SpirvOp.BitwiseAnd,
            uintType,
            y,
            yMask);
        var yPointer = module.AddInstruction(
            SpirvOp.AccessChain,
            uintStoragePointer,
            yTerm,
            uintConstants[0],
            yIndex);
        var yOffset = module.AddInstruction(
            SpirvOp.Load,
            uintType,
            yPointer);
        var exactXorOffset = module.AddInstruction(
            SpirvOp.BitwiseXor,
            uintType,
            xOffset,
            yOffset);
        module.AddStatement(SpirvOp.Branch, equationMergeLabel);

        module.AddLabel(blockTableLabel);
        var blockXBase = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            blockX,
            blockWidth);
        var xInBlock = module.AddInstruction(
            SpirvOp.ISub,
            uintType,
            x,
            blockXBase);
        var blockYBase = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            blockY,
            blockHeight);
        var yInBlock = module.AddInstruction(
            SpirvOp.ISub,
            uintType,
            y,
            blockYBase);
        var rowInBlock = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            yInBlock,
            blockWidth);
        var tableIndex = module.AddInstruction(
            SpirvOp.IAdd,
            uintType,
            rowInBlock,
            xInBlock);
        var tablePointer = module.AddInstruction(
            SpirvOp.AccessChain,
            uintStoragePointer,
            xTerm,
            uintConstants[0],
            tableIndex);
        var blockTableOffset = module.AddInstruction(
            SpirvOp.Load,
            uintType,
            tablePointer);
        module.AddStatement(SpirvOp.Branch, equationMergeLabel);

        module.AddLabel(equationMergeLabel);
        var elementOffset = module.AddInstruction(
            SpirvOp.Phi,
            uintType,
            exactXorOffset,
            exactXorLabel,
            blockTableOffset,
            blockTableLabel);

        var sourceSliceBase = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            layer,
            sourceSliceElements);
        var sourceBlockBase = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            blockIndex,
            blockElements);
        var sourceInSlice = module.AddInstruction(
            SpirvOp.IAdd,
            uintType,
            sourceBlockBase,
            elementOffset);
        var sourceElement = module.AddInstruction(
            SpirvOp.IAdd,
            uintType,
            sourceSliceBase,
            sourceInSlice);
        var sourceWordBase = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            sourceElement,
            uintsPerElement);
        var sourceWord = module.AddInstruction(
            SpirvOp.IAdd,
            uintType,
            sourceWordBase,
            wordInElement);
        var sourcePointer = module.AddInstruction(
            SpirvOp.AccessChain,
            uintStoragePointer,
            tiled,
            uintConstants[0],
            sourceWord);
        var word = module.AddInstruction(
            SpirvOp.Load,
            uintType,
            sourcePointer);

        var sliceElements = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            width,
            height);
        var destinationSliceBase = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            layer,
            sliceElements);
        var destinationRowBase = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            y,
            width);
        var destinationInSlice = module.AddInstruction(
            SpirvOp.IAdd,
            uintType,
            destinationRowBase,
            x);
        var destinationElement = module.AddInstruction(
            SpirvOp.IAdd,
            uintType,
            destinationSliceBase,
            destinationInSlice);
        var destinationWordBase = module.AddInstruction(
            SpirvOp.IMul,
            uintType,
            destinationElement,
            uintsPerElement);
        var destinationWord = module.AddInstruction(
            SpirvOp.IAdd,
            uintType,
            destinationWordBase,
            wordInElement);
        var destinationPointer = module.AddInstruction(
            SpirvOp.AccessChain,
            uintStoragePointer,
            linear,
            uintConstants[0],
            destinationWord);
        module.AddStatement(SpirvOp.Store, destinationPointer, word);

        module.AddStatement(SpirvOp.Branch, mergeLabel);
        module.AddLabel(mergeLabel);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 8, 8, 1);
        module.AddEntryPoint(
            SpirvExecutionModel.GLCompute,
            main,
            "main",
            [
                globalInvocationId,
                tiled,
                xTerm,
                yTerm,
                linear,
                pushConstants,
            ]);
        return module.Build();
    }
}
