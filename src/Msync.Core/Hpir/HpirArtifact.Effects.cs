namespace Msync.Core.Hpir;

public static partial class HpirArtifact
{
    private static void WriteEffects(BinaryWriter writer, HpirProgram program)
    {
        if (program.Stage == HpirStage.Relocatable)
        {
            WriteArray(writer, program.TransferInputs, static (output, input) =>
            {
                output.Write((byte)input.Kind);
                output.Write(input.Path is not null);
                if (input.Path is { } path) WritePath(output, path);
                WriteOptional(output, input.Text);
                WritePath(output, input.Destination);
                WriteOptional(output, input.Pattern);
                WriteOptionalSource(output, input.Source);
            });
        }
        else
        {
            WriteArray(writer, program.PreparedTransfers, static (output, transfer) =>
            {
                output.Write((byte)transfer.Kind);
                WriteArray(output, transfer.Paths, static (binary, path) => WriteText(binary, path));
                WriteOptional(output, transfer.Text);
                WriteText(output, transfer.Destination);
                WriteOptional(output, transfer.Pattern);
                WriteOptionalSource(output, transfer.Source);
                WriteOffsets(output, transfer.Predecessors);
            });
        }
    }

    private static HpirProgram ReadEffects(ImageReader reader, HpirProgram program)
    {
        if (program.Stage == HpirStage.Relocatable)
        {
            var inputs = ReadArray(reader, static input => new HpirTransferInput((HpirTransferSource)input.ReadByte(),
                input.ReadBoolean() ? ReadPath(input) : null, ReadOptional(input), ReadPath(input), ReadOptional(input),
                ReadOptionalSource(input)), 10);
            return program with { TransferInputs = inputs };
        }
        var prepared = ReadArray(reader, static input => new HpirPreparedTransfer((HpirTransferSource)input.ReadByte(),
            ReadArray(input, ReadText, 4), ReadOptional(input), ReadText(input), ReadOptional(input),
            ReadOptionalSource(input)) { Predecessors = ReadOffsets(input) }, 16);
        return program with { PreparedTransfers = prepared };
    }

    private static BindingContext? ReadContext(ImageReader reader) => reader.ReadBoolean()
        ? new(ReadText(reader), ReadText(reader), ReadOptional(reader)) : null;

    private static void WriteContextChecks(BinaryWriter writer, IReadOnlyList<HpirContextCheck> checks) =>
        WriteArray(writer, checks, static (output, check) =>
        {
            output.Write((byte)check.Operation);
            WritePath(output, check.Path);
            WriteOptionalSource(output, check.Source);
            output.Write((byte)(check.Value is not { } value ? 0 : value ? 2 : 1));
        });

    private static HpirContextCheck[] ReadContextChecks(ImageReader reader) =>
        ReadArray(reader, static input =>
        {
            var operation = (HpirFileMetadata)input.ReadByte();
            var path = ReadPath(input);
            var source = ReadOptionalSource(input);
            bool? value = input.ReadByte() switch
            {
                0 => null,
                1 => false,
                2 => true,
                _ => throw new MsyncException("Invalid HPIR contextual predicate")
            };
            return new HpirContextCheck(operation, path, source, value);
        }, 8);

    private static void WriteContext(BinaryWriter writer, BindingContext? context)
    {
        writer.Write(context is not null);
        if (context is null) return;
        WriteText(writer, context.WorkingDirectory);
        WriteText(writer, context.HomeDirectory);
        WriteOptional(writer, context.SourceFile);
    }

    private static HpirLocalPath ReadPath(ImageReader reader) => new(ReadText(reader), ReadOptional(reader));

    private static void WritePath(BinaryWriter writer, HpirLocalPath path)
    {
        WriteText(writer, path.Raw);
        WriteOptional(writer, path.WindowsSpelling);
    }

    private static string? ReadOptional(ImageReader reader) => reader.ReadBoolean() ? ReadText(reader) : null;

    private static void WriteOptional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) WriteText(writer, value);
    }

    private static SourceSpan? ReadOptionalSource(ImageReader reader) => reader.ReadBoolean()
        ? new(ReadText(reader), reader.ReadInt32(), reader.ReadInt32()) : null;

    private static void WriteOptionalSource(BinaryWriter writer, SourceSpan? span)
    {
        writer.Write(span.HasValue);
        if (span is not { } source) return;
        WriteText(writer, source.Source);
        writer.Write(source.Start);
        writer.Write(source.Length);
    }
}
