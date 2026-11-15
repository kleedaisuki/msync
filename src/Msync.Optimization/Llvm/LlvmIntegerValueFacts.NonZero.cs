// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp,
// isKnownNonZero, isKnownNonZeroFromOperator, isNonZero{Add,Sub,Mul,Shift}.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    public static bool IsKnownNonZero(LlvmValue value, uint depth = 0, LlvmInstruction? context = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        bool result = IsKnownNonZeroImpl(value, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        Count(metrics, result ? "value_facts.nonzero.proven" : "value_facts.nonzero.unknown");
        return result;
    }

    private static bool IsKnownNonZeroImpl(LlvmValue value, uint depth, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "value_facts.nonzero.visits");
        if (value is LlvmConstant constant)
        {
            if (constant.IsNullValue) return false;
            if (constant is LlvmConstantInt) return true;
            return constant is LlvmGlobalValue { AddressSpace: 0 } global && global.Linkage != LlvmLinkage.ExternalWeakLinkage;
        }
        if (IsKnownNonZeroFromAssume(value, context, tree, metrics, assumptions, allowEphemerals, conditions, condContext)) return true;
        if (depth++ >= MaxDepth) { Count(metrics, "value_facts.nonzero.depth_limit"); return false; }
        if (value is LlvmInstruction instruction && IsKnownNonZeroFromOperator(instruction, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        if (IsNonZeroFromDominatingCondition(value, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        var stripped = StripNullTest(value);
        return stripped is not null && IsKnownNonZero(stripped, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
    }

    private static bool IsKnownNonZeroFromOperator(LlvmInstruction instruction, uint depth, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        bool NonZero(LlvmValue operand) => IsKnownNonZero(operand, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        LlvmKnownBits Bits(LlvmValue operand) => Compute(operand, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        uint width = instruction.Type.IsPointerTy() ? 64 : instruction.Type.GetIntegerBitWidth();
        switch (instruction)
        {
            case LlvmHeapAllocInst:
                Count(metrics, "value_facts.nonzero.heap_result");
                return true;
            case LlvmAllocaInstruction:
                return ((LlvmPointerType)instruction.Type).AddressSpace == 0;
            case LlvmGetElementPtrInstruction gep:
                return IsGepKnownNonNull(gep, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
            case LlvmCastInstruction cast:
                Count(metrics, "value_facts.nonzero.cast");
                switch (cast.Opcode)
                {
                    case LlvmOpcode.BitCast:
                        if ((cast.Operand.Type.IsIntegerTy() || cast.Operand.Type.IsPointerTy()) && width % ValueWidth(cast.Operand) == 0)
                            return NonZero(cast.Operand);
                        break;
                    case LlvmOpcode.IntToPtr:
                        if (ValueWidth(cast.Operand) <= width) return NonZero(cast.Operand);
                        break;
                    case LlvmOpcode.PtrToAddr:
                        return NonZero(cast.Operand);
                    case LlvmOpcode.PtrToInt:
                        if (width >= 64) return NonZero(cast.Operand);
                        break;
                    case LlvmOpcode.Trunc:
                        if (cast.HasNoSignedWrap || cast.HasNoUnsignedWrap) return NonZero(cast.Operand);
                        break;
                    case LlvmOpcode.SExt:
                    case LlvmOpcode.ZExt:
                        return NonZero(cast.Operand);
                }
                break;
            case LlvmBinaryOperator binary:
                Count(metrics, "value_facts.nonzero.binary");
                switch (binary.Opcode)
                {
                    case LlvmOpcode.Xor:
                    case LlvmOpcode.Sub:
                        return NonZeroSub(binary.Left, binary.Right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                    case LlvmOpcode.Or:
                        return MatchOpWithOpEqZero(binary.Left, binary.Right) ||
                            IsKnownNonEqual(binary.Left, binary.Right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext) || NonZero(binary.Right) || NonZero(binary.Left);
                    case LlvmOpcode.Add:
                        return NonZeroAdd(binary, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                    case LlvmOpcode.Mul:
                        if ((useInstructionInfo && binary.HasNoSignedWrap) || (useInstructionInfo && binary.HasNoUnsignedWrap)) return NonZero(binary.Left) && NonZero(binary.Right);
                        var x = Bits(binary.Left);
                        if (!(x.One & new LlvmAPInt(width, 1)).IsZero) return NonZero(binary.Right);
                        var y = Bits(binary.Right);
                        if (!(y.One & new LlvmAPInt(width, 1)).IsZero) return x.IsNonZero || NonZero(binary.Left);
                        return x.CountMaxTrailingZeros() + y.CountMaxTrailingZeros() < width;
                    case LlvmOpcode.Shl:
                        if ((useInstructionInfo && binary.HasNoUnsignedWrap) || (useInstructionInfo && binary.HasNoSignedWrap)) return NonZero(binary.Left);
                        var left = Bits(binary.Left);
                        return !(left.One & new LlvmAPInt(width, 1)).IsZero || NonZeroShift(binary, left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                    case LlvmOpcode.AShr:
                    case LlvmOpcode.LShr:
                        if (binary.IsExact) return NonZero(binary.Left);
                        var known = Bits(binary.Left);
                        if (known.IsNegative) return true;
                        if (depth + 1 < MaxDepth && binary.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasNoUnsignedWrap: true } add &&
                            binary.Right is LlvmConstantInt count && count.Value.Ult(new LlvmAPInt(width, width)))
                        {
                            uint amount = (uint)count.Value.GetLimitedValue();
                            if (!Compute(add.Left, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext).One.LShr(amount).IsZero ||
                                !Compute(add.Right, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext).One.LShr(amount).IsZero) return true;
                        }
                        return NonZeroShift(binary, known, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                    case LlvmOpcode.UDiv:
                    case LlvmOpcode.SDiv:
                        if (binary.IsExact) return NonZero(binary.Left);
                        var dividend = Bits(binary.Left);
                        if (dividend.IsUnknown) return false;
                        var divisor = Bits(binary.Right);
                        if (binary.Opcode == LlvmOpcode.SDiv) { dividend = dividend.Abs(); divisor = divisor.Abs(); }
                        return Compare(LlvmCmpPredicate.ICmpUge, dividend, divisor) == true;
                }
                break;
            case LlvmSelectInstruction select:
                Count(metrics, "value_facts.nonzero.select");
                if ((NonZero(select.TrueValue) || ConditionExcludesZero(select.Condition, select.TrueValue, true)) &&
                    (NonZero(select.FalseValue) || ConditionExcludesZero(select.Condition, select.FalseValue, false))) return true;
                break;
            case LlvmPhiInstruction phi:
                Count(metrics, "value_facts.nonzero.phi");
                if (useInstructionInfo && IsNonZeroRecurrence(phi)) return true;
                uint incomingDepth = Math.Max(depth, MaxDepth - 1);
                for (int n = 0; n < phi.IncomingCount; n++)
                {
                    var incoming = phi.GetIncomingValue(n);
                    if (ReferenceEquals(incoming, phi)) continue;
                    var terminator = phi.GetIncomingBlock(n).GetTerminator();
                    if (terminator is LlvmCondBrInstruction branch &&
                        (ReferenceEquals(branch.GetSuccessor(0), phi.Parent) != ReferenceEquals(branch.GetSuccessor(1), phi.Parent)) &&
                        ConditionExcludesZero(branch.Condition, incoming, ReferenceEquals(branch.GetSuccessor(0), phi.Parent))) continue;
                    if (!IsKnownNonZero(incoming, incomingDepth, terminator, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null)) return false;
                }
                return true;
            case LlvmFreezeInstruction freeze:
                return NonZero(freeze.Operand) && LlvmValueTracking.IsGuaranteedNotToBePoison(freeze.Operand, tree, context, depth, metrics, assumptions, allowEphemerals, conditions);
            case LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } intrinsic } call when IsMinMaxIntrinsicKind(intrinsic.Kind):
                return IsKnownNonZeroMinMax(call, intrinsic.Kind, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
            case LlvmLoadInstruction:
            case LlvmCallInstruction:
                // These source cases use metadata, intrinsic IDs and attributes absent from this IR.
                Count(metrics, "value_facts.nonzero.no_source_facts");
                return false;
        }
        return Compute(instruction, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext).IsNonZero;
    }

    private static bool NonZeroSub(LlvmValue left, LlvmValue right, uint depth, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null) => MatchOpWithOpEqZero(left, right) ||
        (left is LlvmConstant { IsNullValue: true } && IsKnownNonZero(right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) ||
        IsKnownNonEqual(left, right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);

    private static bool NonZeroAdd(LlvmBinaryOperator binary, uint depth, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        var x = binary.Left; var y = binary.Right;
        bool NonZero(LlvmValue operand) => IsKnownNonZero(operand, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        if (IsNonZeroAddressPlusOffset(x, y) || IsNonZeroAddressPlusOffset(y, x)) return true;
        if (MatchOpWithOpEqZero(x, y)) return true;
        if ((useInstructionInfo && binary.HasNoUnsignedWrap)) return NonZero(y) || NonZero(x);
        var a = Compute(x, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext); var b = Compute(y, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        if (a.IsNonNegative && b.IsNonNegative && (NonZero(y) || NonZero(x))) return true;
        if (a.IsNegative && b.IsNegative)
        {
            var mask = LlvmAPInt.GetSignedMaxValue(a.BitWidth);
            if (!(a.One & mask).IsZero || !(b.One & mask).IsZero) return true;
        }
        if (a.IsNonNegative && IsKnownToBeAPowerOfTwo(y, false, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        if (b.IsNonNegative && IsKnownToBeAPowerOfTwo(x, false, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        return LlvmKnownBits.AddSub(true, (useInstructionInfo && binary.HasNoSignedWrap), (useInstructionInfo && binary.HasNoUnsignedWrap), a, b).IsNonZero;
    }

    private static bool NonZeroShift(LlvmBinaryOperator binary, LlvmKnownBits value, uint depth, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (value.IsUnknown) return false;
        var maxShift = Compute(binary.Right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext).GetMaxValue();
        if (maxShift.Uge(new LlvmAPInt(maxShift.BitWidth, value.BitWidth))) return false;
        uint amount = (uint)maxShift.GetLimitedValue();
        LlvmAPInt Shift(LlvmAPInt bits) => binary.Opcode switch
        { LlvmOpcode.Shl => bits.Shl(amount), LlvmOpcode.LShr => bits.LShr(amount), _ => bits.AShr(amount) };
        LlvmAPInt Inverse(LlvmAPInt bits) => binary.Opcode == LlvmOpcode.Shl ? bits.LShr(value.BitWidth - amount) : bits.Shl(value.BitWidth - amount);
        if (!Shift(value.One).IsZero) return true;
        return Inverse(value.Zero) == Inverse(LlvmAPInt.GetAllOnes(value.BitWidth)) &&
            IsKnownNonZero(binary.Left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
    }
    private static bool MatchOpWithOpEqZero(LlvmValue x, LlvmValue y) => MatchEqZeroExtension(x, y) || MatchEqZeroExtension(y, x);
    private static bool MatchEqZeroExtension(LlvmValue x, LlvmValue y) => x is LlvmCastInstruction
        { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt, Operand: LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpEq, Right: LlvmConstant { IsNullValue: true } } cmp } && ReferenceEquals(cmp.Left, y);
    private static uint ValueWidth(LlvmValue value) => value.Type.IsPointerTy() ? 64 : value.Type.GetIntegerBitWidth();

    private static bool IsNonZeroRecurrence(LlvmPhiInstruction phi)
    {
        if (!MatchSimpleRecurrence(phi, out var binary, out var start, out var step) || start is not LlvmConstantInt c || c.IsZero) return false;
        return binary.Opcode switch
        {
            LlvmOpcode.Add => binary.HasNoUnsignedWrap || (binary.HasNoSignedWrap && step is LlvmConstantInt s && c.IsNegative == s.IsNegative),
            LlvmOpcode.Mul => (binary.HasNoUnsignedWrap || binary.HasNoSignedWrap) && step is LlvmConstantInt { IsZero: false },
            LlvmOpcode.Shl => binary.HasNoUnsignedWrap || binary.HasNoSignedWrap,
            LlvmOpcode.AShr or LlvmOpcode.LShr => binary.IsExact,
            LlvmOpcode.Or => true,
            _ => false
        };
    }
}
