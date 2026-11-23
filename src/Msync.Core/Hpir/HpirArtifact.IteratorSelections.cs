namespace Msync.Core.Hpir;

public static partial class HpirArtifact
{
    private static void WriteIteratorSelections(BinaryWriter writer, IReadOnlyList<HpirIteratorSelection> selections) =>
        WriteArray(writer, selections, static (output, selection) =>
        {
            WritePath(output, selection.Directory);
            WriteText(output, selection.Pattern);
            output.Write(selection.ExplicitLocal);
            WriteOptionalSource(output, selection.Source);
            output.Write(selection.Members is not null);
            if (selection.Members is { } members) WriteArray(output, members, WritePath);
        });

    private static HpirIteratorSelection[] ReadIteratorSelections(ImageReader reader) =>
        ReadArray(reader, static input =>
        {
            HpirLocalPath directory = ReadPath(input);
            string pattern = ReadText(input);
            bool local = input.ReadBoolean();
            SourceSpan? source = ReadOptionalSource(input);
            var members = input.ReadBoolean() ? ReadArray(input, ReadPath, 5) : null;
            return new HpirIteratorSelection(directory, pattern, local, source, members);
        }, 12);
}
