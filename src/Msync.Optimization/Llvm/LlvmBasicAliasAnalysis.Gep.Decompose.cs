// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicAliasAnalysis.cpp 562..812.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmBasicAliasAnalysis
{
    internal sealed class VariableGepIndex(CastedValue value, LlvmAPInt scale, LlvmInstruction? context, bool nsw, bool negated)
    {
        internal CastedValue Val = value;
        internal LlvmAPInt Scale = scale;
        internal readonly LlvmInstruction? CtxI = context;
        internal bool IsNSW = nsw;
        internal bool IsNegated = negated;
        internal bool HasNegatedScaleOf(VariableGepIndex other) =>
            IsNegated == other.IsNegated ? Scale == -other.Scale : Scale == other.Scale;
    }
    internal sealed class DecomposedGep(LlvmValue value, uint indexSize)
    {
        internal LlvmValue Base = value;
        internal LlvmAPInt Offset = LlvmAPInt.GetZero(indexSize);
        internal readonly List<VariableGepIndex> VarIndices = [];
        internal LlvmGepNoWrapFlags NWFlags = LlvmGepNoWrapFlags.All;
    }

    private DecomposedGep DecomposeGepExpression(LlvmValue value, LlvmDominatorTree? tree)
    {
        const uint maximumLookup = 10; // ValueTracking.h MaxLookupSearchDepth.
        uint remaining = maximumLookup;
        Count("aa.gep.decompose.queries");
        var context = value as LlvmInstruction;
        uint indexSize = Layout.GetIndexTypeSizeInBits(value.Type);
        var result = new DecomposedGep(value, indexSize);
        do
        {
            Count("aa.gep.decompose.steps");
            if (value is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast } cast)
            {
                var operand = cast.Operand;
                if (operand.Type is not LlvmPointerType || Layout.GetIndexTypeSizeInBits(operand.Type) != indexSize)
                { result.Base = value; return result; }
                value = operand;
                continue;
            }
            if (value is not LlvmGetElementPtrInstruction gep)
            {
                if (value is LlvmPhiInstruction { IncomingCount: 1 } phi)
                { value = phi.GetIncomingValue(0); continue; }
                // GlobalAlias, intrinsic IDs and returned-argument attributes are
                // not represented; no alias-return fact can be inferred by name.
                result.Base = value;
                return result;
            }
            result.NWFlags &= gep.NoWrapFlags;
            LlvmType indexed = gep.SourceElementType;
            for (int index = 0; index < gep.IndexCount; index++)
            {
                Count("aa.gep.decompose.indices");
                var operand = gep.GetIndex(index);
                if (index != 0 && indexed is LlvmStructType structure)
                {
                    uint field = (uint)((LlvmConstantInt)operand).GetZExtValue();
                    if (field != 0)
                        result.Offset += new LlvmAPInt(indexSize, Layout.GetStructLayout(structure).GetElementOffset(field).GetFixedValue(), implicitTrunc: true);
                    indexed = structure.Elements[(int)field];
                    continue;
                }
                bool vector = index != 0 && indexed is LlvmVectorType;
                if (index != 0) indexed = indexed switch
                {
                    LlvmArrayType array => array.ElementType,
                    LlvmVectorType vectorType => vectorType.ElementType,
                    _ => throw new InvalidOperationException("Invalid sequential GEP index.")
                };
                if (operand is LlvmConstantInt { IsZero: true }) continue;
                LlvmTypeSize stride = vector ? Layout.GetTypeStoreSize(indexed) : Layout.GetTypeAllocSize(indexed);
                if (stride.IsScalable) { result.Base = value; return result; }
                var scale = new LlvmAPInt(indexSize, stride.GetFixedValue(), implicitTrunc: true);
                if (operand is LlvmConstantInt constant)
                {
                    result.Offset += scale * constant.Value.SExtOrTrunc(indexSize);
                    continue;
                }
                bool nusw = gep.NoWrapFlags.HasNoUnsignedSignedWrap, nuw = gep.NoWrapFlags.HasNoUnsignedWrap;
                uint width = operand.Type.GetIntegerBitWidth();
                uint extend = indexSize > width ? indexSize - width : 0;
                uint truncate = indexSize < width ? width - indexSize : 0;
                var expression = GetLinearExpression(new CastedValue(operand, 0, extend, truncate, nusw && nuw), 0, tree);
                expression = expression.Mul(scale, nuw, nusw);
                result.Offset += expression.Offset;
                scale = expression.Scale;
                if (!expression.IsNUW) result.NWFlags = result.NWFlags.WithoutNoUnsignedWrap();
                for (int previous = 0; previous < result.VarIndices.Count; previous++)
                {
                    var existing = result.VarIndices[previous];
                    if (!ReferenceEquals(existing.Val.V, expression.Val.V) || !existing.Val.HasSameCastsAs(expression.Val)) continue;
                    scale += existing.Scale;
                    expression.IsNSW = expression.IsNUW = false;
                    result.VarIndices.RemoveAt(previous);
                    break;
                }
                if (!scale.IsZero) result.VarIndices.Add(new(expression.Val, scale, context, expression.IsNSW, false));
            }
            value = gep.PointerOperand;
        } while (--remaining != 0);
        result.Base = value;
        Count("aa.gep.decompose.search_limit");
        return result;
    }

    private void SubtractDecomposedGeps(DecomposedGep destination, DecomposedGep source, LlvmAAQueryInfo query)
    {
        if (destination.Offset.Ult(source.Offset)) destination.NWFlags = destination.NWFlags.WithoutNoUnsignedWrap();
        destination.Offset -= source.Offset;
        foreach (var from in source.VarIndices)
        {
            bool found = false;
            for (int index = 0; index < destination.VarIndices.Count; index++)
            {
                var into = destination.VarIndices[index];
                if (!EqualInPotentialCycles(into.Val.V, from.Val.V, query) || !into.Val.HasSameCastsAs(from.Val)) continue;
                if (into.IsNegated) { into.Scale = -into.Scale; into.IsNegated = false; into.IsNSW = false; }
                if (into.Scale != from.Scale)
                {
                    if (into.Scale.Ult(from.Scale)) destination.NWFlags = destination.NWFlags.WithoutNoUnsignedWrap();
                    into.Scale -= from.Scale;
                    into.IsNSW = false;
                }
                else destination.VarIndices.RemoveAt(index);
                found = true;
                break;
            }
            if (found) continue;
            destination.VarIndices.Add(new(from.Val, from.Scale, from.CtxI, from.IsNSW, true));
            destination.NWFlags = destination.NWFlags.WithoutNoUnsignedWrap();
        }
    }
}
