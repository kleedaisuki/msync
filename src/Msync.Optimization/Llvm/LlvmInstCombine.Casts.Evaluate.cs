// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp 38–198.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue EvaluateInDifferentType(LlvmValue value, LlvmType type, bool signed) =>
            EvaluateInDifferentTypeImpl(value, type, signed, new());
        private LlvmValue EvaluateInDifferentTypeImpl(LlvmValue value, LlvmType type, bool signed,
            Dictionary<LlvmValue, LlvmValue> processed)
        {
            Count("casts.evaluate.nodes");
            if (processed.TryGetValue(value, out var cached)) { Count("casts.evaluate.cache_hits"); return cached; }
            if (value is LlvmConstant constant)
            {
                if (value.Type == type) return constant;
                var cast = value.Type.GetScalarSizeInBits() > type.GetScalarSizeInBits() ? LlvmOpcode.Trunc :
                    signed ? LlvmOpcode.SExt : LlvmOpcode.ZExt;
                return LlvmConstantFolding.ConstantFoldCastOperand(cast, constant, type, DL, Metrics)!;
            }
            var instruction = (LlvmInstruction)value;
            LlvmInstruction result;
            switch (instruction)
            {
                case LlvmBinaryOperator binary when binary.Opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or
                    LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor or LlvmOpcode.AShr or LlvmOpcode.LShr or
                    LlvmOpcode.Shl or LlvmOpcode.UDiv or LlvmOpcode.URem:
                    var left = EvaluateInDifferentTypeImpl(binary.Left, type, signed, processed);
                    var right = EvaluateInDifferentTypeImpl(binary.Right, type, signed, processed);
                    result = new LlvmBinaryOperator(binary.Opcode, left, right)
                    { IsExact = binary.Opcode is LlvmOpcode.LShr or LlvmOpcode.AShr && binary.IsExact };
                    break;
                case LlvmCastInstruction cast when cast.Opcode is LlvmOpcode.Trunc or LlvmOpcode.ZExt or LlvmOpcode.SExt:
                    if (cast.Operand.Type == type) return cast.Operand;
                    var opcode = cast.Operand.Type.GetScalarSizeInBits() > type.GetScalarSizeInBits() ? LlvmOpcode.Trunc :
                        cast.Opcode == LlvmOpcode.SExt ? LlvmOpcode.SExt : LlvmOpcode.ZExt;
                    var newCast = new LlvmCastInstruction(opcode, cast.Operand, type);
                    if (cast.Opcode == LlvmOpcode.Trunc)
                    {
                        if (opcode == LlvmOpcode.Trunc && cast.Type.GetScalarSizeInBits() <= type.GetScalarSizeInBits())
                        { newCast.HasNoSignedWrap = cast.HasNoSignedWrap; newCast.HasNoUnsignedWrap = cast.HasNoUnsignedWrap; }
                        else if (opcode == LlvmOpcode.ZExt && cast.HasNoUnsignedWrap) newCast.IsNonNegative = true;
                    }
                    result = newCast;
                    break;
                case LlvmSelectInstruction select:
                    result = new LlvmSelectInstruction(select.Condition,
                        EvaluateInDifferentTypeImpl(select.TrueValue, type, signed, processed),
                        EvaluateInDifferentTypeImpl(select.FalseValue, type, signed, processed));
                    break;
                case LlvmPhiInstruction phi:
                    var newPhi = new LlvmPhiInstruction(type, phi.IncomingCount);
                    for (int index = 0; index < phi.IncomingCount; index++)
                        newPhi.AddIncoming(EvaluateInDifferentTypeImpl(phi.GetIncomingValue(index), type, signed, processed), phi.GetIncomingBlock(index));
                    result = newPhi;
                    break;
                case LlvmCastInstruction cast when cast.Opcode is LlvmOpcode.FPToUI or LlvmOpcode.FPToSI:
                    result = new LlvmCastInstruction(cast.Opcode, cast.Operand, type);
                    break;
                case LlvmCallInstruction call when call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.VScale:
                    var scaleDeclaration = LlvmIntrinsic.GetDeclaration(instruction.Parent!.Module!, LlvmIntrinsicKind.VScale, (LlvmIntegerType)type, Metrics);
                    result = new LlvmCallInstruction(scaleDeclaration.FunctionType, scaleDeclaration, []);
                    break;
                case LlvmCallInstruction call when call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Abs:
                    var argument = EvaluateInDifferentTypeImpl(call.GetArgument(0), type, signed, processed);
                    var absDeclaration = LlvmIntrinsic.GetDeclaration(instruction.Parent!.Module!, LlvmIntrinsicKind.Abs, (LlvmIntegerType)type, Metrics);
                    result = new LlvmCallInstruction(absDeclaration.FunctionType, absDeclaration, [argument, LlvmConstantInt.GetBool(type.Context, false)]);
                    break;
                case LlvmCallInstruction call when call.CalledFunction?.IntrinsicDescriptor is { } descriptor && LlvmIntrinsic.IsMinMax(descriptor.Kind):
                    var arg0 = EvaluateInDifferentTypeImpl(call.GetArgument(0), type, signed, processed);
                    var arg1 = EvaluateInDifferentTypeImpl(call.GetArgument(1), type, signed, processed);
                    var declaration = LlvmIntrinsic.GetDeclaration(instruction.Parent!.Module!, descriptor.Kind, (LlvmIntegerType)type, Metrics);
                    result = new LlvmCallInstruction(declaration.FunctionType, declaration, [arg0, arg1]);
                    break;
                default: throw new UnreachableException("Type evaluation requires a supported expression selected by its source predicate.");
            }
            result.TakeName(instruction);
            InsertNewInstWith(result, instruction);
            if (!value.HasOneUse) processed[value] = result;
            Count("casts.evaluate.instructions");
            return result;
        }
    }
}
