using System.Text;
using Msync.Compiler.Syntax;

namespace Msync.Cli;

internal static partial class SkillReference
{
    private sealed record LibraryEntry(string Name, string Signature, SourceSpan Span,
        IReadOnlyList<LibraryEntry>? Members = null);

    private static string RenderLibrary(string[] parts)
    {
        string module = parts[1];
        string source = ReadStandard(module);
        var document = Parser.Parse(source);
        IReadOnlyList<LibraryEntry> entries = LibraryDeclarations(source, document);
        LibraryEntry? selected = null;
        int depth = 2;
        while (depth < parts.Length && parts[depth] != "_source")
        {
            string name = parts[depth];
            selected = entries.FirstOrDefault(entry => entry.Name == name)
                ?? throw UnknownDeclaration(parts, depth, entries);
            entries = selected.Members ?? [];
            depth++;
        }
        if (depth + 1 < parts.Length) throw UnknownDeclaration(parts, depth + 1, []);

        string qualified = "$std::" + string.Join("::", parts[1..depth]);
        StringBuilder body = new($"# Standard library: {qualified}\n\n");
        if (depth < parts.Length || selected is { Members: null })
        {
            body.Append("Source embedded in this executable, in its library declaration context.\n\n");
            SourceSpan span = selected?.Span ?? new(0, source.Length);
            body.Append("```msync\n").Append(source[CommentStart(source, span.Start)..span.End].TrimEnd()).Append("\n```\n");
        }
        else
        {
            body.Append(document.Statements.All(statement => statement is ConceptDeclaration)
                ? "Named compile-time requirements, not runtime values.\n\n"
                : "Select a declaration by appending its exact name to this command.\n\n");
            foreach (var entry in entries)
            {
                body.Append("- `").Append(entry.Signature).Append('`');
                if (entry.Members is not null)
                    body.Append(" — `msync skill ").Append(string.Join(' ', parts)).Append(' ')
                        .Append(entry.Name).Append('`');
                body.Append('\n');
            }
            body.Append("\nFull source: `msync skill ").Append(string.Join(' ', parts)).Append(" _source`.\n");
        }
        body.Append("\nBack: `msync skill ").Append(string.Join(' ', parts[..^1])).Append("`.\n");
        return body.ToString();
    }

    private static ArgumentException UnknownDeclaration(string[] parts, int depth, IReadOnlyList<LibraryEntry> entries)
    {
        string parent = "msync skill " + string.Join(' ', parts[..depth]);
        string names = string.Join(", ", entries.Select(entry => entry.Name));
        string detail = names.Length == 0 ? "This declaration has no members." : $"Available names: {names}.";
        return new($"Unknown library declaration '{parts[depth]}'. {detail} Run '{parent}' for its reference.");
    }

    private static List<LibraryEntry> LibraryDeclarations(string source, Document document)
    {
        List<LibraryEntry> entries = [];
        foreach (var statement in document.Statements)
        {
            LibraryEntry? entry = statement switch
            {
                ScopeDeclaration scope => new(scope.Name, $"scope {scope.Name}", scope.Span, ScopeEntries(source, scope.Scope)),
                FunctionDeclaration function => new(function.Name,
                    Signature(source, function.Span.Start, function.Function.Body.Span.Start), function.Span),
                NativeDeclaration native => new(native.Name, Signature(source, native.Span.Start, native.Span.End), native.Span),
                ConceptDeclaration concept => new(concept.Name, $"concept {concept.Name}", concept.Span),
                LetStatement { Pattern: NamePattern name } binding => new LibraryEntry(name.Name, $"let {name.Name}", binding.Span),
                _ => null
            };
            if (entry is not null && !entry.Name.StartsWith('_')) entries.Add(entry);
        }
        return entries;
    }

    private static LibraryEntry[] ScopeEntries(string source, ScopeExpr scope) => scope.Members
        .Where(member => !member.Name.StartsWith('_'))
        .Select(member => member.Value switch
        {
            FunctionExpr function => new LibraryEntry(member.Name,
                Signature(source, member.Span.Start, function.Body.Span.Start), member.Span),
            ScopeExpr nested => new(member.Name, $"{member.Name}: scope", member.Span, ScopeEntries(source, nested)),
            _ => new(member.Name, member.Name, member.Span)
        }).ToArray();

    private static string Signature(string source, int start, int end) => string.Join(' ',
        source[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int CommentStart(string source, int declaration)
    {
        if (declaration == 0) return 0;
        int line = source.LastIndexOf('\n', declaration - 1) + 1;
        if (!source.AsSpan(line, declaration - line).IsWhiteSpace()) return declaration;
        int first = line;
        while (first > 0)
        {
            int end = first - 1;
            int start = end == 0 ? 0 : source.LastIndexOf('\n', end - 1) + 1;
            if (!source.AsSpan(start, end - start).TrimStart().StartsWith("#", StringComparison.Ordinal)) break;
            first = start;
        }
        return first;
    }
}
