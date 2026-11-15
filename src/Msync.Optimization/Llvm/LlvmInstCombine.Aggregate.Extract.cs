// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp visitExtractValueInst.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitExtractValueInst(LlvmExtractValueInstruction extract)
        {
            LlvmValue aggregate = extract.AggregateOperand;
            if (extract.Indices.Count == 0) return ReplaceInstUsesWith(extract, aggregate);
            if (LlvmInstructionSimplify.SimplifyInstructionWithOperands(extract, [aggregate], Query(extract)) is { } simplified)
                return ReplaceInstUsesWith(extract, simplified);
            if (extract.Indices.Count == 1 && extract.Indices[0] == 0 &&
                aggregate is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind.Name: "llvm.frexp" } frexp &&
                frexp.GetArgument(0) is LlvmSelectInstruction select && FoldFrexpOfSelect(extract, frexp, select) is { } frexpResult)
                return ReplaceInstUsesWith(extract, frexpResult);
            if (aggregate is LlvmInsertValueInstruction insert) return FoldExtractOfInsert(extract, insert);
            if (FoldExtractOfOverflowIntrinsic(extract) is { } overflow) return overflow;
            if (aggregate is LlvmLoadInstruction load)
            {
                if (aggregate.Type is LlvmStructType { } structure && structure.IsScalableTy()) return null;
                if (load.IsSimple && load.HasOneUse)
                {
                    List<LlvmValue> indices = [LlvmConstantInt.Get(LlvmType.GetInt32Ty(extract.Context), 0)];
                    foreach (uint index in extract.Indices) indices.Add(LlvmConstantInt.Get(LlvmType.GetInt32Ty(extract.Context), index));
                    Builder.SetInsertPoint(load);
                    LlvmValue address = Builder.CreateInBoundsGep(load.Type, load.PointerOperand, indices);
                    LlvmLoadInstruction replacement = Builder.CreateLoad(extract.Type, address);
                    replacement.LogicalType = extract.LogicalType;
                    Count("aggregate.extract.load-narrowed");
                    return ReplaceInstUsesWith(extract, replacement);
                }
            }
            if (aggregate is LlvmPhiInstruction phi && FoldOpIntoPhi(extract, phi) is { } folded) return folded;
            if (aggregate is LlvmSelectInstruction aggregateSelect && FoldOpIntoSelect(extract, aggregateSelect, true) is { } selected)
                return selected;
            return null;
        }

        private LlvmInstruction? FoldExtractOfInsert(LlvmExtractValueInstruction extract, LlvmInsertValueInstruction insert)
        {
            int index = 0;
            while (index < extract.Indices.Count && index < insert.Indices.Count)
            {
                if (extract.Indices[index] != insert.Indices[index])
                    return new LlvmExtractValueInstruction(insert.AggregateOperand, extract.Indices) { LogicalType = extract.LogicalType };
                ++index;
            }
            if (index == extract.Indices.Count && index == insert.Indices.Count)
                return ReplaceInstUsesWith(extract, insert.InsertedValueOperand);
            if (index == extract.Indices.Count)
            {
                LlvmValue prefix = Builder.CreateExtractValue(insert.AggregateOperand, extract.Indices);
                return new LlvmInsertValueInstruction(prefix, insert.InsertedValueOperand, insert.Indices.Skip(index).ToArray())
                { LogicalType = extract.LogicalType };
            }
            return new LlvmExtractValueInstruction(insert.InsertedValueOperand, extract.Indices.Skip(index).ToArray())
            { LogicalType = extract.LogicalType };
        }

        private LlvmValue? FoldFrexpOfSelect(LlvmExtractValueInstruction extract, LlvmCallInstruction frexp,
            LlvmSelectInstruction select)
        {
            if (!select.HasOneUse || !frexp.HasOneUse) return null;
            LlvmConstantFP? constant;
            LlvmValue variable;
            bool constantIsTrue;
            if (select.TrueValue is LlvmConstantFP trueConstant)
                (constant, variable, constantIsTrue) = (trueConstant, select.FalseValue, true);
            else if (select.FalseValue is LlvmConstantFP falseConstant)
                (constant, variable, constantIsTrue) = (falseConstant, select.TrueValue, false);
            else return null;
            Builder.SetInsertPoint(extract);
            var replacementCall = Builder.Insert(new LlvmCallInstruction(frexp.FunctionType, frexp.CalledFunction!, [variable]), "frexp");
            replacementCall.CopyIRFlags(frexp);
            LlvmValue mantissa = Builder.CreateExtractValue(replacementCall, 0, "mantissa");
            LlvmAPFloat constantMantissa = constant.Value.Frexp(out _, LlvmRoundingMode.NearestTiesToEven);
            LlvmConstantFP value = LlvmConstantFP.Get(select.TrueValue.Type, constantMantissa);
            Count("aggregate.extract.frexp-select");
            return Builder.CreateSelect(select.Condition, constantIsTrue ? value : mantissa,
                constantIsTrue ? mantissa : value, "select.frexp", select.FastMathFlags);
        }
    }
}
