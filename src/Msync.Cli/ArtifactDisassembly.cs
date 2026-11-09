using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Msync.Core;

namespace Msync.Cli;

internal sealed record ArtifactDisassemblyResult(string Text, int FormatVersion, string Stage,
    int OptimizationLevel, JsonArray Symbols, JsonArray References, JsonArray Hovers)
{
    internal JsonObject ToJson() => new()
    {
        ["schemaVersion"] = 1,
        ["formatVersion"] = FormatVersion,
        ["languageVersion"] = Artifact.LanguageVersion,
        ["stage"] = Stage,
        ["optimizationLevel"] = OptimizationLevel,
        ["text"] = Text,
        ["symbols"] = Symbols,
        ["references"] = References,
        ["hovers"] = Hovers
    };
}

internal static class ArtifactDisassembly
{
    internal static ArtifactDisassemblyResult Read(byte[] bytes, string sourceUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using MemoryStream stream = new(bytes, writable: false);
        Module module;
        try { module = Artifact.Read(stream); }
        catch (Exception error) when (error is MsyncException or IOException or ArgumentException or OverflowException or InvalidOperationException)
        { throw new MsyncException($"Cannot disassemble {sourceUri}: {error.Message}", inner: error); }
        int version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        return new Renderer(module, version, sourceUri, cancellationToken).Render();
    }

    internal static string Quote(string value)
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

    private sealed class Renderer(Module module, int version, string sourceUri, CancellationToken cancellationToken)
    {
        private readonly StringBuilder text = new();
        private readonly List<string> lines = [];
        private readonly JsonArray symbols = [];
        private readonly JsonArray references = [];
        private readonly JsonArray hovers = [];
        private readonly Dictionary<string, (int Line, int Character)> definitions = new(StringComparer.Ordinal);
        private readonly List<(JsonObject Reference, string Target)> pending = [];
        private readonly Dictionary<(int Function, int Position), BasicBlock> blocks =
            (module.Blocks ?? []).ToDictionary(block => (block.Function, block.Start));
        private int line;

        internal ArtifactDisassemblyResult Render()
        {
            Emit("; msync typed IR — read-only disassembly");
            Emit("; Optimized source cannot be reconstructed from discarded instructions.");
            Emit($"; Inspecting this document does not bind, optimize or execute the artifact.");
            Emit($"artifact {module.Stage.ToString().ToLowerInvariant()} format {version} language {Artifact.LanguageVersion}");
            Emit($"optimization O{module.OptimizationLevel} semantics {Quote(module.Semantics.ToString())} entry @f{module.EntryPoint}");
            Reference(line - 1, "entry @f" + module.EntryPoint, 6, "@f" + module.EntryPoint);
            Emit($"origin {Quote(sourceUri)}");
            if (module.Context is { } context)
            {
                Emit("context {");
                Emit($"  cwd = {Quote(context.WorkingDirectory)}");
                Emit($"  home = {Quote(context.HomeDirectory)}");
                if (context.SourceFile is { } source) Emit($"  source = {Quote(source)}");
                Emit("}");
            }
            Emit("");
            Emit($"; {module.Constants.Count} constants, {module.Functions.Count} functions");
            for (int index = 0; index < module.Constants.Count; index++) Constant(index);
            for (int index = 0; index < module.Functions.Count; index++) Function(index);
            foreach (var (reference, target) in pending)
                if (definitions.TryGetValue(target, out var destination))
                {
                    reference["targetLine"] = destination.Line;
                    reference["targetCharacter"] = destination.Character;
                    references.Add((JsonNode)reference);
                }
            return new(text.ToString(), version, module.Stage.ToString().ToLowerInvariant(), module.OptimizationLevel, symbols, references, hovers);
        }

        private void Constant(int index)
        {
            string identifier = "@c" + index;
            Value value = module.Constants[index];
            int at = line;
            Emit($"const {identifier}: {value.Type} = {ValueText(value)};");
            definitions[identifier] = (at, 6);
            symbols.Add((JsonNode)Symbol(identifier, value.Type.ToString(), 14, at, line - 1, 6, identifier.Length));
            hovers.Add((JsonNode)new JsonObject { ["range"] = Range(at, 6, identifier.Length), ["markdown"] = $"**Constant {identifier}** · `{value.Type}`\n\nThe declaration shows the exact stored value and path provenance. Loading a Scope constant constructs a fresh instance; it does not share a live mutable object." });
        }

        private void Function(int index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FunctionCode function = module.Functions[index];
            string identifier = "@f" + index;
            string parameters = string.Join(", ", Enumerable.Range(0, function.ParameterCount).Select(parameter =>
                $"%{function.CaptureCount + parameter}: {function.ParameterTypes?[parameter] ?? TypeRef.Any}"));
            string signature = $"({parameters}) -> {function.ReturnType}";
            Emit("");
            int start = line;
            Emit($"fn {identifier} {Quote(function.Name)}{signature} captures {function.CaptureCount} locals {function.LocalCount} {{");
            definitions[identifier] = (start, 3);
            JsonArray children = [];
            JsonObject symbol = Symbol(function.Name + " (" + identifier + ")", signature, 12, start, start, 3, identifier.Length, children);
            symbols.Add((JsonNode)symbol);
            string captures = function.CaptureCount == 0 ? "No captured slots." : $"Capture slots: `%0`…`%{function.CaptureCount - 1}`.";
            hovers.Add((JsonNode)new JsonObject { ["range"] = Range(start, 3, identifier.Length), ["markdown"] = $"**{function.Name}**\n\n`{signature}`\n\n{captures} Parameter slots follow captures. Total local slots: {function.LocalCount}." });
            JsonObject? blockSymbol = null;
            for (int position = 0; position < function.Instructions.Count; position++)
            {
                if (blocks.TryGetValue((index, position), out var block))
                {
                    if (blockSymbol is not null) End(blockSymbol, line - 1);
                    string name = Block(index, position);
                    int at = line;
                    Emit($"  block {name} [{block.Start}..{block.End}) {(block.HasEffects ? "effects" : "pure")} successors [{string.Join(", ", block.Successors.Select(successor => Block(index, successor)))}]:");
                    definitions[name] = (at, 8);
                    blockSymbol = Symbol(name, block.HasEffects ? "observable effects" : "pure instructions", 3, at, at, 8, name.Length);
                    children.Add((JsonNode)blockSymbol);
                    foreach (int successor in block.Successors) Reference(at, "successors [", 0, Block(index, successor), occurrence: Block(index, successor));
                }
                Instruction(index, position, function.Instructions[position]);
            }
            if (blockSymbol is not null) End(blockSymbol, line - 1);
            // A legal branch can target the function's end rather than an instruction.
            if (function.Instructions.Any(instruction => instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse && instruction.A == function.Instructions.Count
                || instruction.Op == OpCode.IteratorNext && instruction.B == function.Instructions.Count))
            {
                string exit = Block(index, function.Instructions.Count);
                int at = line;
                Emit($"  exit {exit}:");
                definitions[exit] = (at, 7);
                children.Add((JsonNode)Symbol(exit, "function exit", 3, at, at, 7, exit.Length));
            }
            Emit("}");
            End(symbol, line - 1);
        }

        private void Instruction(int function, int position, Instruction instruction)
        {
            int at = line;
            string operation = instruction.Op.ToString();
            string operands = Operands(function, instruction);
            string prefix = $"    {position:D4}: ";
            Emit(prefix + operation + (operands.Length == 0 ? "" : " " + operands));
            hovers.Add((JsonNode)new JsonObject { ["range"] = Range(at, prefix.Length, operation.Length), ["markdown"] = $"**{operation}**\n\n{Description(instruction)}" });
            foreach (var target in Targets(function, instruction)) Reference(at, prefix, prefix.Length, target);
            if (instruction.Span is not { } span) return;
            string provenance = $"      ; source {Quote(span.Source)} utf16 [{span.Start}..{span.Start + span.Length})";
            int sourceLine = line;
            Emit(provenance);
            JsonObject sourceReference = new()
            {
                ["range"] = Range(sourceLine, 8, provenance.Length - 8),
                ["sourceName"] = span.Source,
                ["sourceStart"] = span.Start,
                ["sourceLength"] = span.Length
            };
            if (SourceUri(span.Source) is { } uri) sourceReference["sourceUri"] = uri;
            references.Add((JsonNode)sourceReference);
        }

        private string Operands(int function, Instruction instruction)
        {
            string Text() => Quote(instruction.Text ?? "");
            return instruction.Op switch
            {
                OpCode.Constant => "@c" + instruction.A,
                OpCode.LoadLocal or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.LoadCell or OpCode.LoadStorage => "%" + instruction.A,
                OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse => Block(function, instruction.A),
                OpCode.IteratorNext => $"%{instruction.A}, exhausted -> {Block(function, instruction.B)}",
                OpCode.MakeClosure => $"@f{instruction.A}, captures {instruction.B}",
                OpCode.Call when instruction.Text is not null => $"arguments {instruction.A}, dispatch {{{Dispatch(instruction.Text)}}}",
                OpCode.Call when instruction.B > 0 => $"arguments {instruction.A}, specialized @f{instruction.B - 1}",
                OpCode.Call => $"arguments {instruction.A}, dynamic callee",
                OpCode.ProcessCall => $"arguments {instruction.A}, {((module.Semantics & ModuleSemantics.DirectionalScheduling) != 0 && (instruction.B & 1) != 0 && (instruction.B & 2) == 0 ? "release" : "join")} flags {instruction.B}",
                OpCode.NativeCall => $"{Text()}, arguments {instruction.A}",
                OpCode.LoadBuiltin => "$" + (instruction.Text ?? "").TrimStart('$'),
                OpCode.MakeRange => "items " + instruction.A,
                OpCode.MakeLazyRange => $"items {instruction.A}, deferred-mask {Text()}" + (instruction.B > 0 ? $", item-type @c{instruction.B - 1}" : ""),
                OpCode.MakeScope => $"fields [{string.Join(", ", (instruction.Text ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(Quote))}], allocation-site {instruction.B}",
                OpCode.GetMember or OpCode.SetMember => Text() + (instruction.A > 0 ? $", slot #{instruction.A - 1} (layout-checked hint)" : ""),
                OpCode.GetCapture => "capture " + instruction.A,
                OpCode.SpecializeClosure when instruction.Text is not null => "dispatch {" + Dispatch(instruction.Text) + "}",
                OpCode.SpecializeClosure => "@f" + instruction.A,
                OpCode.RegexMatch => instruction.Text is null ? "dynamic pattern" : Text(),
                OpCode.SelectPath => "pattern " + Text(),
                OpCode.Convert => "to " + Text(),
                OpCode.DeferRange => "item-type " + Text(),
                OpCode.Barrier => $"{Direction(instruction.A)}" + (instruction.B != 0 ? ", region-start" : "") + (instruction.Text is { } region ? ", region " + Quote(region) : ""),
                OpCode.Acquire => instruction.A == 0 ? "session-owned pending work" : "selected process",
                _ when instruction.A != 0 || instruction.B != 0 || instruction.Text is not null => $"a={instruction.A}, b={instruction.B}" + (instruction.Text is null ? "" : ", text=" + Text()),
                _ => ""
            };
        }

        private static string Dispatch(string value) => string.Join(", ", FunctionDispatch.Parse(value).Select(pair => $"@f{pair.Key} -> @f{pair.Value}"));
        private static string Direction(int value) => value switch { 1 => "before (release)", 2 => "after (acquire)", 3 => "before-and-after (acqrel)", _ => "flags " + value };

        private static IEnumerable<string> Targets(int function, Instruction instruction)
        {
            if (instruction.Op == OpCode.Constant) yield return "@c" + instruction.A;
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse) yield return Block(function, instruction.A);
            if (instruction.Op == OpCode.IteratorNext) yield return Block(function, instruction.B);
            if (instruction.Op == OpCode.MakeClosure || instruction.Op == OpCode.SpecializeClosure && instruction.Text is null) yield return "@f" + instruction.A;
            if (instruction.Op == OpCode.Call && instruction.B > 0) yield return "@f" + (instruction.B - 1);
            if (instruction.Op is OpCode.Call or OpCode.SpecializeClosure && instruction.Text is not null)
                foreach (var pair in FunctionDispatch.Parse(instruction.Text)) { yield return "@f" + pair.Key; yield return "@f" + pair.Value; }
            if (instruction.Op == OpCode.MakeLazyRange && instruction.B > 0) yield return "@c" + (instruction.B - 1);
        }

        private void Reference(int at, string prefix, int start, string target, string? occurrence = null)
        {
            string current = Line(at);
            int offset = Find(current, occurrence ?? target, occurrence is null ? start : current.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length);
            if (offset < 0) return;
            pending.Add((new JsonObject { ["range"] = Range(at, offset, target.Length) }, target));
        }

        private static int Find(string text, string token, int start)
        {
            while (start < text.Length)
            {
                int at = text.IndexOf(token, start, StringComparison.Ordinal);
                if (at < 0) return -1;
                int next = at + token.Length;
                if (next == text.Length || !char.IsLetterOrDigit(text[next]) && text[next] != '_') return at;
                start = next;
            }
            return -1;
        }

        private string? SourceUri(string source)
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var explicitUri)
                && (explicitUri.IsFile || explicitUri.Scheme is "msync-stdlib" or "vscode-remote")) return explicitUri.AbsoluteUri;
            if (source.StartsWith('<')) return null;
            if (module.Context?.SourceFile is { } bound && Path.IsPathFullyQualified(bound)
                && (source == bound || Path.GetFileName(source) == Path.GetFileName(bound)))
                return new Uri(Path.GetFullPath(bound)).AbsoluteUri;
            return Path.IsPathFullyQualified(source) ? new Uri(source).AbsoluteUri : null;
        }

        private static string ValueText(Value value) => value switch
        {
            NullValue => "null",
            BoolValue boolean => boolean.Data ? "true" : "false",
            StringValue literal => Quote(literal.Data),
            NumberValue number => $"number({number.Data.ToString("R", CultureInfo.InvariantCulture)}, bits=0x{BitConverter.DoubleToUInt64Bits(number.Data):x16})",
            ConnectionValue connection => $"connection({connection.Kind}, {Quote(connection.Address)})",
            PathValue path => $"path(raw={Quote(path.Raw)}" + (path.LocalSpelling is { } local ? ", native=" + Quote(local) : "")
                + (path.Connection is { } endpoint ? ", endpoint=" + ValueText(endpoint) : "")
                + (path.WorkingDirectory is { } cwd ? ", cwd=" + Quote(cwd) : "")
                + (path.HomeDirectory is { } home ? ", home=" + Quote(home) : "") + ")",
            TypeValue type => "type(" + type.Data + ")",
            FunctionValue function => function.NativeName is { } native ? "native(" + Quote(native) + ")"
                : $"closure(@f{function.Function}, [{string.Join(", ", (function.Captures ?? []).Select(ValueText))}])",
            ScopeValue scope => "scope { " + string.Join(", ", scope.Members.Select(pair => Quote(pair.Key) + ": " + ValueText(pair.Value))) + " }",
            _ => value.Type + "(" + Quote(value.ToString() ?? "") + ")"
        };

        private static string Description(Instruction instruction) => instruction.Op switch
        {
            OpCode.Constant => "Push an immutable constant or a fresh construction from the constant table.",
            OpCode.LoadLocal => "Push the current local value; captured cells are dereferenced.",
            OpCode.LoadCell => "Push the shared capture location, promoting an uncaptured local when necessary.",
            OpCode.LoadStorage => "Push the local storage representation without losing a shared cell's identity.",
            OpCode.StoreLocal => "Pop a value and update the existing local location; aliases retain the same location.",
            OpCode.InitializeLocal => "Pop a value into a fresh logical local location, rather than mutating an older captured cell.",
            OpCode.Transfer or OpCode.LocalTransfer => "Pop destination and source; schedule their transfer in dependency order and push its Bool result.",
            OpCode.Call => "Pop arguments and the callable, invoke its selected typed specialization, and push the result. Invocation-owned jobs join at its return.",
            OpCode.ProcessCall => "Pop arguments and executable, launch the native process, and push Process. The release flag controls whether launch waits for completion.",
            OpCode.MakeScope => "Pop declared field values and create a fresh lexical Scope. Raw captured cells retain their shared identities.",
            OpCode.GetMember => "Pop a receiver and push its named member value.",
            OpCode.SetMember => "Pop assigned value and receiver, update the existing Scope field, and push Null.",
            OpCode.MakeClosure => "Pop captured locations and create a callable with the indicated bytecode identity.",
            OpCode.Barrier => "Apply the specified directional ordering boundary; paired region markers retain statement ownership.",
            OpCode.Acquire => "Wait for the selected Process or the session-owned pending frontier, not unrelated external jobs.",
            OpCode.Return => "Return the top value and join pending work owned by this invocation.",
            OpCode.IteratorNext => "Advance the indicated iterator slot; push its next value or branch to the exhausted target.",
            OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse => "Transfer control to the block label; conditional branches consume a Bool.",
            OpCode.NativeCall => "Invoke the named registered host operation with the indicated argument count.",
            OpCode.Pop => "Discard the top value without removing its producer's observable effects.",
            _ => "Typed stack operation. Operands name the bytecode's actual locations and targets; no source expression is reconstructed."
        };

        private static string Block(int function, int position) => $"@b{function}_{position:D4}";
        private void Emit(string value) { cancellationToken.ThrowIfCancellationRequested(); lines.Add(value); text.Append(value).Append('\n'); line++; }
        private string Line(int index) => lines[index];
        private static JsonObject Position(int row, int column) => new() { ["line"] = row, ["character"] = column };
        private static JsonObject Range(int row, int column, int length) => new() { ["start"] = Position(row, column), ["end"] = Position(row, column + length) };
        private JsonObject Symbol(string name, string detail, int kind, int start, int end, int column, int length, JsonArray? children = null)
        {
            JsonObject symbol = new()
            {
                ["name"] = name,
                ["detail"] = detail,
                ["kind"] = kind,
                ["range"] = new JsonObject { ["start"] = Position(start, 0), ["end"] = Position(end, Line(end).Length) },
                ["selectionRange"] = Range(start, column, length)
            };
            if (children is not null) symbol["children"] = children;
            return symbol;
        }
        private void End(JsonObject symbol, int end) => symbol["range"]!["end"] = Position(end, Line(end).Length);
    }
}
