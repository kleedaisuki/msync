using Msync.Compiler;
using Msync.Core;
using Frontend = Msync.Compiler.Compiler;

internal static class EndpointChecks
{
    private static readonly ConnectionValue Forge = new(ConnectionKind.Ssh, "forge");
    private static readonly ConnectionValue Other = new(ConnectionKind.Ssh, "other");

    public static void Run()
    {
        LiteralAliasesAndJoins();
        GenericMetadataIsolation();
        SessionBindingDescriptions();
        Console.WriteLine("static endpoint identity and live session binding insight ok");
    }

    private static void LiteralAliasesAndJoins()
    {
        const string source = """
            let remote = ssh://forge;
            let alias = remote;
            scope Hosts { primary: remote, nested: scope { guest: wsl://Ubuntu } };
            let projected = Hosts::primary;
            let same = if exists ~/condition { ssh://forge } else { ssh://forge };
            let different = if exists ~/condition { ssh://forge } else { ssh://other };
            let absent? = if exists ~/condition { ssh://forge } else { null };
            let guest = $wsl;
            let local = $local;
            let directory = $pwd;
            let file = $file;
            let environment = $env;
            let search = $path;
            let ipv6list = [ssh://user@[2001:db8::1]:2222, ssh://forge];
            """;
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///endpoints.msync");
        Check(analysis.Diagnostic is null, "literal endpoint sample failed compilation");
        foreach (string name in new[] { "remote", "alias", "projected", "same" })
            Check(Binding(analysis, source, name).Endpoint == Forge, "known endpoint identity was lost: " + name);
        Check(Binding(analysis, source, "different").Endpoint is null, "different branch endpoints were collapsed into one host");
        Check(Binding(analysis, source, "absent").Endpoint is null, "a possibly absent endpoint became an unconditional completion target");
        Check(Binding(analysis, source, "guest").Endpoint == new ConnectionValue(ConnectionKind.Wsl), "$wsl hint differs from runtime semantics");
        Check(Binding(analysis, source, "local").Endpoint == new ConnectionValue(ConnectionKind.Local), "$local hint differs from runtime semantics");
        Check(Binding(analysis, source, "directory").Endpoint == new ConnectionValue(ConnectionKind.Local, "pwd"), "$pwd lost its contextual identity");
        Check(Binding(analysis, source, "file").Endpoint == new ConnectionValue(ConnectionKind.Local, "file"), "$file lost its contextual identity");
        Check(Binding(analysis, source, "environment").Endpoint == new ConnectionValue(ConnectionKind.Environment), "$env is not a local filesystem endpoint");
        Check(Binding(analysis, source, "search").Endpoint == new ConnectionValue(ConnectionKind.SearchPath), "$path is not an ordinary local endpoint");
        SemanticOccurrence hosts = analysis.Occurrences.Single(item => item.Span.Start == source.IndexOf("Hosts", StringComparison.Ordinal));
        Check(hosts.Members!.Single(item => item.Name == "primary").Endpoint == Forge, "Scope member endpoint identity was lost");
        var nested = hosts.Members!.Single(item => item.Name == "nested");
        Check(nested.Members!.Single().Endpoint == new ConnectionValue(ConnectionKind.Wsl, "Ubuntu"), "nested Scope descriptors lost their endpoint identity");
        EditorAnalysis recovery = EditorCompiler.Analyze(source + "alias::", "file:///endpoints.msync");
        Check(recovery.RecoveryBindings!.Single(item => item.Name == "alias").Endpoint == Forge, "incomplete qualifier recovery lost its accepted endpoint alias");
    }

    private static void GenericMetadataIsolation()
    {
        const string source = """
            fn identity(endpoint) { endpoint };
            let first = identity(ssh://forge);
            let second = identity(ssh://other);
            fn firstItem(values) { $std::ranges::front(values) };
            let firstRange = firstItem([ssh://forge]);
            let secondRange = firstItem([ssh://other]);
            fn wrap(endpoint) { fn() { endpoint } };
            let firstClosure = wrap(ssh://forge);
            let secondClosure = wrap(ssh://other);
            let firstCaptured = firstClosure();
            let secondCaptured = secondClosure();
            fn constant(unused) { ssh://forge };
            let known = constant(ssh://other);
            let captured = ssh://forge;
            fn lexical() { captured };
            let captured = ssh://other;
            let frozen = lexical();
            fn member(value) { value::host };
            let scopedFirst = member(scope { host: ssh://forge });
            let scopedSecond = member(scope { host: ssh://other });
            """;
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///generic-endpoints.msync");
        Check(analysis.Diagnostic is null, "generic endpoint sample failed compilation: " + analysis.Diagnostic?.EditorMessage);
        foreach (string name in new[] { "first", "second", "firstRange", "secondRange", "firstCaptured", "secondCaptured" })
            Check(Binding(analysis, source, name).Endpoint is null, "type-shared specialization froze an actual argument endpoint: " + name);
        Check(Binding(analysis, source, "known").Endpoint == Forge, "a literal-returning function lost its known endpoint");
        Check(Binding(analysis, source, "frozen").Endpoint == Forge, "a later shadow changed a captured endpoint hint");
        Check(Binding(analysis, source, "scopedFirst").Endpoint == Forge && Binding(analysis, source, "scopedSecond").Endpoint == Other,
            "exact Scope-identity specializations lost their immutable member metadata");
        int sharedMember = source.IndexOf("value::host", StringComparison.Ordinal) + "value::".Length;
        Check(analysis.Occurrences.Single(item => item.Span.Start == sharedMember && item.Span.Length == 4).Endpoint is null,
            "a shared generic source occurrence reports the last specialized endpoint");
        Module module = Frontend.Compile(source);
        Check(module.Functions.Count(item => item.Name == "identity" && item.ParameterTypes?.SequenceEqual([TypeRef.Connection]) == true) == 1
            && module.Functions.Count(item => item.Name == "firstItem") == 1,
            "endpoint hints became value-based bytecode specialization");
    }

    private static void SessionBindingDescriptions()
    {
        CompilerSession session = new();
        _ = session.Compile("let remote = ssh://forge; scope Hosts { guest: $wsl }; fn send(file) { file -> remote::file };");
        session.AcceptSubmission();
        IReadOnlyList<SemanticMember> first = session.DescribeBindings();
        Check(first.Single(item => item.Name == "remote").Endpoint == Forge, "live session lost its accepted endpoint");
        Check(first.Single(item => item.Name == "Hosts").Members!.Single().Endpoint == new ConnectionValue(ConnectionKind.Wsl), "live Scope members lost endpoint metadata");
        Check(first.Single(item => item.Name == "send").Signature.StartsWith("fn send(", StringComparison.Ordinal), "live session lost callable signature metadata");
        _ = session.Compile("let remote = ssh://other;");
        session.ReconcileSubmission(_ => false);
        Check(session.DescribeBindings().Single(item => item.Name == "remote").Endpoint == Forge, "failed shadow replaced an accepted endpoint hint");
        Module changed = session.Compile("let remote = ssh://other; let unfinished = ssh://unreached;");
        int completed = changed.Functions[0].Instructions.First(item => item.Op == OpCode.StoreLocal).A;
        session.ReconcileSubmission(slot => slot == completed);
        var current = session.DescribeBindings();
        Check(current.Single(item => item.Name == "remote").Endpoint == Other && current.All(item => item.Name != "unfinished"),
            "live hints did not follow the initialized execution prefix");
        Check(first.Single(item => item.Name == "remote").Endpoint == Forge, "a previously returned prompt snapshot was mutated");
    }

    private static SemanticOccurrence Binding(EditorAnalysis analysis, string source, string name)
    {
        int start = source.IndexOf("let " + name, StringComparison.Ordinal) + 4;
        return analysis.Occurrences.Single(item => item.Span.Start == start && item.Span.Length == name.Length);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
