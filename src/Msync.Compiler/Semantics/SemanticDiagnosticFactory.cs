using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler.Semantics;

internal static class SemanticDiagnosticFactory
{
    public static CompileException Create(HirSource source, SourceSpan span, string message, string code)
    {
        (string? label, string? help, string? note) = code switch
        {
            "E0201" => ("Null is possible here",
                "Handle Null first with 'if value is not null { ... }', or match null and the remaining cases.",
                "Postfix 'value?' means 'value is null'; it does not test whether a value is present."),
            "E0202" => ("unknown type or concept",
                "Use a declared concept, a qualified standard concept, or "
                    + string.Join(", ", LanguageTypes.Names.Select(name => name == "Range" ? "Range<T>" : name)) + ".", null),
            "E0209" => ("not every possible value is covered",
                "Add arms for the missing cases, or a final '_ => { ... }' arm. Include null when the input admits Null.", null),
            _ => (null, null, null)
        };
        return new(message, source.Text ?? "", source.Name, span, label, help, note, code: code);
    }

    public static CompileException TypeMismatch(HirSource source, SourceSpan span, TypeRef actual, TypeRef expected)
    {
        return new($"Expected {expected}, found {actual}.", source.Text ?? "", source.Name, span,
            label: $"this expression has type {actual}", help: TypeHelp(actual, expected), code: "E0203");
    }

    public static CompileException QualificationOrder(HirSource source, SourceSpan span) =>
        new("Write 'connection @ path' instead of 'path @ connection'.", source.Text ?? "", source.Name, span,
            label: "the Connection now comes first",
            help: "Place the Connection on the left of '@'. To preserve a path expression's earlier effects, evaluate it in a binding before qualifying that binding.",
            code: "E0238");

    private static string TypeHelp(TypeRef actual, TypeRef expected)
    {
        if (actual.Nullable && !expected.Nullable)
            return "Handle the Null case first: use 'if value is not null { ... }' or match null and the remaining cases.";
        if (expected == TypeRef.Bool)
            return "Conditions and boolean operators require Bool. Use 'exists path' for a filesystem test, or 'value is null' for a Null test.";
        if (expected == TypeRef.Function)
            return "Call an fn value. A Scope groups members; select its function with 'scope::operation(...)'.";
        if (expected.Kind == TypeKind.Range)
            return "Iterate a Range, such as '[first, second]' or 'directory/<pattern>'. A single Path is not a collection.";
        if (actual == TypeRef.Connection && TypeRef.Union(TypeRef.Path, TypeRef.String).Accepts(expected))
            return "A Connection selects an endpoint. Qualify a Path or String with 'connection @ path'.";
        return ExplicitConversions.Possible(actual, expected)
            ? $"Convert explicitly with 'value as {expected}'. Only Path and String convert implicitly."
            : $"Supply a {expected} value here, or use a new binding for a different storage type.";
    }

    public static CompileException MissingMember(HirSource source, SourceSpan span, string name,
        IEnumerable<string> members)
    {
        string[] names = members.Order(StringComparer.Ordinal).ToArray();
        string help = names.Length == 0 ? "This Scope has no declared members. Add the field to its scope declaration."
            : "Select a declared member: " + string.Join(", ", names.Select(item => $"'{item}'")) + ".";
        return new($"Scope has no member '{name}'.", source.Text ?? "", source.Name, span,
            label: $"'{name}' is not declared in this Scope", help: help, code: "E0205");
    }

    public static CompileException ShadowedStandardType(HirSource source, SourceSpan span, string message,
        DiagnosticLocation declaration)
    {
        CompileException error = new(message, source.Text ?? "", source.Name, span,
            label: "the standard namespace is shadowed here",
            help: "Rename the lexical 'std' binding to use qualified standard concept declarations.", code: "E0202");
        error.Diagnostic.Related.Add(declaration);
        return error;
    }

    public static CompileException NullAdmission(HirSource source, SourceSpan use, HirLocation location)
    {
        string kind = location.Kind == HirLocationKind.Field ? "field" : "binding";
        return new($"A {kind} that admits Null must declare '?'.", source.Text ?? "", source.Name, use,
            label: $"'{location.Name}' does not admit Null",
            help: $"Write '{location.Name}?' to allow Null, or provide a non-null initializer.",
            fix: new($"Allow Null for '{location.Name}'", new(location.Source.Start + location.Source.Length, 0), "?"),
            code: "E0201");
    }

    public static void FixedStorage(CompileException error, HirSource source, HirLocation location, TypeRef expected)
    {
        error.Diagnostic.Related.Add(new(source.Name, source.Text ?? "",
            new(location.Source.Start, location.Source.Length),
            $"'{location.Name}' has fixed storage type {expected}; assignments change its value, not its type"));
    }

    public static CompileException Arity(HirSource source, SourceSpan span, string message,
        IEnumerable<string>? parameters = null, DiagnosticLocation? declaration = null)
    {
        string[]? names = parameters?.ToArray();
        string help = names is null
            ? "Supply the declared arguments in parameter order. An optional value still occupies an argument position."
            : names.Length == 0 ? "This function takes no arguments."
                : "Supply arguments for: " + string.Join(", ", names) + ".";
        CompileException error = new(message, source.Text ?? "", source.Name, span,
            label: "the argument count does not match this signature",
            help: help,
            code: "E0204");
        if (declaration is not null) error.Diagnostic.Related.Add(declaration);
        return error;
    }

    public static CompileException Concept(HirSource source, SourceSpan span, string message) =>
        new(message, source.Text ?? "", source.Name, span,
            label: "the structural requirement is not satisfied",
            help: "Match the concept's required fields and callable signatures, including Null admission and return types.",
            code: "E0214");
}
