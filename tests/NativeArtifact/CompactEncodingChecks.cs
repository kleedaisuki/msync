using System.Text;
using Msync.Core;
using Msync.Core.Native;
using Msync.Optimization;

internal static class CompactEncodingChecks
{
    private sealed record Section(byte Kind, int LengthOffset, int Start, int Length);

    internal static void Run(Action<bool, string> check)
    {
        const string filename = "repeated-source-𐀀-\ud800.msync";
        SourceSpan source = new(filename, 9, 3);
        NativeInstruction[] code = Enumerable.Repeat(new NativeInstruction(NativeOp.Nop, Source: source), 1000)
            .Concat([new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)]).ToArray();
        NativeProgram program = new([new(NativeKind.Number, Number: 7), new(NativeKind.String, filename)],
            [new("pooled-debug", [new(0)], code, new([], 0, []))], [new(TypeKind.Number)], []);
        byte[] bytes = Encode(program);
        Section[] sections = Sections(bytes);
        Section instructions = sections.Single(section => section.Kind == 9);
        check(instructions.Length == 1005, "Nop is not one byte or code retained inline debug operands");
        check(bytes.Length < 5000, "Repeated source data was not pooled independently of code");
        NativeProgram decoded = NativeArtifact.Read(new MemoryStream(bytes));
        string decodedSource = decoded.Functions[0].Instructions[0].Source!.Value.Source;
        check(decodedSource == filename && ReferenceEquals(decodedSource, decoded.Constants[1].Text), "UTF16 pool lost exact code units or shared identity");
        check(decoded.Functions[0].Instructions.Take(1000).All(instruction => instruction.Source == source), "Sparse debug mapping lost original PCs/spans");
        check(Encode(decoded).SequenceEqual(bytes), "Compact re-encoding is not deterministic");

        NativeInstruction unusual = new(NativeOp.Nop, A: int.MinValue, B: int.MaxValue, C: 0, Type: 0,
            Immediate: long.MinValue, Symbol: "unused\udc00", Inputs: []);
        NativeProgram extensions = program with { Functions = [program.Functions[0] with { Instructions = [unusual, code[^2], code[^1]] }] };
        NativeInstruction roundtrip = NativeArtifact.Read(new MemoryStream(Encode(extensions))).Functions[0].Instructions[0];
        check(roundtrip.A == unusual.A && roundtrip.B == unusual.B && roundtrip.C == 0 && roundtrip.Type == 0 && roundtrip.Immediate == long.MinValue
            && roundtrip.Symbol == unusual.Symbol && roundtrip.Inputs is { Count: 0 }, "Optional operands lost exact signed values/null-versus-empty data");
        NativeProgram plan = Fixtures.Plan();
        NativeProgram extraResult = plan with
        {
            Functions = [plan.Functions[0] with
            { Instructions = plan.Functions[0].Instructions.Select((instruction, pc) => pc == 0 ? instruction with { Result = 0 } : instruction).ToArray() }]
        };
        check(NativeArtifact.Read(new MemoryStream(Encode(extraResult))).Functions[0].Instructions[0].Result == 0,
            "Optional result register outside an opcode's usual schema was lost");

        byte[] revision = bytes.ToArray(); revision[14] = 255;
        Reject(revision, "encoding", check);
        Section header = sections[0];
        byte[] stringIndex = bytes.ToArray(); stringIndex[header.Start] = 127;
        Reject(stringIndex, "pool index", check);
        byte[] sectionTag = bytes.ToArray(); sectionTag[sections[2].LengthOffset - 1] = 2;
        Reject(sectionTag, "section", check);
        byte[] trailing = bytes.ToArray(); trailing[header.LengthOffset]++;
        Reject(trailing, "Unconsumed", check);
        byte[] noHeader = bytes.ToArray(); noHeader[header.LengthOffset] = 0;
        Reject(noHeader, "byte", check);
        Section debug = sections.Single(section => section.Kind == 8);
        using (BinaryReader reader = new(new MemoryStream(bytes)))
        {
            reader.BaseStream.Position = debug.Start; _ = Unsigned(reader);
            byte[] duplicatePc = bytes.ToArray(); duplicatePc[(int)reader.BaseStream.Position] = 0;
            Reject(duplicatePc, "debug instruction", check);
        }
        byte[] badImmediate = bytes.ToArray(); badImmediate[instructions.Start + 1001] = 0x80;
        Reject(badImmediate, "Noncanonical", check);

        foreach (Section section in sections)
        {
            int end = section.Start + section.Length;
            Reject(bytes[..Math.Max(0, end - 1)], "byte", check);
        }
        NativeProgram scalar = Fixtures.Literal(new(NativeKind.Number, Number: 1));
        byte[] tiny = Encode(scalar);
        Section metadata = Sections(tiny).Single(section => section.Kind == 7);
        using (BinaryReader reader = new(new MemoryStream(tiny)))
        {
            reader.BaseStream.Position = metadata.Start;
            _ = Unsigned(reader); _ = Unsigned(reader); _ = Unsigned(reader); _ = Unsigned(reader); _ = Unsigned(reader);
            reader.ReadBoolean();
            int offset = checked((int)reader.BaseStream.Position);
            List<byte> hugeCount = tiny.ToList(); hugeCount.RemoveAt(offset);
            hugeCount.InsertRange(offset, new byte[] { 0xfe, 0xff, 0xff, 0xff, 0x0f });
            hugeCount[metadata.LengthOffset] += 4;
            Reject(hugeCount.ToArray(), "instruction table", check);
        }
    }

    private static byte[] Encode(NativeProgram program)
    {
        using MemoryStream output = new(); NativeArtifact.Write(NativeOptimizer.PlanStorage(program), output); return output.ToArray();
    }

    private static void Reject(byte[] bytes, string message, Action<bool, string> check)
    {
        foreach (Stream input in new Stream[] { new MemoryStream(bytes), new ReadPipe(bytes) })
        {
            using (input)
            {
                try { NativeArtifact.Read(input); throw new InvalidOperationException("Accepted malformed compact encoding"); }
                catch (MsyncException error)
                { check(error.Message.Contains("byte", StringComparison.Ordinal) && error.Message.Contains(message, StringComparison.OrdinalIgnoreCase), error.Message); }
            }
        }
    }

    private static Section[] Sections(byte[] bytes)
    {
        using BinaryReader reader = new(new MemoryStream(bytes), Encoding.Unicode);
        reader.BaseStream.Position = 15;
        uint strings = Unsigned(reader);
        for (int index = 0; index < strings; index++)
        { int length = reader.ReadInt32(); reader.BaseStream.Position += checked(length * 2); }
        uint spans = Unsigned(reader);
        for (int index = 0; index < spans; index++) { _ = Unsigned(reader); reader.BaseStream.Position += 8; }
        uint count = Unsigned(reader);
        List<Section> sections = [];
        for (int index = 0; index < count; index++)
        {
            byte kind = reader.ReadByte(); int lengthOffset = checked((int)reader.BaseStream.Position);
            int length = checked((int)Unsigned(reader)); int start = checked((int)reader.BaseStream.Position);
            sections.Add(new(kind, lengthOffset, start, length)); reader.BaseStream.Position += length;
        }
        return sections.ToArray();
    }

    private static uint Unsigned(BinaryReader reader)
    {
        uint result = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte next = reader.ReadByte(); result |= (uint)(next & 127) << shift;
            if (next < 128) return result;
        }
        throw new InvalidOperationException("Invalid test fixture varint");
    }
}
