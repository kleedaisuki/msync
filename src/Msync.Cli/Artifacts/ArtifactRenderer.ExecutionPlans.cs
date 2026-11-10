using System.Text.Json.Nodes;
using Msync.Core;

namespace Msync.Cli;

internal sealed partial class ArtifactRenderer
{
    private readonly JsonArray executionPlans = [];

    private void Plan(int index, ExecutionPlan plan)
    {
        string identifier = "@p" + index;
        Emit("");
        int start = line;
        Emit($"execution_plan {identifier} owner @f{plan.Function} region [{plan.Start}..{plan.End}) eliminated {plan.EliminatedTransfers} {{");
        definitions[identifier] = (start, 15);
        Reference(start, "owner ", 0, "@f" + plan.Function);
        JsonArray children = [], transfers = [], batches = [];
        JsonObject symbol = Symbol(identifier, $"{plan.Transfers.Count} transfers, {plan.Batches.Count} batches", 3,
            start, start, 15, identifier.Length, children);
        symbols.Add((JsonNode)symbol);
        hovers.Add((JsonNode)new JsonObject
        {
            ["range"] = Range(start, 15, identifier.Length),
            ["markdown"] = "**Contextual execution plan**\n\nConcrete operations and dependencies prepared by the backend. The VM executes this graph without checking whether its filesystem observations are still current. A failed operation reports its actual error."
        });
        PlanResults(identifier, plan.ResultTransfers);
        foreach (PlannedTransfer transfer in plan.Transfers)
            transfers.Add((JsonNode)PlanTransfer(identifier, transfer, children));
        for (int batch = 0; batch < plan.Batches.Count; batch++)
            batches.Add((JsonNode)PlanBatch(identifier, batch, plan.Batches[batch]));
        Emit("}");
        End(symbol, line - 1);
        executionPlans.Add((JsonNode)new JsonObject
        {
            ["id"] = index,
            ["function"] = plan.Function,
            ["start"] = plan.Start,
            ["end"] = plan.End,
            ["eliminatedTransfers"] = plan.EliminatedTransfers,
            ["resultTransfers"] = plan.ResultTransfers is { } observed ? Integers(observed) : null,
            ["transfers"] = transfers,
            ["batches"] = batches
        });
    }

    private void PlanResults(string plan, IReadOnlyList<int>? results)
    {
        if (results is null) return;
        int at = line;
        Emit($"  result all [{string.Join(", ", results.Select(result => plan + "n" + result))}]");
        foreach (int result in results) Reference(at, "result all [", 0, plan + "n" + result);
    }

    private JsonObject PlanTransfer(string plan, PlannedTransfer transfer, JsonArray symbols)
    {
        string node = plan + "n" + transfer.Id;
        int at = line;
        Emit($"  transfer {node} {transfer.Kind} mode {transfer.Mode} predecessors [{string.Join(", ", transfer.Predecessors.Select(predecessor => plan + "n" + predecessor))}]");
        definitions[node] = (at, 11);
        foreach (int predecessor in transfer.Predecessors) Reference(at, "predecessors [", 0, plan + "n" + predecessor);
        Emit("    source = " + ValueText(transfer.Source));
        Emit("    destination = " + ValueText(transfer.Destination));
        foreach (PlanResource read in transfer.Reads) Emit("    read " + ResourceText(read));
        foreach (PlanResource write in transfer.Writes) Emit("    write " + ResourceText(write));
        symbols.Add((JsonNode)Symbol(node, transfer.Kind.ToString(), 12, at, line - 1, 11, node.Length));
        JsonObject result = new()
        {
            ["id"] = transfer.Id,
            ["kind"] = transfer.Kind.ToString(),
            ["mode"] = transfer.Mode.ToString(),
            ["source"] = ValueText(transfer.Source),
            ["destination"] = ValueText(transfer.Destination),
            ["reads"] = Resources(transfer.Reads),
            ["writes"] = Resources(transfer.Writes),
            ["predecessors"] = Integers(transfer.Predecessors)
        };
        if (transfer.Span is not { } span) return result;
        Origin(span, 4);
        result["sourceSpan"] = new JsonObject { ["source"] = span.Source, ["start"] = span.Start, ["length"] = span.Length };
        return result;
    }

    private JsonObject PlanBatch(string plan, int index, ExecutionBatch batch)
    {
        int at = line;
        string name = plan + "b" + index;
        Emit($"  batch {name} endpoint {ArtifactDisassembly.Quote(batch.Endpoint)} transfers [{string.Join(", ", batch.Transfers.Select(transfer => plan + "n" + transfer))}]");
        foreach (int transfer in batch.Transfers) Reference(at, "transfers [", 0, plan + "n" + transfer);
        return new() { ["endpoint"] = batch.Endpoint, ["transfers"] = Integers(batch.Transfers) };
    }

    private static string ResourceText(PlanResource resource) =>
        $"endpoint {ArtifactDisassembly.Quote(resource.Endpoint)} path {ArtifactDisassembly.Quote(resource.Path)} {(resource.Subtree ? "subtree" : "exact")}";

    private static JsonObject ResourceJson(PlanResource resource) =>
        new() { ["endpoint"] = resource.Endpoint, ["path"] = resource.Path, ["subtree"] = resource.Subtree };

    private static JsonArray Resources(IReadOnlyList<PlanResource> resources) =>
        new(resources.Select(resource => (JsonNode?)ResourceJson(resource)).ToArray());

    private static JsonArray Integers(IReadOnlyList<int> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
