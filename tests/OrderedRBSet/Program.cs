using Msync.Optimization;
using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

if (args.Contains("--churn-only", StringComparer.Ordinal))
{
    await ChurnChecks.Run();
    return;
}

List<(bool Insert, int Value)> operations = [];
for (int value = 0; value < 48; value++) operations.Add((true, value));
for (int value = 47; value >= 0; value -= 2) operations.Add((false, value));
Random random = new(1741);
for (int step = 0; step < 180; step++) operations.Add((random.Next(3) != 0, random.Next(80)));
for (int value = 79; value >= 0; value--) operations.Add((false, value));
for (int value = 0; value < 24; value++) operations.Add((true, value));

string commands = string.Join(",", operations.Select(operation => $"object {{ insert: {operation.Insert.ToString().ToLowerInvariant()}, value: {operation.Value.ToString(CultureInfo.InvariantCulture)} }}"));
string source = ArenaObservation.Source + "\n" + """
    let tree_type = $std::containers::Set;
    let tree = tree_type::new(Number);
    fn row(owner, index: Number) {
        object {
            index: index,
            key?: $std::arena::key(owner, index),
            left: $std::arena::child(owner, index, true),
            right: $std::arena::child(owner, index, false),
            parent: $std::arena::parent(owner, index),
            red: $std::arena::red(owner, index),
        }
    };
    fn capture(self, result: Bool) {
        let owner = self::arena;
        let count = $std::arena::size(owner);
        let traversal = observe_indices(owner, count);
        let nodes = $std::storage::vector_empty(decltype(row(owner, 0)));
        for index in traversal::indices {
            $std::storage::vector_push(nodes, row(owner, index));
        };
        object {
            result: result,
            root: $std::arena::root(owner),
            count: count,
            nil_red: $std::arena::red(owner, 0),
            nodes: $std::storage::vector_take(nodes),
            finished: traversal::finished,
            snapshot: tree_type::snapshot(self),
        }
    };
    let reports = $std::storage::vector_empty(decltype(capture(tree, false)));
    for operation in [
    """ + commands + """
    ] {
        let result = if operation::insert {
            tree_type::insert(tree, operation::value)
        } else { tree_type::remove(tree, operation::value) };
        $std::storage::vector_push(reports, capture(tree, result));
    };
    object {
        reports: $std::storage::vector_take(reports),
        lower?: tree_type::lower_bound(tree, 9),
        between: tree_type::range(tree, 4, 12),
        found: tree_type::contains(tree, 13),
        missing: tree_type::contains(tree, 48),
    }
    """;
int checks = 0;
foreach (int level in new[] { 0, 2, 3 })
{
    Console.WriteLine($"Compiling RBTreeSet O{level}");
    Module relocatable = Compiler.Compile(source, "rb_tree_set_properties.msync", level);
    foreach (Module image in new[] { relocatable, RoundTrip(relocatable), RoundTrip(Binding.Bind(relocatable, new(Environment.CurrentDirectory, Environment.CurrentDirectory))) })
    {
        ScopeValue result = (ScopeValue)await Interpreter.ExecuteAsync(image);
        List<Value> reports = await Items((RangeValue)result["reports"]);
        Check(reports.Count == operations.Count, "Every mutation has an observation");
        SortedSet<int> model = [];
        for (int step = 0; step < operations.Count; step++)
        {
            (bool insert, int value) = operations[step];
            bool changed = insert ? model.Add(value) : model.Remove(value);
            ScopeValue report = (ScopeValue)reports[step];
            Check(report["result"] is BoolValue boolean && boolean.Data == changed, $"Mutation result at {step}");
            List<Value> snapshot = await Items((RangeValue)report["snapshot"]);
            Check(snapshot.Cast<NumberValue>().Select(number => (int)number.Data).SequenceEqual(model), $"Sorted membership at {step}");
            var nodes = (await Items((RangeValue)report["nodes"])).Cast<ScopeValue>()
                .ToDictionary(node => Integer(node["index"]));
            int root = Integer(report["root"]);
            Check(Integer(report["count"]) == model.Count, $"Stored count at {step}");
            Check(report["finished"] is BoolValue { Data: true }, $"Arena walk terminates at {step}");
            Check(report["nil_red"] is BoolValue { Data: false }, $"NIL is black at {step}");
            Check(root == 0 || nodes[root]["red"] is BoolValue { Data: false }, $"Root is black at {step}");
            Check(root == 0 || Integer(nodes[root]["parent"]) == 0, $"Root has NIL parent at {step}");
            HashSet<int> visited = [];
            _ = Walk(root, null, null, 0);
            Check(visited.Count == model.Count && visited.Count == nodes.Count, $"Exactly the active nodes are reachable at {step}");

            int Walk(int index, int? lower, int? upper, int parent)
            {
                if (index == 0) return 1;
                Check(visited.Add(index), $"No cycle or duplicate child at {step}");
                Check(index > 0 && nodes.ContainsKey(index), $"Node index belongs to arena at {step}");
                ScopeValue node = nodes[index];
                int key = Integer(node["key"]);
                Check((lower is null || key > lower) && (upper is null || key < upper), $"BST ordering at {step}");
                Check(Integer(node["parent"]) == parent, $"Parent link at {step}");
                int left = Integer(node["left"]), right = Integer(node["right"]);
                bool red = ((BoolValue)node["red"]).Data;
                if (red)
                    Check(Black(left) && Black(right), $"No adjacent red nodes at {step}");
                int leftHeight = Walk(left, lower, key, index);
                int rightHeight = Walk(right, key, upper, index);
                Check(leftHeight == rightHeight, $"Equal black height at {step}");
                return leftHeight + (red ? 0 : 1);
            }

            bool Black(int index) => index == 0 || nodes[index]["red"] is BoolValue { Data: false };
        }
        Check(result["found"] is BoolValue { Data: true } && result["missing"] is BoolValue { Data: false }, "Search membership");
        Check(result["lower"] is ScopeValue lower && Integer(lower["value"]) == 9, "Inclusive lower bound");
        List<Value> between = await Items((RangeValue)result["between"]);
        Check(between.Cast<NumberValue>().Select(number => (int)number.Data).SequenceEqual(Enumerable.Range(4, 8)), "Half-open ordered range");
        Console.WriteLine($"RBTreeSet O{level} {image.Stage}: {operations.Count} mutation observations");
    }
}

string customSource = """
    let tree_type = $std::containers::Set;
    let tree = tree_type::with_policy(["Klee", "albedo", "KLEE"], object {
        Self: String,
        sample: "",
        less: fn(left: String, right: String) {
            $std::string::to_lowercase(left) < $std::string::to_lowercase(right)
        },
    });
    let alias = tree;
    tree_type::insert(alias, "Sucrose");
    let prior = tree_type::snapshot(tree);
    tree_type::remove(tree, "ALBEDO");
    object {
        prior: prior,
        current: tree_type::snapshot(alias),
        count: tree_type::size(tree),
        found: tree_type::contains(tree, "klee"),
    }
    """;
Module custom = Compiler.Compile(customSource, "rb_tree_set_policy.msync", 2);
ScopeValue policyResult = (ScopeValue)await Interpreter.ExecuteAsync(RoundTrip(Binding.Bind(custom, new(Environment.CurrentDirectory, Environment.CurrentDirectory))));
Check((await Items((RangeValue)policyResult["prior"])).Cast<StringValue>().Select(value => value.Data).SequenceEqual(new[] { "albedo", "Klee", "Sucrose" }), "Snapshot membership stays stable");
Check((await Items((RangeValue)policyResult["current"])).Cast<StringValue>().Select(value => value.Data).SequenceEqual(new[] { "Klee", "Sucrose" }), "Custom comparator and shared alias");
Check(Integer(policyResult["count"]) == 2 && policyResult["found"] is BoolValue { Data: true }, "Comparator equivalence determines uniqueness");

string endpointSource = """
    let set_type = $std::containers::Set;
    let tree = set_type::new([2, 1, 3]);
    let first? = set_type::first(tree);
    let last? = set_type::last(tree);
    let found? = set_type::get(tree, 2);
    let absent? = set_type::get(tree, 4);
    let removed_first? = set_type::pop_first(tree);
    let removed_last? = set_type::pop_last(tree);
    let added = set_type::extend(tree, [0, 2, 4, 4]);
    let retained = set_type::snapshot(tree);
    set_type::clear(tree);
    object {
        first?: first,
        last?: last,
        found?: found,
        absent?: absent,
        removed_first?: removed_first,
        removed_last?: removed_last,
        added: added,
        retained: retained,
        cleared: set_type::is_empty(tree),
        empty_first?: set_type::pop_first(tree),
        empty_last?: set_type::pop_last(tree),
    }
    """;
Module endpoints = Compiler.Compile(endpointSource, "rb_tree_set_endpoints.msync", 2);
ScopeValue endpointResult = (ScopeValue)await Interpreter.ExecuteAsync(RoundTrip(Binding.Bind(endpoints, new(Environment.CurrentDirectory, Environment.CurrentDirectory))));
Check(Integer(((ScopeValue)endpointResult["first"])["value"]) == 1 && Integer(((ScopeValue)endpointResult["last"])["value"]) == 3, "Ordered endpoints");
Check(Integer(((ScopeValue)endpointResult["found"])["value"]) == 2 && endpointResult["absent"] is NullValue, "Point lookup preserves explicit absence");
Check(Integer(((ScopeValue)endpointResult["removed_first"])["value"]) == 1 && Integer(((ScopeValue)endpointResult["removed_last"])["value"]) == 3, "Balanced endpoint removal");
Check(Integer(endpointResult["added"]) == 2, "Extend counts only new keys");
Check((await Items((RangeValue)endpointResult["retained"])).Cast<NumberValue>().Select(value => (int)value.Data).SequenceEqual(new[] { 0, 2, 4 }), "Retained snapshot survives clear");
Check(endpointResult["cleared"] is BoolValue { Data: true } && endpointResult["empty_first"] is NullValue && endpointResult["empty_last"] is NullValue, "Empty endpoint removal is absent");
Console.WriteLine($"PASS {checks} RBTreeSet mutation, balance, policy and stage checks");

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
static int Integer(Value value) => checked((int)((NumberValue)value).Data);
static async Task<List<Value>> Items(RangeValue range)
{
    List<Value> items = [];
    await foreach (Value value in range.Items) items.Add(value);
    return items;
}
static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes);
}
