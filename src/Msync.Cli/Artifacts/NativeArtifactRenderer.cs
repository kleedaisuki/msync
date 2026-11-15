using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Msync.Core;
using Msync.Core.Native;
using static Msync.Cli.ArtifactDisassembly;

namespace Msync.Cli;

// Inspection describes portable data only: it neither creates a runtime image
// nor invokes contextual preparation, source loading or host operations.
internal sealed partial class NativeArtifactRenderer(NativeProgram module, int version, string sourceUri, CancellationToken cancellationToken)
{
    private readonly StringBuilder text = new();
    private readonly List<string> lines = [];
    private readonly JsonArray symbols = [];
    private readonly JsonArray references = [];
    private readonly JsonArray hovers = [];
    private readonly Dictionary<string, (int Line, int Character)> definitions = new(StringComparer.Ordinal);
    private int line;

    internal ArtifactDisassemblyResult Render()
    {
        Emit("; msync typed IR — read-only disassembly");
        Emit("; Optimized source cannot be reconstructed from discarded instructions.");
        Emit("; Inspecting this document does not bind, optimize or execute the artifact.");
        Emit($"artifact {module.Stage.ToString().ToLowerInvariant()} format {version} language {NativeArtifact.LanguageVersion} representation {NativeProgram.Representation}");
        Emit($"optimization O{module.OptimizationLevel} semantics {Quote(module.Semantics.ToString())} entry @f{module.EntryPoint}");
        Emit($"origin {Quote(sourceUri)}");
        if (module.Context is { } context)
        {
            Emit("context {");
            Emit("  cwd = " + Quote(context.WorkingDirectory));
            Emit("  home = " + Quote(context.HomeDirectory));
            if (context.SourceFile is { } source) Emit("  source = " + Quote(source));
            Emit("}");
        }
        Tables();
        for (int index = 0; index < module.Constants.Count; index++)
            Table("const", "@c" + index, module.Constants[index].Kind + " = " + Constant(module.Constants[index]));
        for (int index = 0; index < (module.ExecutionPlans?.Count ?? 0); index++) Plan(index, module.ExecutionPlans![index]);
        for (int index = 0; index < module.Functions.Count; index++) Function(index);
        LinkReferences();
        return new(text.ToString(), version, module.Stage.ToString().ToLowerInvariant(), module.OptimizationLevel,
            symbols, references, hovers, ExecutionPlans: executionPlans);
    }

    private void Tables()
    {
        for (int id = 0; id < module.Types.Count; id++)
        {
            NativeCodeType type = module.Types[id];
            string element = type.Element < 0 ? "" : $" element @t{type.Element}";
            string pointee = type.Pointee < 0 ? "" : $" pointee @t{type.Pointee}";
            string layouts = type.Layouts is not { Count: > 0 } cases ? ""
                : " layouts [" + string.Join(", ", cases.Select(layout => "@l" + layout)) + "]";
            Table("type", "@t" + id, type.Kind + element + pointee + layouts);
        }
        for (int id = 0; id < module.Layouts.Count; id++)
            Table("layout", "@l" + id, "{ " + string.Join(", ", module.Layouts[id].Fields.Select((field, ordinal) =>
                $"#{ordinal} {Quote(field.Name)}: @t{field.Type}")) + " }");
        for (int id = 0; id < module.Projections.Count; id++)
            Table("place", "@d" + id, "[" + string.Join(", ", module.Projections[id].Cases.Select(field => $"@l{field.Layout}.#{field.Field}")) + "]");
        for (int id = 0; id < module.Statics.Count; id++)
            Table("static", "@s" + id, $"@l{module.Statics[id].Layout} declaration {module.Statics[id].Declaration}");
        foreach (SessionDispatchTable dispatch in module.SessionDispatches ?? [])
            Table("dispatch", "@q" + dispatch.Slot, "{ " + string.Join(", ", dispatch.Targets.Select(pair => $"@f{pair.Key} -> @f{pair.Value}")) + " }");
        foreach (NativeAllocationPlan allocation in module.Allocations)
            Emit($"allocation owner @f{allocation.Function} instruction {Block(allocation.Function, allocation.Instruction)} layout @l{allocation.Layout} placement {allocation.Placement}");
    }

    private void Table(string label, string identifier, string details)
    {
        int at = line, column = label.Length + 1;
        Emit($"{label} {identifier} = {details};");
        Define(identifier, at, column);
        symbols.Add((JsonNode)Symbol(identifier, details, 14, at, at, column, identifier.Length));
        Hover(at, column, identifier.Length, $"**{label} {identifier}**\n\n`{details}`\n\nPortable typed data; inspection does not instantiate live resources.");
    }

    private void Function(int index)
    {
        NativeFunctionCode function = module.Functions[index];
        string identifier = "@f" + index;
        string signature = "(" + string.Join(", ", function.Signature.Parameters.Select(type => "@t" + type)) + $") -> @t{function.Signature.Result}";
        Emit("");
        int start = line;
        Emit($"fn {identifier} {Quote(function.Name)}{signature} arguments {function.ParameterCount} captures {function.CaptureCount} locals {function.Slots.Count} template {function.IsTemplate.ToString().ToLowerInvariant()} {{");
        Define(identifier, start, 3);
        JsonArray children = [];
        JsonObject symbol = Symbol(function.Name + " (" + identifier + ")", signature, 12, start, start, 3, identifier.Length, children);
        symbols.Add((JsonNode)symbol);
        Hover(start, 3, identifier.Length, $"**{function.Name}**\n\n`{signature}`\n\nCapture slots precede parameters. Location captures preserve shared typed addresses; value captures copy descriptors.");
        for (int slot = 0; slot < function.Slots.Count; slot++)
        {
            NativeSlot contract = function.Slots[slot];
            string name = Slot(index, slot);
            int at = line;
            Emit($"  slot {name}: @t{contract.Type} {contract.Role} escapes {contract.Escapes.ToString().ToLowerInvariant()} {Quote(contract.Name ?? "")};");
            Define(name, at, 7);
            children.Add((JsonNode)Symbol(name, contract.Role.ToString(), 13, at, at, 7, name.Length));
        }
        for (int capture = 0; capture < function.Signature.Captures.Count; capture++)
        {
            NativeCaptureContract contract = function.Signature.Captures[capture];
            Emit($"  capture #{capture} {contract.Mode} @t{contract.Type} -> {Slot(index, capture)};");
        }
        for (int pc = 0; pc < function.Instructions.Count; pc++) Instruction(index, pc, function.Instructions[pc], children);
        string exit = Block(index, function.Instructions.Count);
        int end = line;
        Emit($"  exit {exit}:");
        Define(exit, end, 7);
        Emit("}");
        End(symbol, line - 1);
    }

    private void Instruction(int function, int pc, NativeInstruction instruction, JsonArray children)
    {
        string block = Block(function, pc);
        int at = line;
        Emit($"  block {block} successors [{string.Join(", ", Successors(pc, instruction).Select(next => Block(function, next)))}]:");
        Define(block, at, 8);
        JsonObject symbol = Symbol(block, instruction.Op.ToString(), 3, at, at, 8, block.Length);
        children.Add((JsonNode)symbol);
        string prefix = $"    {pc:D4}: ";
        string result = instruction.Result < 0 ? "" : Slot(function, instruction.Result) + " = ";
        string type = instruction.Type < 0 ? "" : $" type @t{instruction.Type}";
        string inputs = instruction.Inputs is not { } values ? "" : " inputs [" + string.Join(", ", values.Select(value => Slot(function, value))) + "]";
        int operationLine = line;
        Emit(prefix + result + instruction.Op + " " + Operands(function, instruction) + type + inputs
            + (instruction.Symbol is null ? "" : " symbol " + Quote(instruction.Symbol)) + $" encoded(a={instruction.A}, b={instruction.B}, c={instruction.C}, immediate={instruction.Immediate})");
        Hover(operationLine, prefix.Length + result.Length, instruction.Op.ToString().Length,
            $"**{instruction.Op}**\n\n{Description(instruction.Op)}");
        if (instruction.Source is { } origin) Origin(origin, 6);
        End(symbol, line - 1);
    }

    private static string Operands(int function, NativeInstruction instruction)
    {
        string Register(int value) => value < 0 ? value.ToString(CultureInfo.InvariantCulture) : Slot(function, value);
        return instruction.Op switch
        {
            NativeOp.Constant => "@c" + instruction.A,
            NativeOp.Allocate => $"@l{instruction.A}, allocation-site {instruction.B}",
            NativeOp.ProjectAddress => $"{Register(instruction.A)}, @d{instruction.B}",
            NativeOp.MakeFunction => instruction.A < 0 ? "native callable"
                : $"@f{instruction.A}" + (instruction.B < 0 ? ", signature environment" : $", environment @l{instruction.B}"),
            NativeOp.AdaptFunction or NativeOp.Call => Register(instruction.A)
                + (instruction.B < 0 ? ", dynamic callee" : $", specialized @f{instruction.B}")
                + (instruction.C < 0 ? "" : $", dispatch @q{instruction.C}")
                + (instruction.Symbol is null ? "" : ", dispatch " + Dispatch(instruction.Symbol)),
            NativeOp.Jump => Block(function, instruction.A),
            NativeOp.Branch => $"{Register(instruction.A)}, true -> {Block(function, instruction.B)}, false -> {Block(function, instruction.C)}",
            NativeOp.OpenIterator => $"{Register(instruction.A)}, iterator {instruction.B}",
            NativeOp.CloseIterator => $"iterator {instruction.A}",
            NativeOp.NextIterator => $"iterator {instruction.A}, exhausted -> {Block(function, instruction.B)}",
            NativeOp.TestStatic or NativeOp.StaticAddress or NativeOp.PublishStatic => "@s" + instruction.A,
            NativeOp.ExecutePlan or NativeOp.PlanResult => "@p" + instruction.A,
            NativeOp.AddressOffset => $"{Register(instruction.A)}, layout @l{instruction.B}, field #{instruction.C}",
            NativeOp.IndexAddress => $"{Register(instruction.A)}, index {Register(instruction.B)}, layout @l{instruction.C}",
            NativeOp.CopyMemory => $"destination {Register(instruction.A)}, source {Register(instruction.B)}, count {Register(instruction.C)}, layout @l{instruction.Immediate}",
            NativeOp.ClearMemory => $"{Register(instruction.A)}, count {Register(instruction.C)}, layout @l{instruction.Immediate}",
            NativeOp.RangeFromMemory => $"{Register(instruction.A)}, count {Register(instruction.B)}, layout @l{instruction.C}, field #{instruction.Immediate}",
            NativeOp.EnvironmentAddress => $"{Register(instruction.A)}, environment @l{instruction.B}",
            NativeOp.Unary => $"{(NativeUnary)instruction.Immediate} {Register(instruction.A)}"
                + ((NativeUnary)instruction.Immediate == NativeUnary.HasLayout ? $", @t{instruction.B}"
                    : (NativeUnary)instruction.Immediate == NativeUnary.IsType ? ", " + Register(instruction.B) : ""),
            NativeOp.Binary => $"{(NativeBinary)instruction.Immediate} {Register(instruction.A)}, {Register(instruction.B)}",
            NativeOp.Copy or NativeOp.Move or NativeOp.FrameAddress or NativeOp.Load or NativeOp.Convert or NativeOp.Narrow
                or NativeOp.Drop or NativeOp.Return or NativeOp.ProcessCall or NativeOp.SelectPath or NativeOp.Acquire
                or NativeOp.DeferRange => Register(instruction.A),
            NativeOp.Store or NativeOp.BindLocation => $"{Register(instruction.A)}, {Register(instruction.B)}",
            _ => ""
        };
    }

    private static IEnumerable<int> Successors(int pc, NativeInstruction instruction) => instruction.Op switch
    {
        NativeOp.Return or NativeOp.Unreachable => [],
        NativeOp.Jump => [instruction.A],
        NativeOp.Branch => new[] { instruction.B, instruction.C }.Distinct(),
        NativeOp.NextIterator => new[] { pc + 1, instruction.B }.Distinct(),
        _ => [pc + 1]
    };

    private static string Dispatch(string? encoded) => encoded is null ? "{}" :
        "{" + string.Join(", ", FunctionDispatch.Targets(encoded).Select(pair => $"@f{pair.Key} -> @f{pair.Value}")) + "}";

    private static string Description(NativeOp operation) => operation switch
    {
        NativeOp.Allocate => "Allocate a fresh logical namespace with its immutable typed field layout. Placement metadata does not change alias identity.",
        NativeOp.FrameAddress or NativeOp.BindLocation or NativeOp.EnvironmentAddress => "Form or bind a typed shared location without copying its stored value.",
        NativeOp.AddressOffset or NativeOp.ProjectAddress or NativeOp.IndexAddress => "Form an address using the compiled static layout or finite projection table; no runtime named-object lookup is required.",
        NativeOp.Load or NativeOp.Store => "Read or update the typed logical location. Existing aliases continue to address the same storage.",
        NativeOp.MakeFunction or NativeOp.AdaptFunction => "Create or specialize a callable with explicit value and location capture contracts.",
        NativeOp.TestStatic => "Read whether the named declaration has published its fields; this does not allocate or initialize it.",
        NativeOp.StaticAddress => "Expose the named declaration's stable typed allocation identity, allocating its storage once if needed.",
        NativeOp.PublishStatic => "Publish the initialized fields of this named declaration without replacing its allocation identity.",
        NativeOp.Binary => "Apply the encoded binary operator. Number arithmetic preserves IEEE 754 order and bits; Remainder uses a truncating quotient, not mathematical modulo.",
        NativeOp.Call or NativeOp.CallNative => "Invoke the selected typed callable; invocation-owned nonescaping work joins at return.",
        NativeOp.Transfer or NativeOp.ProcessCall => "Perform the selected host operation only during execution, never during artifact inspection.",
        NativeOp.ExecutePlan or NativeOp.PlanResult => "Execute or observe the referenced prepared dependency graph without replanning or testing filesystem freshness.",
        NativeOp.Jump or NativeOp.Branch or NativeOp.NextIterator => "Transfer control to the indicated instruction or iterator exhaustion target.",
        NativeOp.Return => "Return the typed result, preserving escaping captures, deferred ranges and Processes.",
        _ => "Typed native IR operation. Operands describe the portable native instruction, not reconstructed source."
    };

    private void LinkReferences()
    {
        for (int row = 0; row < lines.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string value = lines[row];
            bool quoted = false, escaped = false;
            int scan = 0;
            foreach (Match match in Identifier().Matches(value))
            {
                for (; scan < match.Index; scan++)
                {
                    char character = value[scan];
                    if (escaped) { escaped = false; continue; }
                    if (quoted && character == '\\') { escaped = true; continue; }
                    if (character == '"') quoted = !quoted;
                }
                if (quoted || !definitions.TryGetValue(match.Value, out var target)
                    || target == (row, match.Index)) continue;
                references.Add((JsonNode)new JsonObject
                {
                    ["range"] = Range(row, match.Index, match.Length),
                    ["targetLine"] = target.Line,
                    ["targetCharacter"] = target.Character
                });
            }
        }
    }

    [GeneratedRegex(@"@[a-z]+\d+(?:_\d+|[nb]\d+)?|%f\d+_\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    private void Origin(SourceSpan span, int indent)
    {
        string provenance = $"{new string(' ', indent)}; source {Quote(span.Source)} utf16 [{span.Start}..{span.Start + span.Length})";
        int at = line;
        Emit(provenance);
        JsonObject reference = new()
        {
            ["range"] = Range(at, indent + 2, provenance.Length - indent - 2),
            ["sourceName"] = span.Source,
            ["sourceStart"] = span.Start,
            ["sourceLength"] = span.Length
        };
        if (SourceUri(span.Source, module.Context?.SourceFile) is { } uri) reference["sourceUri"] = uri;
        references.Add((JsonNode)reference);
    }

    private static string Constant(NativeConstant value) => value.Kind switch
    {
        NativeKind.Null => "null",
        NativeKind.Bool => value.Boolean ? "true" : "false",
        NativeKind.Number => $"number({value.Number.ToString("R", CultureInfo.InvariantCulture)}, bits=0x{BitConverter.DoubleToUInt64Bits(value.Number):x16})",
        NativeKind.String => Quote(value.Text ?? ""),
        NativeKind.Connection => $"connection({value.ConnectionKind}, {Quote(value.Text ?? "")})",
        NativeKind.Path => PathText(value.Path ?? new NativePath(value.Text ?? "")),
        NativeKind.Type => "type(" + value.Type + ")",
        _ => throw new MsyncException($"Unsupported portable constant kind {value.Kind}")
    };

    private static string PathText(NativePath path) => "path(raw=" + Quote(path.Raw)
        + (path.LocalSpelling is { } local ? ", native=" + Quote(local) : "")
        + (path.Connection is { } endpoint ? $", endpoint=connection({endpoint.Kind}, {Quote(endpoint.Address)})" : "")
        + (path.WorkingDirectory is { } cwd ? ", cwd=" + Quote(cwd) : "")
        + (path.HomeDirectory is { } home ? ", home=" + Quote(home) : "") + ")";

    private static string PlanValue(NativePlanValue value) => value.Kind switch
    {
        NativeKind.Path => PathText(value.Path!.Value),
        NativeKind.Range => $"range(item-type={value.ItemType}, items=[{string.Join(", ", value.Items!.Select(PlanValue))}])",
        _ => Constant(value.Literal!)
    };

    private static string Block(int function, int pc) => $"@b{function}_{pc:D4}";
    private static string Slot(int function, int slot) => $"%f{function}_{slot}";
    private void Define(string identifier, int row, int column) => definitions.Add(identifier, (row, column));
    private void Emit(string value) { cancellationToken.ThrowIfCancellationRequested(); lines.Add(value); text.Append(value).Append('\n'); line++; }
    private void Hover(int row, int column, int length, string markdown) => hovers.Add((JsonNode)new JsonObject { ["range"] = Range(row, column, length), ["markdown"] = markdown });
    private JsonObject Symbol(string name, string detail, int kind, int start, int end, int column, int length, JsonArray? children = null)
    {
        JsonObject symbol = new()
        {
            ["name"] = name,
            ["detail"] = detail,
            ["kind"] = kind,
            ["range"] = new JsonObject { ["start"] = Position(start, 0), ["end"] = Position(end, lines[end].Length) },
            ["selectionRange"] = Range(start, column, length)
        };
        if (children is not null) symbol["children"] = children;
        return symbol;
    }
    private void End(JsonObject symbol, int end) => symbol["range"]!["end"] = Position(end, lines[end].Length);
}
