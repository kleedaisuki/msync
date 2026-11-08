using Msync.Compiler;
using Msync.Core;
using Frontend = Msync.Compiler.Compiler;

internal static class QualificationContextChecks
{
    private const string Prefix = "let remote = ssh://forge; ";
    private static readonly ConnectionValue Remote = new(ConnectionKind.Ssh, "forge");
    private static readonly ConnectionValue Local = new(ConnectionKind.Local);

    public static void Run()
    {
        TransparentPathFlow();
        LeafOwnership();
        EvaluationBoundaries();
        UnknownEndpoints();
        MalformedRecovery();
        ArtifactIsolation();
        Console.WriteLine("grouped physical completion follows path flow without crossing evaluation boundaries");
    }

    private static void TransparentPathFlow()
    {
        Qualified(Prefix + "remote::(/tmp/§)", Remote, "/tmp");
        Qualified(Prefix + "let folder = /tmp; remote::(folder/§)", Remote, "/tmp");
        Qualified("scope Endpoints { remote: ssh://forge }; Endpoints::remote::(./rel/§)", Remote, "./rel");
        Qualified(Prefix + "remote::(\"folder\"/§)", Remote, "folder");
        Qualified(Prefix + "remote::{ /tmp/§ }", Remote, "/tmp");
        Qualified(Prefix + "remote::(if true { /tmp/§ } else { other })", Remote, "/tmp");
        Qualified(Prefix + "remote::(if true { other } else { /tmp/§ })", Remote, "/tmp");
        Qualified(Prefix + "remote::(match true { true => { /tmp/§ }, false => { other } })", Remote, "/tmp");
        const string folder = "let remote = ssh://forge; let folder = remote::./rel; ";
        Qualified(folder + "$local::(folder/§)", Local, "./rel");
        Qualified(folder + "remote::($local::(folder/§))", Remote, "./rel");
        Qualified(folder + "$local::(remote::(./rel/§))", Local, "./rel");
        Qualified("$wsl::(./rel/§)", new(ConnectionKind.Wsl), "./rel");
    }

    private static void EvaluationBoundaries()
    {
        Unqualified(Prefix + "fn identity(path) { path }; remote::identity(/tmp/§)");
        Unqualified(Prefix + "remote::((fn() { /tmp/§ })())");
        Unqualified(Prefix + "remote::(fn() { /tmp/§ })");
        Unqualified(Prefix + "remote::(if exists (/tmp/§) { one } else { two })");
        Unqualified(Prefix + "remote::(match exists (/tmp/§) { true => { one }, false => { two } })");
        Unqualified(Prefix + "remote::{ /tmp/§; other }");
        Unqualified(Prefix + "remote::{ let temporary = /tmp/§; temporary }");
    }

    private static void LeafOwnership()
    {
        foreach (string expression in new[]
        {
            "remote:: \"./rel/§\"", "remote::(\"./rel/§\")", "remote::{ \"./rel/§\" }",
            "remote::(if true { \"./rel/§\" } else { other })",
            "remote::(match true { true => { \"./rel/§\" }, false => { other } })",
            "remote::($local::\"./rel/§\")"
        })
        {
            EditorContext context = At(Prefix + expression);
            Check(context.HasPhysicalOwner && context.PhysicalEndpoint == Remote, "transparent path leaf lost its physical owner: " + expression);
        }
        EditorContext local = At(Prefix + "$local::(remote::\"./rel/§\")");
        Check(local.HasPhysicalOwner && local.PhysicalEndpoint == Local, "outermost physical leaf qualification did not win");
        foreach (string expression in new[]
        {
            "fn identity(value) { value }; remote::identity(\"./rel/§\")",
            "remote::((fn() { \"./rel/§\" })())",
            "remote::(if exists \"./rel/§\" { one } else { two })",
            "remote::(match exists \"./rel/§\" { true => { one }, false => { two } })"
        })
        {
            EditorContext context = At(Prefix + expression);
            Check(!context.HasPhysicalOwner && context.Receiver?.Type != "Connection", "physical leaf ownership crossed an evaluation boundary: " + expression);
        }
        EditorContext lexical = At(Prefix + "scope Config { root: ./rel }; remote::(Config::ro§)");
        Check(!lexical.HasPhysicalOwner && lexical.Receiver?.Type == "Scope" && lexical.Receiver.Members?.Single().Name == "root",
            "outer qualification turned a Scope member-name lookup into a physical pathname");
        EditorContext unknown = At("fn inspect(remote: Connection) { remote:: \"./rel/§\" }");
        Check(unknown.HasPhysicalOwner && unknown.PhysicalEndpoint is null, "opaque leaf ownership became an ordinary local path");
        EditorContext ambiguous = At("fn inspect(settings) { settings::endpoint::\"./rel/§\" }; inspect(scope { endpoint: ssh://one }); inspect(scope { endpoint: ssh://two });");
        Check(ambiguous.HasPhysicalOwner && ambiguous.PhysicalEndpoint is null, "a physical leaf reports one of several actual generic endpoint owners");
        EditorContext rewritten = At(Prefix + "fn inspect(unknown: Connection) { $local::(unknown::\"./rel/§\") }");
        Check(rewritten.HasPhysicalOwner && rewritten.PhysicalEndpoint is null,
            "an outer known endpoint invented a literal value through an unknown contextual projection");
    }

    private static void UnknownEndpoints()
    {
        EditorContext endpoint = At("fn inspect(remote: Connection) { remote::(/tmp/§) }");
        Check(endpoint.ReceiverKind == EditorReceiverKind.PathJoin && endpoint.Receiver?.KnownPath is null && endpoint.Receiver?.KnownText is null,
            "a grouped unknown endpoint became a local filesystem target");
        EditorContext path = At(Prefix + "fn inspect(path: Path) { remote::(path/§) }");
        Check(path.Receiver?.KnownPath is null, "qualification invented a path value for an opaque parameter");
        EditorContext contextual = At("$env::(/tmp/§)");
        Check(contextual.Receiver?.KnownPath is null, "a contextual endpoint was projected as an ordinary physical path");
        const string polymorphic = "fn inspect(settings) { settings::endpoint::(/tmp/§) }; inspect(scope { endpoint: ssh://one }); inspect(scope { endpoint: ssh://two });";
        Check(At(polymorphic).Receiver?.KnownPath is null, "one specialization overwrote the endpoint of another invocation's source receiver");
    }

    private static void ArtifactIsolation()
    {
        const string source = Prefix + "remote::(/tmp/) -> remote::./target;";
        byte[] before = Bytes(Frontend.Compile(source, "qualified.msync", 0));
        _ = EditorCompiler.Analyze(source, "qualified.msync");
        Check(before.AsSpan().SequenceEqual(Bytes(Frontend.Compile(source, "qualified.msync", 0))), "editor qualification changed normal relocatable artifact bytes");
    }

    private static void MalformedRecovery()
    {
        const string source = "let ready = scope { useful: true }; fn inspect(scope) { scope::endpoint::(/tmp/) }; inspect(scope { endpoint: ssh://one }); inspect(scope { endpoint: ssh://two });";
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///malformed.msync");
        Check(analysis.Diagnostic is not null, "malformed keyword parameter lost its diagnostic");
        Check(analysis.At(source.Length).Bindings.Any(item => item.Name == "ready" && item.Members?.Single().Name == "useful"),
            "malformed editor specialization erased an unrelated known binding");
        const string recursive = "fn count(value: Number) -> Bool { if value == 0 { true } else { count(value - 1) } }; count(3)";
        Check(EditorCompiler.Analyze(recursive, "file:///recursive.msync").Diagnostic is null,
            "same-key recursive inference stopped reusing its reserved specialization");
    }

    private static void Qualified(string source, ConnectionValue connection, string raw)
    {
        PathValue? path = At(source).Receiver?.KnownPath;
        Check(path is not null && path.Connection == connection && path.Raw == raw,
            $"grouped path receiver differs from qualification semantics: {source}; expected {connection}::{raw}, got {path}");
    }

    private static void Unqualified(string source)
    {
        PathValue? path = At(source).Receiver?.KnownPath;
        Check(path is not null && path.Connection is null,
            "an enclosing qualifier crossed a call/function/condition/discard boundary: " + source);
    }

    private static EditorContext At(string marked)
    {
        int caret = marked.IndexOf('§');
        Check(caret >= 0, "fixture has no caret");
        string source = marked.Remove(caret, 1);
        return EditorCompiler.Analyze(source, "file:///qualification.msync").At(caret);
    }

    private static byte[] Bytes(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        return stream.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
