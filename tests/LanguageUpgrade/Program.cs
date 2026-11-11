using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "language-upgrade-contracts");
Directory.CreateDirectory(root);
int assertions = 0;
const string qualification = """
    let count = 0;
    fn source() { global count; count = count * 10 + 1; ./leaf };
    fn endpoint() { global count; count = count * 10 + 2; $local };
    endpoint() @ source();
    count
    """;

foreach ((string source, Func<Value, bool> accepts) in new (string, Func<Value, bool>)[]
{
    ("true as Number", value => value is NumberValue { Data: 1 }),
    ("false as String", value => value is StringValue { Data: "false" }),
    ("42 as String", value => value is StringValue { Data: "42" }),
    ("\"true\" as Bool", value => value is BoolValue { Data: true }),
    ("\"0b101\" as Number", value => value is NumberValue { Data: 5 }),
    ("let file: Path = \"12.5\"; file as Number", value => value is NumberValue { Data: 12.5 }),
    ("let file: Path = \"false\"; file as Bool", value => value is BoolValue { Data: false }),
    ("\"file\" as Path", value => value is PathValue { Raw: "file" }),
    ("./file as String", value => value is StringValue { Data: "./file" }),
    ("ssh://forge as String", value => value is StringValue { Data: "ssh://forge" }),
    ("\"ssh://forge\" as Connection", value => value is ConnectionValue { Kind: ConnectionKind.Ssh, Address: "forge" }),
    ("Number as String", value => value is StringValue { Data: "Number" }),
    ("null as Number?", value => value is NullValue),
    ("let path: Path = \"leaf\"; let text: String = path; text", value => value is StringValue { Data: "leaf" }),
    ("fn path(value: Path) -> String { value }; path(\"leaf\")", value => value is StringValue { Data: "leaf" }),
    ("fn text(value: String) -> Path { value }; text(./leaf)", value => value is PathValue { Raw: "./leaf" }),
    ("let connection = ssh://forge; connection @ ./leaf", value => value is PathValue { Raw: "./leaf", Connection.Kind: ConnectionKind.Ssh, Connection.Address: "forge" }),
    ("$local @ \"a b\"", value => value is PathValue { Raw: "a b", Connection.Kind: ConnectionKind.Local }),
    (qualification, value => value is NumberValue { Data: 21 }),
    ("let count = 0; fn source() { count = count + 1; ./leaf }; let path = $local @ source(); count", value => value is NumberValue { Data: 1 }),
    ("let state = 1; fn outer() { global state; fn inner() { global state; state = state + 1; }; inner(); }; outer(); state", value => value is NumberValue { Data: 2 }),
    ("let file? = if exists ./missing-contract-file { ./leaf } else { null }; if not file? { file as String } else { \"absent\" }", value => value is StringValue { Data: "absent" }),
})
    await AcrossArtifacts(source, accepts);

await HIRQualificationOrder();

await AcrossArtifacts("let values = [\"1\", \"2\", \"3\"] as Range<Number>; $std::ranges::fold(values, 0, fn(sum: Number, next: Number) { sum + next })",
    value => value is NumberValue { Data: 6 });

const string counter = """
    concept Readable { read: fn() -> Number, };
    concept Counter { Readable, next: fn() -> Number, };
    fn factory(initial: Number) -> Counter {
        scope Shared {
            value: initial,
            read: fn() -> Number { value },
            next: fn() -> Number { value = value + 1; value },
        };
        Shared
    };
    let first: Counter = factory(10);
    first::next();
    let second: Counter = factory(99);
    second::next();
    first::read()
    """;
await AcrossArtifacts(counter, value => value is NumberValue { Data: 12 });

foreach ((string source, string diagnostic) in new[]
{
    ("let value: String = 12;", "String"),
    ("let value: Number = true;", "Number"),
    ("fn value(item: Number) { item }; value(\"12\");", "Number"),
    ("fn value() -> String { 12 }; value();", "String"),
    ("scope {} as Number", "cast"),
    ("[1] as String", "cast"),
    ("true as Path", "cast"),
    ("ssh://forge as Number", "cast"),
    ("(fn() { true }) as String", "cast"),
    ("null as Number", "Null"),
    ("let value? = if exists ./missing-contract-file { 1 } else { null }; value as Number", "Number?"),
    ("$local::./leaf", "@"),
    ("./leaf @ ./other", "Connection"),
    ("./leaf @ $local", "Write 'connection @ path'"),
    ("$local @ true", "Path"),
    ("let value = 1; value = \"1\";", "Number"),
    ("let value = scope { number: 1 }; value::number = true;", "Number"),
})
    Reject(source, diagnostic);

Console.WriteLine($"PASS {assertions} integrated cast, qualification, constraint, capture and fixed-storage contracts");

async Task AcrossArtifacts(string source, Func<Value, bool> accepts)
{
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module module = Compiler.Compile(source, "language-upgrade.msync", level);
        foreach ((string stage, Module image) in new[]
        {
            ("source", module),
            ("mso", RoundTrip(module)),
            ("mexe", RoundTrip(Binding.Bind(module, new(root, root))))
        })
        {
            Value result = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root));
            Require(accepts(result), $"O{level} {stage}: {source}\nproduced {result.Type}: {result}");
        }
    }
}

static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes);
}

void Reject(string source, string diagnostic)
{
    try { Compiler.Compile(source, "language-upgrade-error.msync"); }
    catch (CompileException error)
    {
        Require(error.Diagnostic.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)
            || error.Diagnostic.Help?.Contains(diagnostic, StringComparison.OrdinalIgnoreCase) == true,
            $"Expected '{diagnostic}' in diagnostic for {source}: {error.Diagnostic.Message}");
        return;
    }
    throw new InvalidOperationException("Accepted invalid source: " + source);
}

void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

async Task HIRQualificationOrder()
{
    HirModule graph = Compiler.Analyze(qualification, "qualification-order.msync");
    int index = Enumerable.Range(0, graph.Nodes.Length).Single(i => graph.Nodes[i].Kind == HirKind.QualifyOn);
    HirNode node = graph.Nodes[index];
    int left = node.Children.Start;
    foreach ((HirModule source, double expected) in new[]
    {
        (graph, 21d),
        (graph with
        {
            Nodes = graph.Nodes.SetItem(index, node with { Kind = HirKind.Qualify }),
            Edges = graph.Edges.SetItem(left, graph.Edges[left + 1]).SetItem(left + 1, graph.Edges[left])
        }, 12d)
    })
    {
        using MemoryStream artifact = new();
        HirArtifact.Write(source, artifact);
        artifact.Position = 0;
        Module bytecode = HirLowerer.Lower(HirArtifact.Read(artifact)).Module;
        Value result = await Interpreter.ExecuteAsync(bytecode, new(WorkingDirectory: root));
        Require(result is NumberValue number && number.Data == expected,
            $"Serialized qualification must evaluate in the recorded operand order; expected {expected}, found {result}.");
    }
}
