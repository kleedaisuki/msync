using Msync.Core;
using Msync.Runtime;

internal static class BorrowedPathChecks
{
    public static async Task<int> Run(string root)
    {
        int assertions = 0;
        var remote = new ConnectionValue(ConnectionKind.Ssh, "unused");
        var unbound = new PathValue("left/symbolic.md", root, LocalSpelling: "right/native.txt");
        var local = unbound with { Connection = new(ConnectionKind.Local) };
        var qualified = unbound with { Connection = remote };

        Check(PathSemantics.FileName(new("folder/name/", Connection: remote)) == "name",
            "Regex selection's basename discards trailing separators");
        await Compare(new PathValue("folder/name/", root, remote), "filename", OpCode.Equal, "", true, explicitCast: false);
        await Compare(new PathValue("folder/.gitignore", root, remote), "extension", OpCode.Equal, "", true, explicitCast: true);
        await Compare(new PathValue("folder/..", root, remote), "stem", OpCode.Equal, "..", true, explicitCast: false);
        await Compare(new PathValue("folder/name.", root, remote), "extension", OpCode.Equal, ".", true, explicitCast: true);
        await Compare(new PathValue("folder/left\\stem.md", root, remote), "stem", OpCode.Equal, "left\\stem", true, explicitCast: true);
        await Compare(new PathValue("folder/\U00010000", root, remote), "filename", OpCode.Greater, "\uE000", true, explicitCast: false);
        await Compare(new StringValue("folder/left\\stem.md"), "stem", OpCode.Equal,
            OperatingSystem.IsWindows() ? "stem" : "left\\stem", true, explicitCast: true);
        await Compare(new StringValue("folder\\left/other.md\\"), "filename", OpCode.Equal,
            OperatingSystem.IsWindows() ? "" : "other.md\\", true, explicitCast: false);
        await Compare(unbound, "extension", OpCode.Equal,
            OperatingSystem.IsWindows() ? ".txt" : ".md", true, explicitCast: true);
        await Compare(local, "extension", OpCode.Equal, ".md", true, explicitCast: true);
        await Compare(qualified, "extension", OpCode.Equal, ".md", true, explicitCast: true);

        var captured = unbound with { WorkingDirectory = Path.Combine(root, "old"), HomeDirectory = Path.Combine(root, "home") };
        await Owned(captured, "extension", captured.WithRaw(".md", OperatingSystem.IsWindows() ? ".txt" : null));
        await Owned(captured with { LocalSpelling = "other/native.md" }, "extension", captured.WithRaw(".md"));
        await Owned(local, "filename", local.WithRaw("symbolic.md") with { HomeDirectory = root });
        await Owned(qualified, "stem", qualified.WithRaw("symbolic") with { HomeDirectory = root });
        await Owned(new StringValue("folder/left\\leaf.txt"), "filename",
            new PathValue(OperatingSystem.IsWindows() ? "leaf.txt" : "left\\leaf.txt", root, HomeDirectory: root));
        await Owned(new StringValue("folder/"), "filename", new PathValue("", root, HomeDirectory: root));
        return assertions;

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        async Task Compare(Value operand, string native, OpCode comparison, string text, bool expected, bool explicitCast)
        {
            string name = "fs." + native;
            List<Instruction> code = [new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: name)];
            if (explicitCast)
                code.AddRange([new(OpCode.Constant, 2), new(OpCode.NativeCall, 2, Text: "cast")]);
            else
                code.Add(new(OpCode.Convert, Text: "String"));
            code.AddRange([new(OpCode.Constant, 1), new(comparison), new(OpCode.Return)]);

            var result = await Execute(name, code, TypeRef.Bool, [operand, new StringValue(text), new TypeValue(TypeRef.String)]);
            Check(result is BoolValue boolean && boolean.Data == expected,
                $"Borrowed lexical comparison changed {operand} ({name} {comparison} {text})");
        }

        async Task Owned(Value operand, string native, PathValue expected)
        {
            string name = "fs." + native;
            Instruction[] code = [new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: name), new(OpCode.Return)];
            var result = await Execute(name, code, TypeRef.Path, [operand]);
            Check(result is PathValue path && path == expected,
                $"Owned lexical operation changed {operand} ({name}): {result} != {expected}");
        }

        async Task<Value> Execute(string name, IReadOnlyList<Instruction> code, TypeRef resultType, IReadOnlyList<Value> constants)
        {
            Module executable = new(constants, [new("projection", 0, 0, code, resultType)],
                Stage: ArtifactStage.Executable, Context: new(root, root));
            using MemoryStream encoded = new();
            Artifact.Write(executable, encoded);
            encoded.Position = 0;
            var profile = new RuntimeProfile(instructionSites: true);
            var options = new ExecutionOptions(WorkingDirectory: root) { Profile = profile };
            var result = await Interpreter.ExecuteAsync(Artifact.Read(encoded), options);
            var samples = profile.Drain();
            var nativeCall = samples.Single(sample => sample.Name == "vm.native" && sample.Attributes["native.name"] == name);
            Check(nativeCall.Metrics["fast_calls"] == 1, $"{name} bypassed the lexical target");
            Check(samples.Count(sample => sample.Name == "vm.instruction") == code.Count,
                "Lexical projection lost logical instructions");
            return result;
        }
    }
}
