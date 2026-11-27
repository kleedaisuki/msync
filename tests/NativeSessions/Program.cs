using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

await AliasWitnessChecks.Run();
await MixedCallableChecks.Run();
await DeferredCallableChecks.Run();

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "native-session-" + Guid.NewGuid().ToString("N"));
string before = Path.Combine(root, "before");
string after = Path.Combine(root, "after");
Directory.CreateDirectory(Path.Combine(before, "folder"));
Directory.CreateDirectory(Path.Combine(after, "folder"));
await File.WriteAllTextAsync(Path.Combine(before, "folder", "old-entry"), "before");
await File.WriteAllTextAsync(Path.Combine(after, "folder", "new-entry"), "after");
try
{
    CompilerSession compiler = new();
    await using NativeExecutionSession execution = new(new(WorkingDirectory: before) { Input = new StringReader("stdin-value\n") });
    async Task<NativeValue> Submit(string source)
    {
        try
        {
            NativeValue result = await execution.ExecuteAsync(compiler.Compile(source));
            compiler.AcceptSubmission(execution.GetLocalValue);
            return result;
        }
        catch
        {
            compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
            throw;
        }
    }
    void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    Check((await Submit("$std::io::read_line()")).AsString() == "stdin-value", "Native session stdin did not use the configured reader.");
    await Submit("let value = 1; fn read() { value }; let alias = object { value: 2, read: fn { value } }; let shared = alias;");
    await Submit("value = 3; shared::value = 4;");
    Check((await Submit("read() + alias::read()")).AsNumber() == 7, "Native shared captures lost accepted mutations.");
    await Submit("let value = 100;");
    Check((await Submit("read() + value")).AsNumber() == 103, "Shadowing reanchored a captured location.");
    await Submit("let endpoint = ssh://session-host; let config = object { remote: endpoint };");
    const string endpointExpression = "config::";
    Check(compiler.AnalyzeEditor(endpointExpression, execution.GetLocalValue).At(endpointExpression.Length)
        .Receiver?.Members?.Single(member => member.Name == "remote").Endpoint?.Address == "session-host",
        "Accepted native Connection metadata disappeared from namespace completion.");
    await Submit("fn identity(value) { value }; let number_type = Number;");
    Check((await Submit("identity(true)")).AsBool(), "Bool specialization failed.");
    Check((await Submit("identity(42)")).AsNumber() == 42, "Number specialization failed.");
    Check((await Submit("number_type == Number")).AsBool(), "Type witness was not retained.");
    await Submit("object FirstSite { value: 1 }; object SecondSite { value: \"second\" };");
    await Submit("FirstSite::value = 22; let retained_site = FirstSite; fn read_site(seed) { retained_site::value + seed };");
    await Submit("let different_layout = object { tag: true, text: \"layout\" };");
    Check((await Submit("read_site(3)")).AsNumber() == 25, "Named static scope or escaped static capture changed identity.");
    Check((await Submit("SecondSite::value")).AsString() == "second", "Static declarations reused another allocation.");
    await Submit("let folder = ./folder; fn captured() { folder }; fn literal() { ./folder }; fn make(value) { fn() { value } }; let returned = make(folder);");
    await Submit("let deferred = $std::ranges::transform([1], fn(value) { ./folder });");
    execution.ChangeWorkingDirectory(after);
    foreach (string expression in new[] { "folder/", "captured()/", "returned()/" })
    {
        var receiver = compiler.AnalyzeEditor(expression, execution.GetLocalValue).At(expression.Length).Receiver;
        Check(receiver?.KnownPath is { } path && Path.GetFullPath(path.LocalPath(after)) == Path.Combine(before, "folder"),
            "Editor projection lost retained native path: " + expression);
    }
    try { await Submit("let completed = 9; $std::fs::canonical(./missing); let unfinished = 10;"); }
    catch (MsyncException) { }
    Check((await Submit("completed")).AsNumber() == 9, "Completed prefix export was lost.");
    Check(!compiler.BindingNames.Contains("unfinished"), "Uninitialized suffix export was accepted.");
    _ = compiler.AnalyzeEditor("identity(\"query only\"); fn draft() { alias::", execution.GetLocalValue);
    Check((await Submit("identity(false)")).AsBool() == false, "Editor-only instantiation changed callable identities.");
    if (!OperatingSystem.IsWindows())
    {
        await Submit("fn start() { $std::process::spawn(\"/bin/sh\", [\"-c\", \"cat\"]) }; let child = start();");
        await Submit("$std::process::write_stdin(child, \"across-submissions\"); $std::process::close_stdin(child);");
        Check((await Submit("$std::process::stdout($std::process::wait(child))")).AsString() == "across-submissions",
            "Returned Process ownership ended at a submission boundary.");
    }
    Check(Path.GetFullPath((await Submit("literal()")).AsPath().LocalPath(after)) == Path.Combine(before, "folder"),
        "Old callable literal was rebound after cd.");
    Check(Path.GetFullPath((await Submit("$std::ranges::first(deferred)")).AsPath().LocalPath(after)) == Path.Combine(before, "folder"),
        "Deferred range callback literal was rebound after cd.");
    Check(Path.GetFullPath((await Submit("./folder")).AsPath().LocalPath(after)) == Path.Combine(after, "folder"),
        "New literal reused an unrelated definition context.");
    await Submit("let optional? = null; fn read_optional() { optional }; let holder = object { field?: null, read: fn { field } };");
    await Submit("optional = 42; holder::field = 43;");
    Check((await Submit("read_optional() + holder::read()")).AsNumber() == 85,
        "First non-null writes reinterpreted existing physical capture storage.");
    NativeCompilerSession namedAlias = new();
    await using NativeExecutionSession aliasExecution = new();
    Check((await aliasExecution.ExecuteAsync(namedAlias.Compile("40 + 2"))).AsNumber() == 42,
        "The native-named compiler facade must remain an alias of the canonical native session.");
    Console.WriteLine("native persistent roots, aliases, captures, shadowing, Type, specialization, path projection and partial failure passed");
}
finally { Directory.Delete(root, true); }
