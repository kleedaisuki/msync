// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineLoadStoreAlloca.cpp combineLoadToOperationType/combineStoreToValueType;
// IR/Instructions.cpp CastInst::isNoopCast.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool MemoryIsSwiftError(LlvmValue value) => value is LlvmAllocaInstruction { SwiftError: true } ||
            value is LlvmArgument { Parent: { } function } argument && function.ParamHasAttr((int)argument.ArgNo, LlvmAttributeKind.SwiftError);

        private LlvmInstruction? CombineLoadToOperationType(LlvmLoadInstruction load)
        {
            if (!load.IsUnordered || load.IsElementwise || load.UseEmpty || MemoryIsSwiftError(load.PointerOperand)) return null;
            if (!load.HasOneUse || load.Users.Single() is not LlvmCastInstruction cast) return null;
            var destination = cast.Type;
            if (cast.Opcode == LlvmOpcode.BitCast && destination.IsX86_AMXTy()) return null;
            if (!cast.IsNoopCast(DL) || load.Type.IsPtrOrPtrVectorTy() != destination.IsPtrOrPtrVectorTy() ||
                load.IsAtomic && !IsSupportedAtomicType(destination)) return null;
            var replacement = CombineLoadToNewType(load, destination);
            ReplaceInstUsesWith(cast, replacement);
            EraseInstFromFunction(cast);
            Count("instcombine.memory.load.operation_type");
            return load;
        }

        private bool CombineStoreToValueType(LlvmStoreInstruction store)
        {
            if (!store.IsUnordered || store.IsElementwise || MemoryIsSwiftError(store.PointerOperand)) return false;
            if (store.ValueOperand is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } bitcast)
            {
                var value = bitcast.Operand;
                if (value.Type.IsX86_AMXTy()) return false;
                if (!store.IsAtomic || IsSupportedAtomicType(value.Type))
                {
                    CombineStoreToNewValue(store, value);
                    Count("instcombine.memory.store.value_type");
                    return true;
                }
            }
            // likeBitCastFromVector requires ExtractElementInst, which is not
            // represented by this IR carrier. Aggregate stores still unpack below.
            return false;
        }
    }
}
