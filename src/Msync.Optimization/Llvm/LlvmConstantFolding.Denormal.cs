// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantFolding.cpp
// flushDenormalConstant/getInstrDenormalMode/flushDenormalConstantFP/FlushFPConstant.
using System.Diagnostics;
using Msync.Core.Llvm;
using Mode = Msync.Core.Llvm.LlvmDenormalMode;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? FlushFPConstant(LlvmConstant operand, LlvmFunction? contextFunction, bool isOutput,
        Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.fp.flush_queries");
        if (operand is LlvmConstantFP floating)
        {
            if (!floating.Value.IsDenormal) return floating;
            LlvmDenormalModeInfo mode = contextFunction?.GetDenormalMode(floating.Value.Semantics) ?? LlvmDenormalModeInfo.Dynamic;
            Mode selected = isOutput ? mode.Output : mode.Input;
            switch (selected)
            {
                case Mode.Dynamic: Count(metrics, "constant_fold.fp.flush_dynamic"); return null;
                case Mode.IEEE: Count(metrics, "constant_fold.fp.flush_ieee_preserved"); return floating;
                case Mode.PreserveSign:
                    Count(metrics, "constant_fold.fp.flush_preserve_sign");
                    return LlvmConstantFP.Get(floating.Type, LlvmAPFloat.GetZero(floating.Value.Semantics, floating.IsNegative));
                case Mode.PositiveZero:
                    Count(metrics, "constant_fold.fp.flush_positive_zero");
                    return LlvmConstantFP.Get(floating.Type, LlvmAPFloat.GetZero(floating.Value.Semantics));
                default: throw new UnreachableException("unknown denormal mode");
            }
        }
        if (operand is LlvmConstantAggregateZero or LlvmUndefValue) return operand;
        if (operand.Type is LlvmVectorType vector && operand.GetSplatValue() is LlvmConstantFP splat)
        {
            LlvmConstant? folded = FlushFPConstant(splat, contextFunction, isOutput, metrics);
            return folded is null ? null : LlvmConstantVector.GetSplat(vector.ElementCount, folded);
        }
        if (operand is LlvmConstantExpr) return operand;
        if (operand is not (LlvmConstantVector or LlvmConstantDataVector)) return null;
        uint count = ((LlvmFixedVectorType)operand.Type).NumElements;
        List<LlvmConstant> elements = new((int)count);
        for (uint index = 0; index < count; ++index)
        {
            LlvmConstant element = operand.GetAggregateElement(index)!;
            if (element is LlvmUndefValue) { elements.Add(element); continue; }
            if (element is not LlvmConstantFP) return null;
            LlvmConstant? folded = FlushFPConstant(element, contextFunction, isOutput, metrics);
            if (folded is null) return null;
            elements.Add(folded);
            Count(metrics, "constant_fold.fp.flush_vector_elements");
        }
        return LlvmConstantVector.Get(elements);
    }
}
