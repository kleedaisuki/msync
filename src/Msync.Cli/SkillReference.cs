using System.Text;

namespace Msync.Cli;

internal static class SkillReference
{
    private sealed record Topic(string Path, string Title, string Resource, string? Module = null);

    private static readonly Topic[] Topics = BuildTopics();

    private static Topic[] BuildTopics() =>
    [
        new("", "Use msync 0.8", "overview"),
        new("language", "Bindings, values and expression syntax", "language"),
        new("language/functions", "Generic functions, callbacks and recursion", "functions"),
        new("language/scopes", "Lexical scopes and member access", "scopes"),
        new("language/types", "Types, Null admission and branch refinement", "types"),
        new("language/numbers", "Number literals, arithmetic and discrete counts", "numbers"),
        new("language/conversions", "Explicit conversion with as", "conversions"),
        new("language/concepts", "Named structural and algebraic requirements", "concepts"),
        new("language/match", "Exhaustive value, regex and type matching", "match"),
        new("paths", "Path values, literal text and file transfers", "paths"),
        new("paths/connections", "Native SSH and WSL endpoint qualification", "connections"),
        new("library", "Source-backed standard library", "library"),
        .. StandardModules().Select(ModuleTopic),
        new("workflow", "Check, inspect, format and execute", "workflow"),
        new("workflow/artifacts", "Compile .mso and bind .mexe", "artifacts"),
        new("troubleshooting", "Read and repair source diagnostics", "troubleshooting")
    ];

    private static Topic ModuleTopic(string module)
    {
        bool concepts = Msync.Compiler.Syntax.Parser.Parse(ReadStandard(module)).Statements
            .All(statement => statement is Msync.Compiler.Syntax.ConceptDeclaration);
        return new("library/" + module, concepts ? $"Compile-time {module} declarations" : $"Executable {module} declarations and operations",
            concepts ? "concept-module" : "module", module);
    }

    private static IEnumerable<string> StandardModules() => typeof(SkillReference).Assembly
        .GetManifestResourceNames()
        .Where(name => name.EndsWith(".msync", StringComparison.Ordinal))
        .Select(name => name[..^6].Split('.').Last())
        .Where(name => !name.StartsWith('_'))
        .Order(StringComparer.Ordinal);

    public static string Render(IReadOnlyList<string> topics)
    {
        string path = string.Join('/', topics.SelectMany(topic => topic.Split('/', StringSplitOptions.RemoveEmptyEntries)));
        Topic? selected = Topics.FirstOrDefault(topic => topic.Path == path);
        if (selected is null) throw UnknownTopic(path);
        string body = Read($"Msync.Skills.{selected.Resource}.md");
        if (selected.Module is { } module)
        {
            string source = ReadStandard(module);
            body = body.Replace("{{module}}", module, StringComparison.Ordinal)
                .Replace("{{source}}", source.TrimEnd(), StringComparison.Ordinal);
        }
        StringBuilder result = new(body.TrimEnd());
        Topic[] children = Topics.Where(topic => Parent(topic.Path) == path && topic.Path != path).ToArray();
        if (children.Length != 0)
        {
            result.Append("\n\n## Explore next\n\n");
            foreach (Topic child in children)
            {
                string command = $"- `msync skill {child.Path.Replace('/', ' ')}`:";
                result.Append(command).Append(command.Length + child.Title.Length + 2 > 80 ? "\n  " : " ")
                    .Append(child.Title).Append(".\n");
            }
        }
        if (path.Length != 0)
        {
            string parent = Parent(path)!;
            result.Append($"\nBack: `msync skill{(parent.Length == 0 ? "" : " " + parent.Replace('/', ' '))}`.\n");
        }
        return result.ToString().TrimEnd() + "\n";
    }

    private static ArgumentException UnknownTopic(string path)
    {
        string parent = path;
        while (parent.Length != 0 && !Topics.Any(topic => topic.Path == parent)) parent = Parent(parent)!;
        string choices = string.Join(", ", Topics.Where(topic => Parent(topic.Path) == parent && topic.Path != parent)
            .Select(topic => $"'{topic.Path.Replace('/', ' ')}'"));
        string command = "msync skill" + (parent.Length == 0 ? "" : " " + parent.Replace('/', ' '));
        if (choices.Length == 0)
        {
            string up = Parent(parent)!;
            string siblings = "msync skill" + (up.Length == 0 ? "" : " " + up.Replace('/', ' '));
            return new($"Unknown skill topic '{path.Replace('/', ' ')}'. Topic '{parent.Replace('/', ' ')}' has no subtopics. Run '{command}' to read it, or '{siblings}' for sibling topics.");
        }
        return new($"Unknown skill topic '{path.Replace('/', ' ')}'. Available topics: {choices}. Run '{command}' for an overview.");
    }

    private static string? Parent(string path)
    {
        if (path.Length == 0) return null;
        int separator = path.LastIndexOf('/');
        return separator >= 0 ? path[..separator] : "";
    }

    private static string Read(string resource)
    {
        using Stream stream = typeof(SkillReference).Assembly.GetManifestResourceStream(resource)!;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string ReadStandard(string module)
    {
        var assembly = typeof(SkillReference).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith($".{module}.msync", StringComparison.Ordinal));
        return Read(resource);
    }
}
