using Operands = Msync.Core.Native.NativeOperands;

namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static Operands PresentOperands(NativeInstruction value)
    {
        Operands result = Operands.None;
        if (value.Result != -1) result |= Operands.Result;
        if (value.A != -1) result |= Operands.A;
        if (value.B != -1) result |= Operands.B;
        if (value.C != -1) result |= Operands.C;
        if (value.Type != -1) result |= Operands.Type;
        if (value.Immediate != 0) result |= Operands.Immediate;
        if (value.Symbol is not null) result |= Operands.Symbol;
        if (value.Inputs is not null) result |= Operands.Inputs;
        return result;
    }

    private static void WriteCompactInstruction(CompactWriter writer, NativeInstruction instruction)
    {
        if (!Enum.IsDefined(instruction.Op))
        { writer.Write((byte)0x7f); writer.Write((byte)instruction.Op); return; }
        Operands schema = NativeInstructionFacts.Schema(instruction.Op).Encoded;
        Operands extra = PresentOperands(instruction) & ~schema;
        writer.Write((byte)((byte)instruction.Op | (extra == 0 ? 0 : 0x80)));
        WriteOperands(writer, instruction, schema);
        if (extra == 0) return;
        writer.Write((byte)extra);
        WriteOperands(writer, instruction, extra);
    }

    private static void WriteOperands(CompactWriter writer, NativeInstruction value, Operands fields)
    {
        if ((fields & Operands.Result) != 0) writer.Write(value.Result);
        if ((fields & Operands.A) != 0) writer.Write(value.A);
        if ((fields & Operands.B) != 0) writer.Write(value.B);
        if ((fields & Operands.C) != 0) writer.Write(value.C);
        if ((fields & Operands.Type) != 0) writer.Write(value.Type);
        if ((fields & Operands.Immediate) != 0) writer.Immediate(value.Immediate);
        if ((fields & Operands.Symbol) != 0) writer.Symbol(value.Symbol);
        if ((fields & Operands.Inputs) != 0) writer.Inputs(value.Inputs);
    }

    private struct DecodedOperands
    {
        internal int Result, A, B, C, Type;
        internal long Immediate;
        internal string? Symbol;
        internal int[]? Inputs;
    }

    private static NativeInstruction ReadCompactInstruction(CompactReader reader, SourceSpan? source)
    {
        byte tag = reader.ReadByte();
        if (tag == 0x7f) throw new MsyncException($"Unknown native opcode {reader.ReadByte()}");
        NativeOp op = (NativeOp)(tag & 0x7f);
        Operands schema = NativeInstructionFacts.Schema(op).Encoded;
        DecodedOperands value = new() { Result = -1, A = -1, B = -1, C = -1, Type = -1 };
        ReadOperands(reader, ref value, schema);
        if ((tag & 0x80) != 0)
        {
            Operands extra = (Operands)reader.ReadByte();
            if (extra == 0 || (extra & schema) != 0) throw new MsyncException("Invalid native IR extended operand mask");
            ReadOperands(reader, ref value, extra);
        }
        return new(op, value.Result, value.A, value.B, value.C, value.Type, value.Immediate, value.Symbol, value.Inputs, source);
    }

    private static void ReadOperands(CompactReader reader, ref DecodedOperands value, Operands fields)
    {
        if ((fields & Operands.Result) != 0) value.Result = reader.ReadInt32();
        if ((fields & Operands.A) != 0) value.A = reader.ReadInt32();
        if ((fields & Operands.B) != 0) value.B = reader.ReadInt32();
        if ((fields & Operands.C) != 0) value.C = reader.ReadInt32();
        if ((fields & Operands.Type) != 0) value.Type = reader.ReadInt32();
        if ((fields & Operands.Immediate) != 0) value.Immediate = reader.Immediate();
        if ((fields & Operands.Symbol) != 0) value.Symbol = reader.Symbol();
        if ((fields & Operands.Inputs) != 0) value.Inputs = reader.Inputs();
    }
}
