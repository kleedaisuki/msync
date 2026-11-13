namespace Msync.Core.Native;

public static partial class NativeLegacyV6Importer
{
    private sealed partial class Importer
    {
        private sealed partial class FunctionImport
        {
            private void Compact()
            {
                bool changed;
                do
                {
                    int[] relocation = new int[code.Count + 1]; int count = 0;
                    bool[] keep = new bool[code.Count];
                    for (int pc = 0; pc < code.Count; pc++)
                    {
                        relocation[pc] = count;
                        keep[pc] = code[pc].Op != NativeOp.Nop && !(code[pc].Op == NativeOp.Jump && code[pc].A == pc + 1);
                        if (keep[pc]) count++;
                    }
                    relocation[^1] = count; changed = count != code.Count;
                    if (!changed) return;
                    List<NativeInstruction> compacted = new(count);
                    for (int pc = 0; pc < code.Count; pc++)
                    {
                        if (!keep[pc]) continue;
                        NativeInstruction instruction = code[pc];
                        compacted.Add(instruction.Op switch
                        {
                            NativeOp.Jump => instruction with { A = relocation[instruction.A] },
                            NativeOp.Branch => instruction with { B = relocation[instruction.B], C = relocation[instruction.C] },
                            NativeOp.NextIterator => instruction with { B = relocation[instruction.B] },
                            _ => instruction
                        });
                    }
                    for (int pc = 0; pc < Map.Length; pc++) Map[pc] = relocation[Map[pc]];
                    for (int plan = 0; plan < owner.Allocations.Count; plan++)
                        if (owner.Allocations[plan].Function == index)
                            owner.Allocations[plan] = owner.Allocations[plan] with { Instruction = relocation[owner.Allocations[plan].Instruction] };
                    code.Clear(); code.AddRange(compacted);
                } while (changed);
            }
        }
    }
}
