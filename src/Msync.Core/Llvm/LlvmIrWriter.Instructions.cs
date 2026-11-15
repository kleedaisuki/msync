namespace Msync.Core.Llvm;

public static partial class LlvmIrWriter
{
    private sealed partial class Writer
    {
        private void WriteInstruction(LlvmInstruction instruction)
        {
            string result = instruction.Type.IsVoidTy() ? "" : Reference(instruction) + " = ";
            output.WriteLine("  " + result + Instruction(instruction));
        }

        private string Instruction(LlvmInstruction instruction) => instruction switch
        {
            LlvmHeapAllocInst allocation => $"heapalloc {Typed(allocation.ByteSize)}, align {allocation.Alignment.Value}"
                + (allocation.AddressSpace == 0 ? "" : $", addrspace({allocation.AddressSpace})"),
            LlvmFileInInst input => $"file.in {input.Format.ToString().ToLowerInvariant()} {Type(input.Type)} from {Typed(input.SourceOperand)}",
            LlvmFileOutInst destination => $"file.out {FileOutMode(destination.Mode)} {Type(destination.Type)} {Typed(destination.Value)}, {Typed(destination.Destination)}",
            LlvmBinaryOperator binary => Binary(binary),
            LlvmUnaryOperator unary => $"fneg {FastMath(unary.FastMathFlags)}{Typed(unary.Operand)}",
            LlvmCastInstruction cast => $"{Mnemonic(cast.Opcode)} {FastMath(cast.FastMathFlags)}{CastFlags(cast)}{Typed(cast.Operand)} to {Type(cast.Type)}",
            LlvmCmpInstruction comparison => $"{Mnemonic(comparison.Opcode)} {ComparisonFlags(comparison)}{Predicate(comparison.Predicate)} {Typed(comparison.Left)}, {Reference(comparison.Right)}",
            LlvmSelectInstruction select => $"select {FastMath(select.FastMathFlags)}{Typed(select.Condition)}, {Typed(select.TrueValue)}, {Typed(select.FalseValue)}",
            LlvmFreezeInstruction freeze => "freeze " + Typed(freeze.Operand),
            LlvmExtractElementInstruction extract => "extractelement " + Typed(extract.VectorOperand) + ", " + Typed(extract.IndexOperand),
            LlvmInsertElementInstruction insert => "insertelement " + Typed(insert.VectorOperand) + ", " + Typed(insert.InsertedElementOperand) + ", " + Typed(insert.IndexOperand),
            LlvmShuffleVectorInstruction shuffle => "shufflevector " + Typed(shuffle.Left) + ", " + Typed(shuffle.Right) + ", " + Typed(shuffle.ShuffleMaskForBitcode),
            LlvmInsertValueInstruction insert => "insertvalue " + Typed(insert.AggregateOperand) + ", " + Typed(insert.InsertedValueOperand)
                + string.Concat(insert.Indices.Select(index => ", " + index.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            LlvmExtractValueInstruction extract => "extractvalue " + Typed(extract.AggregateOperand)
                + string.Concat(extract.Indices.Select(index => ", " + index.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            LlvmAllocaInstruction alloca => $"alloca {(alloca.InAlloca ? "inalloca " : "")}{(alloca.SwiftError ? "swifterror " : "")}{Type(alloca.AllocatedType)}, {Typed(alloca.ArraySize)}, align {alloca.Alignment.Value}"
                + (alloca.AddressSpace == 0 ? "" : $", addrspace({alloca.AddressSpace})"),
            LlvmLoadInstruction load => Load(load),
            LlvmStoreInstruction store => Store(store),
            LlvmGetElementPtrInstruction gep => Gep(gep),
            LlvmReturnInstruction ret => ret.ReturnValue is { } value ? "ret " + Typed(value) : "ret void",
            LlvmUncondBrInstruction branch => "br label " + Reference(branch.Destination),
            LlvmCondBrInstruction branch => $"br {Typed(branch.Condition)}, label {Reference(branch.GetSuccessor(0))}, label {Reference(branch.GetSuccessor(1))}",
            LlvmPhiInstruction phi => Phi(phi),
            LlvmSwitchInstruction selection => Switch(selection),
            LlvmUnreachableInstruction => "unreachable",
            LlvmCallInstruction call => Call(call),
            _ => throw new NotSupportedException($"Text rendering is not implemented for LLVM instruction {instruction.Opcode}.")
        };

        private string Binary(LlvmBinaryOperator binary) => $"{Mnemonic(binary.Opcode)} {FastMath(binary.FastMathFlags)}"
            + (binary.HasNoUnsignedWrap ? "nuw " : "") + (binary.HasNoSignedWrap ? "nsw " : "")
            + (binary.IsExact ? "exact " : "") + Typed(binary.Left) + ", " + Reference(binary.Right);
        private static string CastFlags(LlvmCastInstruction cast) => (cast.HasNoUnsignedWrap ? "nuw " : "")
            + (cast.HasNoSignedWrap ? "nsw " : "") + (cast.IsNonNegative ? "nneg " : "");
        private static string ComparisonFlags(LlvmCmpInstruction comparison) => comparison switch
        {
            LlvmICmpInstruction integer => integer.SameSign ? "samesign " : "",
            LlvmFCmpInstruction floating => FastMath(floating.FastMathFlags),
            _ => ""
        };
        private string Load(LlvmLoadInstruction load) => "load " + (load.IsAtomic ? "atomic " : "")
            + (load.IsVolatile ? "volatile " : "") + Type(load.Type) + ", " + Typed(load.PointerOperand)
            + Atomic(load.Ordering, load.SyncScopeId) + $", align {load.Alignment.Value}"
            + (load.HasInvariantLoadMetadata ? InvariantLoadMetadata() : "");
        private string Store(LlvmStoreInstruction store) => "store " + (store.IsAtomic ? "atomic " : "")
            + (store.IsVolatile ? "volatile " : "") + Typed(store.ValueOperand) + ", " + Typed(store.PointerOperand)
            + Atomic(store.Ordering, store.SyncScopeId) + $", align {store.Alignment.Value}";
        private string Gep(LlvmGetElementPtrInstruction gep)
        {
            var flags = gep.NoWrapFlags;
            string wrap = flags.IsInBounds ? "inbounds " : flags.HasNoUnsignedSignedWrap ? "nusw " : "";
            if (flags.HasNoUnsignedWrap) wrap += "nuw ";
            return "getelementptr " + wrap + Type(gep.SourceElementType) + ", " + Typed(gep.PointerOperand)
                + string.Concat(gep.Indices.Select(index => ", " + Typed(index)));
        }
        private string Phi(LlvmPhiInstruction phi) => "phi " + FastMath(phi.FastMathFlags) + Type(phi.Type) + " "
            + string.Join(", ", Enumerable.Range(0, phi.IncomingCount).Select(index =>
                $"[ {Reference(phi.GetIncomingValue(index))}, {Reference(phi.GetIncomingBlock(index))} ]"));
        private string Switch(LlvmSwitchInstruction selection) => $"switch {Typed(selection.Condition)}, label {Reference(selection.DefaultDestination)} ["
            + string.Concat(Enumerable.Range(0, selection.CaseCount).Select(index =>
                $"\n    {Typed(selection.GetCaseValue(index))}, label {Reference(selection.GetCaseDestination(index))}")) + "\n  ]";
        private string Call(LlvmCallInstruction call)
        {
            string tail = call.TailCallKind switch
            {
                LlvmTailCallKind.None => "", LlvmTailCallKind.Tail => "tail ",
                LlvmTailCallKind.MustTail => "musttail ", LlvmTailCallKind.NoTail => "notail ",
                _ => throw new ArgumentOutOfRangeException(nameof(call))
            };
            string convention = call.CallingConvention == 0 ? "" : $"cc {call.CallingConvention} ";
            string bundles = call.OperandBundles.Count == 0 ? "" : " [ "
                + string.Join(", ", call.OperandBundles.Select(bundle => Quote(System.Text.Encoding.UTF8.GetBytes(bundle.Tag))
                    + "(" + string.Join(", ", bundle.Inputs.Select(Typed)) + ")")) + " ]";
            return tail + "call " + FastMath(call.FastMathFlags) + convention + Type(call.FunctionType.IsVarArg ? call.FunctionType : call.Type)
                + " " + Reference(call.CalledOperand) + "(" + string.Join(", ", call.Arguments.Select(Typed)) + ")" + bundles;
        }
        private static string Mnemonic(LlvmOpcode opcode) => opcode.ToString().ToLowerInvariant();
        private static string Predicate(LlvmCmpPredicate predicate) => predicate.ToString()[4..].ToLowerInvariant();
        private static string FileOutMode(LlvmFileOutMode mode) => mode switch
        {
            LlvmFileOutMode.Transfer => "transfer", LlvmFileOutMode.WriteText => "text",
            LlvmFileOutMode.AppendText => "append.text", LlvmFileOutMode.WriteBytes => "bytes",
            LlvmFileOutMode.AppendBytes => "append.bytes", _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        private static string Atomic(LlvmAtomicOrdering ordering, uint scope)
        {
            if (ordering == LlvmAtomicOrdering.NotAtomic) return "";
            string synchronization = scope switch
            {
                1 => "", 0 => " syncscope(\"singlethread\")",
                _ => throw new NotSupportedException($"Context-defined synchronization scope {scope} has not been ported.")
            };
            return synchronization + " " + (ordering switch
            {
                LlvmAtomicOrdering.Unordered => "unordered", LlvmAtomicOrdering.Monotonic => "monotonic",
                LlvmAtomicOrdering.Acquire => "acquire", LlvmAtomicOrdering.Release => "release",
                LlvmAtomicOrdering.AcquireRelease => "acq_rel", LlvmAtomicOrdering.SequentiallyConsistent => "seq_cst",
                _ => throw new ArgumentOutOfRangeException(nameof(ordering))
            });
        }
        private static string FastMath(LlvmFastMathFlags flags)
        {
            if (flags == LlvmFastMathFlags.Fast) return "fast ";
            return (flags.HasFlag(LlvmFastMathFlags.AllowReassoc) ? "reassoc " : "")
                + (flags.HasFlag(LlvmFastMathFlags.NoNaNs) ? "nnan " : "")
                + (flags.HasFlag(LlvmFastMathFlags.NoInfs) ? "ninf " : "")
                + (flags.HasFlag(LlvmFastMathFlags.NoSignedZeros) ? "nsz " : "")
                + (flags.HasFlag(LlvmFastMathFlags.AllowReciprocal) ? "arcp " : "")
                + (flags.HasFlag(LlvmFastMathFlags.AllowContract) ? "contract " : "")
                + (flags.HasFlag(LlvmFastMathFlags.ApproxFunc) ? "afn " : "");
        }
    }
}
