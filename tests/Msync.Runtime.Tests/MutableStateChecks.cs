using Msync.Compiler;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using static RuntimeFixture;

internal static class MutableStateChecks
{
    internal static async Task RunCurrentState()
    {
        foreach (int level in new[] { 0, 3 })
        {
            NativeProgram capture = Compile("let value = \"before\"; let read = fn() { value }; value = \"after\"; read()", level);
            foreach (NativeProgram image in new[] { Reload(capture), Reload(Bind(capture, Root)) })
                Check((await Run(image)).AsString() == "after", "Source-free closure lost live captured location");
            NativeProgram fresh = Reload(Compile("object { nested: object { value: \"before\" } }", level));
            foreach (NativeProgram image in new[] { fresh, Reload(Bind(fresh, Root)) })
            {
                NativeAddress first = (await Run(image)).AsAddress();
                NativeAddress nested = first.Field(0).Load().AsAddress();
                nested.Field(0).Store(NativeValue.FromString("changed"));
                NativeAddress second = (await Run(image)).AsAddress();
                Check(first != second && nested != second.Field(0).Load().AsAddress(), "Separate executions shared mutable allocation");
                Check(second.Field(0).Load().AsAddress().Field(0).Load().AsString() == "before", "Fresh initializer replayed mutated storage");
                Check(image.Constants.All(value => value.Kind != NativeKind.Address), "Mutable allocation was serialized as a portable constant");
            }
        }
        string root = Path.Combine(Root, "session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            NativeCompilerSession compiler = new("runtime-session.msync", 0);
            await using NativeExecutionSession session = new(new(WorkingDirectory: root));
            await session.ExecuteAsync(compiler.Compile("fn item() { \"lazy\" -> ./lazy-flag; ./lazy-flag }; let sequence = [item()];"));
            compiler.AcceptSubmission(session.GetLocalValue);
            Check(await File.ReadAllTextAsync(Path.Combine(root, "lazy-flag")) == "lazy", "Stored session Range must initialize immediately");
            NativeValue value = await session.ExecuteAsync(compiler.Compile("$std::ranges::first(sequence)"));
            compiler.AcceptSubmission(session.GetLocalValue);
            Check(value.AsPath().Raw == "./lazy-flag" && await File.ReadAllTextAsync(Path.Combine(root, "lazy-flag")) == "lazy", "Stored Range lost its initialized Path or I/O context");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS native live captures, fresh allocations and initialized session Range");
    }
}
