using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class EditorSessionChecks
{
    public static async Task RunProcessMembers()
    {
        string executable = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : "/bin/sh";
        string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        foreach (int code in new[] { 0, 7 })
        {
            string[] arguments = OperatingSystem.IsWindows() ? ["/d", "/c", $"exit /b {code}"] : ["-c", $"exit {code}"];
            string source = $"let process = {Quote(executable)}({string.Join(", ", arguments.Select(Quote))}); "
                + $"process::success == {(code == 0 ? "true" : "false")} and process::exit_code == {code} "
                + "and process::success == $std::process::success(process) "
                + "and process::stdout == \"\" and process::stderr == \"\"";
            Module module = Msync.Compiler.Compiler.Compile(source, "process-members.msync");
            Check(await Interpreter.ExecuteAsync(module) is BoolValue { Data: true }, "Process member results disagree with the native child exit status");
        }
        Console.WriteLine("actual native Process fields and success accessor match exit 0/7");
    }

    public static async Task Run()
    {
        string original = Environment.CurrentDirectory;
        string? home = Environment.GetEnvironmentVariable("HOME");
        string? profile = Environment.GetEnvironmentVariable("USERPROFILE");
        string root = Path.Combine(original, ".temp", "editor-session-" + Guid.NewGuid().ToString("N"));
        string before = Path.Combine(root, "before");
        string after = Path.Combine(root, "after");
        Directory.CreateDirectory(Path.Combine(before, "folder"));
        Directory.CreateDirectory(Path.Combine(after, "folder"));
        Directory.CreateDirectory(Path.Combine(before, "home", "dotfiles"));
        Directory.CreateDirectory(Path.Combine(after, "home", "dotfiles"));
        await File.WriteAllTextAsync(Path.Combine(before, "folder", "old-entry"), "before");
        await File.WriteAllTextAsync(Path.Combine(after, "folder", "new-entry"), "after");
        try
        {
            Environment.CurrentDirectory = before;
            Environment.SetEnvironmentVariable("HOME", Path.Combine(before, "home"));
            Environment.SetEnvironmentVariable("USERPROFILE", Path.Combine(before, "home"));
            CompilerSession compiler = new();
            ExecutionSession execution = new();
            async Task<Value> Submit(string source)
            {
                Value value = await execution.ExecuteAsync(compiler.Compile(source));
                compiler.AcceptSubmission();
                return value;
            }
            await Submit("""
                let folder = ./folder;
                let home = ~/dotfiles;
                scope Config { location: folder, remote: ssh://forge };
                fn captured() { folder };
                fn make(value) { fn() { value } };
                let returned = make(folder);
                fn identity(value) { value };
                """);
            string[] bindings = compiler.BindingNames.Order(StringComparer.Ordinal).ToArray();
            Environment.CurrentDirectory = after;
            Environment.SetEnvironmentVariable("HOME", Path.Combine(after, "home"));
            Environment.SetEnvironmentVariable("USERPROFILE", Path.Combine(after, "home"));
            Check(compiler.AnalyzeEditor("captured()/").At("captured()/".Length).Receiver?.KnownPath is null,
                "an unobserved closure capture invented a physical working-directory projection");
            foreach (string expression in new[] { "folder/", "Config::location/", "captured()/", "returned()/" })
            {
                EditorAnalysis analysis = compiler.AnalyzeEditor(expression, execution.GetLocalValue);
                PathValue path = analysis.At(expression.Length).Receiver?.KnownPath
                    ?? throw new InvalidOperationException("accepted Path hint missing: " + expression);
                Check(Path.GetFullPath(path.LocalPath(after)) == Path.Combine(before, "folder"), "accepted Path lost its captured working directory: " + expression);
                Check(File.Exists(Path.Combine(path.LocalPath(after), "old-entry")) && !File.Exists(Path.Combine(path.LocalPath(after), "new-entry")),
                    "completion would enumerate the new cwd instead of the captured directory: " + expression);
            }
            const string homeExpression = "home/";
            PathValue capturedHome = compiler.AnalyzeEditor(homeExpression, execution.GetLocalValue).At(homeExpression.Length).Receiver!.KnownPath!;
            Check(capturedHome.LocalPath(after) == Path.Combine(before, "home", "dotfiles"), "accepted Path lost its captured home directory");
            const string editing = "identity(\"probe only\"); fn draft(parameter: Path) { Config::";
            EditorAnalysis snapshot = compiler.AnalyzeEditor(editing, execution.GetLocalValue);
            var context = snapshot.At(editing.Length);
            Check(context.Receiver?.Members?.Single(item => item.Name == "remote").Endpoint?.Address == "forge", "accepted Scope Connection member disappeared");
            string signature = context.Bindings.Single(item => item.Name == "identity").Signature;
            Check(compiler.BindingNames.Order(StringComparer.Ordinal).SequenceEqual(bindings), "editing changed accepted bindings");
            Check(await Submit("identity(true)") is BoolValue { Data: true }, "a new real Bool specialization failed after an editor-only String probe");
            Check(snapshot.At(editing.Length).Bindings.Single(item => item.Name == "identity").Signature == signature,
                "an existing analysis snapshot observed later live template signature mutations");
            _ = compiler.AnalyzeEditor("identity(./future); fn draft() { Config::", execution.GetLocalValue);
            Check(await Submit("identity(42)") is NumberValue { Data: 42 }, "a later real specialization reused an editor function-table identity");
            Check(await Submit("captured()") is PathValue result && Path.GetFullPath(result.LocalPath(after)) == Path.Combine(before, "folder"),
                "editor capture metadata changed the actual closure's retained path");
            Console.WriteLine("actual REPL editor snapshots preserve cwd/home/captures and live specialization identities");
        }
        finally
        {
            Environment.CurrentDirectory = original;
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("USERPROFILE", profile);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
