// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// transformConstExprCastCall (5492–5743), represented ordinary CallInst path.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool TransformConstExprCastCall(LlvmCallBase call)
        {
            if (LlvmValueTracking.StripPointerCasts(call.CalledOperand, Metrics) is not LlvmFunction callee) return false;
            if (callee.IsDeclaration || callee.Thunk || callee.Naked || call is LlvmCallInstruction { TailCallKind: LlvmTailCallKind.MustTail }) return false;
            LlvmFunctionType signature = callee.FunctionType;
            LlvmType oldReturnType = call.Type, newReturnType = signature.ReturnType;
            LlvmParameterAttributes returnAttributes = call.ReturnAttributes;
            if (!ReferenceEquals(oldReturnType, newReturnType))
            {
                if (newReturnType.IsStructTy()) return false;
                if (!LlvmCastInstruction.IsBitOrNoopPointerCastable(newReturnType, oldReturnType, DL) && !call.UseEmpty) return false;
                if (!call.UseEmpty && returnAttributes.Overlaps(LlvmAttributeFunctions.TypeIncompatible(newReturnType, returnAttributes))) return false;
            }
            int common = Math.Min(signature.NumParams, call.ArgumentCount);
            for (int index = 0; index < signature.NumParams; ++index)
                if (callee.GetParamAttributes(index).Has(LlvmAttributeKind.InAlloca) || callee.GetParamAttributes(index).Has(LlvmAttributeKind.Preallocated)) return false;
            for (int index = 0; index < common; ++index)
            {
                LlvmType parameter = signature.Parameters[index];
                if (!LlvmCastInstruction.IsBitOrNoopPointerCastable(call.GetArgument(index).Type, parameter, DL)) return false;
                LlvmParameterAttributes attributes = call.GetParamAttributes(index);
                if (attributes.Overlaps(LlvmAttributeFunctions.TypeIncompatible(parameter, attributes, LlvmAttributeSafetyKind.UnsafeToDrop))) return false;
                if (attributes.Has(LlvmAttributeKind.InAlloca) || attributes.Has(LlvmAttributeKind.Preallocated) || attributes.Has(LlvmAttributeKind.SwiftError)) return false;
                if (attributes.Has(LlvmAttributeKind.ByVal) != callee.GetParamAttributes(index).Has(LlvmAttributeKind.ByVal)) return false;
            }
            if (signature.NumParams < call.ArgumentCount && signature.IsVarArg)
                for (int index = signature.NumParams; index < call.ArgumentCount; ++index)
                    if (call.GetParamAttributes(index).Has(LlvmAttributeKind.StructRet)) return false;
            var arguments = new List<LlvmValue>();
            var attributesForArguments = new List<LlvmParameterAttributes>();
            var logicalTypes = new List<int>();
            for (int index = 0; index < common; ++index)
            {
                LlvmValue argument = call.GetArgument(index);
                LlvmType parameter = signature.Parameters[index];
                if (!ReferenceEquals(argument.Type, parameter)) argument = Builder.CreateBitOrPointerCast(argument, parameter);
                arguments.Add(argument); logicalTypes.Add(call.GetOperandUse(index).LogicalType);
                LlvmParameterAttributes attributes = call.GetParamAttributes(index);
                attributesForArguments.Add(attributes.RemoveMask(LlvmAttributeFunctions.TypeIncompatible(parameter, attributes, LlvmAttributeSafetyKind.SafeToDrop)));
            }
            for (int index = common; index < signature.NumParams; ++index)
            {
                arguments.Add(LlvmConstant.GetNullValue(signature.Parameters[index])); attributesForArguments.Add(default); logicalTypes.Add(-1);
            }
            if (signature.IsVarArg)
            {
                for (int index = signature.NumParams; index < call.ArgumentCount; ++index)
                {
                    LlvmValue argument = call.GetArgument(index);
                    if (argument.Type is LlvmIntegerType { BitWidth: < 32 }) argument = Builder.CreateZExt(argument, LlvmType.GetIntNTy(call.Context, 32));
                    arguments.Add(argument); attributesForArguments.Add(call.GetParamAttributes(index)); logicalTypes.Add(call.GetOperandUse(index).LogicalType);
                }
            }
            if (newReturnType.IsVoidTy()) call.SetName("");
            var replacement = new LlvmCallInstruction(signature, callee, arguments, call.GetOperandBundlesAsDefs())
                { LogicalType = call.LogicalType, Source = call.Source };
            CallsCopyFunctionAttributes(call, replacement);
            replacement.ReturnAttributes = returnAttributes.RemoveMask(LlvmAttributeFunctions.TypeIncompatible(newReturnType, returnAttributes));
            for (int index = 0; index < attributesForArguments.Count; ++index)
            {
                replacement.SetParamAttributes(index, attributesForArguments[index]); replacement.GetOperandUse(index).LogicalType = logicalTypes[index];
            }
            if (call is LlvmCallInstruction original) { replacement.TailCallKind = original.TailCallKind; replacement.FastMathFlags = original.FastMathFlags; }
            InsertNewInstBefore(replacement, call); replacement.TakeName(call);
            LlvmValue result = replacement;
            if (!ReferenceEquals(oldReturnType, replacement.Type) && !call.UseEmpty)
            {
                result = Builder.CreateBitOrPointerCast(replacement, oldReturnType);
                if (result is LlvmInstruction cast) { cast.Source = call.Source; cast.LogicalType = call.LogicalType; }
                Worklist.PushUsersToWorkList(call);
            }
            if (!call.UseEmpty) ReplaceInstUsesWith(call, result);
            else if (ReferenceEquals(oldReturnType, result.Type)) call.ReplaceAllUsesWith(result);
            EraseInstFromFunction(call);
            Count("calls.cast_callee_transformed"); return true;
        }
    }
}
