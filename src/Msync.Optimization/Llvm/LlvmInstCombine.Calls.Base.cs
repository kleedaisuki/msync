// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// visitCallBase (5204–5490), target disableAllFunctions configuration.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitCallBase(LlvmCallBase call)
        {
            Count("calls.base.visited");
            bool changed = false;
            for (int index = 0; index < call.ArgumentCount; ++index)
            {
                LlvmValue argument = call.GetArgument(index);
                if (!argument.Type.IsPointerTy()) continue;
                bool provenance = (call.GetParamAttributes(index).DereferenceableBytes ?? 0) > 0 && !LlvmFunction.NullPointerIsDefined(F, ((LlvmPointerType)argument.Type).AddressSpace);
                if (call.ParamHasAttr(index, LlvmAttributeKind.NonNull) || provenance)
                {
                    if (SimplifyNonNullOperand(argument, provenance) is not { } replacement) continue;
                    ReplaceOperand(call, index, replacement); changed = true; Count("calls.argument.nonnull_simplified");
                }
                else if (IsKnownNonZero(argument, call))
                { call.AddParamAttr(index, LlvmAttributeKind.NonNull); changed = true; Count("calls.argument.nonnull_added"); }
            }
            LlvmValue callee = call.CalledOperand;
            LlvmFunction? function = callee as LlvmFunction;
            if ((function is null || !ReferenceEquals(function.FunctionType, call.FunctionType)) && TransformConstExprCastCall(call)) return null;
            if (function is not null)
            {
                if (call.IsConvergent && !function.Convergent && function.IntrinsicDescriptor is null)
                { call.Convergent = false; Count("calls.convergent_removed"); return call; }
                if (function.CallingConvention != call.CallingConvention
                    && !(function.CallingConvention == 0 && TLI.IsCallingConvCCompatible(call))
                    && !(call.CallingConvention == 0 && TLI.IsCallingConvCCompatible(function)) && !function.IsDeclaration)
                {
                    CreateNonTerminatorUnreachable(call);
                    if (!call.Type.IsVoidTy()) ReplaceInstUsesWith(call, LlvmPoisonValue.Get(call.Type));
                    Count("calls.incompatible_calling_convention"); return EraseInstFromFunction(call);
                }
            }
            if (callee is LlvmConstantPointerNull && !LlvmFunction.NullPointerIsDefined(F) || callee is LlvmUndefValue)
            {
                if (!call.Type.IsVoidTy()) ReplaceInstUsesWith(call, LlvmPoisonValue.Get(call.Type));
                CreateNonTerminatorUnreachable(call); Count("calls.undefined_callee"); return EraseInstFromFunction(call);
            }
            Debug.Assert(TLI.AllFunctionsDisabled);
            // Source LibCallSimplifier has no available library identities in this target configuration.
            // Trampolines, ptrauth, inline asm and GC statepoints have no represented instruction/constant classes.
            if (!call.UseEmpty && call is not LlvmCallInstruction { TailCallKind: LlvmTailCallKind.MustTail }
                && call.GetReturnedArgOperand() is { } returned && returned.Type.CanLosslesslyBitCastTo(call.Type))
                return ReplaceInstUsesWith(call, Builder.CreateBitOrPointerCast(returned, call.Type));
            if (function is not null && call.GetOperandBundle("kcfi") is { } kcfi)
            {
                var definitions = call.GetOperandBundlesAsDefs().Where(bundle => bundle.Tag != kcfi.Tag).ToArray();
                var replacement = new LlvmCallInstruction(call.FunctionType, call.CalledOperand, call.Arguments.ToArray(), definitions);
                replacement.CopyAttributesFrom(call);
                replacement.Source = call.Source; replacement.LogicalType = call.LogicalType;
                if (call is LlvmCallInstruction original) { replacement.TailCallKind = original.TailCallKind; replacement.FastMathFlags = original.FastMathFlags; }
                Count("calls.kcfi_removed_from_direct"); return replacement;
            }
            return changed ? call : null;
        }
    }
}
