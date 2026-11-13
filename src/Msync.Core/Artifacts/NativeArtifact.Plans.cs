namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static NativeExecutionPlan ReadPlan(BinaryReader reader)
    {
        int function = reader.ReadInt32(), start = reader.ReadInt32(), end = reader.ReadInt32();
        int eliminated = reader.ReadInt32();
        int[]? results = ReadOptionalArray(reader, value => value.ReadInt32());
        NativePlannedTransfer[] transfers = ReadArray(reader, value =>
        {
            int id = value.ReadInt32();
            NativePlanValue source = ReadPlanValue(value);
            NativePath destination = ReadPath(value);
            PlannedTransferKind kind = (PlannedTransferKind)value.ReadByte();
            TransferDestinationMode mode = (TransferDestinationMode)value.ReadByte();
            PlanResource[] reads = ReadArray(value, ReadResource);
            PlanResource[] writes = ReadArray(value, ReadResource);
            int[] predecessors = ReadArray(value, item => item.ReadInt32());
            return new NativePlannedTransfer(id, source, destination, kind, reads, writes,
                predecessors, ReadSpan(value), mode);
        });
        ExecutionBatch[] batches = ReadArray(reader, value =>
        {
            string endpoint = ReadText(value);
            return new ExecutionBatch(ReadArray(value, item => item.ReadInt32()), endpoint);
        });
        return new(function, start, end, transfers, batches, eliminated, results);
    }

    private static void WritePlan(BinaryWriter writer, NativeExecutionPlan plan)
    {
        writer.Write(plan.Function); writer.Write(plan.Start); writer.Write(plan.End);
        writer.Write(plan.EliminatedTransfers);
        WriteOptionalArray(writer, plan.ResultTransfers, static (output, value) => output.Write(value));
        WriteArray(writer, plan.Transfers, static (output, transfer) =>
        {
            output.Write(transfer.Id); WritePlanValue(output, transfer.Source); WritePath(output, transfer.Destination);
            output.Write((byte)transfer.Kind); output.Write((byte)transfer.Mode);
            WriteArray(output, transfer.Reads, WriteResource);
            WriteArray(output, transfer.Writes, WriteResource);
            WriteArray(output, transfer.Predecessors, static (item, value) => item.Write(value));
            WriteSpan(output, transfer.Span);
        });
        WriteArray(writer, plan.Batches, static (output, batch) =>
        { WriteText(output, batch.Endpoint); WriteArray(output, batch.Transfers, static (item, value) => item.Write(value)); });
    }

    private static void WritePlanValue(BinaryWriter writer, NativePlanValue value)
    {
        Stack<NativePlanValue> pending = new();
        pending.Push(value);
        while (pending.TryPop(out NativePlanValue? current))
        {
            if (current.Kind == NativeKind.Range)
            {
                writer.Write((byte)NativeKind.Range); WriteType(writer, current.ItemType!);
                IReadOnlyList<NativePlanValue> items = current.Items!;
                writer.Write(items.Count);
                for (int index = items.Count - 1; index >= 0; index--) pending.Push(items[index]);
            }
            else if (current.Kind == NativeKind.Path)
            { writer.Write((byte)NativeKind.Path); WritePath(writer, current.Path!.Value); }
            else WriteConstant(writer, current.Literal!);
        }
    }

    private static NativePlanValue ReadPlanValue(BinaryReader reader)
    {
        Stack<PlanRange> pending = new();
        while (true)
        {
            NativeKind kind = (NativeKind)reader.ReadByte();
            NativePlanValue value;
            if (kind == NativeKind.Range)
            {
                TypeRef type = ReadType(reader);
                int count = reader.ReadInt32();
                if (count < 0 || reader.BaseStream.CanSeek
                    && count > reader.BaseStream.Length - reader.BaseStream.Position)
                    throw new MsyncException("Invalid native plan snapshot length");
                if (count != 0) { pending.Push(new(type, count)); continue; }
                value = NativePlanValue.Snapshot([], type);
            }
            else value = kind == NativeKind.Path ? NativePlanValue.FromPath(ReadPath(reader))
                : NativePlanValue.Scalar(ReadConstant(reader, kind));
            while (pending.TryPeek(out PlanRange? parent))
            {
                parent.Items.Add(value);
                if (parent.Items.Count != parent.Expected) break;
                pending.Pop();
                value = NativePlanValue.Snapshot(parent.Items, parent.ItemType);
            }
            if (pending.Count == 0) return value;
        }
    }

    private sealed class PlanRange(TypeRef itemType, int count)
    {
        internal TypeRef ItemType { get; } = itemType;
        internal List<NativePlanValue> Items { get; } = [];
        internal int Expected { get; } = count;
    }

    private static NativePath ReadPath(BinaryReader reader) => new(ReadText(reader), ReadOptional(reader),
        reader.ReadBoolean() ? new NativeConnection((ConnectionKind)reader.ReadByte(), ReadText(reader)) : null,
        ReadOptional(reader), ReadOptional(reader));

    private static void WritePath(BinaryWriter writer, NativePath path)
    {
        WriteText(writer, path.Raw); WriteOptional(writer, path.WorkingDirectory); writer.Write(path.Connection.HasValue);
        if (path.Connection is { } connection) { writer.Write((byte)connection.Kind); WriteText(writer, connection.Address); }
        WriteOptional(writer, path.HomeDirectory); WriteOptional(writer, path.LocalSpelling);
    }

    private static PlanResource ReadResource(BinaryReader reader) => new(ReadText(reader), ReadText(reader), reader.ReadBoolean());
    private static void WriteResource(BinaryWriter writer, PlanResource resource)
    { WriteText(writer, resource.Endpoint); WriteText(writer, resource.Path); writer.Write(resource.Subtree); }

    private static void ValidatePlans(NativeProgram program)
    {
        if (program.ExecutionPlans is not { Count: > 0 } plans) return;
        if (program.Stage != ArtifactStage.Executable) throw new MsyncException("Relocatable native IR cannot contain contextual execution plans");
        foreach (NativeExecutionPlan plan in plans)
        {
            if ((uint)plan.Function >= (uint)program.Functions.Count || plan.Start < 0
                || plan.End < plan.Start || plan.EliminatedTransfers < 0)
                throw new MsyncException("Invalid native execution plan region");
            foreach (int result in plan.ResultTransfers ?? [])
                if ((uint)result >= (uint)plan.Transfers.Count)
                    throw new MsyncException("Native execution plan result refers to a missing transfer");
            for (int index = 0; index < plan.Transfers.Count; index++)
            {
                NativePlannedTransfer transfer = plan.Transfers[index];
                if (transfer.Id != index || !Enum.IsDefined(transfer.Kind) || !Enum.IsDefined(transfer.Mode))
                    throw new MsyncException("Invalid native execution plan transfer", transfer.Span);
                ValidatePath(transfer.Destination, transfer.Span);
                ValidatePlanValue(transfer.Source);
                bool validSource = transfer.Kind switch
                {
                    PlannedTransferKind.WriteText => transfer.Source.Kind == NativeKind.String,
                    PlannedTransferKind.GenericCopy => transfer.Source.Kind is NativeKind.Path or NativeKind.String or NativeKind.Range,
                    PlannedTransferKind.EnsureDirectory => transfer.Source.Kind is NativeKind.Null or NativeKind.Path,
                    _ => transfer.Source.Kind == NativeKind.Path
                };
                if (!validSource) throw new MsyncException("Native transfer source disagrees with its selected operation", transfer.Span);
                HashSet<int> predecessors = [];
                foreach (int predecessor in transfer.Predecessors)
                    if ((uint)predecessor >= (uint)index || !predecessors.Add(predecessor))
                        throw new MsyncException("Native transfer predecessors must identify distinct earlier transfers", transfer.Span);
                ValidateSpan(transfer.Span);
            }
            HashSet<int> batched = [];
            foreach (ExecutionBatch batch in plan.Batches)
                foreach (int transfer in batch.Transfers)
                    if ((uint)transfer >= (uint)plan.Transfers.Count || !batched.Add(transfer))
                        throw new MsyncException("Native execution batch refers to a missing or repeated transfer");
        }
    }

    private static void ValidatePlanValue(NativePlanValue root)
    {
        Stack<NativePlanValue> pending = new();
        pending.Push(root);
        while (pending.TryPop(out NativePlanValue? value))
        {
            switch (value.Kind)
            {
                case NativeKind.Path: ValidatePath(value.Path!.Value); break;
                case NativeKind.Range:
                    ValidateTypeValue(value.ItemType!);
                    foreach (NativePlanValue item in value.Items!)
                    {
                        TypeRef actual = item.Kind == NativeKind.Range ? new(TypeKind.Range, item.ItemType)
                            : new(Kind(item.Kind));
                        if (!value.ItemType!.Accepts(actual)) throw new MsyncException("Native plan snapshot member violates its element type");
                        pending.Push(item);
                    }
                    break;
                default: ValidateConstant(value.Literal!); break;
            }
        }
    }

    private static void ValidatePath(NativePath path, SourceSpan? source = null)
    {
        if (path.Raw is null || path.Connection is { } connection
            && (!Enum.IsDefined(connection.Kind) || connection.Address is null))
            throw new MsyncException("Invalid native Path descriptor", source);
    }
}
