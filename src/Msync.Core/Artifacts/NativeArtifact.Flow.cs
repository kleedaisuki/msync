namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private sealed class FlowState(int registers, int iterators)
    {
        internal bool[] Values { get; } = new bool[registers];
        internal bool[] Open { get; } = new bool[iterators];
        internal bool[] MaybeOpen { get; } = new bool[iterators];
        internal FlowState Copy()
        {
            FlowState copy = new(Values.Length, Open.Length);
            Values.CopyTo(copy.Values, 0); Open.CopyTo(copy.Open, 0); MaybeOpen.CopyTo(copy.MaybeOpen, 0);
            return copy;
        }
        internal bool Merge(FlowState other) => Intersect(Values, other.Values)
            | Intersect(Open, other.Open) | Union(MaybeOpen, other.MaybeOpen);
        private static bool Intersect(bool[] target, bool[] source)
        {
            bool changed = false;
            for (int index = 0; index < target.Length; index++)
                if (target[index] && !source[index]) { target[index] = false; changed = true; }
            return changed;
        }
        private static bool Union(bool[] target, bool[] source)
        {
            bool changed = false;
            for (int index = 0; index < target.Length; index++)
                if (!target[index] && source[index]) { target[index] = true; changed = true; }
            return changed;
        }
    }

    private static void ValidateFlow(NativeFunctionCode function)
    {
        if (function.Instructions.Count == 0)
        {
            if (function.IsTemplate) return;
            throw new MsyncException($"Native function '{function.Name}' has no entry instruction");
        }
        FlowState?[] incoming = new FlowState[function.Instructions.Count];
        FlowState entry = new(function.Slots.Count, function.IteratorCount);
        for (int index = 0; index < function.CaptureCount + function.ParameterCount; index++) entry.Values[index] = true;
        incoming[0] = entry;
        Queue<int> pending = new();
        pending.Enqueue(0);
        while (pending.TryDequeue(out int pc))
        {
            FlowState state = incoming[pc]!.Copy();
            NativeInstruction instruction = function.Instructions[pc];
            foreach (int register in NativeInstructionFacts.ValueReads(instruction, includeExtendedInputs: false))
                if (!state.Values[register])
                    throw Error(instruction, $"Register {register} is not definitely initialized at instruction {pc} in '{function.Name}'");
            if (instruction.Op == NativeOp.OpenIterator)
            {
                if (state.MaybeOpen[instruction.B]) throw Error(instruction, "Native iterator is reopened before its previous lifetime ends");
                state.Open[instruction.B] = state.MaybeOpen[instruction.B] = true;
            }
            if (instruction.Op is NativeOp.NextIterator or NativeOp.CloseIterator)
            {
                if (!state.Open[instruction.A]) throw Error(instruction, "Native iterator is not definitely open on this control-flow edge");
                if (instruction.Op == NativeOp.CloseIterator)
                    state.Open[instruction.A] = state.MaybeOpen[instruction.A] = false;
            }
            if (instruction.Op == NativeOp.Move && instruction.A != instruction.Result) state.Values[instruction.A] = false;
            if (instruction.Op == NativeOp.Drop) state.Values[instruction.A] = false;
            // Binding makes a location usable, just as a by-location capture is usable at entry.
            // Whether its pointee contains data remains a checked memory access, not a register fact.
            if (instruction.Op == NativeOp.BindLocation) state.Values[instruction.A] = true;
            if (instruction.Op == NativeOp.NextIterator)
            {
                Propagate(instruction.B, state);
                state.Values[instruction.Result] = true;
                Continue(pc, state);
            }
            else
            {
                if (instruction.Result >= 0) state.Values[instruction.Result] = true;
                switch (instruction.Op)
                {
                    case NativeOp.Return: case NativeOp.Unreachable: break;
                    case NativeOp.Jump: Propagate(instruction.A, state); break;
                    case NativeOp.Branch: Propagate(instruction.B, state); Propagate(instruction.C, state); break;
                    default: Continue(pc, state); break;
                }
            }
        }

        void Continue(int pc, FlowState state)
        {
            if (pc + 1 == function.Instructions.Count)
                throw Error(function.Instructions[pc], $"Reachable native function '{function.Name}' falls through without returning");
            Propagate(pc + 1, state);
        }
        void Propagate(int target, FlowState state)
        {
            if (incoming[target] is not { } previous)
            { incoming[target] = state.Copy(); pending.Enqueue(target); }
            else if (previous.Merge(state)) pending.Enqueue(target);
        }
    }

}
