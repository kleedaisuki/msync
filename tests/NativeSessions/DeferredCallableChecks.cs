using Msync.Compiler;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class DeferredCallableChecks
{
    public static async Task Run()
    {
        string previous = Environment.CurrentDirectory;
        string root = Path.GetFullPath(Path.Combine(".temp", "native-deferred-" + Guid.NewGuid().ToString("N")));
        string before = Path.Combine(root, "before"), after = Path.Combine(root, "after");
        Directory.CreateDirectory(before);
        Directory.CreateDirectory(after);
        try
        {
            foreach (int optimization in new[] { 0, 2 })
            {
                Environment.CurrentDirectory = before;
                CompilerSession compiler = new(optimization: optimization);
                await using NativeExecutionSession execution = new(new(Output: TextWriter.Null, Error: TextWriter.Null));
                async Task<NativeValue> Submit(string source)
                {
                    NativeValue result = await execution.ExecuteAsync(compiler.Compile(source));
                    compiler.AcceptSubmission(execution.GetLocalValue);
                    return result;
                }
                await Submit("let operation = fn { ./a }; let delayed = $std::ranges::transform([true], fn(item) { operation() });");
                Environment.CurrentDirectory = after;
                await Submit("operation = fn { ./b };");
                Check(await Submit("$std::ranges::front(delayed)"), Path.Combine(before, "b"), after,
                    "Deferred callback lost its caller context or replacement target.");
                await Submit("let captured = ./b; operation = fn { captured };");
                Check(await Submit("$std::ranges::front(delayed)"), Path.Combine(after, "b"), before,
                    "Deferred callback lost the replacement closure's captured path.");
            }
            foreach (int optimization in new[] { 0, 2 }) await CheckReadOrigins(root, optimization);
            await CheckExplicitDirectory(before, after, takeover: false);
            await CheckExplicitDirectory(before, after, takeover: true);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(root, true);
        }
        Console.WriteLine("native deferred callable replacement, reader origins and directory ownership passed O0/O2");
    }

    private static async Task CheckReadOrigins(string root, int optimization)
    {
        string directory = Path.Combine(root, "read-origins-" + optimization);
        Directory.CreateDirectory(directory);
        CompilerSession compiler = new(optimization: optimization);
        await using NativeExecutionSession execution = new(new(WorkingDirectory: directory));
        await execution.ExecuteAsync(compiler.Compile("""
            $std::io::create_directory(./nested);
            $std::io::write_text("input", "origin
            ");
            let lines = $std::io::read_lines("input");
            let bytes = $std::io::read_bytes("input");
            $std::environment::set_working_directory(./nested);
            $std::io::write_text("input", "changed
            ");
            $std::string::join(lines, "|") -> ./result;
            $std::io::write_bytes(./binary, bytes);
            """));
        string lines = await File.ReadAllTextAsync(Path.Combine(directory, "result"));
        string bytes = await File.ReadAllTextAsync(Path.Combine(directory, "binary"));
        string changed = await File.ReadAllTextAsync(Path.Combine(directory, "nested", "input"));
        if (lines != "origin" || bytes != "origin\n" || changed != "changed\n")
            throw new InvalidOperationException($"Deferred reader origins O{optimization}: lines={lines}, bytes={bytes}, changed={changed}");
        if (execution.WorkingDirectory != Path.Combine(directory, "nested"))
            throw new InvalidOperationException("A directory-changing native call failed to update the execution authority.");
    }

    private static async Task CheckExplicitDirectory(string before, string after, bool takeover)
    {
        Environment.CurrentDirectory = before;
        CompilerSession compiler = new();
        await using NativeExecutionSession execution = new(takeover ? null : new(WorkingDirectory: before));
        if (takeover) execution.ChangeWorkingDirectory(before);
        await execution.ExecuteAsync(compiler.Compile("let retained = ./a;"));
        compiler.AcceptSubmission(execution.GetLocalValue);
        Environment.CurrentDirectory = after;
        Check(await execution.ExecuteAsync(compiler.Compile("./b")), Path.Combine(before, "b"), after,
            "Host ambient changes overrode an explicitly owned session directory.");
    }

    private static void Check(NativeValue value, string expected, string workingDirectory, string message)
    {
        if (Path.GetFullPath(value.AsPath().LocalPath(workingDirectory)) != expected)
            throw new InvalidOperationException(message);
    }
}
