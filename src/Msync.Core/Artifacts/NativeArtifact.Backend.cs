namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static void WriteBackend(BinaryWriter writer, NativeBackendCode? backend)
    {
        writer.Write(backend is not null);
        if (backend is null) return;
        WriteArray(writer, backend.Connections, static (output, connection) =>
        { output.Write((byte)connection.Kind); WriteText(output, connection.Address); });
        WriteArray(writer, backend.Functions, static (output, function) =>
        {
            output.Write(function.PureSynchronous); output.Write(function.ContextFree); output.Write(function.ObservableBinary);
            WriteArray(output, function.RawWritable, static (target, value) => target.Write(value));
            WriteArray(output, function.Instructions, static (target, code) =>
            {
                target.Write((byte)code.Op); target.Write(code.Result); target.Write(code.A); target.Write(code.B); target.Write(code.C);
                target.Write(code.Span); target.Write(code.Immediate); target.Write((byte)code.Handler); target.Write(code.Operand);
                target.Write(code.OperandKinds); target.Write(code.NeedsLayoutCheck); target.Write((byte)code.Numbers); target.Write(code.Connection);
            });
        });
    }

    private static NativeBackendCode? ReadBackend(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        NativeConnection[] connections = ReadArray(reader, input => new NativeConnection((ConnectionKind)input.ReadByte(), ReadText(input)));
        NativeBackendFunction[] functions = ReadArray(reader, input =>
        {
            bool synchronous = input.ReadBoolean(), contextFree = input.ReadBoolean(), observable = input.ReadBoolean();
            bool[] raw = ReadArray(input, value => value.ReadBoolean());
            NativeBackendInstruction[] code = ReadArray(input, value => new NativeBackendInstruction(
                (NativeOp)value.ReadByte(), value.ReadInt32(), value.ReadInt32(), value.ReadInt32(), value.ReadInt32(),
                value.ReadInt32(), value.ReadInt64(), (NativeBackendHandler)value.ReadByte(), value.ReadInt32(),
                value.ReadUInt64(), value.ReadBoolean(), (NativeNumberForm)value.ReadByte(), value.ReadInt32()));
            return new NativeBackendFunction(code, raw, synchronous, contextFree, observable);
        });
        return new(functions, connections);
    }
}
