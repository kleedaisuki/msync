using Msync.Core;
using Msync.Core.Native;

// Exhaustive path-state exploration for the bounded generated corpus; no production CFG/facts helpers.
internal static class InstructionValidationOracle
{
    private readonly record struct State(int Pc, ulong Values, ulong Iterators);
    internal static bool Accepts(NativeProgram program)
    {
        NativeFunctionCode body = program.Functions[0];
        Queue<State> pending = new(); HashSet<State> visited = [];
        pending.Enqueue(new(0, (1UL << body.ParameterCount) - 1, 0));
        while (pending.TryDequeue(out State state))
        {
            if (!visited.Add(state)) continue;
            if ((uint)state.Pc >= (uint)body.Instructions.Count) return false;
            NativeInstruction instruction = body.Instructions[state.Pc]; ulong values = state.Values, iterators = state.Iterators;
            bool Has(int register) => register >= 0 && register < body.Slots.Count && (values & (1UL << register)) != 0;
            void Next(int pc, ulong bits, ulong cursors) => pending.Enqueue(new(pc, bits, cursors));
            switch (instruction.Op)
            {
                case NativeOp.Constant:
                    if (instruction.Result < 0 || instruction.Result >= body.Slots.Count || instruction.A < 0 || instruction.A >= program.Constants.Count) return false;
                    TypeKind produced = program.Constants[instruction.A].Kind == NativeKind.Number ? TypeKind.Number : TypeKind.Bool;
                    if ((program.Types[body.Slots[instruction.Result].Type].Kind & produced) == 0) return false;
                    values |= 1UL << instruction.Result; break;
                case NativeOp.Copy:
                    if (!Has(instruction.A)) return false;
                    values |= 1UL << instruction.Result; break;
                case NativeOp.Branch:
                    if (!Has(instruction.A) || program.Types[body.Slots[instruction.A].Type].Kind != TypeKind.Bool) return false;
                    Next(instruction.B, values, iterators); Next(instruction.C, values, iterators); continue;
                case NativeOp.Jump: Next(instruction.A, values, iterators); continue;
                case NativeOp.OpenIterator:
                    if (!Has(instruction.A) || (iterators & (1UL << instruction.B)) != 0) return false;
                    iterators |= 1UL << instruction.B; break;
                case NativeOp.NextIterator:
                    if ((iterators & (1UL << instruction.A)) == 0) return false;
                    Next(instruction.B, values, iterators); values |= 1UL << instruction.Result; break;
                case NativeOp.CloseIterator:
                    if ((iterators & (1UL << instruction.A)) == 0) return false;
                    iterators &= ~(1UL << instruction.A); break;
                case NativeOp.Return: if (!Has(instruction.A)) return false; continue;
                case NativeOp.Nop: break;
                default: throw new InvalidOperationException("Generated oracle has no " + instruction.Op);
            }
            Next(state.Pc + 1, values, iterators);
        }
        return true;
    }
}
