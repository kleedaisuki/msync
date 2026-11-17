using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class BorrowedPathChecks
{
    public static async Task<int> Run(string root)
    {
        int assertions = 0;
        NativeConnection remote = new(ConnectionKind.Ssh, "unused");
        NativePath unbound = new("left/symbolic.md", root, HomeDirectory: root, LocalSpelling: "right/native.txt");
        NativePath local = unbound with { Connection = new(ConnectionKind.Local) };
        NativePath qualified = unbound with { Connection = remote };
        Check(PathSemantics.FileName(new("folder/name/", Connection: remote)) == "name", "Regex basename discards trailing separators");
        await Compare(new NativePath("folder/name/", root, remote), "filename", NativeBinary.Equal, "", false);
        await Compare(new NativePath("folder/.gitignore", root, remote), "extension", NativeBinary.Equal, "", true);
        await Compare(new NativePath("folder/..", root, remote), "stem", NativeBinary.Equal, "..", false);
        await Compare(new NativePath("folder/name.", root, remote), "extension", NativeBinary.Equal, ".", true);
        await Compare(new NativePath("folder/left\\stem.md", root, remote), "stem", NativeBinary.Equal, "left\\stem", true);
        await Compare(new NativePath("folder/\U00010000", root, remote), "filename", NativeBinary.Greater, "\uE000", false);
        await Compare(NativeValue.FromString("folder/left\\stem.md"), "stem", NativeBinary.Equal, OperatingSystem.IsWindows() ? "stem" : "left\\stem", true);
        await Compare(NativeValue.FromString("folder\\left/other.md\\"), "filename", NativeBinary.Equal, OperatingSystem.IsWindows() ? "" : "other.md\\", false);
        await Compare(unbound, "extension", NativeBinary.Equal, OperatingSystem.IsWindows() ? ".txt" : ".md", true);
        await Compare(local, "extension", NativeBinary.Equal, ".md", true);
        await Compare(qualified, "extension", NativeBinary.Equal, ".md", true);

        NativePath captured = unbound with { WorkingDirectory = Path.Combine(root, "old"), HomeDirectory = Path.Combine(root, "home") };
        await Owned(captured, "extension", captured.WithRaw(".md", OperatingSystem.IsWindows() ? ".txt" : null));
        await Owned(captured with { LocalSpelling = "other/native.md" }, "extension", captured.WithRaw(".md"));
        await Owned(local, "filename", local.WithRaw("symbolic.md") with { HomeDirectory = root });
        await Owned(qualified, "stem", qualified.WithRaw("symbolic") with { HomeDirectory = root });
        await Owned(new NativePath("folder/leaf.txt", root), "filename", new("leaf.txt", root));
        await Owned(NativeValue.FromString("folder/left\\leaf.txt"), "filename",
            new(OperatingSystem.IsWindows() ? "leaf.txt" : "left\\leaf.txt", root, HomeDirectory: root));
        await Owned(NativeValue.FromString("folder/"), "filename", new("", root, HomeDirectory: root));
        return assertions;

        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        async Task Compare(NativeValue operand, string native, NativeBinary comparison, string text, bool explicitCast)
        {
            NativeProgram program = new([NativeFixture.Constant(operand), new(NativeKind.String, text)],
                [new("projection", [new(0), new(1), new(2), new(2), new(3)],
                    [new(NativeOp.Constant, 0, 0), new(NativeOp.CallNative, 1, Symbol: "fs." + native, Inputs: [0]),
                     new(NativeOp.Convert, 2, 1, Type: 2, Immediate: explicitCast ? 1 : 0), new(NativeOp.Constant, 3, 1),
                     new(NativeOp.Binary, 4, 2, 3, Immediate: (long)comparison), new(NativeOp.Return, A: 4)], new([], 3, []))],
                [new(operand.Type.Kind), new(TypeKind.Path), new(TypeKind.String), new(TypeKind.Bool)], []);
            NativeValue result = await Execute(program, "fs." + native);
            Check(result.AsBool(), $"Lexical comparison changed {operand} ({native} {comparison} {text})");
        }

        async Task Owned(NativeValue operand, string native, NativePath expected)
        {
            NativeProgram program = new([NativeFixture.Constant(operand)],
                [new("projection", [new(0), new(1)], [new(NativeOp.Constant, 0, 0),
                    new(NativeOp.CallNative, 1, Symbol: "fs." + native, Inputs: [0]), new(NativeOp.Return, A: 1)], new([], 1, []))],
                [new(operand.Type.Kind), new(TypeKind.Path)], []);
            NativeValue result = await Execute(program, "fs." + native);
            Check(result.AsPath() == expected, $"Owned lexical operation changed {operand} ({native}): {System.Text.Json.JsonSerializer.Serialize(result.AsPath())} != {System.Text.Json.JsonSerializer.Serialize(expected)}");
        }

        async Task<NativeValue> Execute(NativeProgram program, string name)
        {
            RuntimeProfile profile = new(instructionSites: true);
            BindingContext binding = new(root, root);
            NativeProgram contextual = NativeFixture.WithStorage(program with { Stage = ArtifactStage.Executable, Context = binding });
            // The check measures the actual lexical builtin, not a contextualized constant.
            NativeProgram residual = NativeBackendCompiler.Compile(contextual, new(null, binding));
            await using NativeInterpreter interpreter = new(NativeFixture.Codec(residual), new(WorkingDirectory: root) { Profile = profile });
            NativeValue result = await interpreter.ExecuteAsync();
            IReadOnlyList<RuntimeProfileSample> samples = profile.Drain();
            RuntimeProfileSample call = samples.Single(sample => sample.Name == "vm.native" && sample.Attributes["native.name"] == name);
            Check(call.Metrics["calls"] == 1, $"{name} did not execute exactly once");
            Check(samples.Count(sample => sample.Name == "vm.instruction") == program.Functions[0].Instructions.Count,
                "Lexical projection lost logical instruction sites");
            return result;
        }
    }
}
