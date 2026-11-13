namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static void ValidatePointerUses(NativeProgram program, NativeFunctionCode function)
    {
        NativeAddressAnalysis facts = new(program, function);
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            switch (instruction.Op)
            {
                case NativeOp.AddressOffset:
                    HashSet<int>? layouts = facts.Layouts(instruction.A);
                    if (layouts is not { Count: 1 } || !layouts.Contains(instruction.B))
                        throw Error(instruction, "Direct native address formation requires proof of its one allocation layout; use a finite projection for alternatives");
                    break;
                case NativeOp.IndexAddress:
                case NativeOp.RangeFromMemory:
                    RequireRegion(instruction.A, instruction.C);
                    break;
                case NativeOp.CopyMemory:
                    RequireRegion(instruction.B, checked((int)instruction.Immediate));
                    RequireRegion(instruction.A, checked((int)instruction.Immediate));
                    break;
                case NativeOp.ClearMemory:
                    RequireRegion(instruction.A, checked((int)instruction.Immediate));
                    break;
                case NativeOp.ProjectAddress:
                    HashSet<int>? alternatives = facts.Layouts(instruction.A);
                    if (alternatives is not { Count: > 0 }
                        || alternatives.Any(id => !program.Projections[instruction.B].Cases.Any(item => item.Layout == id)))
                        throw Error(instruction, $"Native projection does not cover every possible allocation layout in '{function.Name}' at instruction {pc} (base %{instruction.A}; admitted [{string.Join(',', alternatives ?? [])}]; cases [{string.Join(',', program.Projections[instruction.B].Cases.Select(item => item.Layout))}])");
                    break;
                case NativeOp.Store:
                    int source = function.Slots[instruction.B].Type;
                    foreach (int actual in facts.PhysicalTypes(instruction.A))
                        if (!PhysicalAssignable(program, actual, source) && !PathStringStore(program, actual, source))
                            throw Error(instruction, $"Native stored value is not admitted by every possible selected physical field (stored t{source}, selected t{actual})");
                    break;
                case NativeOp.BindLocation:
                    RequirePhysicalPointer(facts, instruction, instruction.B, function.Slots[instruction.A].Type);
                    break;
                case NativeOp.MakeFunction when instruction.A >= 0:
                    NativeFunctionCode target = program.Functions[instruction.A];
                    for (int index = 0; index < target.CaptureCount; index++)
                    {
                        NativeCaptureContract capture = target.Signature.Captures[index];
                        if (instruction.B >= 0)
                        {
                            NativeCodeType field = program.Types[program.Layouts[instruction.B].Fields[index].Type];
                            if (field.Pointee >= 0) RequirePhysicalPointer(facts, instruction, instruction.Inputs![index], field.Pointee);
                        }
                        else if (capture.Mode == NativeCaptureMode.Location)
                            RequirePhysicalPointer(facts, instruction, instruction.Inputs![index], capture.Type);
                    }
                    break;
                case NativeOp.Call:
                    foreach (int targetIndex in CallTargets(program, instruction))
                    {
                        NativeSignature signature = program.Functions[targetIndex].Signature;
                        for (int index = 0; index < signature.Parameters.Count; index++)
                            if (program.Types[signature.Parameters[index]].Pointee >= 0)
                                RequirePhysicalPointer(facts, instruction, instruction.Inputs![index],
                                    program.Types[signature.Parameters[index]].Pointee);
                    }
                    break;
            }
        }

        void RequireRegion(int register, int layout)
        {
            // Runtime checks concrete offsets and counts; admission proves the physical element schema.
            HashSet<int>? alternatives = facts.Layouts(register);
            if (alternatives is { Count: > 0 } && alternatives.All(id => SameLayout(id, layout))) return;
            NativeCodeLayout expected = program.Layouts[layout];
            IReadOnlySet<int> physical = facts.PhysicalTypes(register);
            if (expected.Fields.Count == 1 && physical.Count > 0
                && physical.All(type => SameType(program, type, expected.Fields[0].Type))) return;
            throw new MsyncException("Native memory region requires proof of its physical stride layout");
        }
        bool SameLayout(int left, int right)
        {
            IReadOnlyList<NativeCodeField> first = program.Layouts[left].Fields, second = program.Layouts[right].Fields;
            return first.Count == second.Count && first.Where((field, index) => !SameType(program, field.Type, second[index].Type)).Any() == false;
        }

        void RequirePhysicalPointer(NativeAddressAnalysis facts, NativeInstruction instruction, int register, int expected)
        {
            foreach (int actual in facts.PhysicalTypes(register))
                if (!SameType(program, actual, expected))
                    throw Error(instruction, "A shared writable location requires the same physical storage contract on every alternative");
        }
    }

    private static bool PhysicalAssignable(NativeProgram program, int destination, int source) =>
        NativeTypeRelations.StorageAccepts(program.Types, destination, source);

    private static bool PathStringStore(NativeProgram program, int destination, int source) =>
        NativeTypeRelations.PathStringStore(program.Types, destination, source);

    private static IEnumerable<int> CallTargets(NativeProgram program, NativeInstruction instruction)
    {
        if (instruction.B >= 0) yield return instruction.B;
        if (instruction.C >= 0)
            foreach (int target in program.SessionDispatches![instruction.C].Targets.Values) yield return target;
        if (instruction.Symbol is { } encoded)
            foreach (int target in FunctionDispatch.Targets(encoded).Values) yield return target;
    }

}
