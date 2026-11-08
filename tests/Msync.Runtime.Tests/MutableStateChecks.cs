using System.Text;
using Msync.Core;
using Msync.Runtime;

internal static class MutableStateChecks
{
    public static async Task RunCompatibility()
    {
        using MemoryStream bytes = VersionTwoSnapshotCapture();
        Module old = Artifact.Read(bytes);
        foreach (int level in new[] { 0, 3 })
        {
            Module optimized = Optimizer.Optimize(old, level);
            Value relocatable = await Interpreter.ExecuteAsync(optimized);
            Equal("before", relocatable, $"format 2 closure capture at O{level}");
            Module bound = Binding.Bind(optimized, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
            Value executable = await Interpreter.ExecuteAsync(bound);
            Equal("before", executable, $"bound format 2 closure capture at O{level}");
        }
        await ConstantIsolation();
        Console.WriteLine("PASS: four old-format snapshot captures and fresh mutable Scope constants");
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
