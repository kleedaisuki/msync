namespace Msync.Core.Native;

public static partial class LegacyV6Reader
{
    private static void ValidateMetadata(LegacyV6Module module)
    {
        if (module.Semantics != ModuleSemantics.Current || !Enum.IsDefined(module.Stage))
            throw new MsyncException("Unsupported format-6 execution semantics or stage");
        if ((module.Stage == ArtifactStage.Executable) != (module.Context is not null))
            throw new MsyncException("Format-6 stage and binding context disagree");
        Index(module.EntryPoint, module.Functions.Count, "entrypoint");
        for (int index = 0; index < module.Types.Count; index++)
        {
            LegacyV6CodeType type = module.Types[index];
            if ((type.Kinds & ~4095) != 0 || type.Element < -1 || type.Element >= index
                || type.Element >= 0 && (type.Kinds & ((ushort)TypeKind.Range | LegacyStorageMask)) == 0)
                throw new MsyncException($"Invalid format-6 code type {index}");
            Sorted(type.Layouts, module.Layouts.Count, "type layout");
            if (type.Layouts.Count != 0 && (type.Kinds & (ushort)TypeKind.Scope) == 0)
                throw new MsyncException("A non-namespace format-6 type declares namespace layouts");
        }
        foreach (LegacyV6Layout layout in module.Layouts)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (LegacyV6Field field in layout.Fields)
            {
                if (field.Name.Length == 0 || !names.Add(field.Name)) throw new MsyncException("Invalid format-6 namespace field name");
                Index(field.Type, module.Types.Count, "field type");
            }
        }
        foreach (int layout in module.Statics) Index(layout, module.Layouts.Count, "static layout");
        foreach (LegacyV6Projection projection in module.Projections)
        {
            if (projection.Name.Length == 0) throw new MsyncException("A format-6 field projection requires a name");
            int previous = -1;
            foreach (NativeProjectionCase item in projection.Cases)
            {
                Index(item.Layout, module.Layouts.Count, "projection layout");
                if (item.Layout <= previous) throw new MsyncException("Format-6 projection layouts are not sorted and unique");
                previous = item.Layout;
                Index(item.Field, module.Layouts[item.Layout].Fields.Count, "projection field");
                if (module.Layouts[item.Layout].Fields[item.Field].Name != projection.Name)
                    throw new MsyncException("A format-6 projection does not select its named field");
            }
        }
        foreach (LegacyV6Value constant in module.Constants) ValidateValue(module, constant);
        for (int function = 0; function < module.Functions.Count; function++) ValidateFunction(module, function);
        if (module.Functions[module.EntryPoint].Signature.RequiredStatics.Count != 0)
            throw new MsyncException("A format-6 entrypoint cannot require initialized static namespaces");
        foreach (LegacyV6Plan plan in module.Plans) ValidatePlan(module, plan);
    }

    private static void ValidateFunction(LegacyV6Module module, int index)
    {
        LegacyV6Function function = module.Functions[index];
        LegacyV6Signature signature = function.Signature;
        if (function.LocalCount < 0 || (long)signature.Parameters.Count + signature.Captures.Count > function.LocalCount)
            throw new MsyncException($"Invalid format-6 local layout in '{function.Name}'");
        Index(signature.Result, module.Types.Count, "function result type");
        foreach (int parameter in signature.Parameters) Index(parameter, module.Types.Count, "parameter type");
        foreach (LegacyV6Capture capture in signature.Captures) Index(capture.Type, module.Types.Count, "capture type");
        Sorted(signature.RequiredStatics, module.Statics.Count, "required static");
        foreach (LegacyV6Instruction instruction in function.Instructions)
        {
            if (!Enum.IsDefined(instruction.Op) || instruction.Op is LegacyV6Op.MakeScope or LegacyV6Op.GetMember
                or LegacyV6Op.SetMember or LegacyV6Op.GetSingleton or LegacyV6Op.SetSingleton)
                throw new MsyncException($"Opcode {instruction.Op} is not defined by format 6", instruction.Source);
            switch (instruction.Op)
            {
                case LegacyV6Op.Constant: Index(instruction.A, module.Constants.Count, "constant"); break;
                case LegacyV6Op.LoadLocal: case LegacyV6Op.LoadCell: case LegacyV6Op.LoadStorage:
                case LegacyV6Op.StoreLocal: case LegacyV6Op.InitializeLocal: case LegacyV6Op.IteratorNext:
                    Index(instruction.A, function.LocalCount, "local"); break;
                case LegacyV6Op.NewScope: Index(instruction.A, module.Layouts.Count, "allocation layout"); break;
                case LegacyV6Op.LoadField: case LegacyV6Op.StoreField: Index(instruction.A, module.Projections.Count, "field projection"); break;
                case LegacyV6Op.AdmitScope: Index(instruction.A, module.Types.Count, "admitted type"); break;
                case LegacyV6Op.MakeClosure: case LegacyV6Op.SpecializeClosure:
                    if (instruction.A >= 0) Index(instruction.A, module.Functions.Count, "function");
                    else throw new MsyncException("An exported format-6 artifact cannot use an open session dispatch", instruction.Source);
                    break;
                case LegacyV6Op.LoadStaticScope: case LegacyV6Op.PublishStatic:
                case LegacyV6Op.LoadStaticField: case LegacyV6Op.StoreStaticField:
                    Index(instruction.A, module.Statics.Count, "static namespace");
                    if (instruction.Op is LegacyV6Op.LoadStaticField or LegacyV6Op.StoreStaticField)
                        Index(instruction.B, module.Layouts[module.Statics[instruction.A]].Fields.Count, "static field");
                    break;
                case LegacyV6Op.ExecutePlan: case LegacyV6Op.PlanResult:
                    Index(instruction.A, module.Plans.Count, "execution plan");
                    if (module.Plans[instruction.A].Function != index || instruction.B < 0)
                        throw new MsyncException("Invalid format-6 plan instruction", instruction.Source);
                    break;
            }
            if (instruction.Op == LegacyV6Op.LoadCell) Index(instruction.B - 1, module.Types.Count, "location storage type");
            if (instruction.Op is LegacyV6Op.Jump or LegacyV6Op.JumpIfFalse or LegacyV6Op.JumpIfTrue or LegacyV6Op.JumpIfStaticReady)
                Target(instruction.A, function.Instructions.Count);
            if (instruction.Op == LegacyV6Op.JumpIfStaticReady) Index(instruction.B, module.Statics.Count, "static namespace");
            if (instruction.Op == LegacyV6Op.IteratorNext) Target(instruction.B, function.Instructions.Count);
            if (instruction.Op == LegacyV6Op.Call && instruction.B < 0)
                throw new MsyncException("An exported format-6 artifact cannot use an open call signature", instruction.Source);
        }
    }

    private static void ValidateValue(LegacyV6Module module, LegacyV6Value initial)
    {
        Stack<LegacyV6Value> pending = new();
        pending.Push(initial);
        while (pending.TryPop(out LegacyV6Value? value))
        {
            if (value.Kind == LegacyV6ValueKind.Function && value.NativeName is null)
            {
                Index(value.Function, module.Functions.Count, "constant function");
                if ((value.Items?.Count ?? 0) != module.Functions[value.Function].Signature.Captures.Count)
                    throw new MsyncException("A format-6 function constant has the wrong capture count");
            }
            if (value.Kind == LegacyV6ValueKind.Namespace)
            {
                Index(value.Layout, module.Layouts.Count, "constant layout");
                if (value.Items?.Count != module.Layouts[value.Layout].Fields.Count)
                    throw new MsyncException("A format-6 namespace constant has the wrong field count");
            }
            foreach (LegacyV6Value child in value.Items ?? []) pending.Push(child);
        }
    }
    private static void ValidatePlan(LegacyV6Module module, LegacyV6Plan plan)
    {
        if (module.Stage != ArtifactStage.Executable || plan.Start < 0 || plan.End < plan.Start || plan.EliminatedTransfers < 0)
            throw new MsyncException("Invalid format-6 execution-plan region");
        Index(plan.Function, module.Functions.Count, "plan function");
        foreach (int result in plan.Results ?? []) Index(result, plan.Transfers.Count, "plan result");
        for (int index = 0; index < plan.Transfers.Count; index++)
        {
            LegacyV6Transfer transfer = plan.Transfers[index];
            if (transfer.Id != index || !Enum.IsDefined(transfer.Kind) || !Enum.IsDefined(transfer.Mode))
                throw new MsyncException("Invalid format-6 planned transfer", transfer.Span);
            ValidateValue(module, transfer.Source);
            HashSet<int> predecessors = [];
            foreach (int predecessor in transfer.Predecessors)
                if ((uint)predecessor >= (uint)index || !predecessors.Add(predecessor))
                    throw new MsyncException("Invalid format-6 plan predecessor", transfer.Span);
        }
        HashSet<int> batched = [];
        foreach (ExecutionBatch batch in plan.Batches)
            foreach (int transfer in batch.Transfers)
                if ((uint)transfer >= (uint)plan.Transfers.Count || !batched.Add(transfer))
                    throw new MsyncException("Invalid format-6 execution batch");
    }
    private static void Sorted(IReadOnlyList<int> values, int count, string description)
    {
        int previous = -1;
        foreach (int value in values)
        {
            Index(value, count, description);
            if (value <= previous) throw new MsyncException($"Format-6 {description} indices are not sorted and unique");
            previous = value;
        }
    }
    private static void Target(int target, int count)
    {
        if (target < 0 || target > count) throw new MsyncException($"Invalid format-6 branch target {target}");
    }
}
