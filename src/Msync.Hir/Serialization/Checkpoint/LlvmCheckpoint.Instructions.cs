using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmCheckpointArtifact
{
    private sealed partial class Encoder
    {
        private void Instruction(LlvmInstruction instruction)
        {
            writer.Write((byte)instruction.Opcode);
            Int(instruction.OperandCount);
            for (int i = 0; i < instruction.OperandCount; ++i) Id(instruction.GetOperand(i)!);
            switch (instruction)
            {
                case LlvmBinaryOperator binary:
                    Flags(binary.HasNoUnsignedWrap, binary.HasNoSignedWrap, binary.IsExact, binary.IsDisjoint);
                    break;
                case LlvmCastInstruction cast:
                    Flags(cast.HasNoUnsignedWrap, cast.HasNoSignedWrap, cast.IsNonNegative, cast.HasNonNull);
                    break;
                case LlvmICmpInstruction cmp:
                    writer.Write((byte)cmp.Predicate);
                    writer.Write(cmp.SameSign);
                    break;
                case LlvmFCmpInstruction cmp: writer.Write((byte)cmp.Predicate); break;
                case LlvmPhiInstruction phi:
                    for (int i = 0; i < phi.IncomingCount; ++i) Id(phi.GetIncomingBlock(i));
                    break;
                case LlvmSwitchInstruction @switch:
                    for (int i = 0; i < @switch.CaseCount; ++i) Id(@switch.GetCaseValue(i));
                    break;
                case LlvmCallInstruction call: Call(call); break;
                case LlvmExtractValueInstruction extract: Seq(extract.Indices, writer.Write); break;
                case LlvmInsertValueInstruction insert: Seq(insert.Indices, writer.Write); break;
                case LlvmShuffleVectorInstruction shuffle: Seq(shuffle.ShuffleMask, Int); break;
                case LlvmAllocaInstruction alloca:
                    Type(alloca.AllocatedType);
                    writer.Write(alloca.Alignment.Value);
                    Int(alloca.AllocatedLogicalType);
                    Optional(alloca.StorageView, view =>
                    { Type(view.StorageType); Int(view.LogicalType); writer.Write(view.ByteOffset); });
                    Flags(alloca.InAlloca, alloca.SwiftError);
                    break;
                case LlvmLoadInstruction load:
                    Memory(load.Alignment, load.Ordering, load.SyncScopeId, load.IsVolatile, load.IsElementwise);
                    writer.Write(load.HasInvariantLoadMetadata);
                    break;
                case LlvmStoreInstruction store:
                    Memory(store.Alignment, store.Ordering, store.SyncScopeId, store.IsVolatile, store.IsElementwise);
                    break;
                case LlvmGetElementPtrInstruction gep:
                    Type(gep.SourceElementType);
                    writer.Write(gep.NoWrapFlags.Raw);
                    Reference(gep.TargetStorageType, Type);
                    break;
                case LlvmHeapAllocInst heap:
                    writer.Write(heap.Alignment.Value);
                    Int(heap.LogicalLayout);
                    Int(heap.LogicalAllocationSite);
                    break;
                case LlvmFileInInst file: writer.Write((byte)file.Format); break;
                case LlvmFileOutInst file: writer.Write((byte)file.Mode); break;
                case LlvmUnreachableInstruction or LlvmReturnInstruction or LlvmUncondBrInstruction or LlvmCondBrInstruction
                    or LlvmUnaryOperator or LlvmSelectInstruction or LlvmFreezeInstruction or LlvmExtractElementInstruction
                    or LlvmInsertElementInstruction: break;
                default: throw new InvalidOperationException($"Unsupported checkpoint instruction {instruction.GetType().Name}.");
            }
            if (instruction is ILlvmFastMathInstruction math) writer.Write((byte)math.FastMathFlags);
            Optional(instruction.Source, source => { Text(source.Source); Int(source.Start); Int(source.Length); });
            for (int i = 0; i < instruction.OperandCount; ++i) UseInfo(instruction.GetOperandUse(i));
            ValueInfo(instruction);
        }

        private void Memory(LlvmAlign align, LlvmAtomicOrdering ordering, uint scope, bool volatileAccess, bool elementwise)
        {
            writer.Write(align.Value);
            writer.Write((byte)ordering);
            writer.Write(scope);
            Flags(volatileAccess, elementwise);
        }

        private void Call(LlvmCallInstruction call)
        {
            Type(call.FunctionType);
            Int(call.ArgumentCount);
            Seq(call.OperandBundles, bundle => { Text(bundle.Tag); Int(bundle.InputCount); });
            writer.Write((byte)call.TailCallKind);
            CallAttributes(call);
        }
    }

    private sealed partial class Decoder
    {
        private readonly record struct MemoryAccess(LlvmAlign Alignment, LlvmAtomicOrdering Ordering, uint Scope,
            bool IsVolatile, bool IsElementwise);

        private LlvmInstruction Instruction(LlvmType type, LlvmBasicBlock block)
        {
            LlvmOpcode opcode = EnumByte<LlvmOpcode>();
            var operands = Seq(Operand, 4);
            foreach (var operand in operands) CheckLocal(operand, block);
            var instruction = CreateInstruction(opcode, type, operands, block);
            if (instruction.Type != type || instruction.OperandCount != operands.Length)
                throw Invalid($"{opcode} result type or operand count mismatch");
            if (instruction is ILlvmFastMathInstruction math)
            {
                byte flags = reader.ReadByte();
                if (flags > (byte)LlvmFastMathFlags.Fast) throw Invalid("invalid fast math flags");
                math.FastMathFlags = (LlvmFastMathFlags)flags;
            }
            instruction.Source = Optional(() => new SourceSpan(Text(), Int(), Int()));
            if (instruction.Source is { Start: < 0 } or { Length: < 0 }) throw Invalid("invalid source range");
            for (int i = 0; i < operands.Length; ++i)
            {
                if (operands[i] is ForwardValue)
                {
                    if (!operands[i].Type.IsFirstClassType() || operands[i].Type.IsLabelTy())
                        throw Invalid("invalid forward operand type");
                    fixups.Add((instruction, i, forward[operands[i]]));
                }
                UseInfo(instruction.GetOperandUse(i));
            }
            return instruction;
        }

        private LlvmInstruction CreateInstruction(LlvmOpcode opcode, LlvmType type, LlvmValue[] args, LlvmBasicBlock block)
        {
            int count = args.Length;
            switch (opcode)
            {
                case >= LlvmOpcode.Add and <= LlvmOpcode.Xor when count == 2 && args[0].Type == args[1].Type:
                    return Binary(opcode, args);
                case >= LlvmOpcode.Trunc and <= LlvmOpcode.AddrSpaceCast when count == 1:
                    return Cast(opcode, type, args[0]);
                case LlvmOpcode.ICmp when count == 2 && args[0].Type == args[1].Type:
                    return new LlvmICmpInstruction(Predicate(integer: true), args[0], args[1]) { SameSign = Bool() };
                case LlvmOpcode.FCmp when count == 2 && args[0].Type == args[1].Type:
                    return new LlvmFCmpInstruction(Predicate(integer: false), args[0], args[1]);
                case LlvmOpcode.PHI: return Phi(type, args, block);
                case LlvmOpcode.Call when count >= 1: return Call(args);
                case LlvmOpcode.Switch when count >= 2 && args[0].Type.IsIntegerTy(): return Switch(args);
                case LlvmOpcode.Unreachable when count == 0: return new LlvmUnreachableInstruction(context);
                case LlvmOpcode.UncondBr when count == 1: return new LlvmUncondBrInstruction(Destination(args[0]));
                case LlvmOpcode.CondBr when count == 3 && args[0].Type.IsIntegerTy(1):
                    return new LlvmCondBrInstruction(args[0], Destination(args[1]), Destination(args[2]));
                case LlvmOpcode.Ret when count <= 1 && (count == 0 ? block.Parent!.ReturnType.IsVoidTy() : args[0].Type == block.Parent!.ReturnType):
                    return new LlvmReturnInstruction(context, count == 0 ? null : args[0]);
                case LlvmOpcode.FNeg when count == 1: return new LlvmUnaryOperator(args[0]);
                case LlvmOpcode.Freeze when count == 1: return new LlvmFreezeInstruction(args[0]);
                case LlvmOpcode.Select when count == 3 && args[1].Type == args[2].Type:
                    return new LlvmSelectInstruction(args[0], args[1], args[2]);
                case LlvmOpcode.ExtractElement when count == 2 && args[0].Type is LlvmVectorType && args[1].Type.IsIntegerTy():
                    return new LlvmExtractElementInstruction(args[0], args[1]);
                case LlvmOpcode.InsertElement when count == 3 && args[0].Type is LlvmVectorType vector && args[1].Type == vector.ElementType && args[2].Type.IsIntegerTy():
                    return new LlvmInsertElementInstruction(args[0], args[1], args[2]);
                case LlvmOpcode.ShuffleVector when count == 2 && args[0].Type is LlvmVectorType && args[0].Type == args[1].Type:
                    return new LlvmShuffleVectorInstruction(args[0], args[1], Seq(Int, 4));
                case LlvmOpcode.ExtractValue when count == 1:
                    return new LlvmExtractValueInstruction(args[0], AggregateIndices(args[0], type));
                case LlvmOpcode.InsertValue when count == 2:
                    return new LlvmInsertValueInstruction(args[0], args[1], AggregateIndices(args[0], args[1].Type));
                case LlvmOpcode.Alloca when count == 1 && type is LlvmPointerType address && args[0].Type.IsIntegerTy():
                    return Alloca(address, args[0]);
                case LlvmOpcode.Load when count == 1 && args[0].Type.IsPointerTy(): return Load(type, args[0]);
                case LlvmOpcode.Store when count == 2 && args[1].Type.IsPointerTy(): return Store(args[0], args[1]);
                case LlvmOpcode.GetElementPtr when count >= 1 && args[0].Type.IsPtrOrPtrVectorTy(): return Gep(args);
                case LlvmOpcode.HeapAlloc when count == 1 && type is LlvmPointerType pointer && args[0].Type.IsIntegerTy():
                    return new LlvmHeapAllocInst(args[0], Alignment(reader.ReadUInt64()), pointer.AddressSpace)
                    { LogicalLayout = Int(), LogicalAllocationSite = Int() };
                case LlvmOpcode.FileIn when count == 1:
                    return new LlvmFileInInst(type, args[0], EnumByte<LlvmFileInFormat>());
                case LlvmOpcode.FileOut when count == 2:
                    return new LlvmFileOutInst(type, args[0], args[1], EnumByte<LlvmFileOutMode>());
                default: throw Invalid($"unsupported instruction or invalid operands for {opcode}");
            }
        }

        private static LlvmBasicBlock Destination(LlvmValue value) =>
            value as LlvmBasicBlock ?? throw Invalid("branch destination is not a basic block");

        private LlvmBinaryOperator Binary(LlvmOpcode opcode, LlvmValue[] args)
        {
            uint flags = FlagBits(4);
            return new(opcode, args[0], args[1])
            {
                HasNoUnsignedWrap = Has(flags, 0), HasNoSignedWrap = Has(flags, 1),
                IsExact = Has(flags, 2), IsDisjoint = Has(flags, 3)
            };
        }

        private LlvmCastInstruction Cast(LlvmOpcode opcode, LlvmType type, LlvmValue arg)
        {
            uint flags = FlagBits(4);
            return new(opcode, arg, type)
            {
                HasNoUnsignedWrap = Has(flags, 0), HasNoSignedWrap = Has(flags, 1),
                IsNonNegative = Has(flags, 2), HasNonNull = Has(flags, 3)
            };
        }

        private LlvmCmpPredicate Predicate(bool integer)
        {
            var predicate = EnumByte<LlvmCmpPredicate>();
            if (integer != (predicate is >= LlvmCmpPredicate.ICmpEq and <= LlvmCmpPredicate.ICmpSle))
                throw Invalid("predicate does not match comparison");
            return predicate;
        }

        private LlvmPhiInstruction Phi(LlvmType type, LlvmValue[] args, LlvmBasicBlock block)
        {
            var phi = new LlvmPhiInstruction(type, args.Length);
            foreach (var arg in args)
            {
                var predecessor = Id<LlvmBasicBlock>();
                if (arg.Type != type || predecessor.Parent != block.Parent) throw Invalid("invalid phi incoming pair");
                phi.AddIncoming(arg, predecessor);
            }
            return phi;
        }

        private LlvmSwitchInstruction Switch(LlvmValue[] args)
        {
            var result = new LlvmSwitchInstruction(args[0], Destination(args[1]), args.Length - 2);
            for (int i = 2; i < args.Length; ++i)
            {
                var value = Id<LlvmConstantInt>();
                if (value.Type != args[0].Type) throw Invalid("switch constant type differs from its condition");
                result.AddCase(value, Destination(args[i]));
            }
            return result;
        }

        private LlvmCallInstruction Call(LlvmValue[] args)
        {
            var type = Type() as LlvmFunctionType ?? throw Invalid("call has no signature");
            int argumentCount = Int(), cursor = argumentCount;
            if (argumentCount < type.NumParams || argumentCount >= args.Length ||
                !type.IsVarArg && argumentCount != type.NumParams || !args[^1].Type.IsPointerTy())
                throw Invalid("call signature does not match operands");
            for (int i = 0; i < type.NumParams; ++i)
                if (args[i].Type != type.Parameters[i]) throw Invalid("call argument type mismatch");
            var bundles = Seq(() =>
            {
                string tag = Text();
                int size = Int();
                if (size < 0 || size > args.Length - 1 - cursor) throw Invalid("invalid call bundle size");
                int start = cursor;
                cursor += size;
                return new LlvmOperandBundleDef(tag, args[start..cursor]);
            }, 8);
            if (cursor != args.Length - 1) throw Invalid("unclaimed call operands");
            var call = new LlvmCallInstruction(type, args[^1], args[..argumentCount], bundles)
            { TailCallKind = EnumByte<LlvmTailCallKind>() };
            CallAttributes(call);
            return call;
        }

        private uint[] AggregateIndices(LlvmValue aggregate, LlvmType element)
        {
            uint[] indices = Seq(reader.ReadUInt32, 4);
            if (indices.Length == 0 || LlvmExtractValueInstruction.GetIndexedType(aggregate.Type, indices) != element)
                throw Invalid("invalid aggregate indices");
            return indices;
        }

        private static LlvmAlign Alignment(ulong value) => System.Numerics.BitOperations.IsPow2(value)
            ? new(value) : throw Invalid($"invalid alignment {value}");

        private LlvmAllocaInstruction Alloca(LlvmPointerType type, LlvmValue size)
        {
            LlvmType allocated = Type();
            var instruction = new LlvmAllocaInstruction(allocated, size, type.AddressSpace, Alignment(reader.ReadUInt64()))
            {
                AllocatedLogicalType = Int(),
                StorageView = Optional(() => new LlvmStorageView(Type(), Int(), reader.ReadUInt64()))
            };
            uint flags = FlagBits(2);
            instruction.InAlloca = Has(flags, 0);
            instruction.SwiftError = Has(flags, 1);
            return instruction;
        }

        private MemoryAccess Memory()
        {
            var align = Alignment(reader.ReadUInt64());
            var ordering = EnumByte<LlvmAtomicOrdering>();
            uint scope = reader.ReadUInt32(), flags = FlagBits(2);
            return new(align, ordering, scope, Has(flags, 0), Has(flags, 1));
        }

        private LlvmLoadInstruction Load(LlvmType type, LlvmValue pointer)
        {
            var access = Memory();
            return new(type, pointer, access.Alignment, access.IsVolatile, access.Ordering, access.Scope)
            { IsElementwise = access.IsElementwise, HasInvariantLoadMetadata = Bool() };
        }

        private LlvmStoreInstruction Store(LlvmValue value, LlvmValue pointer)
        {
            var access = Memory();
            return new(value, pointer, access.Alignment, access.IsVolatile, access.Ordering, access.Scope)
            { IsElementwise = access.IsElementwise };
        }

        private LlvmGetElementPtrInstruction Gep(LlvmValue[] args)
        {
            LlvmType source = Type();
            var indices = args[1..];
            if (indices.Any(value => !value.Type.IsIntOrIntVectorTy()) ||
                LlvmGetElementPtrInstruction.GetIndexedType(source, indices) is null)
                throw Invalid("invalid address indices");
            byte flags = reader.ReadByte();
            if ((flags & ~7) != 0) throw Invalid("invalid address wrap flags");
            return new(source, args[0], indices, LlvmGepNoWrapFlags.FromRaw(flags)) { TargetStorageType = Reference(Struct) };
        }
    }
}
