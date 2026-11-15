// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: EarlyCSE.cpp CallValue,
// hashCallInst and processNode readonly/writeonly-call handling.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmEarlyCse
{
    private static bool CanHandleCall(LlvmInstruction instruction)
    {
        if (instruction is not LlvmCallInstruction call || call.Parent!.Parent!.IsPresplitCoroutine) return false;
        var effects = LlvmMemoryEffects.CreateFromIntValue(call.GetMemoryEffects().Raw);
        return effects.OnlyReadsMemory || effects.OnlyWritesMemory;
    }

    private static bool IsMemSet(LlvmInstruction instruction) =>
        instruction is LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor } &&
        (descriptor.Kind == LlvmIntrinsicKind.MemSet || descriptor.Kind == LlvmIntrinsicKind.MemSetInline);

    private sealed class CallComparer : IEqualityComparer<LlvmInstruction>
    {
        public int GetHashCode(LlvmInstruction instruction)
        {
            var call = (LlvmCallInstruction)instruction;
            HashCode hash = new(); hash.Add(call.Opcode);
            if (call.IsConvergent) hash.Add(call.Parent);
            foreach (var operand in call.Operands) hash.Add(operand);
            return hash.ToHashCode();
        }
        public bool Equals(LlvmInstruction? left, LlvmInstruction? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is not LlvmCallInstruction a || right is not LlvmCallInstruction b) return false;
            if (a.IsConvergent && a.Parent != b.Parent) return false;
            return a.IsIdenticalToWhenDefined(b, intersectAttrs: true);
        }
    }

    private readonly record struct AvailableCall(LlvmInstruction? Instruction, uint Generation);

    private sealed partial class Work
    {
        private readonly ScopedTable<LlvmInstruction, AvailableCall> calls = new(new CallComparer());

        private void ProcessCall(LlvmCallInstruction call)
        {
            Count("call.lookup");
            var available = calls.Lookup(call);
            if (available.Instruction is { } instruction &&
                IsSameMemoryGeneration(available.Generation, generation, instruction, call) &&
                instruction.MayReadFromMemory() == call.MayReadFromMemory())
            {
                CombineFlags(call, instruction);
                if (!call.UseEmpty) call.ReplaceAllUsesWith(instruction);
                Erase(call, "call-cse"); return;
            }
            if (call.MayWriteToMemory()) AdvanceGeneration("memory.kill.call_writer");
            calls.Insert(call, new(call, generation)); Count("call.insert");
        }

        private static void IntersectCallAttributes(LlvmInstruction from, LlvmValue to)
        {
            if (from is not LlvmCallBase source || to is not LlvmCallBase target) return;
            bool success = target.TryIntersectAttributes(source);
            Debug.Assert(success);
        }
    }
}
