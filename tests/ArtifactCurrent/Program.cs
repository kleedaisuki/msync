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
    try
    {
        Value failed = await Interpreter.ExecuteAsync(Artifact.Read(bound));
        Require(failed is BoolValue { Data: false }, "Missing selected input did not report transfer failure");
    }
    catch (MsyncException error)
    {
        Require(error.ToString().Contains("input", StringComparison.Ordinal), "Missing selected input lost operation context");
    }
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"PASS: {assertions} current artifact and actual transfer contracts");
