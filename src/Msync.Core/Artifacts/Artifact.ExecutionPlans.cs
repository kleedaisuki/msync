namespace Msync.Core;

public static partial class Artifact
{
    private static ExecutionPlan ReadExecutionPlan(BinaryReader reader)
    {
        int function = reader.ReadInt32();
        int start = reader.ReadInt32();
        int end = reader.ReadInt32();
        int eliminated = reader.ReadInt32();
        int[]? resultTransfers = reader.ReadBoolean() ? ReadArray(reader, item => item.ReadInt32()) : null;
        var transfers = ReadArray(reader, input =>
        {
            int id = input.ReadInt32();
            Value source = ReadValue(input);
            var destination = ReadValue(input) as PathValue
                ?? throw new MsyncException("An execution plan destination must be a Path");
            var kind = (PlannedTransferKind)input.ReadByte();
            var mode = (TransferDestinationMode)input.ReadByte();
            PlanResource[] reads = ReadArray(input, ReadPlanResource);
            PlanResource[] writes = ReadArray(input, ReadPlanResource);
            int[] predecessors = ReadArray(input, item => item.ReadInt32());
            SourceSpan? span = input.ReadBoolean()
                ? new(input.ReadString(), input.ReadInt32(), input.ReadInt32()) : null;
            return new PlannedTransfer(id, source, destination, kind, reads, writes, predecessors, span, mode);
        });
        var batches = ReadArray(reader, input =>
        {
            string endpoint = input.ReadString();
            return new ExecutionBatch(ReadArray(input, item => item.ReadInt32()), endpoint);
        });
        return new(function, start, end, transfers, batches, eliminated, resultTransfers);
    }

    private static void WriteExecutionPlan(BinaryWriter writer, ExecutionPlan plan)
    {
        writer.Write(plan.Function);
        writer.Write(plan.Start);
        writer.Write(plan.End);
        writer.Write(plan.EliminatedTransfers);
        writer.Write(plan.ResultTransfers is not null);
        if (plan.ResultTransfers is { } results)
            WriteArray(writer, results, (output, transfer) => output.Write(transfer));
        WriteArray(writer, plan.Transfers, (output, transfer) =>
        {
            output.Write(transfer.Id);
            WriteValue(output, transfer.Source);
            WriteValue(output, transfer.Destination);
            output.Write((byte)transfer.Kind);
            output.Write((byte)transfer.Mode);
            WriteArray(output, transfer.Reads, WritePlanResource);
            WriteArray(output, transfer.Writes, WritePlanResource);
            WriteArray(output, transfer.Predecessors, (item, predecessor) => item.Write(predecessor));
            output.Write(transfer.Span.HasValue);
            if (transfer.Span is { } span)
            {
                output.Write(span.Source);
                output.Write(span.Start);
                output.Write(span.Length);
            }
        });
        WriteArray(writer, plan.Batches, (output, batch) =>
        {
            output.Write(batch.Endpoint);
            WriteArray(output, batch.Transfers, (item, transfer) => item.Write(transfer));
        });
    }

    private static PlanResource ReadPlanResource(BinaryReader reader) =>
        new(reader.ReadString(), reader.ReadString(), reader.ReadBoolean());

    private static void WritePlanResource(BinaryWriter writer, PlanResource resource)
    {
        writer.Write(resource.Endpoint);
        writer.Write(resource.Path);
        writer.Write(resource.Subtree);
    }

    private static void ValidateExecutionPlans(Module module)
    {
        if (module.ExecutionPlans is not { Count: > 0 } plans) return;
        if (module.Stage != ArtifactStage.Executable)
            throw new MsyncException("Relocatable bytecode cannot contain contextual execution plans");
        foreach (ExecutionPlan plan in plans)
        {
            if ((uint)plan.Function >= (uint)module.Functions.Count || plan.Start < 0
                || plan.End < plan.Start || plan.EliminatedTransfers < 0)
                throw new MsyncException("Invalid execution plan region");
            if (plan.ResultTransfers is { } results)
                foreach (int result in results)
                    if ((uint)result >= (uint)plan.Transfers.Count)
                        throw new MsyncException("An execution plan result refers to a missing transfer");
            for (int index = 0; index < plan.Transfers.Count; index++)
            {
                PlannedTransfer transfer = plan.Transfers[index];
                if (transfer.Id != index || !Enum.IsDefined(transfer.Kind) || !Enum.IsDefined(transfer.Mode))
                    throw new MsyncException("Invalid execution plan transfer", transfer.Span);
                bool validSource = transfer.Kind switch
                {
                    PlannedTransferKind.WriteText => transfer.Source is StringValue,
                    PlannedTransferKind.GenericCopy => transfer.Source is PathValue or StringValue or RangeValue,
                    PlannedTransferKind.EnsureDirectory => transfer.Source is NullValue or PathValue,
                    _ => transfer.Source is PathValue
                };
                if (!validSource)
                    throw new MsyncException("Execution plan transfer kind disagrees with its source value", transfer.Span);
                ValidateConstant(transfer.Source, module);
                ValidateConstant(transfer.Destination, module);
                HashSet<int>? predecessors = transfer.Predecessors.Count > 1 ? [] : null;
                foreach (int predecessor in transfer.Predecessors)
                    if ((uint)predecessor >= (uint)index || predecessors is not null && !predecessors.Add(predecessor))
                        throw new MsyncException("Execution plan predecessors must identify distinct earlier transfers", transfer.Span);
            }
            HashSet<int> batched = [];
            foreach (ExecutionBatch batch in plan.Batches)
                foreach (int transfer in batch.Transfers)
                    if ((uint)transfer >= (uint)plan.Transfers.Count || !batched.Add(transfer))
                        throw new MsyncException("An execution batch refers to a missing or repeated transfer");
        }
    }
}
