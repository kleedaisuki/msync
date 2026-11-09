using Msync.Compiler;
using Msync.Core;

internal static class ContextualDirectoryChecks
{
    public static void Run()
    {
        var pwd = At("./root/§ @ $pwd");
        Check(pwd.Receiver?.KnownPath is { Raw: "./root", Connection.Kind: ConnectionKind.Local }, "pwd prefix lost its literal local path");
        var source = At("./root/§ @ $file");
        Check(source.Receiver?.KnownPath is { Raw: "./root", Connection: { Kind: ConnectionKind.Local, Address: "file" } }, "file prefix lost its source-directory projection");
        var grouped = At("(./root/§) @ $file");
        Check(grouped.HasPhysicalOwner && grouped.PhysicalEndpoint is { Kind: ConnectionKind.Local, Address: "file" }, "known contextual endpoint became an unknown owner");
        var opaque = At("fn inspect(path: Path) { (path/§) @ $file }");
        Check(opaque.Receiver?.KnownPath is null, "opaque Path parameter invented a directory value");
        var rebound = At("let remote = ssh://forge; (./root/§ @ $file) @ remote");
        Check(rebound.Receiver?.KnownPath is null,
            "unresolved source-directory path was reinterpreted as a remote relative directory");
        var special = At("pwd/§ @ $pwd");
        Check(special.Receiver?.KnownPath is null, "a dynamic pwd accessor became the literal file named pwd");
        Console.WriteLine("contextual directory endpoints retain identity without fabricating dynamic path values");
    }

    private static EditorContext At(string marked)
    {
        int position = marked.IndexOf('§');
        return EditorCompiler.Analyze(marked.Remove(position, 1), "file:///workspace/docs/editing.msync").At(position);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
