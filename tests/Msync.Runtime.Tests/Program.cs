using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using Msync.Optimization;
using static RuntimeFixture;

if (args.Contains("--files", StringComparer.Ordinal)) { await FileJourneys(); return; }
if (args.Contains("--validation", StringComparer.Ordinal)) { ValidationProperties.Run(); await ValidationProperties.RunRegions(); return; }
if (args.Contains("--profile", StringComparer.Ordinal)) { await RuntimeProfileChecks.RunAsync(); return; }
if (args.Contains("--inline-native", StringComparer.Ordinal)) { await InlineNativeChecks.RunAsync(); return; }
if (args.Contains("--mutable-state", StringComparer.Ordinal)) { await MutableStateChecks.RunCurrentState(); return; }

await Number("let empty = object {}; $std::meta::member_count(empty)", 0);
Check(PathSemantics.Join(new NativePath("left\\", Connection: new(ConnectionKind.Ssh, "host")), "leaf") == "left\\/leaf", "Remote path join erased a POSIX backslash");
foreach (NativeConstant value in new NativeConstant[] { new(NativeKind.Null), new(NativeKind.Bool, Boolean: true) })
{
    NativeProgram regex = new([value, new(NativeKind.String, ".+")], [new("regex-partial", [new(0), new(1), new(2)],
        [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1), new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.RegexMatch), new(NativeOp.Return, A: 2)], new([], 2, []))],
        [new(value.Kind == NativeKind.Null ? TypeKind.Null : TypeKind.Bool), new(TypeKind.String), new(TypeKind.Bool)], []);
    Check(!(await Run(Reload(regex))).AsBool(), "Regex matched a nontext payload");
}
await Number("fn identity(x) { x }; if identity(\"argument\") == \"argument\" { 1 } else { 0 }", 1);
await Number("if \"𐀀\" > \"\" { 1 } else { 0 }", 1);
await Number("if ./𐀀 > ./ { 1 } else { 0 }", 1);
await Number("12 * 3", 36);
NativeProgram arithmetic = new([new(NativeKind.Number, Number: 12), new(NativeKind.Number, Number: 3)],
    [new("folded-arithmetic", [new(0), new(0), new(0)],
        [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1), new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.Multiply), new(NativeOp.Return, A: 2)], new([], 0, []))],
    [new(TypeKind.Number)], []);
NativeProgram folded = NativeOptimizer.Optimize(arithmetic, 3);
Check((await Run(Reload(folded))).AsNumber() == 36, "Native constant folding changed value");
Check(folded.Functions[0].Instructions.All(instruction => instruction.Op != NativeOp.Binary), "Folded result retained executable arithmetic");
await Number("let flag = true; if flag { 42 } else { 0 }", 42);
Random random = new(413);
for (int index = 0; index < 100; index++)
{
    string text = string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => char.ConvertFromUtf32(random.Next(0x20, 0xd7ff))));
    NativeProgram program = new([new(NativeKind.String, text)], [new("unicode", [new(0)],
        [new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)], new([], 0, []))], [new(TypeKind.String)], []);
    NativeProgram decoded = Reload(program);
    Check((await Run(decoded)).AsString() == text, "Binary literal changed Unicode bytes");
    Check(decoded.Stage == ArtifactStage.Relocatable && decoded.Context is null, "Object acquired context");
}
await FileJourneys();
await MutableStateChecks.RunCurrentState();
await InlineNativeChecks.RunAsync();
await RuntimeProfileChecks.RunAsync();
ValidationProperties.Run();
await ValidationProperties.RunRegions();
Console.WriteLine($"PASS native runtime user journeys: {Assertions} assertions");

static async Task FileJourneys()
{
    string root = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    try
    {
        NativeProgram writes = Compile("\"klee\n世界\" -> ./a; ./a -> ./b; ./b -> ./c");
        NativeProgram executable = Reload(Bind(writes, root));
        Check(executable.ExecutionPlans is { Count: > 0 }, "Known local transfers were not contextually planned");
        await Run(executable, new(Jobs: 4));
        Check(await File.ReadAllTextAsync(Path.Combine(root, "c")) == "klee\n世界", "Read-after-write lost bytes");
        string other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
        await Run(executable, new(WorkingDirectory: other));
        Check(!File.Exists(Path.Combine(other, "a")), "Executable relocated frozen cwd");
        await Run(Reload(writes), new(WorkingDirectory: other));
        Check(File.Exists(Path.Combine(other, "c")), "Relocatable object did not bind current cwd");
        NativeProgram lazy = Compile("fn item() { \"lazy\" -> ./lazy-flag; ./lazy-flag }; $std::ranges::empty($std::ranges::take([item()], 0))");
        Check((await Run(Reload(lazy), new(WorkingDirectory: root))).AsBool(), "Lazy take0 is not empty");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "lazy-flag")) == "lazy",
            "take(0) must not suppress literal source initialization");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path.Combine(root, "real")); Directory.CreateSymbolicLink(Path.Combine(root, "alias"), "real");
            await Run(Compile("\"alias-data\" -> ./real/data; ./alias/data -> ./result"), new(WorkingDirectory: root, Jobs: 4));
            Check(await File.ReadAllTextAsync(Path.Combine(root, "result")) == "alias-data", "Symlink resource dependency read stale source");
            NativeProgram process = Compile("/bin/sh(\"-c\", \"printf hello\")");
            NativeProcess result = (await Run(Reload(process), new(WorkingDirectory: root))).AsProcess();
            result = await result.WaitAsync(); Check(result.ExitCode == 0 && result.StandardOutput == "hello", "Path process lost output");
            try { await Run(process, new(WorkingDirectory: root, SpoolLimit: 3)); throw new InvalidOperationException("Spool limit ignored"); }
            catch (MsyncException error) { Check(error.ToString().Contains("spool limit", StringComparison.Ordinal), error.ToString()); }
            NativeProgram deferred = Compile("fn items() { /bin/sh(\"-c\", \"printf x >> deferred-calls\"); [./item] }; let r = items(); $std::ranges::first(r); $std::ranges::first(r)");
            await Run(Reload(deferred), new(WorkingDirectory: root));
            Check(await File.ReadAllTextAsync(Path.Combine(root, "deferred-calls")) == "x", "Ordinary Range factory must execute once");
        }
        List<MsyncException> reports = []; using StringWriter errors = new();
        NativeProgram missing = Compile("./missing -> ./b");
        NativeInterpreter interpreter = new(Reload(missing), new(WorkingDirectory: root, Error: errors) { ReportError = reports.Add });
        await using (interpreter)
        {
            Check(!(await interpreter.ExecuteAsync()).AsBool(), "Failed transfer did not return false");
            Check(interpreter.FailedTransfers == 1 && reports.Count == 1, "Transfer failure accounting or callback lost: failed=" + interpreter.FailedTransfers + ", reports=" + reports.Count + ", fallback=" + errors);
            Check(reports[0].Span is not null && reports[0].ToString().Contains("missing", StringComparison.Ordinal), "Transfer lost source/native context");
            Check(errors.ToString().Length == 0, "Structured reporting duplicated fallback errors");
        }
        Check(interpreter.FailedTransfers == 1, "Disposal counted an already observed failed transfer twice");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "b")) == "klee\n世界", "Failed transfer damaged destination");
        using StringWriter fallbackErrors = new();
        await using NativeInterpreter fallback = new(Reload(missing), new(WorkingDirectory: root, Error: fallbackErrors));
        Check(!(await fallback.ExecuteAsync()).AsBool() && fallback.FailedTransfers == 1,
            "Fallback diagnostic path changed false result or failure accounting");
        Check(fallbackErrors.ToString().Contains("missing", StringComparison.Ordinal), "Fallback stream lost failed source context");

        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tools"));
            await File.WriteAllTextAsync(Path.Combine(root, "tools", "program.exe"), "lookup fixture");
            Environment.SetEnvironmentVariable("PATH", "tools");
            NativeValue found = await Run(Compile("$path @ \"program.exe\""), new(WorkingDirectory: root));
            Check(found.AsPath().Raw == Path.Combine(root, "tools", "program.exe"), "PATH lookup lost explicit extension or captured cwd");
            NativeValue unresolved = await Run(Compile("$path @ \"./future.exe\""), new(WorkingDirectory: root));
            Check(unresolved.AsPath().Raw == Path.Combine(root, "future.exe"), "Explicit path qualification prematurely required existence");
            if (!OperatingSystem.IsWindows())
                Check((await Run(Compile("$std::shell::which(\"program.exe\")"), new(WorkingDirectory: root))).Kind == NativeKind.Null,
                    "shell.which admitted a nonexecutable PATH entry");
        }
        finally { Environment.SetEnvironmentVariable("PATH", previousPath); }

    }
    finally { Directory.Delete(root, true); }
}
