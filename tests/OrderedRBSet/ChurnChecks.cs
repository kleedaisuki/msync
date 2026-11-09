using Msync.Optimization;
using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Runtime;

internal static class ChurnChecks
{
    public static async Task Run(string? containersPath = null)
    {
        string source = """
            let set_type = $std::containers::Set;
            let buffer = $std::storage::Buffer;
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
                    Self: Scope,
                    sample: prototype,
                    less: fn(left: Scope, right: Scope) {
                        left::priority < right::priority
                    },
                }
            );
            fn fill(tree, round: Number) {
                for priority in $std::ranges::iota(0, 16) {
                    set_type::insert(tree, scope {
                        priority: priority,
                        label: $std::string::repeat("payload", 128),
                    });
                };
            };
            fill(tree, 0);
            let first_nodes = buffer::snapshot(tree::nodes);
            let original = set_type::key(tree, 1);
            let removed = 0;
            for round in $std::ranges::iota(0, 5) {
                for priority in $std::ranges::iota(0, 16) {
                    if set_type::remove(tree, scope {
                        priority: priority, label: "query only"
                    }) { removed = removed + 1; };
                };
                if round < 4 { fill(tree, round + 1); };
            };
            scope {
                first_nodes: first_nodes,
                final_nodes: buffer::snapshot(tree::nodes),
                free: buffer::snapshot(tree::free),
                removed: removed,
                count: tree::count,
                original_label: original::label,
                source_calls: source_calls,
                observed_count: set_type::size(observed),
            }
            """;
        if (containersPath is not null)
        {
            string library = File.ReadAllText(containersPath);
            var declarations = Parser.Parse(library, containersPath).Statements
                .OfType<ScopeDeclaration>().Where(scope => scope.Name is "NaturalOrder" or "Set");
            string prefix = string.Join("\n", declarations.Select(scope => library.Substring(scope.Span.Start, scope.Span.Length)));
            source = prefix + "\n" + source.Replace("$std::containers::Set", "Set", StringComparison.Ordinal);
            Console.WriteLine("Exact source declaration SHA256: " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(prefix))));
        }
        Module relocatable = Compiler.Compile(source, "rb_set_retired_payloads.msync", 2);
        int checks = 0;
        foreach (Module module in new[] { relocatable, RoundTrip(relocatable), RoundTrip(Binding.Bind(relocatable, new(Environment.CurrentDirectory, Environment.CurrentDirectory))) })
        {
            ScopeValue result = (ScopeValue)await Interpreter.ExecuteAsync(module);
            List<Value> first = await Items((RangeValue)result["first_nodes"]);
            List<Value> final = await Items((RangeValue)result["final_nodes"]);
            List<Value> free = await Items((RangeValue)result["free"]);
            Require(first.Count == 17 && final.Count == 17 && free.Count == 16, "Arena stays at its live high-water mark");
            Require(((NumberValue)result["removed"]).Data == 80 && ((NumberValue)result["count"]).Data == 0, "Five insert/remove cycles retain behavior");
            Require(((NumberValue)result["source_calls"]).Data == 3 && ((NumberValue)result["observed_count"]).Data == 3, "Construction evaluates each deferred input exactly once");
            Require(result["original_label"] is StringValue label && label.Data == string.Concat(Enumerable.Repeat("payload", 128)), "Retirement never changes the user's key object");
            for (int index = 0; index < final.Count; index++)
            {
                ScopeValue node = (ScopeValue)final[index];
                Require(ReferenceEquals(first[index], final[index]), "Owned node Scopes are reused, not recreated");
                Require((await Items((RangeValue)node["payload"])).Count == 0, "Retired slots release payload references immediately");
                Require(node["red"] is BoolValue { Data: false } && ((NumberValue)node["left"]).Data == 0 && ((NumberValue)node["right"]).Data == 0, "Retired structural metadata is reset");
            }
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
