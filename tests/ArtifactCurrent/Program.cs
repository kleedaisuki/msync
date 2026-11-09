using Msync.Optimization;
using Msync.Core;
using Msync.Runtime;

int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

Module constant = new([new PathValue("./input", "captured-cwd", LocalSpelling: "native-input")],
    [new("main", 0, 0, [new(OpCode.Constant), new(OpCode.Return)], TypeRef.Path)]);
using MemoryStream bytes = new();
Artifact.Write(constant, bytes);
byte[] image = bytes.ToArray();
Require(BitConverter.ToUInt16(image, 8) == 4, "Writer did not publish current format 4");
bytes.Position = 0;
Module loaded = Artifact.Read(bytes);
Require(loaded.Constants[0].Equals(constant.Constants[0]), "Roundtrip changed captured path projection");
Require((await Interpreter.ExecuteAsync(loaded)) is PathValue { Raw: "./input", LocalSpelling: "native-input" }, "Current artifact did not execute");
foreach (ushort old in new ushort[] { 1, 2, 3 })
{
    using MemoryStream obsolete = new();
    using (BinaryWriter writer = new(obsolete, System.Text.Encoding.UTF8, leaveOpen: true))
    {
        writer.Write("MSYNCBC\0"u8);
        writer.Write(old);
    }
    obsolete.Position = 0;
    try { Artifact.Read(obsolete); throw new InvalidOperationException($"Accepted obsolete format {old}"); }
    catch (MsyncException error)
    {
        Require(error.Message.Contains($"format {old}", StringComparison.Ordinal), "Lost original format diagnostic");
        Require(error.Message.Contains("Recompile", StringComparison.Ordinal), "Missing recompilation guidance");
    }
}
using MemoryStream invalid = new();
using (BinaryWriter writer = new(invalid, System.Text.Encoding.UTF8, leaveOpen: true))
{
    writer.Write("MSYNCBC\0"u8); writer.Write(Artifact.FormatVersion);
    writer.Write(Artifact.LanguageVersion); writer.Write((byte)0);
}
invalid.Position = 0;
try { Artifact.Read(invalid); throw new InvalidOperationException("Accepted obsolete execution semantics"); }
catch (MsyncException error)
{
    Require(error.Message.Contains("execution semantics", StringComparison.Ordinal), "Invalid semantics failed in unrelated decoder logic");
}
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "artifact-current-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string source = Path.Combine(root, "input");
    string output = Path.Combine(root, "output");
    await File.WriteAllTextAsync(source, "current artifact transfer");
    Module transfer = new([new PathValue(source), new PathValue(output)],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Transfer), new(OpCode.Return)], TypeRef.Bool)]);
    Module executable = Binding.Bind(transfer, new(root, root));
    using MemoryStream bound = new();
    Artifact.Write(executable, bound); bound.Position = 0;
    Require(await Interpreter.ExecuteAsync(Artifact.Read(bound)) is BoolValue { Data: true }, "Executable transfer failed");
    Require(await File.ReadAllTextAsync(output) == "current artifact transfer", "Executable transfer changed bytes");
    File.Delete(source);
    bound.Position = 0;
    using StringWriter errors = new();
    Interpreter missing = new(Artifact.Read(bound), new(Error: errors));
    Require(await missing.ExecuteAsync() is BoolValue { Data: false }, "Missing selected input did not return false");
    Require(missing.FailedTransfers == 1 && errors.ToString().Contains("input", StringComparison.Ordinal), "Selected failure lost accounting or the real source path");
    Require(await File.ReadAllTextAsync(output) == "current artifact transfer", "Failed retransmission changed completed destination bytes");
}
finally { Directory.Delete(root, recursive: true); }
Module singleton = new([new NumberValue(0), new NumberValue(1)],
    [new("main", 0, 1,
    [
        new(OpCode.GetSingleton, 1), new(OpCode.Dup), new(OpCode.IsNull), new(OpCode.JumpIfFalse, 8),
        new(OpCode.Pop), new(OpCode.Constant, 0), new(OpCode.MakeScope, 1, Text: "count"), new(OpCode.SetSingleton, 1),
        new(OpCode.StoreLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "count"),
        new(OpCode.Constant, 1), new(OpCode.Add), new(OpCode.SetMember, Text: "count"), new(OpCode.Pop),
        new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "count"), new(OpCode.Return)
    ], TypeRef.Number)]);
Module sharedPlan = Binding.Bind(singleton, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
Artifact.Validate(sharedPlan);
Interpreter firstRun = new(sharedPlan);
Require(await firstRun.ExecuteAsync() is NumberValue { Data: 1 }, "First executable singleton initialization failed");
Require(await firstRun.ExecuteAsync() is NumberValue { Data: 2 }, "A cached plan skipped selected mutable operations");
Require(await Interpreter.ExecuteAsync(sharedPlan) is NumberValue { Data: 1 }, "Cached executable metadata leaked singleton state into another interpreter");
Console.WriteLine($"PASS: {assertions} current artifact and actual transfer contracts");
