using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class ChurnChecks
{
    public static async Task Run()
    {
        string source = ArenaObservation.Source + "\n" + """
            let set_type = $std::containers::Set;
            let source_calls = 0;
            let observed = set_type::new($std::ranges::transform([1, 2, 3],
                fn(value: Number) {
                    source_calls = source_calls + 1;
                    value
                }
            ));
            let prototype = scope { priority: 0, label: "" };
            let tree = set_type::with_policy(
                $std::ranges::take([prototype], 0), scope {
                    Self: decltype(prototype),
                    sample: prototype,
                    less: fn(left, right) -> Bool {
                        left::priority < right::priority
                    },
                }
            );
            fn fill(tree) {
                for priority in $std::ranges::iota(0, 16) {
                    set_type::insert(tree, scope {
                        priority: priority,
                        label: $std::string::repeat("payload", 128),
                    });
                };
            };
            fill(tree);
            let retained = set_type::snapshot(tree);
            let row_ids = $std::storage::vector_empty(Number);
            let removed = 0;
            for round in $std::ranges::iota(0, 5) {
                let traversal = observe_indices(tree::arena, set_type::size(tree));
                for index in traversal::indices { $std::storage::vector_push(row_ids, index); };
                for priority in $std::ranges::iota(0, 16) {
                    if set_type::remove(tree, scope {
                        priority: priority, label: "query only"
                    }) { removed = removed + 1; };
                };
                if round < 4 { fill(tree); };
            };
            scope {
                arena: tree::arena,
                retained: retained,
                row_ids: $std::storage::vector_take(row_ids),
                removed: removed,
                count: set_type::size(tree),
                source_calls: source_calls,
                observed_count: set_type::size(observed),
            }
            """;
        Module relocatable = Compiler.Compile(source, "rb_set_retired_payloads.msync", 2);
        int checks = 0;
        foreach (Module module in new[] { relocatable, RoundTrip(relocatable), RoundTrip(Binding.Bind(relocatable, new(Environment.CurrentDirectory, Environment.CurrentDirectory))) })
        {
            ScopeValue result = (ScopeValue)await Interpreter.ExecuteAsync(module);
            int[] rowIds = (await Items((RangeValue)result["row_ids"])).Cast<NumberValue>()
                .Select(value => (int)value.Data).ToArray();
            Require(rowIds.Length == 80, "Every filled tree has exactly 16 observed rows");
            for (int cycle = 0; cycle < 5; cycle++)
                Require(rowIds.Skip(cycle * 16).Take(16).Order().SequenceEqual(Enumerable.Range(1, 16)),
                    "Arena reuses its initial 16 row indices");
            Require(((NumberValue)result["removed"]).Data == 80 && ((NumberValue)result["count"]).Data == 0, "Five insert/remove cycles retain behavior");
            Require(((NumberValue)result["source_calls"]).Data == 3 && ((NumberValue)result["observed_count"]).Data == 3, "Construction evaluates each deferred input exactly once");
            var arena = (StorageValue)result["arena"];
            Require(!arena.RetainedReferences().Any(), "Retired rows retain no key references");
            var retained = (await Items((RangeValue)result["retained"])).Cast<ScopeValue>().ToArray();
            Require(retained.Length == 16 && retained.Select(value => ((NumberValue)value["priority"]).Data)
                .SequenceEqual(Enumerable.Range(0, 16).Select(priority => (double)priority)),
                "The retained snapshot still has its original membership and order");
            string label = string.Concat(Enumerable.Repeat("payload", 128));
            Require(retained.All(value => value["label"] is StringValue keyLabel && keyLabel.Data == label),
                "Retirement never changes the user's key objects");
        }
        Console.WriteLine($"PASS {checks} Set churn/retired-reference/high-water checks");
        return;

        void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static async Task<List<Value>> Items(RangeValue range)
    {
        List<Value> result = [];
        await foreach (Value value in range.Items) result.Add(value);
        return result;
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes);
    }
}
