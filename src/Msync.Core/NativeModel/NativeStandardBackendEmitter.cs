namespace Msync.Core.Native;

// Direct encoding into existing standard execution handlers. This is not an
// optimizer and does not infer aliasing, borrowing, unsigned ranges or raw writes.
public static class NativeStandardBackendEmitter
{
    public static NativeBackendFunction EmitFunction(NativeProgram program, int index, CompilationTrace? trace = null)
    {
        var function = program.Functions[index];
        var instructions = new NativeBackendInstruction[function.Instructions.Count];
        bool pureSynchronous = true, contextFree = true, observableBinary = false;
        long effectfulSites = 0, contextualSites = 0, binarySites = 0, selectSites = 0, machineSites = 0;
        for (int pc = 0; pc < instructions.Length; ++pc)
        {
            var instruction = function.Instructions[pc];
            instructions[pc] = new(instruction.Op, instruction.Result, instruction.A, instruction.B, instruction.C,
                1, instruction.Immediate, NativeBackendHandler.Standard, pc,
                instruction.Type < 0 ? ulong.MaxValue : StorageKinds(program.Types[instruction.Type]),
                NeedsLayoutCheck(program, function, instruction));
            if (instruction.Op == NativeOp.Binary) ++binarySites;
            if (instruction.Op == NativeOp.Select) ++selectSites;
            if (instruction.Op is NativeOp.MachineBinary or NativeOp.MachineCompare or NativeOp.MachineCast
                or NativeOp.MachineIntrinsic or NativeOp.MachineUnary) ++machineSites;
            if (instruction.Op == NativeOp.Binary &&
                (program.Types[function.Slots[instruction.A].Type].Kind != TypeKind.Number ||
                 program.Types[function.Slots[instruction.B].Type].Kind != TypeKind.Number)) observableBinary = true;
            if (instruction.Op is NativeOp.Call or NativeOp.CallNative or NativeOp.Transfer or NativeOp.OpenIterator
                or NativeOp.NextIterator or NativeOp.CloseIterator or NativeOp.ProcessCall or NativeOp.SelectPath
                or NativeOp.Barrier or NativeOp.Acquire or NativeOp.ExecutePlan or NativeOp.PlanResult)
            { pureSynchronous = false; contextFree = false; ++effectfulSites; }
            if (instruction.Op == NativeOp.Constant && program.Constants[instruction.A].Kind == NativeKind.Path ||
                instruction.Op == NativeOp.Binary && (NativeBinary)instruction.Immediate is NativeBinary.PathJoin or NativeBinary.Qualify ||
                instruction.Op == NativeOp.ComposeFunction && instruction.Symbol is { } name && name.StartsWith("fs.", StringComparison.Ordinal)
                    && NativeSignatures.TryGet(name, out var signature) && signature.Effect == NativeEffect.Read)
            { contextFree = false; ++contextualSites; }
        }
        trace?.Metric("instructions", instructions.Length);
        trace?.Metric("handlers.standard", instructions.Length);
        trace?.Metric("classification.binary_sites", binarySites);
        trace?.Metric("classification.select_sites", selectSites);
        trace?.Metric("classification.machine_sites", machineSites);
        trace?.Metric("classification.effectful_sites", effectfulSites);
        trace?.Metric("classification.contextual_sites", contextualSites);
        trace?.Metric("classification.pure_synchronous", pureSynchronous ? 1 : 0);
        trace?.Metric("classification.context_free", contextFree ? 1 : 0);
        trace?.Metric("classification.observable_binary", observableBinary ? 1 : 0);
        trace?.Metric("raw_writable.slots", 0);
        return new(instructions, new bool[function.Slots.Count], pureSynchronous, contextFree, observableBinary);
    }

    private static ulong StorageKinds(NativeCodeType type) => type.Machine is { } machine ? machine.PhysicalType.Kinds : type.Pointee >= 0
        ? NativeType.Of(NativeKind.Address).Kinds : NativeType.FromTypeRef(new(type.Kind)).Kinds;

    // The same direct descriptor rule used by NativeExecutionOperand.Link for
    // explicit Convert/Narrow semantics; ordinary loads/stores add no admission.
    private static bool NeedsLayoutCheck(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction)
    {
        if (instruction.Op is not (NativeOp.Narrow or NativeOp.Convert)) return false;
        var source = program.Types[function.Slots[instruction.A].Type];
        var target = program.Types[instruction.Type];
        if (source.Pointee >= 0 || target.Pointee >= 0) return true;
        return target.Layouts is not null &&
            (source.Layouts is null || !source.Layouts.All(target.Layouts.Contains));
    }
}
