// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/Loads.cpp
// decomposeLinearExpression, Loads.h LinearExpression; GEPNoWrapFlags.h intersectForOffsetAdd.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private sealed class ICmpLinearExpression(LlvmValue pointer, uint width)
        {
            internal LlvmValue BasePointer = pointer;
            internal LlvmValue? Index;
            internal LlvmAPInt Scale = LlvmAPInt.GetZero(width), Offset = LlvmAPInt.GetZero(width);
            internal LlvmGepNoWrapFlags Flags = LlvmGepNoWrapFlags.All;
        }

        private ICmpLinearExpression ICmpDecomposeLinearExpression(LlvmValue pointer)
        {
            uint width = DL.GetIndexTypeSizeInBits(pointer.Type);
            var result = new ICmpLinearExpression(pointer, width);
            while (LlvmGepOperator.TryGet(result.BasePointer, out var gep) && !gep.SourceElementType.IsScalableTy())
            {
                Count("icmp.indexed_global.geps_decomposed");
                LlvmValue? variable = null;
                foreach (var index in gep.Indices)
                {
                    if (index is LlvmConstantInt) continue;
                    if (result.Index is not null || variable is not null) return result;
                    variable = index;
                }
                if (variable is not null && !variable.Type.IsIntegerTy(width)) return result;
                result.BasePointer = gep.PointerOperand;
                result.Flags = gep.User == pointer ? gep.NoWrapFlags : result.Flags.IntersectForOffsetAdd(gep.NoWrapFlags);
                var scale = new LlvmAPInt(width, 1);
                if (variable is LlvmBinaryOperator binary && binary.Right is LlvmConstantInt constant)
                {
                    bool scaled = false;
                    if (binary.Opcode == LlvmOpcode.Mul) { scale *= constant.Value; scaled = true; }
                    else if (binary.Opcode == LlvmOpcode.Shl && constant.Value.GetLimitedValue() < width)
                    { scale = scale.Shl((uint)constant.Value.GetZExtValue()); scaled = true; }
                    if (scaled)
                    {
                        if (!binary.HasNoSignedWrap) result.Flags = result.Flags.WithoutNoUnsignedSignedWrap();
                        if (!binary.HasNoUnsignedWrap) result.Flags = result.Flags.WithoutNoUnsignedWrap();
                        variable = binary.Left;
                    }
                }
                LlvmType indexed = gep.SourceElementType;
                for (int i = 0; i < gep.IndexCount; ++i)
                {
                    var index = gep.GetIndex(i);
                    if (i != 0 && indexed is LlvmStructType structure)
                    {
                        var field = (LlvmConstantInt)index;
                        uint member = (uint)field.GetZExtValue();
                        if (!field.IsZero) result.Offset += new LlvmAPInt(width, DL.GetStructLayout(structure).GetElementOffset(member).GetFixedValue(), implicitTrunc: true);
                        indexed = structure.Elements[(int)member];
                        continue;
                    }
                    bool vectorElement = i != 0 && indexed is LlvmVectorType;
                    if (i != 0) indexed = indexed switch
                    {
                        LlvmArrayType array => array.ElementType,
                        LlvmVectorType vector => vector.ElementType,
                        _ => indexed
                    };
                    if (index is LlvmConstantInt { IsZero: true }) continue;
                    var stride = new LlvmAPInt(width, (vectorElement ? DL.GetTypeStoreSize(indexed) : DL.GetTypeAllocSize(indexed)).GetFixedValue(), implicitTrunc: true);
                    if (index is LlvmConstantInt offset) result.Offset += offset.Value.SExtOrTrunc(width) * stride;
                    else { result.Index = variable; result.Scale = stride * scale; }
                }
            }
            return result;
        }

    }
}
