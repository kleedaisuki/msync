using System.Text;
using Msync.Core;
using Msync.Runtime;

namespace Msync.Components;

internal static class MemoryPipelineWorkloads
{
    internal static bool IsMemory(string name) => name is "memory_branches" or "memory_loop" or "layout_factory";

    internal static void Prepare(string name, string root)
    {
        if (name != "memory_branches") return;
        string directory = Path.Combine(root, ".temp", "memory-performance");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "selected.flag"), "selected");
    }

    internal static bool TrySource(string name, int size, out string source)
    {
        source = name switch
        {
            "memory_branches" => Branches(size),
            "memory_loop" => Loop(size),
            "layout_factory" => Factory(size),
            _ => ""
        };
        return IsMemory(name);
    }

    private static string Branches(int size)
    {
        StringBuilder source = new("let selected = exists ./.temp/memory-performance/selected.flag;\n");
        for (int index = 0; index < size; index++)
            source.AppendLine($"let box_{index} = scope {{ file: ./before_{index}, marker: 0 }}; if selected {{ box_{index}::file = ./input_{index}; }} else {{ box_{index}::file = ./other_{index}; }};");
        source.Append("if ");
        source.AppendJoin(" and ", Enumerable.Range(0, size).Select(index => $"box_{index}::file == ./input_{index}"));
        return source.Append($" {{ ./input_{size - 1} }} else {{ ./incorrect }}").ToString();
    }

    private static string Loop(int size)
    {
        StringBuilder source = new("let box = scope { value: 0, total: 0 }; for item in [");
        source.AppendJoin(", ", Enumerable.Range(1, size));
        source.Append("] { if item > 0 { box::value = item; } else { box::value = 0; }; box::total = box::total + box::value; };\n");
        double expected = (double)size * (size + 1) / 2;
        return source.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"if box::total == {expected} {{ ./input_{size - 1} }} else {{ ./incorrect }}").ToString();
    }

    private static string Factory(int size)
    {
        StringBuilder source = new("fn make(file: Path) { scope { file: file, marker: 0, optional?: null } };\n[");
        source.AppendJoin(", ", Enumerable.Range(0, size).Select(index => $"make(./input_{index})"));
        return source.Append(']').ToString();
    }

    internal static object Execute(Msync.Core.Module module, ExecutionOptions runtime, int size, string name)
    {
        Value result = Interpreter.ExecuteAsync(module, runtime).GetAwaiter().GetResult();
        return name == "layout_factory" ? Consume(result).GetAwaiter().GetResult() : result;
    }

    private sealed record ObservedScopes(int Count, Value Last);

    private static async Task<ObservedScopes> Consume(Value value)
    {
        RangeValue range = (RangeValue)value;
        while (range.Resolve is { } resolve) range = await resolve(CancellationToken.None);
        int count = 0;
        Value last = Value.Null;
        await foreach (Value item in range.Items)
        {
            ScopeValue scope = (ScopeValue)item;
            last = scope.Members["file"];
            count++;
        }
        return new(count, last);
    }

    internal static void Verify(Msync.Core.Module module, ExecutionOptions runtime, int size, string name)
    {
        object observed = Execute(module, runtime, size, name);
        Value result = observed is ObservedScopes scopes && scopes.Count == size ? scopes.Last
            : observed is Value value ? value
            : throw new InvalidOperationException($"{name}: expected {size} fresh Scope instances, got {observed}");
        if (result is not PathValue path || !path.Raw.EndsWith($"input_{size - 1}", StringComparison.Ordinal))
            throw new InvalidOperationException($"{name}: expected input_{size - 1}, got {result}");
    }
}
