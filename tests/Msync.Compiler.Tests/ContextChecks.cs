using Msync.Compiler;
using Msync.Core;

internal static class ContextChecks
{
    public static void Run()
    {
        UnfinishedFunctions();
        ScopeInitializers();
        GenericContexts();
        ReturnedScopes();
        ProcessMembers();
        SessionIsolation();
        Builtins();
        Console.WriteLine("nested lexical editor contexts and isolated session probes ok");
    }

    private static void UnfinishedFunctions()
    {
        const string source = "let Outer = scope { root: true }; fn draft(file: Path, unknown) { let Local = scope { item: true }; Local::";
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///nested.msync");
        EditorContext context = analysis.At(source.Length);
        Check(analysis.Diagnostic is not null, "incomplete function lost its original syntax error");
        Check(context.Bindings.Single(item => item.Name == "file").Type == "Path", "an annotated function parameter disappeared while editing");
        Check(context.Bindings.Single(item => item.Name == "unknown").Type == "unknown", "an uninstantiated parameter was guessed to be a Path");
        Check(context.Bindings.Any(item => item.Name == "Outer"), "an unused outer lexical name was omitted by runtime capture filtering");
        Check(context.Receiver?.Members?.Single().Name == "item", "local Scope receiver is unavailable in an unfinished function");
        Check(context.ReceiverKind == EditorReceiverKind.Member, "lexical member selection lost its source-owned receiver kind");
        const string joined = "fn draft(file: Path) { file/";
        EditorContext path = EditorCompiler.Analyze(joined, "file:///unknown-path.msync").At(joined.Length);
        Check(path.ReceiverKind == EditorReceiverKind.PathJoin && path.Receiver?.Type == "Path" && path.Receiver.KnownPath is null,
            "an unknown Path parameter cannot be distinguished from lexical member selection");
        const string shadow = "let item = scope { outer: true }; fn draft(item: Path) { let value = item; let item = scope { inner: true }; item::";
        EditorContext local = EditorCompiler.Analyze(shadow, "file:///shadow.msync").At(shadow.Length);
        Check(local.Bindings.Single(item => item.Name == "value").Type == "Path", "a future binding leaked into an earlier initializer");
        Check(local.Receiver?.Members?.Single().Name == "inner", "local shadowing selected the outer Scope");
        const string closed = "fn done(parameter: Path) { let local = scope { item: true }; local };";
        EditorContext outside = EditorCompiler.Analyze(closed, "file:///closed.msync").At(closed.Length);
        Check(outside.Bindings.Any(item => item.Name == "done") && outside.Bindings.All(item => item.Name is not ("parameter" or "local")),
            "a closed function leaked its local context at the following caret boundary");
        const string dormantBranch = "fn draft() { if true {} else { let Local = scope { item: true }; Local::";
        EditorContext branch = EditorCompiler.Analyze(dormantBranch, "file:///branch.msync").At(dormantBranch.Length);
        Check(branch.Receiver?.Members?.Single().Name == "item", "a statically unselected branch lost its lexical editing context");
        var recovered = Msync.Compiler.Syntax.Parser.ParseForEditor("fn incomplete() { true");
        try
        {
            _ = new CompilerSession().Compile("fn incomplete() { true", recovered.Document);
            throw new InvalidOperationException("a recovered editor tree entered ordinary session compilation");
        }
        catch (Msync.Compiler.Syntax.SyntaxException error)
        {
            Check(ReferenceEquals(error, recovered.Error), "strict compilation replaced the original recovery diagnostic");
        }
    }

    private static void ScopeInitializers()
    {
        const string source = "scope SomeClass { remote: ssh://forge, send: fn(file: Path) { ./ @ remote";
        EditorContext context = EditorCompiler.Analyze(source, "file:///scope.msync").At(source.IndexOf("./", StringComparison.Ordinal) + 2);
        Check(context.Bindings.Single(item => item.Name == "remote").Endpoint == new ConnectionValue(ConnectionKind.Ssh, "forge"),
            "a previous Scope member is not available to its function initializer");
        Check(context.HasPhysicalOwner && context.PhysicalEndpoint?.Address == "forge", "a Scope's Connection field was treated as another Scope or Path");
        const string nested = "fn draft() { let Hosts = scope { inner: scope { remote: ssh://forge } }; ./ @ Hosts::inner::remote";
        EditorContext selected = EditorCompiler.Analyze(nested, "file:///chain.msync").At(nested.IndexOf("./", StringComparison.Ordinal) + 2);
        Check(selected.HasPhysicalOwner && selected.PhysicalEndpoint?.Address == "forge", "a nested Scope field chain lost its endpoint identity");
        const string initializer = "let outer = scope { kept: true }; scope Block { prior: outer, current: prior::";
        EditorContext member = EditorCompiler.Analyze(initializer, "file:///initializer.msync").At(initializer.Length);
        Check(member.Receiver?.Members?.Single().Name == "kept", "unfinished Scope member initializer lost prior lexical members");
    }

    private static void GenericContexts()
    {
        const string source = "fn inspect(value) { value:: }; inspect(scope { common: true, first: true }); inspect(scope { common: false, second: true });";
        int caret = source.IndexOf("value::", StringComparison.Ordinal) + "value::".Length;
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///generic.msync");
        EditorContext context = analysis.At(caret);
        Check(context.Receiver?.Members?.Select(item => item.Name).SequenceEqual(new[] { "common" }) == true,
            "generic receiver completion advertises members absent from another actual argument");
        Check(context.Bindings.Single(item => item.Name == "value").Members?.Select(item => item.Name).SequenceEqual(new[] { "common" }) == true,
            "generic parameter binding metadata depends on the last specialization only");
        Check(!context.Bindings.Any(item => item.Name is "first" or "second"), "Scope fields leaked into unrelated function bindings");
    }

    private static void ReturnedScopes()
    {
        const string source = "fn factory() { scope { nested: scope { entry: true } } }; fn draft() { factory()::nested::";
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///return.msync");
        Check(analysis.At(source.Length).Receiver?.Members?.Single().Name == "entry", "a function-returned Scope chain cannot complete in an uncalled function");
        const string callArgument = "let target = scope { entry: true }; fn use(value) { value }; fn draft(path: Path) { use(target::";
        Check(EditorCompiler.Analyze(callArgument, "file:///argument.msync").At(callArgument.Length).Receiver?.Members?.Single().Name == "entry",
            "a nested incomplete call argument lost its lexical receiver");
    }

    private static void SessionIsolation()
    {
        CompilerSession session = new();
        CompilerSession control = new();
        const string definitions = "let root = scope { child: true }; fn identity(value) { value };";
        _ = session.Compile(definitions); session.AcceptSubmission();
        _ = control.Compile(definitions); control.AcceptSubmission();
        string[] names = session.BindingNames.Order(StringComparer.Ordinal).ToArray();
        const string editing = "identity(\"editor only\"); fn draft(parameter: Path) { root::";
        EditorAnalysis analysis = session.AnalyzeEditor(editing);
        Check(analysis.At(editing.Length).Receiver?.Members?.Single().Name == "child", "REPL current input lost the accepted Scope binding");
        Check(session.BindingNames.Order(StringComparer.Ordinal).SequenceEqual(names), "editor analysis changed live REPL bindings");
        Module actual = session.Compile("identity(true)");
        Module expected = control.Compile("identity(true)");
        Check(SameModule(actual, expected), "an editor probe changed later real bytecode/functions/constants or specialization identities");
        session.AcceptSubmission(); control.AcceptSubmission();
        _ = session.AnalyzeEditor("identity(~/probe); fn draft() { root::");
        Module after = session.Compile("identity(\"real text\")");
        Module baseline = control.Compile("identity(\"real text\")");
        Check(SameModule(after, baseline), "a second editor probe mutated an existing live template's specialization cache");
    }

    private static void ProcessMembers()
    {
        const string source = "fn inspect(process: Process) { process::";
        var context = EditorCompiler.Analyze(source, "file:///process.msync").At(source.Length);
        Check(context.Receiver?.Members?.Select(item => item.Name).SequenceEqual(new[] { "stdout", "stderr", "exit_code", "success" }) == true,
            "Process completion differs from the actual runtime's supported members");
        Check(context.Receiver!.Members!.Single(item => item.Name == "success").Type == "Bool", "Process success capability is not Bool");
        _ = Msync.Compiler.Compiler.Compile("let process = ./tool(); process::success");
        try
        {
            _ = Msync.Compiler.Compiler.Compile("let process = ./tool(); process::pid");
            throw new InvalidOperationException("compiler still advertises the nonexistent runtime pid field");
        }
        catch (CompileException error) { Check(error.Diagnostic.Code == "E0205", "unsupported Process field lost its member diagnostic"); }
    }

    private static void Builtins()
    {
        var standard = EditorCompiler.BuiltinBindings.Single(item => item.Name == "$std");
        var boolean = standard.Members!.Single(item => item.Name == "bool");
        Check(boolean.Members!.Any(item => item.Name == "both" && item.Type == "Function"), "cached builtin metadata omitted a source-defined standard function");
        Check(EditorCompiler.BuiltinBindings.Single(item => item.Name == "$wsl").Endpoint?.Kind == ConnectionKind.Wsl,
            "cached builtin endpoint metadata is unavailable");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool SameModule(Module actual, Module expected) => actual.Stage == expected.Stage && actual.OptimizationLevel == expected.OptimizationLevel
        && actual.Constants.SequenceEqual(expected.Constants) && actual.Functions.Count == expected.Functions.Count
        && actual.Functions.Zip(expected.Functions).All(pair => pair.First.Name == pair.Second.Name
            && pair.First.ParameterCount == pair.Second.ParameterCount && pair.First.LocalCount == pair.Second.LocalCount
            && pair.First.CaptureCount == pair.Second.CaptureCount && pair.First.ReturnType == pair.Second.ReturnType
            && (pair.First.ParameterTypes ?? []).SequenceEqual(pair.Second.ParameterTypes ?? [])
            && pair.First.Instructions.SequenceEqual(pair.Second.Instructions));
}
