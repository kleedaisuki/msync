using System.Globalization;
using System.Text.Json.Nodes;
using Msync.Core;
using Msync.Core.Hpir;
using static Msync.Cli.ArtifactDisassembly;

namespace Msync.Cli;

internal static class HpirArtifactRenderer
{
    internal static ArtifactDisassemblyResult Render(HpirProgram program, CancellationToken token)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        var definitions = new HpirListingSite[program.Functions.Count];
        List<HpirListingSite> calls = [];
        JsonArray references = [], symbols = [], hovers = [];
        HpirDisassembly.Write(program, text, Observe, token);
        for (int index = 0; index < definitions.Length; ++index)
        {
            var site = definitions[index];
            var function = program.Functions[index];
            string detail = $"frame={function.FrameBytes}, {function.Parameters.Count} physical parameters, result {function.Result}";
            JsonObject selection = Range(site.Line, site.Column, site.Length);
            symbols.Add((JsonNode)new JsonObject
            {
                ["name"] = function.Name,
                ["detail"] = detail,
                ["kind"] = 12,
                ["range"] = new JsonObject
                {
                    ["start"] = Position(site.Line, 0),
                    ["end"] = Position(site.Line + function.Instructions.Length + 1, 0)
                },
                ["selectionRange"] = selection
            });
            hovers.Add((JsonNode)new JsonObject { ["range"] = Range(site.Line, site.Column, site.Length),
                ["markdown"] = $"Physical HPIR entry @{index}; {detail}." });
        }
        foreach (var site in calls)
        {
            var target = definitions[site.CalledFunction];
            references.Add((JsonNode)new JsonObject { ["range"] = Range(site.Line, site.CallColumn, site.CallLength),
                ["targetLine"] = target.Line, ["targetCharacter"] = target.Column });
        }
        return new(text.ToString(), HpirArtifact.FormatVersion, Stage(program), program.OptimizationLevel >= 0 ? program.OptimizationLevel : null, symbols, references, hovers, ArtifactKind: "hpir");

        void Observe(HpirListingSite site)
        {
            if (site.Instruction < 0) { definitions[site.Function] = site; return; }
            if (site.CalledFunction >= 0) calls.Add(site);
            if (program.Functions[site.Function].Instructions[site.Instruction].Op == HpirOp.AddressOfConnection)
                hovers.Add((JsonNode)new JsonObject { ["range"] = Range(site.Line, site.Column, site.Length),
                    ["markdown"] = "Use `connection @ path` for qualification. `value::member` is an Object member projection." });
            if (site.Source is not { } source) return;
            JsonObject reference = new() { ["range"] = Range(site.Line, site.Column, site.Length),
                ["sourceName"] = source.Source, ["sourceStart"] = source.Start, ["sourceLength"] = source.Length };
            if (SourceUri(source.Source, program.Context?.SourceFile) is { } uri) reference["sourceUri"] = uri;
            references.Add((JsonNode)reference);
        }
    }

    internal static JsonObject Structured(HpirProgram program)
    {
        JsonArray functions = [];
        foreach (var function in program.Functions)
        {
            JsonArray code = [];
            foreach (var instruction in function.Instructions)
                code.Add((JsonNode)new JsonObject { ["op"] = instruction.Op.ToString(), ["d"] = instruction.D,
                    ["a"] = instruction.A, ["b"] = instruction.B,
                    ["bits"] = unchecked((ulong)instruction.Immediate).ToString("x16", CultureInfo.InvariantCulture) });
            JsonArray calls = [];
            foreach (var call in function.Calls)
                calls.Add((JsonNode)new JsonObject { ["target"] = call.Target, ["arguments"] = Offsets(call.Arguments) });
            JsonArray safepoints = [];
            foreach (var root in function.Safepoints)
                safepoints.Add((JsonNode)new JsonObject { ["instruction"] = root.Instruction,
                    ["roots"] = Offsets(root.RootOffsets), ["borrows"] = Offsets(root.BorrowOffsets) });
            functions.Add((JsonNode)new JsonObject { ["name"] = function.Name, ["frameBytes"] = function.FrameBytes,
                ["resultWidth"] = (int)function.Result, ["instructions"] = code, ["calls"] = calls, ["safepoints"] = safepoints });
        }
        JsonArray imports = [];
        foreach (var import in program.Imports)
        {
            JsonObject signature = new() { ["service"] = import.Service.ToString(), ["binding"] = import.Binding,
                ["resultWidth"] = (int)import.Result, ["argumentWidths"] = Offsets(import.Arguments.Select(width => (int)width)) };
            if (import.Service == HpirService.ProcessStart)
            {
                JsonArray argv = [];
                foreach (var argument in import.ProcessArguments) argv.Add((JsonNode?)argument.ToString());
                signature["processArguments"] = argv;
            }
            imports.Add((JsonNode)signature);
        }
        JsonArray layouts = [], globals = [], types = [], checks = [];
        foreach (var layout in program.Layouts)
            layouts.Add((JsonNode)new JsonObject { ["bytes"] = layout.Size, ["alignment"] = layout.Alignment,
                ["roots"] = Offsets(layout.PointerOffsets), ["borrows"] = Offsets(layout.BorrowOffsets) });
        foreach (var global in program.Globals)
            globals.Add((JsonNode)new JsonObject { ["declaration"] = global.Declaration, ["layout"] = global.Layout });
        foreach (var type in program.TypeLiterals)
            types.Add((JsonNode)new JsonObject { ["kinds"] = type.Kinds.ToString(), ["element"] = type.Element });
        foreach (var check in program.ContextChecks)
        {
            JsonObject observation = new() { ["operation"] = HpirFileMetadataImports.NativeName(check.Operation),
                ["path"] = check.Path.Raw,
                ["state"] = check.Value.HasValue ? "frozen" : program.Stage == HpirStage.Relocatable ? "recipe" : "residual" };
            if (check.Value is { } result) observation["value"] = result;
            checks.Add((JsonNode)observation);
        }
        JsonArray selections = [];
        foreach (var selection in program.IteratorSelections)
        {
            JsonObject item = new() { ["directory"] = selection.Directory.Raw, ["pattern"] = selection.Pattern,
                ["explicitLocal"] = selection.ExplicitLocal,
                ["state"] = selection.Members is not null ? "frozen" : program.Stage == HpirStage.Relocatable ? "recipe" : "residual" };
            if (selection.Members is { } members)
            {
                JsonArray names = [];
                foreach (var member in members) names.Add((JsonNode?)member.Raw);
                item["members"] = names;
            }
            selections.Add((JsonNode)item);
        }
        // The source/hover envelope is part of the disassemble JSON contract.
        // Physical graph fields extend it instead of replacing it.
        JsonObject document = Render(program, CancellationToken.None).ToJson();
        document["representation"] = "hpir";
        document["languageVersion"] = LanguageContract.Version;
        document["entryPoint"] = program.EntryPoint;
        document["optimizationLevel"] = program.OptimizationLevel;
        document["functions"] = functions;
        document["imports"] = imports;
        document["layouts"] = layouts;
        document["globals"] = globals;
        JsonArray literals = [];
        foreach (string text in program.TextLiterals) literals.Add((JsonNode?)text);
        document["textLiterals"] = literals;
        document["typeLiterals"] = types;
        document["typeCells"] = Offsets(program.TypeCells);
        document["contextChecks"] = checks;
        document["iteratorSelections"] = selections;
        if (program.Context is { } context)
            document["context"] = new JsonObject { ["workingDirectory"] = context.WorkingDirectory,
                ["homeDirectory"] = context.HomeDirectory, ["sourceFile"] = context.SourceFile };
        return document;
    }

    private static string Stage(HpirProgram program) => program.Stage.ToString().ToLowerInvariant();

    private static JsonArray Offsets(IEnumerable<int> offsets)
    {
        JsonArray array = [];
        foreach (int offset in offsets) array.Add((JsonNode?)offset);
        return array;
    }
}
