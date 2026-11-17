using Msync.Core.Llvm;

namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static void WriteSsaBodies(CompactWriter writer, IReadOnlyList<NativeFunctionCode> functions, CompilationTrace? trace)
    {
        int count = functions.Count(function => function.SsaBody is not null);
        writer.Unsigned((uint)count);
        for (int id = 0; id < functions.Count; id++)
        {
            if (functions[id].SsaBody is not { } body) continue;
            writer.Unsigned((uint)id);
            writer.Unsigned((uint)body.Parameters);
            writer.Write((byte)body.Result);
            writer.Unsigned((uint)body.Values);
            foreach (NativeKind kind in body.Kinds) writer.Write((byte)kind);
            writer.Unsigned((uint)body.Literals.Length);
            foreach (NativeSsaLiteral literal in body.Literals)
            {
                writer.Unsigned((uint)literal.Value);
                writer.Write(literal.Bits);
            }
            writer.Unsigned((uint)body.Blocks.Length);
            foreach (NativeSsaBlock block in body.Blocks) WriteBlock(writer, block);
            trace?.Metric("ssa.artifact.blocks_written", body.Blocks.Length);
        }
        trace?.Metric("ssa.artifact.functions_written", count);
    }

    private static void WriteBlock(CompactWriter writer, NativeSsaBlock block)
    {
        writer.Unsigned((uint)block.Operations.Length);
        foreach (NativeSsaInstruction op in block.Operations)
        {
            writer.Write((ushort)op.Op);
            writer.Write(op.Out);
            writer.Write(op.A);
            writer.Write(op.B);
            writer.Write(op.C);
            writer.Write((byte)op.Predicate);
            if (op.Op == LlvmOpcode.Call)
            {
                writer.Unsigned((uint)op.Target);
                writer.Unsigned((uint)op.Arguments!.Length);
                foreach (int argument in op.Arguments) writer.Unsigned((uint)argument);
            }
        }
        writer.Write(block.Value);
        writer.Write((byte)(block.False is not null ? 2 : block.True is not null ? 1 : 0));
        if (block.True is { } yes) WriteEdge(writer, yes);
        if (block.False is { } no) WriteEdge(writer, no);
    }

    private static void WriteEdge(CompactWriter writer, NativeSsaEdge edge)
    {
        writer.Unsigned((uint)edge.Target);
        writer.Unsigned((uint)edge.Moves.Length);
        foreach (NativeSsaMove move in edge.Moves)
        {
            writer.Unsigned((uint)move.Out);
            writer.Unsigned((uint)move.In);
        }
    }

    private static int ReadSsaBodies(CompactReader reader, NativeFunctionCode[] functions, NativeCodeType[] types,
        CompilationTrace? trace)
    {
        int count = SsaCount(reader, functions.Length);
        int previous = -1;
        for (int item = 0; item < count; item++)
        {
            int id = SsaCount(reader, functions.Length - 1);
            if (id <= previous) throw new MsyncException("Unordered whole-function SSA body");
            previous = id;
            NativeSsaBody body = ReadSsaBody(reader);
            NativeSsaValidation.Validate(body, functions[id], types);
            functions[id] = functions[id] with { SsaBody = body };
            trace?.Metric("ssa.artifact.blocks_read", body.Blocks.Length);
        }
        NativeSsaValidation.ValidateLinks(functions);
        trace?.Metric("ssa.artifact.functions_read", count);
        return count;
    }

    private static NativeSsaBody ReadSsaBody(CompactReader reader)
    {
        int parameters = SsaCount(reader, NativeSsaBody.MaxValues);
        NativeKind result = (NativeKind)reader.ReadByte();
        NativeKind[] kinds = new NativeKind[SsaCount(reader, NativeSsaBody.MaxValues)];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = (NativeKind)reader.ReadByte();
        NativeSsaLiteral[] literals = new NativeSsaLiteral[SsaCount(reader, kinds.Length)];
        for (int i = 0; i < literals.Length; i++)
            literals[i] = new(SsaCount(reader, kinds.Length - 1), reader.ReadUInt64());
        NativeSsaBlock[] blocks = new NativeSsaBlock[SsaCount(reader, NativeSsaBody.MaxBlocks)];
        int remainingOperations = kinds.Length;
        for (int i = 0; i < blocks.Length; i++)
        {
            NativeSsaInstruction[] ops = new NativeSsaInstruction[SsaCount(reader, remainingOperations)];
            remainingOperations -= ops.Length;
            for (int op = 0; op < ops.Length; op++)
            {
                NativeSsaInstruction instruction = new((LlvmOpcode)reader.ReadUInt16(), reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadInt32(), reader.ReadInt32(), (LlvmCmpPredicate)reader.ReadByte());
                if (instruction.Op == LlvmOpcode.Call)
                {
                    int target = SsaCount(reader, int.MaxValue);
                    int[] arguments = new int[SsaCount(reader, NativeSsaBody.MaxValues)];
                    for (int arg = 0; arg < arguments.Length; arg++) arguments[arg] = SsaCount(reader, kinds.Length - 1);
                    instruction = instruction with { Target = target, Arguments = arguments };
                }
                ops[op] = instruction;
            }
            int value = reader.ReadInt32();
            blocks[i] = reader.ReadByte() switch
            {
                0 => new(ops, value),
                1 => new(ops, value, ReadEdge(reader, kinds.Length, blocks.Length)),
                2 => new(ops, value, ReadEdge(reader, kinds.Length, blocks.Length), ReadEdge(reader, kinds.Length, blocks.Length)),
                _ => throw new MsyncException("Invalid whole-function SSA terminator")
            };
        }
        return new(kinds, parameters, result, literals, blocks);
    }

    private static NativeSsaEdge ReadEdge(CompactReader reader, int values, int blocks)
    {
        int target = SsaCount(reader, blocks - 1);
        NativeSsaMove[] moves = new NativeSsaMove[SsaCount(reader, values)];
        for (int i = 0; i < moves.Length; i++)
            moves[i] = new(SsaCount(reader, values - 1), SsaCount(reader, values - 1));
        return new(target, moves);
    }

    private static int SsaCount(CompactReader reader, int max)
    {
        uint count = reader.Unsigned();
        if (max < 0 || count > max) throw new MsyncException("Whole-function SSA index or count is outside its section");
        return (int)count;
    }
}
