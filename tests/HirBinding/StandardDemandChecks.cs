using Msync.Compiler.Semantics;
using Msync.Compiler.Syntax;

internal static class StandardDemandChecks
{
    internal static void Run()
    {
        const string name = "standard-demand.msync";
        const string direct = "let chosen = $std::containers::Set;";
        var projected = Bind(direct).StandardModules["containers"];
        if (!projected.Projected || !projected.Exports.ContainsKey("Set")
            || !projected.Exports.ContainsKey("NaturalOrder") || projected.Exports.ContainsKey("List"))
            throw new InvalidOperationException("A closed direct selection binds its transitive dependencies, not sibling families.");

        foreach (string source in new[] {
            "let chosen = $std::containers;",
            "$std::containers::",
            "$std::containers::Lis;",
            "fn inspect { $std::containers::"
        })
        {
            var module = Bind(source).StandardModules["containers"];
            if (module.Projected || !module.Exports.ContainsKey("List") || !module.Exports.ContainsKey("Set"))
                throw new InvalidOperationException($"An escaped, partial or unknown receiver must keep its full exports: {source}");
        }

        var persistent = new SourceBinder(observations: true, editorMode: true, isSession: true)
            .Bind(direct, name);
        if (persistent.StandardModules["containers"].Projected)
            throw new InvalidOperationException("A later REPL submission may read another export.");

        Console.WriteLine("PASS 6 standard-library demand contracts");

        static BoundProgram Bind(string source) => new SourceBinder(observations: true, editorMode: true)
            .Bind(source, name, Parser.ParseForEditor(source, name).Document);
    }
}
