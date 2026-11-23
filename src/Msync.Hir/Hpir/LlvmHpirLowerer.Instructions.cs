using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private void Emit(LlvmInstruction instruction, LlvmBasicBlock block)
        {
            if (TryEmitFileOut(instruction)) return;
            var source = instruction.Source;
            switch (instruction)
            {
                case LlvmPhiInstruction:
                    return;
                case LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.IsAssume: true }:
                    // This is an optimizer fact. Operand effects remain separate instructions.
                    omittedAssumptions++;
                    return;
                case LlvmUncondBrInstruction jump:
                    branches.Add(new(code.Count, block, jump.Destination));
                    Add(new(HpirOp.Jump), source);
                    return;
                case LlvmCondBrInstruction branch:
                    int condition = Location(branch.Condition);
                    branches.Add(new(code.Count, block, branch.GetSuccessor(0), branch.GetSuccessor(1)));
                    Add(new(HpirOp.Branch, A: condition), source);
                    return;
                case LlvmUnreachableInstruction unreachable:
                    EmitFailure(unreachable, HpirFailure.Unreachable);
                    return;
                case LlvmReturnInstruction ret:
                    Return(ret);
                    return;
                case LlvmAllocaInstruction allocation when allocation.IsStaticAlloca:
                    RecordAddress(allocation, storage[allocation]);
                    return;
                case LlvmGetElementPtrInstruction gep:
                    if (gep.HasAllConstantIndices) Address(gep);
                    else DynamicAddress(gep);
                    return;
                case LlvmHeapAllocInst allocation:
                    Allocate(allocation);
                    return;
                case LlvmFileInInst input when input.Format == LlvmFileInFormat.Lines:
                    EmitTextFileSource(input, input.SourceOperand);
                    return;
                case LlvmFileInInst input when input.Format == LlvmFileInFormat.Text:
                    EmitFileTextRead(input);
                    return;
                case LlvmLoadInstruction load when TryTextLiteral(load) || TryPathLiteral(load) || TryTypeLiteral(load) || TryConnectionLiteral(load):
                    return;
                case LlvmLoadInstruction load when load.IsSimple:
                    Load(load);
                    return;
                case LlvmLoadInstruction { IsVolatile: false, Ordering: LlvmAtomicOrdering.Acquire } ready
                    when geometry.IsGlobalGuard(ready.PointerOperand):
                    Load(ready);
                    return;
                case LlvmStoreInstruction store when store.IsSimple:
                    Store(store);
                    return;
                case LlvmStoreInstruction { IsVolatile: false, Ordering: LlvmAtomicOrdering.Release } publish
                    when geometry.IsGlobalGuard(publish.PointerOperand):
                    Store(publish);
                    return;
                case LlvmFileOutInst transfer when TryFileTextWrite(transfer) || TryFileByteWrite(transfer) || TryLiveTransfer(transfer):
                    return;
                case LlvmInsertValueInstruction:
                    return;
                case LlvmExtractValueInstruction extract:
                    Extract(extract);
                    return;
                case LlvmFreezeInstruction freeze when !Aggregate(freeze.Type):
                    Alias(freeze, Location(freeze.Operand));
                    ForwardTextTag(freeze, freeze.Operand);
                    return;
                case LlvmBinaryOperator binary:
                    Add(new(Binary(binary), Location(binary), Location(binary.Left), Location(binary.Right)), source);
                    return;
                case LlvmUnaryOperator unary when unary.Type.IsDoubleTy():
                    Add(new(HpirOp.NegF64, Location(unary), Location(unary.Operand)), source);
                    return;
                case LlvmFCmpInstruction cmp when cmp.Left.Type.IsDoubleTy():
                    Compare(cmp);
                    return;
                case LlvmICmpInstruction compare:
                    CompareWord(compare);
                    return;
                case LlvmSelectInstruction select:
                    Select(select);
                    return;
                case LlvmCastInstruction cast when cast.Opcode is LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast
                    && Width(cast.Type) == Width(cast.Operand.Type):
                    Alias(cast, Location(cast.Operand));
                    ForwardTextTag(cast, cast.Operand);
                    return;
                case LlvmCastInstruction cast when TryWordCast(cast):
                    return;
                case LlvmCallInstruction call when TrySnapshotSlide(call) || TryVectorRange(call) || TryVectorAccess(call) || TryVectorWindow(call) || TryVectorQuantifier(call)
                    || TryNumericRange(call) || TryLiveTextRange(call) || TryDirectoryRange(call) || TryVectorFilter(call)
                    || TryFutureCall(call):
                    return;
                case LlvmCallInstruction call when TryUnionCall(call):
                    return;
                case LlvmCallInstruction call when TryBindSessionAlias(call):
                    return;
                case LlvmCallInstruction call when TryTypeCall(call) || TryMetaTypeCall(call):
                    return;
                case LlvmCallInstruction counted when TryFixedPackSize(counted) || TryCountedRange(counted):
                    return;
                case LlvmCallInstruction sorted when TrySortedRange(sorted) || TrySnapshotSort(sorted):
                    return;
                case LlvmCallInstruction concat when TryConcatRange(concat):
                    return;
                case LlvmCallInstruction zip when TryZipRange(zip):
                    return;
                case LlvmCallInstruction transform when TryPathTransform(transform) || TryNumericTransform(transform):
                    return;
                case LlvmCallInstruction pipeline when TryTextPipeline(pipeline):
                    return;
                case LlvmCallInstruction format when TryTextFormat(format):
                    return;
                case LlvmCallInstruction session when TrySessionRangeItem(session) || TrySessionSnapshotRead(session):
                    return;
                case LlvmCallInstruction join when TryJoinTextRange(join) || TryAppendTextChunk(join) || TryOrderTextVector(join):
                    return;
                case LlvmCallInstruction call when call.CalledFunction is { } target && ids.TryGetValue(target, out int id):
                    Call(call, id);
                    return;
                case LlvmCallInstruction call when TryRegexGroupLayoutTest(call):
                    return;
                case LlvmCallInstruction call when call.CalledFunction is { } target && geometry.TryLayoutImport(target, source, out int layout):
                    EmitImportCall(call, layout, [Location(call.GetArgument(0))], safepoint: false);
                    return;
                case LlvmCallInstruction call when TryFileRename(call) || TryReadyRegex(call) || TryPathSelection(call) || TryPathCall(call)
                    || TryUniformProjection(call) || TryScalarImport(call) || TryCallable(call):
                    return;
                default:
                    string reason = instruction is LlvmCallInstruction invoke
                        ? $"calling {invoke.CalledFunction?.Name ?? "an indirect entry"} "
                            + $"({string.Join(", ", invoke.Arguments.Select(argument => CarrierName(argument.Type)))})"
                            + $" -> {CarrierName(invoke.Type)}"
                        : $"lowering {instruction.Opcode}";
                    throw Error(reason, instruction);
            }
        }

        private static string CarrierName(LlvmType type) => type switch
        {
            LlvmStructType { Name: { Length: > 0 } name } => name,
            LlvmStructType { Elements.Count: 0 } => "empty record",
            LlvmIntegerType integer => $"i{integer.BitWidth}",
            _ => type.Id.ToString()
        };

        private void Copy(HpirWidth width, int to, int from, SourceSpan? source)
        {
            if (to != from) Add(new(CopyOp(width), to, from), source);
        }

        private void Select(LlvmSelectInstruction select)
        {
            var leaves = geometry.Leaves(select.Type);
            if (leaves.Length == 0) return;
            int condition = Location(select.Condition);
            int branch = code.Count;
            Add(new(HpirOp.Branch, A: condition, B: branch + 1), select.Source);
            SelectArm(select, select.TrueValue, leaves);
            int end = code.Count;
            Add(new(HpirOp.Jump), select.Source);
            code[branch] = code[branch] with { D = code.Count };
            SelectArm(select, select.FalseValue, leaves);
            code[end] = code[end] with { A = code.Count };
        }

        private void SelectArm(LlvmSelectInstruction select, LlvmValue arm, Geometry.Leaf[] leaves)
        {
            ForwardTextTag(select, arm);
            if (FutureCell(select))
            {
                var moves = new List<Move>(2);
                AddCarrierMoves(moves, select, arm, select.Source);
                foreach (var move in moves) Copy(move.Width, move.To, move.From, move.Source);
                return;
            }
            if (UnionCell(select)) Copy(HpirWidth.U64, UnionCompletion(select), UnionCompletion(arm), select.Source);
            foreach (var leaf in leaves)
                Add(new(CopyOp(Width(leaf.Type)), LeafLocation(select, leaf.Offset), LeafLocation(arm, leaf.Offset)), select.Source);
        }

        private HpirOp Binary(LlvmBinaryOperator operation) => operation.Opcode switch
        {
            LlvmOpcode.FAdd when operation.Type.IsDoubleTy() => HpirOp.AddF64,
            LlvmOpcode.FSub when operation.Type.IsDoubleTy() => HpirOp.SubF64,
            LlvmOpcode.FMul when operation.Type.IsDoubleTy() => HpirOp.MulF64,
            LlvmOpcode.FDiv when operation.Type.IsDoubleTy() => HpirOp.DivF64,
            LlvmOpcode.FRem when operation.Type.IsDoubleTy() => HpirOp.RemF64,
            LlvmOpcode.And when Width(operation.Type) == HpirWidth.U8 => HpirOp.AndU8,
            LlvmOpcode.Or when Width(operation.Type) == HpirWidth.U8 => HpirOp.OrU8,
            LlvmOpcode.Xor when Width(operation.Type) == HpirWidth.U8 => HpirOp.XorU8,
            _ when operation.Type is LlvmIntegerType { BitWidth: 64 } => WordBinary(operation),
            _ => throw Error($"lowering {operation.Opcode} on {operation.Type.Id}", operation)
        };
    }
}
