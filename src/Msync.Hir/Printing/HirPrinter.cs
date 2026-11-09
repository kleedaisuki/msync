using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Msync.Hir;

public enum HirTextCategory { Source, Type, Location, Function, Template, Literal, Shape, Signature, Concept, Node, Table }
public sealed record HirTextDefinition(string Identifier, string Name, string Description,
    HirTextCategory Category, int Line, int Character, int Length, HirSourceRange? Source = null);
public sealed record HirTextReference(string Identifier, int Line, int Character, int Length,
    int TargetLine, int TargetCharacter);
public sealed record HirPrintedModule(string Text, ImmutableArray<HirTextDefinition> Definitions,
    ImmutableArray<HirTextReference> References);

public static class HirPrinter
{
    public static string Print(HirModule module, CancellationToken cancellationToken = default) => Describe(module, cancellationToken).Text;
    public static HirPrintedModule Describe(HirModule module, CancellationToken cancellationToken = default) => new Writer(module, cancellationToken).Print();

    public static string Quote(string value)
    {
        StringBuilder text = new("\"");
        foreach (char character in value)
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (char.IsControl(character)) text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(character);
                    break;
            }
        return text.Append('"').ToString();
    }

    private sealed class Writer(HirModule module, CancellationToken cancellationToken)
    {
        private readonly List<string> lines = [];
        private readonly List<HirTextDefinition> definitions = [];

        internal HirPrintedModule Print()
        {
            Emit("; msync typed HIR — immutable semantic program");
            Emit("; This view does not bind, lower, optimize, execute or authenticate.");
            Emit($"artifact hir format {HirArtifact.FormatVersion} language {HirArtifact.LanguageVersion}");
            Emit($"entry @f{module.Entry.Value} {(module.IsSession ? "session" : "standalone")}");
            Emit("");
            for (int index = 0; index < module.Sources.Length; index++)
                Define($"source @s{index} {Quote(module.Sources[index].Name)} {(module.Sources[index].Text is { } source ? $"utf16-length {source.Length}" : "text omitted")}",
                    "@s" + index, module.Sources[index].Name, "Diagnostic source identity; text is never executed.", HirTextCategory.Source);
            for (int index = 0; index < module.Types.Length; index++) Type(index);
            for (int index = 0; index < module.ScopeLayouts.Length; index++)
                Define($"shape @h{index} {{ {string.Join(", ", module.ScopeLayouts[index].Fields.Select(field => $"{Quote(field.Name)}{(field.AdmitsNull ? "?" : "")}: @t{field.StorageType.Value}"))} }}",
                    "@h" + index, "Scope shape " + index, "Fixed field storage constraints, not a runtime instance.", HirTextCategory.Shape);
            for (int index = 0; index < module.Signatures.Length; index++)
            {
                var signature = module.Signatures[index];
                Define($"signature @q{index} ({string.Join(", ", signature.Parameters.Select(type => "@t" + type.Value))}{(signature.Variadic ? ", ..." : "")}) -> @t{signature.Result.Value}",
                    "@q" + index, "Callable signature " + index, "Parameter and result type contract.", HirTextCategory.Signature);
            }
            for (int index = 0; index < module.Concepts.Length; index++) Concept(index);
            for (int index = 0; index < module.Literals.Length; index++)
                Define($"const @c{index} = {Literal(module.Literals[index])}", "@c" + index, "Literal " + index,
                    "Exact source literal data; Number bits are retained.", HirTextCategory.Literal);
            for (int index = 0; index < module.Locations.Length; index++) Location(index);
            for (int index = 0; index < module.Calls.Length; index++)
            {
                var call = module.Calls[index];
                Define($"call @k{index} {call.Kind} {(call.Function.Value >= 0 ? "@f" + call.Function.Value : "")}{(call.NativeName is { } native ? " native " + Quote(native) : "")} targets [{Targets(call.Targets)}] scheduling {call.Scheduling}",
                    "@k" + index, "Call " + index, "Resolved operation identity and closed dispatch alternatives.", HirTextCategory.Table);
            }
            for (int index = 0; index < module.Closures.Length; index++)
                Define($"closure @u{index} @f{module.Closures[index].Function.Value} captures [{Captures(module.Closures[index].Captures)}]",
                    "@u" + index, "Closure " + index, "Capture values and logical locations explicitly.", HirTextCategory.Table);
            for (int index = 0; index < module.TemplateClosures.Length; index++)
                Define($"closure @v{index} @g{module.TemplateClosures[index].Template.Value} captures [{Captures(module.TemplateClosures[index].Captures)}]",
                    "@v" + index, "Generic closure " + index, "Generic identity and bound capture semantics.", HirTextCategory.Table);
            for (int index = 0; index < module.Scopes.Length; index++)
            {
                var scope = module.Scopes[index];
                Define($"scope @o{index} shape @h{scope.Layout} fields [{Locations(scope.Fields)}] site {scope.Site?.Value} singleton {scope.Singleton}{(scope.Self is { } self ? " self @l" + self.Value : "")}",
                    "@o" + index, "Scope construction " + index, "Logical field locations; no bytecode layout is assigned.", HirTextCategory.Table);
            }
            for (int index = 0; index < module.Fors.Length; index++)
                Define($"for @j{index} loop {module.Fors[index].Loop.Value} item @l{module.Fors[index].Item.Value}",
                    "@j" + index, "For loop " + index, "Iteration binding and logical break target.", HirTextCategory.Table);
            for (int index = 0; index < module.Matches.Length; index++)
                Define($"match @m{index} {{ {string.Join(", ", module.Matches[index].Arms.Select(arm => Pattern(arm.Pattern) + " => @n" + arm.Value.Value))} }}",
                    "@m" + index, "Match " + index, "Semantic patterns and result branches.", HirTextCategory.Table);
            for (int index = 0; index < module.Barriers.Length; index++)
                Define($"barrier @z{index} release {module.Barriers[index].Release.ToString().ToLowerInvariant()} acquire {module.Barriers[index].Acquire.ToString().ToLowerInvariant()}",
                    "@z" + index, "Directional barrier " + index, "Preserves selected-work ordering, not an unconditional global join.", HirTextCategory.Table);
            for (int index = 0; index < module.Ranges.Length; index++)
                Define($"range @r{index} modes [{string.Join(", ", module.Ranges[index].Deferred.OrEmpty().Select(deferred => deferred ? "defer" : "value"))}]",
                    "@r" + index, "Range evaluation " + index, "Explicit per-element evaluation mode.", HirTextCategory.Table);
            for (int index = 0; index < module.Adaptations.Length; index++)
                Define($"adapt @a{index} [{Targets(module.Adaptations[index].Targets)}]", "@a" + index,
                    "Function adaptation " + index, "Maps semantic callable identities to finalized bodies.", HirTextCategory.Table);
            for (int index = 0; index < module.Templates.Length; index++) Template(index);
            for (int index = 0; index < module.Functions.Length; index++) Function(index);
            Emit("");
            for (int index = 0; index < module.Nodes.Length; index++) Node(index);
            foreach (var export in module.Exports) Emit($"export {Quote(export.Name)} @l{export.Location.Value}");
            foreach (var import in module.UnresolvedImports.OrEmpty()) Emit($"import unresolved @l{import.Value}");
            return new(string.Join('\n', lines) + "\n", definitions.ToImmutableArray(), References());
        }

        private void Type(int index)
        {
            var type = module.Types[index];
            string detail = type.TypeParameter >= 0 ? "typename " + type.TypeParameter : type.RuntimeType.ToString();
            if (type.Nullable) detail += " nullable";
            if (!type.Alternatives.IsDefaultOrEmpty) detail += " alternatives [" + string.Join(", ", type.Alternatives.Select(item => "@t" + item.Value)) + "]";
            if (type.Element >= 0) detail += " element @t" + type.Element;
            if (type.Shape >= 0) detail += " shape @h" + type.Shape;
            if (type.Signature >= 0) detail += " signature @q" + type.Signature;
            if (type.Concept >= 0) detail += " concept @p" + type.Concept;
            Define($"type @t{index} {detail}", "@t" + index, "Type " + index, detail, HirTextCategory.Type);
        }

        private void Concept(int index)
        {
            var concept = module.Concepts[index];
            string fields = string.Join(", ", concept.Members.Select(member => $"{Quote(member.Name)}{(member.Nullable ? "?" : "")}: @t{member.Type.Value}{(member.Signature >= 0 ? " signature @q" + member.Signature : "")}"));
            Define($"concept @p{index} {Quote(concept.Name)} bases [{string.Join(", ", concept.Bases.Select(item => "@p" + item))}] associated [{string.Join(", ", concept.TypeParameters.Select(item => "@t" + item.Value))}] {{ {fields} }}",
                "@p" + index, concept.Name, "Structural requirement; not a runtime value or a proof of algebraic laws.", HirTextCategory.Concept, concept.Source);
        }

        private void Location(int index)
        {
            var location = module.Locations[index];
            string owner = location.TemplateOwner is { } template ? "@g" + template.Value : location.Owner.Value >= 0 ? "@f" + location.Owner.Value : "import";
            Define($"location @l{index} {Quote(location.Name)}: @t{location.StorageType.Value} owner {owner} {location.Kind.ToString().ToLowerInvariant()} {(location.Mutable ? "mutable" : "readonly")}{(location.AdmitsNull ? " admits-null" : "")}",
                "@l" + index, location.Name, "Logical storage identity with a fixed admitted type; no physical slot number.", HirTextCategory.Location, location.Source);
        }

        private void Template(int index)
        {
            var template = module.Templates[index];
            Define($"template @g{index} {Quote(template.Name)} <{string.Join(", ", template.TypeParameters.Select(Quote))}> ({Locations(template.Parameters)}){(template.ResultAnnotation is { } result ? " -> @t" + result.Value : "")} body @n{template.Body.Value} captures [{Captures(template.Captures)}]",
                "@g" + index, template.Name, "Bound generic semantic body; substitutions do not replay an AST.", HirTextCategory.Template, template.Source);
        }

        private void Function(int index)
        {
            var function = module.Functions[index];
            Define($"fn @f{index} {Quote(function.Name)} ({Locations(function.Parameters)}) -> @t{function.Result.Value} {(function.IsOpen ? "open" : "closed")}{(function.Template is { } template ? " template @g" + template.Value : "")} body {(function.Body.IsNone ? "none" : "@n" + function.Body.Value)} captures [{Captures(function.Captures)}]",
                "@f" + index, function.Name, "Finalized function identity and capture layout; bytecode locals are assigned later.", HirTextCategory.Function);
        }

        private void Node(int index)
        {
            HirNode node = module.Nodes[index];
            string operand = node.Kind switch
            {
                HirKind.Literal => "@c" + node.Data,
                HirKind.TypeValue or HirKind.Coerce or HirKind.Convert or HirKind.IsType => "@t" + node.Data,
                HirKind.ReadLocation or HirKind.ReadStorage or HirKind.Declare or HirKind.Assign => "@l" + node.Data,
                HirKind.Call => "@k" + node.Data,
                HirKind.Closure => "@u" + node.Data,
                HirKind.TemplateClosure => "@v" + node.Data,
                HirKind.AdaptFunction => "@a" + node.Data,
                HirKind.Scope => "@o" + node.Data,
                HirKind.For => "@j" + node.Data,
                HirKind.Match => "@m" + node.Data,
                HirKind.Barrier => "@z" + node.Data,
                HirKind.Range or HirKind.LazyRange => "@r" + node.Data,
                HirKind.Unary => ((HirUnaryOperator)node.Data).ToString(),
                HirKind.Binary => ((HirBinaryOperator)node.Data).ToString(),
                HirKind.Loop or HirKind.Break => "loop " + node.Data,
                _ => node.Data == 0 ? "" : "data " + node.Data
            };
            if (node.Text is { } text) operand += (operand.Length == 0 ? "" : " ") + Quote(text);
            Define($"@n{index}: {node.Kind} {operand} : @t{node.Type.Value} children [{string.Join(", ", module.Children(node).ToArray().Select(child => "@n" + child.Value))}] effects [{node.Effects}] origin @s{node.Source.Source.Value}[{node.Source.Start}..{(long)node.Source.Start + node.Source.Length})",
                "@n" + index, node.Kind + " @n" + index, $"Typed semantic {node.Kind}; effects: {node.Effects}.", HirTextCategory.Node, node.Source);
        }

        private static string Locations(ImmutableArray<HirLocationId> locations) => string.Join(", ", locations.OrEmpty().Select(item => "@l" + item.Value));
        private static string Captures(ImmutableArray<HirCapture> captures) => string.Join(", ", captures.OrEmpty().Select(capture =>
            $"@l{capture.Target.Value} <- {(capture.Mode == HirCaptureMode.Location ? "location @l" + capture.SourceLocation.Value : "value @n" + capture.SourceValue.Value)}"));
        private static string Targets(ImmutableArray<HirDispatchTarget> targets) => string.Join(", ", targets.OrEmpty().Select(target =>
            $"@{(target.Identity.IsTemplate ? "g" : "f")}{target.Identity.Value} -> @f{target.Target.Value}"));
        private static string Pattern(HirPattern pattern) => (pattern.Kind switch
        {
            HirPatternKind.Literal => "literal @c" + pattern.Data,
            HirPatternKind.Type => "type @t" + pattern.Data,
            HirPatternKind.Regex => "regex " + Quote(pattern.Text ?? ""),
            _ => pattern.Kind.ToString().ToLowerInvariant()
        }) + (pattern.Binding is { } binding ? " as @l" + binding.Value : "");

        private static string Literal(HirLiteral literal) => literal.Kind switch
        {
            HirLiteralKind.Null => "null",
            HirLiteralKind.Bool => literal.Boolean ? "true" : "false",
            HirLiteralKind.Number => $"number({literal.Number.ToString("R", CultureInfo.InvariantCulture)}, bits 0x{BitConverter.DoubleToInt64Bits(literal.Number):x16})",
            HirLiteralKind.String => Quote(literal.Text ?? ""),
            HirLiteralKind.Path => "path(" + Quote(literal.Text ?? "") + ")",
            HirLiteralKind.Connection => $"connection({literal.ConnectionKind}, {Quote(literal.Text ?? "")})",
            _ => "type(" + literal.Type + ")"
        };

        private void Emit(string text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add(text);
        }
        private void Define(string text, string identifier, string name, string detail, HirTextCategory category, HirSourceRange? source = null)
        {
            definitions.Add(new(identifier, name, detail, category, lines.Count, text.IndexOf(identifier, StringComparison.Ordinal), identifier.Length, source));
            Emit(text);
        }

        private ImmutableArray<HirTextReference> References()
        {
            var targets = definitions.ToDictionary(item => item.Identifier, StringComparer.Ordinal);
            ImmutableArray<HirTextReference>.Builder result = ImmutableArray.CreateBuilder<HirTextReference>();
            for (int line = 0; line < lines.Count; line++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text = lines[line];
                bool quoted = false;
                for (int at = 0; at < text.Length; at++)
                {
                    if (quoted && text[at] == '\\') { at++; continue; }
                    if (text[at] == '"') { quoted = !quoted; continue; }
                    if (quoted || text[at] != '@' || at + 2 >= text.Length || !char.IsAsciiLetter(text[at + 1]) || !char.IsAsciiDigit(text[at + 2])) continue;
                    int end = at + 3;
                    while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
                    string id = text[at..end];
                    if (targets.TryGetValue(id, out var target) && (line != target.Line || at != target.Character))
                        result.Add(new(id, line, at, end - at, target.Line, target.Character));
                    at = end - 1;
                }
            }
            return result.ToImmutable();
        }
    }
}
