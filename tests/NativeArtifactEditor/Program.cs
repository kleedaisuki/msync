using System.Text.Json.Nodes;
using Msync.Cli;
using Msync.Core;
using Msync.Core.Native;

if (args is ["produce", var path])
{
    NativeArtifact.Write(Fixtures.Plan(), path);
    return;
}
int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
void Inspect(JsonObject document)
{
    string text = document["text"]!.GetValue<string>();
    string[] lines = text.Split('\n');
    string Spelling(JsonNode range)
    {
        int row = range["start"]!["line"]!.GetValue<int>();
        int column = range["start"]!["character"]!.GetValue<int>();
        int endRow = range["end"]!["line"]!.GetValue<int>();
        int endColumn = range["end"]!["character"]!.GetValue<int>();
        Check(row >= 0 && endRow >= row && endRow < lines.Length, "Editor range rows exceed text.");
        Check(column >= 0 && column <= lines[row].Length && endColumn >= 0 && endColumn <= lines[endRow].Length,
            "Editor UTF-16 columns exceed text.");
        return row == endRow ? lines[row][column..endColumn] : "";
    }
    void Symbols(JsonArray values)
    {
        foreach (JsonNode? symbol in values)
        {
            _ = Spelling(symbol!["range"]!);
            Check(Spelling(symbol["selectionRange"]!).Length > 0, "A symbol has no selectable identifier.");
            if (symbol["children"] is JsonArray children) Symbols(children);
        }
    }
    Symbols((JsonArray)document["symbols"]!);
    foreach (JsonNode? reference in (JsonArray)document["references"]!)
    {
        string token = Spelling(reference!["range"]!);
        if (reference["targetLine"] is not { } targetLine) continue;
        int column = reference["targetCharacter"]!.GetValue<int>();
        Check(lines[targetLine.GetValue<int>()][column..].StartsWith(token, StringComparison.Ordinal),
            "A reference does not select its exact definition token.");
    }
    foreach (JsonNode? hover in (JsonArray)document["hovers"]!)
        Check(Spelling(hover!["range"]!).Length > 0 && hover["markdown"]!.GetValue<string>().Length > 0,
            "Hover metadata is empty.");
}

NativeProgram plan = Fixtures.Plan();
using MemoryStream bytes = new();
NativeArtifact.Write(plan, bytes);
JsonObject model = ArtifactDisassembly.Read(bytes.ToArray(), "memfs:/missing/project.mexe").ToJson();
Inspect(model);
Check(model["formatVersion"]!.GetValue<int>() == NativeArtifact.FormatVersion, "Native artifact format was not preserved.");
Check(model["text"]!.GetValue<string>().Contains("native-v1", StringComparison.Ordinal), "Native representation was hidden.");
Check(model["text"]!.GetValue<string>().Contains("\\ud800", StringComparison.Ordinal), "Lone UTF-16 code unit was not represented exactly.");
JsonNode prepared = model["executionPlans"]![0]!;
Check(prepared["resultTransfers"]!.ToJsonString() == "[1,0]", "Selected plan results changed order.");
Check(prepared["transfers"]![1]!["predecessors"]!.ToJsonString() == "[0]", "Plan dependency was lost.");
Check(prepared["batches"]!.AsArray().Count == 2, "Plan batches were lost.");
using (MemoryStream literalBytes = new())
{
    NativeArtifact.Write(Fixtures.Literal(new(NativeKind.String, "@f0 @c0 %f0_0")), literalBytes);
    JsonObject literal = ArtifactDisassembly.Read(literalBytes.ToArray(), "memfs:/literal.mso").ToJson();
    Inspect(literal);
    string[] rows = literal["text"]!.GetValue<string>().Split('\n');
    int constantRow = Array.FindIndex(rows, row => row.StartsWith("const @c0", StringComparison.Ordinal));
    Check(!literal["references"]!.AsArray().Any(reference => reference!["range"]!["start"]!["line"]!.GetValue<int>() == constantRow),
        "Quoted literal identifiers became actionable internal references.");
}
foreach (string legacy in new[] { "tests/LegacyV6Native/Fixtures/aliases.mso", "tests/LegacyV6Native/Fixtures/aliases.mexe" })
{
    JsonObject imported = ArtifactDisassembly.Read(File.ReadAllBytes(legacy), new Uri(Path.GetFullPath(legacy)).AbsoluteUri).ToJson();
    Check(imported["formatVersion"]!.GetValue<int>() == 6, "Accepted format-6 identity was lost.");
    Inspect(imported);
}
using CancellationTokenSource cancellation = new();
cancellation.Cancel();
try
{
    _ = ArtifactDisassembly.Read(bytes.ToArray(), "memfs:/cancelled.mexe", cancellation.Token);
    throw new InvalidOperationException("Cancelled artifact inspection completed.");
}
catch (OperationCanceledException) { checks++; }
try
{
    _ = ArtifactDisassembly.Read([1, 2, 3], "memfs:/invalid.mso");
    throw new InvalidOperationException("Malformed artifact was accepted.");
}
catch (MsyncException error)
{
    Check(error.Message.Contains("memfs:/invalid.mso", StringComparison.Ordinal), "Artifact error lost its source URI.");
}
Console.WriteLine($"{checks} native/closed-format6 artifact editor contracts passed");
