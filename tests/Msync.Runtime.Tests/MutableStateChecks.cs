using System.Text;
using Msync.Core;
using Msync.Runtime;

internal static class MutableStateChecks
{
    public static async Task RunCurrentState()
    {
        using MemoryStream bytes = VersionTwoSnapshotCapture();
        try { Artifact.Read(bytes); throw new InvalidOperationException("Obsolete snapshot-capture artifact was accepted"); }
        catch (MsyncException error)
        {
            if (!error.Message.Contains("format 2", StringComparison.Ordinal) || !error.Message.Contains("Recompile", StringComparison.Ordinal))
                throw new InvalidOperationException("Obsolete artifact did not explain how to migrate", error);
        }
        Module current = new([new StringValue("before"), new StringValue("after")],
            [new("main", 0, 2,
            [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1),
             new(OpCode.InitializeLocal, 1), new(OpCode.Constant, 1), new(OpCode.StoreLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Call), new(OpCode.Return)], TypeRef.String),
             new("read", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.String, CaptureCount: 1)]);
        foreach (int level in new[] { 0, 3 })
        {
            Module optimized = Optimizer.Optimize(current, level);
            using MemoryStream encoded = new();
            Artifact.Write(optimized, encoded); encoded.Position = 0;
            optimized = Artifact.Read(encoded);
            Value relocatable = await Interpreter.ExecuteAsync(optimized);
            Equal("after", relocatable, $"current closure capture at O{level}");
            Module bound = Binding.Bind(optimized, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
            Value executable = await Interpreter.ExecuteAsync(bound);
            Equal("after", executable, $"bound current closure capture at O{level}");
        }
        await ConstantIsolation();
        Console.WriteLine("PASS: obsolete artifact rejection, four current live captures and fresh mutable Scope constants");
    }

    private static async Task ConstantIsolation()
    {
        ScopeValue nested = new(new Dictionary<string, Value> { ["value"] = new StringValue("before") });
        ScopeValue template = new(new Dictionary<string, Value> { ["nested"] = nested });
        Module module = new([template], [new("main", 0, 0,
            [new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.Scope)]);
        ScopeValue first = (ScopeValue)await Interpreter.ExecuteAsync(module);
        ScopeValue firstNested = (ScopeValue)first.Members["nested"];
        firstNested.SetMember("value", new StringValue("changed"));
        ScopeValue second = (ScopeValue)await Interpreter.ExecuteAsync(module);
        if (ReferenceEquals(first, second) || ReferenceEquals(firstNested, second.Members["nested"]))
            throw new InvalidOperationException("Separate module executions shared mutable Scope storage");
        Equal("before", ((ScopeValue)second.Members["nested"]).Members["value"], "second Scope instance");
        Equal("before", nested.Members["value"], "constant template");
    }

    private static void Equal(string expected, Value actual, string context)
    {
        if (actual is not StringValue { Data: var text } || text != expected)
            throw new InvalidOperationException($"{context}: expected '{expected}', received '{actual}'");
    }

    private static MemoryStream VersionTwoSnapshotCapture()
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            // This fixture is emitted independently of the current Artifact writer and
            // opcode enum: the old object captures a value, then updates its source slot.
            writer.Write("MSYNCBC\0"u8);
            writer.Write((ushort)2);
            writer.Write("0.8.0");
            writer.Write((byte)0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(false);
            writer.Write(2);
            writer.Write((byte)2); writer.Write("before");
            writer.Write((byte)2); writer.Write("after");
            writer.Write(2);
            Function(writer, "main", 2, 0,
            [
                (0, 0, 0), (5, 0, 0), (4, 0, 0), (13, 1, 1),
                (5, 1, 0), (0, 1, 0), (5, 0, 0), (4, 1, 0),
                (11, 0, 0), (12, 0, 0)
            ]);
            Function(writer, "snapshot", 1, 1, [(4, 0, 0), (12, 0, 0)]);
        }
        stream.Position = 0;
        return stream;
    }

    private static void Function(BinaryWriter writer, string name, int locals, int captures,
        IReadOnlyList<(byte Op, int A, int B)> instructions)
    {
        writer.Write(name);
        writer.Write(0);
        writer.Write(locals);
        writer.Write(captures);
        writer.Write((ushort)4);
        writer.Write(false);
        writer.Write(false);
        writer.Write(instructions.Count);
        foreach (var (op, a, b) in instructions)
        {
            writer.Write(op); writer.Write(a); writer.Write(b);
            writer.Write(false); writer.Write(false);
        }
    }
}
