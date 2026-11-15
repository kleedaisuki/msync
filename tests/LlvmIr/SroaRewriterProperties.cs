using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SroaRewriterProperties
{
    internal static int Run()
    {
        int checks = 0;
        ScalarPartitions();
        IntegerInsertion(false, 0x012345671357cdefUL);
        IntegerInsertion(true, 0x0123135789abcdefUL);
        VolatileAddressSpace();
        return checks;

        void ScalarPartitions()
        {
            LlvmContext context = new();
            LlvmModule module = new("sroa-rewriter-aggregate", context);
            var number = LlvmType.GetDoubleTy(context);
            var aggregate = LlvmStructType.Get(context, [number, number]);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(number), LlvmLinkage.InternalLinkage, "scalar-parts", module);
            var block = new LlvmBasicBlock(context, "entry", function);
            var size = LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 1);
            var storage = new LlvmAllocaInstruction(aggregate, size); block.Append(storage);
            var left = new LlvmGetElementPtrInstruction(aggregate, storage,
                [LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 0), LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 0)]);
            var right = new LlvmGetElementPtrInstruction(aggregate, storage,
                [LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 0), LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1)]);
            block.Append(left); block.Append(right);
            block.Append(new LlvmStoreInstruction(LlvmConstantFP.Get(number, 1), left));
            block.Append(new LlvmStoreInstruction(LlvmConstantFP.Get(number, 2), right));
            block.Append(new LlvmStoreInstruction(LlvmConstantFP.Get(number, 7), left));
            var lhs = new LlvmLoadInstruction(number, left); var rhs = new LlvmLoadInstruction(number, right);
            block.Append(lhs); block.Append(rhs);
            var sum = new LlvmBinaryOperator(LlvmOpcode.FAdd, lhs, rhs); block.Append(sum);
            var result = new LlvmReturnInstruction(context, sum); block.Append(result);
            LlvmSroa.Run(function, new LlvmDominatorTree(function));
            LlvmInstSimplifyPass.Run(function);
            Require(!Instructions(function).Any(instruction => instruction is LlvmAllocaInstruction or LlvmLoadInstruction or LlvmStoreInstruction),
                "Source aggregate partitions must rewrite their actual loads and stores and promote both scalars.");
            Require(result.ReturnValue is LlvmConstantFP constant && constant.Value.BitwiseIsEqual(new LlvmAPFloat(9)),
                "Partitioned scalar memory keeps both original field values and the overwritten field.");
        }

        void IntegerInsertion(bool bigEndian, ulong expected)
        {
            LlvmContext context = new();
            LlvmModule module = new("sroa-rewriter-byte-insertion", context) { DataLayout = new(bigEndian ? "E" : "e") };
            var i64 = LlvmType.GetInt64Ty(context); var i16 = LlvmType.GetInt16Ty(context); var i8 = LlvmType.GetInt8Ty(context);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(i64), LlvmLinkage.InternalLinkage, "integer-insertion", module);
            var block = new LlvmBasicBlock(context, "entry", function);
            var allocation = new LlvmAllocaInstruction(i64, LlvmConstantInt.Get(i64, 1)); block.Append(allocation);
            block.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(i64, 0x0123456789abcdefUL), allocation));
            var piece = new LlvmGetElementPtrInstruction(i8, allocation, [LlvmConstantInt.Get(i64, 2)]); block.Append(piece);
            block.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(i16, 0x1357), piece));
            var loaded = new LlvmLoadInstruction(i64, allocation); block.Append(loaded);
            var result = new LlvmReturnInstruction(context, loaded); block.Append(result);
            LlvmSroa.Run(function, new LlvmDominatorTree(function)); LlvmInstSimplifyPass.Run(function);
            Require(result.ReturnValue is LlvmConstantInt integer && integer.GetZExtValue() == expected,
                "Partial stores must preserve source little/big-endian bit placement through scalar rewriting.");
            Require(!Instructions(function).Any(instruction => instruction is LlvmLoadInstruction or LlvmStoreInstruction),
                "A full initialized integer followed by a partial store must become scalar SSA values.");
        }

        void VolatileAddressSpace()
        {
            LlvmContext context = new(); LlvmModule module = new("sroa-rewriter-volatile", context);
            var number = LlvmType.GetDoubleTy(context); var function = LlvmFunction.Create(LlvmFunctionType.Get(number), LlvmLinkage.InternalLinkage, "volatile", module);
            var block = new LlvmBasicBlock(context, "entry", function);
            var allocation = new LlvmAllocaInstruction(number, LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1), alignment: new(8)); block.Append(allocation);
            var pointer = new LlvmCastInstruction(LlvmOpcode.AddrSpaceCast, allocation, LlvmPointerType.Get(context, 5)); block.Append(pointer);
            block.Append(new LlvmStoreInstruction(LlvmConstantFP.Get(number, 3), pointer, new(8), isVolatile: true));
            var value = new LlvmLoadInstruction(number, pointer, new(8), isVolatile: true); block.Append(value);
            block.Append(new LlvmReturnInstruction(context, value));
            LlvmSroa.Run(function, new LlvmDominatorTree(function));
            var loads = Instructions(function).OfType<LlvmLoadInstruction>().ToArray();
            var stores = Instructions(function).OfType<LlvmStoreInstruction>().ToArray();
            Require(loads.Length == 1 && stores.Length == 1 && loads[0].IsVolatile && stores[0].IsVolatile,
                "SROA must not turn volatile private memory into ordinary SSA values.");
            Require(((LlvmPointerType)loads[0].PointerOperand.Type).AddressSpace == 5 &&
                ((LlvmPointerType)stores[0].PointerOperand.Type).AddressSpace == 5,
                "Volatile access rewriting preserves the original pointer address space.");
        }

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }

    private static IEnumerable<LlvmInstruction> Instructions(LlvmFunction function) =>
        function.BasicBlocks.SelectMany(block => block.Instructions);
}
