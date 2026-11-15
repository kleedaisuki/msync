// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Scalar/DCE.cpp,
// Utils/Local.cpp isInstructionTriviallyDead / wouldInstructionBeTriviallyDead.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static class LlvmDeadCodeElimination
{
    public static bool Run(LlvmFunction function, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        var work = new Work(function, trace);
        if (trace is null) return work.Run();
        return trace.MeasureDetailed("optimizer.pass.llvm-dce", work, static state => state.Run(),
            input: new CompilationSize(1, work.InstructionCount, 0),
            size: _ => new CompilationSize(1, work.InstructionCount, 0),
            attributes: new Dictionary<string, string> { ["function.name"] = function.Name },
            metrics: _ => work.Metrics!);
    }

    public static bool IsInstructionTriviallyDead(LlvmInstruction instruction, Dictionary<string, ulong>? metrics = null)
    {
        if (metrics is not null) metrics["predicate.checked"] = metrics.GetValueOrDefault("predicate.checked") + 1;
        if (!instruction.UseEmpty) return Decision(false, "used", metrics);
        return WouldInstructionBeTriviallyDead(instruction, metrics);
    }
    public static bool WouldInstructionBeTriviallyDead(LlvmInstruction instruction, Dictionary<string, ulong>? metrics = null)
    {
        if (instruction.IsTerminator) return Decision(false, "terminator", metrics);
        if (instruction.IsEHPad) return Decision(false, "eh-pad", metrics);
        // Project allocation instruction uses the isRemovableAlloc branch.
        if (instruction is LlvmHeapAllocInst) return Decision(true, "removable-allocation", metrics);
        if (!instruction.WillReturn()) return Decision(false, "not-willreturn", metrics);
        if (!instruction.MayHaveSideEffects()) return Decision(true, "no-side-effects", metrics);
        if (instruction is LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } intrinsic } marker)
        {
            if (intrinsic.IsLaunderInvariantGroup) return Decision(true, "dead-launder-invariant-group", metrics);
            if (intrinsic.IsLifetimeStartOrEnd)
            {
                LlvmValue pointer = marker.GetArgument(0);
                if (pointer is LlvmPoisonValue) return Decision(true, "poison-lifetime", metrics);
                foreach (LlvmUse use in pointer.Uses)
                    if (use.User is not LlvmInstruction { IsLifetimeStartOrEnd: true })
                        return Decision(false, "lifetime-with-other-uses", metrics);
                return Decision(true, "lifetime-only-uses", metrics);
            }
        }
        if (instruction is LlvmCallBase { CalledFunction.IntrinsicDescriptor.IsAssume: true } assumption &&
            assumption.OperandBundles.All(bundle => bundle.Tag == "ignore"))
            return Decision(assumption.GetArgument(0) is LlvmConstantInt { IsZero: false }, "assume-constant-condition", metrics);
        if (instruction is LlvmLoadInstruction { IsVolatile: false } load &&
            StripPointerCasts(load.PointerOperand) is LlvmGlobalVariable { IsConstant: true })
            return Decision(true, "constant-global-load", metrics);
        return Decision(false, "side-effects", metrics);
    }
    private static bool Decision(bool dead, string reason, Dictionary<string, ulong>? metrics)
    {
        if (metrics is not null)
        {
            string key = (dead ? "dead." : "retained.") + reason;
            metrics[key] = metrics.GetValueOrDefault(key) + 1;
        }
        return dead;
    }

    private sealed class Work(LlvmFunction function, CompilationTrace? trace)
    {
        private readonly List<LlvmInstruction> pending = [];
        private readonly HashSet<LlvmInstruction> membership = [];
        internal readonly Dictionary<string, ulong>? Metrics = trace is null ? null : [];
        private bool changed;
        internal int InstructionCount => function.BasicBlocks.Sum(block => block.Instructions.Count);

        internal bool Run()
        {
            if (trace is null) { Scan(); Drain(); }
            else
            {
                trace.Run("optimizer.llvm-dce.initial-scan", this, static state => state.Scan());
                trace.Run("optimizer.llvm-dce.worklist", this, static state => state.Drain());
            }
            return changed;
        }

        private void Scan()
        {
            foreach (LlvmBasicBlock block in function.BasicBlocks)
            {
                LlvmInstruction? instruction = block.FirstInstruction;
                while (instruction is not null)
                {
                    LlvmInstruction? next = instruction.Next;
                    Count("scan.visited");
                    if (membership.Contains(instruction)) Count("scan.already_queued");
                    else Eliminate(instruction);
                    instruction = next;
                }
            }
        }

        private void Drain()
        {
            while (pending.Count != 0)
            {
                int index = pending.Count - 1;
                LlvmInstruction instruction = pending[index];
                pending.RemoveAt(index);
                membership.Remove(instruction);
                Count("worklist.popped");
                Eliminate(instruction);
            }
        }

        private bool Eliminate(LlvmInstruction instruction)
        {
            if (!IsTriviallyDead(instruction)) return false;
            if (trace is null) Erase(instruction);
            else trace.Run("optimizer.llvm-dce.erase", (Work: this, Instruction: instruction),
                static state => state.Work.Erase(state.Instruction), SourceAttributes(instruction));
            changed = true;
            return true;
        }

        private void Erase(LlvmInstruction instruction)
        {
            for (int index = 0; index < instruction.OperandCount; ++index)
            {
                LlvmValue operand = instruction.GetOperand(index)!;
                instruction.SetOperand(index, null);
                Count("operands.detached");
                if (!operand.UseEmpty || ReferenceEquals(instruction, operand)) continue;
                if (operand is LlvmInstruction producer && IsTriviallyDead(producer)) Enqueue(producer);
            }
            trace?.Metric("operands.detached", instruction.OperandCount);
            instruction.EraseFromParent();
            Count("instructions.erased");
        }

        private void Enqueue(LlvmInstruction instruction)
        {
            if (!membership.Add(instruction)) { Count("worklist.duplicate"); return; }
            pending.Add(instruction);
            Count("worklist.inserted");
            if (Metrics is not null)
                Metrics["worklist.peak"] = Math.Max(Metrics.GetValueOrDefault("worklist.peak"), (ulong)pending.Count);
        }

        private bool IsTriviallyDead(LlvmInstruction instruction) => IsInstructionTriviallyDead(instruction, Metrics);

        private void Count(string key)
        {
            if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + 1;
        }

        private Dictionary<string, string> SourceAttributes(LlvmInstruction instruction)
        {
            var attributes = new Dictionary<string, string>
            {
                ["function.name"] = function.Name,
                ["block.name"] = instruction.Parent!.Name,
                ["instruction.name"] = instruction.Name,
                ["instruction.opcode"] = instruction.Opcode.ToString()
            };
            if (instruction.Source is { } source)
            {
                attributes["source.name"] = source.Source;
                attributes["source.start"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return attributes;
        }
    }

    // Value.cpp stripPointerCastsAndOffsets<PSK_ZeroIndices>; only represented
    // operators are covered. Call returned-argument attributes are not ported.
    private static LlvmValue StripPointerCasts(LlvmValue value)
    {
        if (!value.Type.IsPointerTy()) return value;
        HashSet<LlvmValue> visited = [value];
        do
        {
            switch (value)
            {
                case LlvmGetElementPtrInstruction gep:
                    if (!gep.HasAllZeroIndices) return value;
                    value = gep.PointerOperand;
                    break;
                case LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast:
                    if (!cast.Operand.Type.IsPointerTy()) return value;
                    value = cast.Operand;
                    break;
                case LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast } cast:
                    value = cast.Operand;
                    break;
                default:
                    return value;
            }
        } while (visited.Add(value));
        return value;
    }
}
