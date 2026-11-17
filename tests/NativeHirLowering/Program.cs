using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime.Native;
using SourceCompiler = Msync.Compiler.Compiler;

int checks = 0;
CheckScopeAllocationContracts();
await CheckSeparatedInitialization();
(string Name, string Source)[] examples =
[
    ("canonical-stdlib-root", """
        let boolean = $std::bool;
        let root = $std;
        boolean::identity = fn(value: Bool) { not value };
        if root::bool::identity(false) { 7 } else { 0 }
        """),
    ("input-alias-before-field-replacement", """
        let namespace = scope { family: scope { read: fn(value) { 2 } } };
        fn change(value) {
            namespace::family = scope { read: fn(value) { 99 } };
            value
        };
        let before = namespace::family::read(change(0));
        before * 100 + namespace::family::read(0)
        """),
    ("native-record-layouts", """
        let pair? = $std::ranges::first($std::ranges::zip([2], [3]));
        let indexed? = $std::ranges::first($std::ranges::enumerate([5]));
        let found? = $std::regex::find("a", "a");
        if pair is null or indexed is null or found is null { 0 }
        else { pair::first + pair::second + indexed::index + indexed::value + found::length }
        """),
    ("native-record-carrier-variants", """
        let selected = if $std::fs::exists(./native-hir-never-existing-native-record) { [2] } else { [true] };
        let pair? = $std::ranges::first($std::ranges::zip(selected, [5]));
        if pair is null { 0 } else { if pair::first is Bool { pair::second } else { 0 } }
        """),
    ("unreachable-return-tail", """
        fn less(left?, right?) -> Bool {
            if left is null { return right is not null; };
            if right is null { return false; };
            if decltype(left) is Number {
                if left < right { return true; };
                if left >= right { return false; };
                return left == left and right != right;
            };
            left < right
        };
        if less(3, 4) { 7 } else { 0 }
        """),
    ("lazy-nested-shape", """
        let Model = decltype(scope { child: scope { count: 1 } });
        fn check(value: Scope) { let typed = value as Model; 42 };
        check(scope { child: scope { count: "wrong" } })
        """),
    ("returned-nested-closure", """
        fn make(seed: Number) {
            let outer = scope { inner: scope { n: seed } };
            fn() { outer::inner::n = outer::inner::n + 1; outer::inner::n }
        };
        let next = make(4);
        next() * 10 + next()
        """),
    ("shape-alias-mutated", """
        let outer = scope { child: scope { count: 1 } };
        let Model = decltype(outer);
        let erased: Scope = outer;
        let checked = erased as Model;
        outer::child = scope { unused: true, count: 7 };
        checked::child::count
        """),
    ("two-node-cycle", """
        let first = scope { value: 1, next?: null };
        let second = scope { value: 2, next?: null };
        first::next = second;
        second::next = first;
        let selected = first;
        let total = 0;
        for _ in $std::ranges::iota(0, 4) {
            total = total + selected::value;
            if selected::next is not null { selected = selected::next; };
        };
        total
        """),
    ("cyclic-shape", """
        let node = scope { value: 3, next?: null };
        node::next = node;
        let Model = decltype(node);
        let erased: Scope = node;
        let checked = erased as Model;
        let next? = checked::next;
        if next is null { 0 } else { next::value }
        """),
    ("generic-allocation-abis", """
        fn box(value) { scope { payload: value } };
        let number = box(7);
        let text = box("text");
        let path = box(./input);
        if text::payload == "text" and path::payload is Path { number::payload } else { 0 }
        """),
    ("explicit-scope-shape", """
        let unrelated = scope { count: "not a Number" };
        let model = scope { count: 1 };
        let erased: Scope = scope { count: 7 };
        let Model = decltype(model);
        let narrowed = erased as Model;
        narrowed::count
        """),
    ("generic-environment-abis", """
        fn keep(value) {
            let saved = value;
            fn() { saved }
        };
        let number = keep(7);
        let text = keep("text");
        let path = keep(./input);
        let endpoint = keep($local);
        let truth = keep(true);
        let kind = keep(Number);
        let operation = keep(fn(x: Number) { x + 2 });
        let items = keep([1, 2]);
        let record = keep(scope { count: 3, text: "record" });
        let total = number() + record()::count;
        for item in items() { total = total + item; };
        if text() == "text" and path() is Path and endpoint() is Connection
            and truth() and kind() == Number { operation()(total) } else { 0 }
        """),
    ("generic-shared-environment", """
        fn make(value) {
            let saved = value;
            scope {
                read: fn() { saved },
                write: fn(next) { saved = next; },
            }
        };
        let number = make(1);
        let text = make("first");
        number::write(9);
        text::write("second");
        if text::read() == "second" { number::read() } else { 0 }
        """),
    ("nullable-loop-flow", """
        let item? = null;
        for value in [1, 2, 3] { item = value; };
        if item is null { 0 } else { item + 1 }
        """),
    ("builtin-storage", """
        let value = scope {
            text: "payload", path: ./input, endpoint: $local,
            number: 7, truth: true, missing?: null, type: Number,
            function: fn(x: Number) { x + 1 }, sequence: [1, 2],
        };
        let alias = value;
        alias::number = 9;
        value::number
        """),
    ("heterogeneous-field", """
        fn choose(flag: Bool) {
            if flag { scope { value: "text" } }
            else { scope { value: ./path } }
        };
        let selected = $std::fs::exists(./native-hir-never-existing-field-selection);
        choose(selected)::value
        """),
    ("captured-parameter", """
        fn make(start: Number) {
            fn() { start = start + 1; start }
        };
        let next = make(5);
        let other = make(30);
        next() * 10000 + other() * 100 + next()
        """),
    ("captured-iteration", """
        let next = fn() { 0 };
        for value in [1, 2, 3] { next = fn() { value }; };
        next()
        """),
    ("rebound-layout", """
        let value = scope { count: 1, text: "first" };
        let original = value;
        let read = fn() { value::count };
        value = scope { text: "second", count: 9 };
        read() + original::count
        """),
    ("control-and-cleanup", """
        fn choose(flag: Bool) {
            for value in [1, 2, 3] {
                if flag { return value; };
                if value == 2 { break; };
            };
            match flag { true => 7, false => 9 }
        };
        choose(false)
        """),
    ("known-branch", """
        let flag = true;
        if flag { 42 } else { "not selected" }
        """),
    ("lazy-captures", """
        let value = 1;
        fn source() { $std::ranges::transform([0, 1], fn(offset: Number) { value + offset }) };
        let sequence = source();
        value = 5;
        let sum = 0;
        for element in sequence { sum = sum + element; };
        sum
        """),
    ("singleton-alias", """
        scope State { count: 1, text: "state" };
        let alias = State;
        alias::count = 4;
        State::count
        """)
];

foreach ((string name, string source) in examples)
{
    HirModule hir = SourceCompiler.Analyze(source, name + ".msync");
    var noSource = hir with { Sources = [.. hir.Sources.Select(item => item with { Text = null })] };
    using MemoryStream high = new();
    HirArtifact.Write(noSource, high); high.Position = 0;
    foreach (var (input, optimizationLevel) in new[] { hir, HirArtifact.Read(high) }
        .SelectMany(input => new[] { 0, 2 }.Select(level => (input, level))))
    {
        NativeProgram program = SourceCompiler.LowerNative(input, optimizationLevel);
        using MemoryStream bytes = new();
        NativeArtifact.WriteProduced(program, bytes); bytes.Position = 0;
        NativeProgram decoded = NativeArtifact.Read(bytes);
        NativeProgram bound = NativePreparation.Prepare(decoded);
        await using (NativeInterpreter interpreter = new(bound))
        {
            NativeValue actual = await interpreter.ExecuteAsync();
            if (name == "heterogeneous-field")
                Require(actual.Kind == NativeKind.Path && actual.AsPath().Raw == "./path", "Path field result differs");
            else
            {
                double expected = name switch
                {
                    "unreachable-return-tail" or "canonical-stdlib-root" => 7,
                    "input-alias-before-field-replacement" => 299,
                    "native-record-layouts" => 11,
                    "native-record-carrier-variants" => 5,
                    "lazy-nested-shape" => 42,
                    "returned-nested-closure" => 56,
                    "shape-alias-mutated" => 7,
                    "cyclic-shape" => 3,
                    "two-node-cycle" => 6,
                    "generic-allocation-abis" => 7,
                    "explicit-scope-shape" => 7,
                    "generic-environment-abis" => 15,
                    "generic-shared-environment" => 9,
                    "nullable-loop-flow" => 4,
                    "builtin-storage" => 9,
                    "captured-parameter" => 63107,
                    "captured-iteration" => 3,
                    "rebound-layout" => 10,
                    "control-and-cleanup" => 9,
                    "known-branch" => 42,
                    "lazy-captures" => 11,
                    "singleton-alias" => 4,
                    _ => throw new InvalidOperationException("Missing independent native execution oracle.")
                };
                Require(actual.Kind == NativeKind.Number && actual.AsNumber() == expected,
                    $"{name}: expected {expected}, got {actual}");
            }
        }
        if (name == "two-node-cycle")
        {
            HashSet<int> constructed = decoded.Functions.SelectMany(function => function.Instructions)
                .Where(instruction => instruction.Op == NativeOp.Allocate).Select(instruction => instruction.A)
                .Concat(decoded.Statics.Select(declaration => declaration.Layout)).ToHashSet();
            Require(decoded.Projections.SelectMany(projection => projection.Cases)
                .All(alternative => constructed.Contains(alternative.Layout)),
                "Logical flow-only schemas were admitted as physical namespace projection cases");
        }
        foreach (var callable in decoded.Functions)
            foreach (var constructor in callable.Instructions.Where(instruction => instruction.Op == NativeOp.MakeFunction))
            {
                Require(constructor.B >= 0, name + ": closure omitted its physical environment layout");
                var fields = decoded.Layouts[constructor.B].Fields;
                var inputs = constructor.Inputs ?? [];
                Require(fields.Count == inputs.Count && fields.Select(field => field.Type)
                    .SequenceEqual(inputs.Select(input => callable.Slots[input].Type)),
                    name + ": closure environment disagrees with actual capture representations");
            }
        Require(decoded.Types.All(type => (type.Kind & TypeKind.Storage) == 0), name + ": invented Storage kind");
        Require(decoded.Functions.SelectMany(function => function.Instructions)
            .All(instruction => instruction.Op != NativeOp.CallNative || instruction.Symbol is not { } symbol
                || !symbol.StartsWith("arena.", StringComparison.Ordinal)
                    && !symbol.StartsWith("storage.", StringComparison.Ordinal)
                    && !symbol.StartsWith("slots.", StringComparison.Ordinal)), name + ": allocator-shaped operation");
        if (name == "heterogeneous-field")
        {
            var fields = decoded.Functions.SelectMany(function => function.Instructions)
                .SelectMany(instruction => instruction.Op switch
                {
                    NativeOp.AddressOffset => [decoded.Layouts[instruction.B].Fields[instruction.C]],
                    NativeOp.ProjectAddress => decoded.Projections[instruction.B].Cases
                        .Select(item => decoded.Layouts[item.Layout].Fields[item.Field]),
                    _ => Enumerable.Empty<NativeCodeField>()
                }).Where(field => field.Name == "value").Select(field => decoded.Types[field.Type].Kind).ToHashSet();
            Require(fields.Contains(TypeKind.String) && fields.Contains(TypeKind.Path),
                "Mixed native field access lost its monomorphic String/Path physical alternatives");
        }
        if (name == "generic-allocation-abis")
            foreach (TypeKind kind in new[] { TypeKind.Number, TypeKind.String, TypeKind.Path })
                Require(decoded.Layouts.Any(layout => layout.Fields is [{ Name: "payload" } field]
                    && decoded.Types[field.Type].Kind == kind),
                    $"Generic allocation lost its monomorphic {kind} payload ABI");
        if (name == "explicit-scope-shape")
        {
            Require(decoded.Functions.SelectMany(function => function.Instructions)
                .Where(instruction => instruction.Op == NativeOp.AddressOffset)
                .Select(instruction => decoded.Layouts[instruction.B].Fields[instruction.C])
                .Any(field => field.Name == "count" && decoded.Types[field.Type].Kind == TypeKind.Number),
                "The checked member did not use the Number field's physical layout");
        }
    }
    Console.WriteLine("PASS " + name + ": source and detached HIR, O0/O2 -> bound native artifact and execution");
}

CheckLiteralBits();
CheckEmptyFreshness();
await CheckDependentShapeRejection();
await CheckPhysicalWriteBoundary();
Console.WriteLine($"PASS {checks} native HIR representation and artifact contracts");

async Task CheckPhysicalWriteBoundary()
{
    const string source = "let actual = scope { value: \"old\" }; actual::value = \"new\"; $std::string::length(actual::value)";
    HirModule original = SourceCompiler.Analyze(source, "checked-write.msync");
    foreach (bool valid in new[] { true, false })
    {
        var types = original.Types.ToBuilder();
        var layouts = original.ScopeLayouts.ToBuilder();
        var nodes = original.Nodes.ToBuilder();
        var edges = original.Edges.ToBuilder();
        var literals = original.Literals.ToBuilder();
        HirTypeId item = new(types.Count); types.Add(new(new(TypeKind.Number | TypeKind.String)));
        int shape = layouts.Count; layouts.Add(new([new("value", item)]));
        HirTypeId view = new(types.Count); types.Add(new(TypeRef.Scope, Shape: shape));
        int assignment = Enumerable.Range(0, nodes.Count).First(index => nodes[index].Kind == HirKind.MemberAssign);
        HirNode operation = nodes[assignment];
        HirNodeId receiver = edges[operation.Children.Start], assigned = edges[operation.Children.Start + 1];
        int first = edges.Count; edges.Add(receiver);
        HirNodeId checkedReceiver = new(nodes.Count);
        nodes.Add(new(HirKind.Convert, view, operation.Source, new(first, 1), view.Value));
        if (!valid)
        {
            int literal = literals.Count; literals.Add(new(HirLiteralKind.Number, Number: 7));
            HirTypeId number = new(types.Count); types.Add(new(TypeRef.Number));
            assigned = new(nodes.Count); nodes.Add(new(HirKind.Literal, number, operation.Source, default, literal));
        }
        first = edges.Count; edges.Add(assigned);
        HirNodeId widened = new(nodes.Count);
        nodes.Add(new(HirKind.Coerce, item, operation.Source, new(first, 1), item.Value));
        edges[operation.Children.Start] = checkedReceiver;
        edges[operation.Children.Start + 1] = widened;
        HirModule input = original with
        {
            Types = types.ToImmutable(),
            ScopeLayouts = layouts.ToImmutable(),
            Nodes = nodes.ToImmutable(),
            Edges = edges.ToImmutable(),
            Literals = literals.ToImmutable()
        };
        foreach (int level in new[] { 0, 2 })
        {
            NativeProgram program = SourceCompiler.LowerNative(input, level);
            using MemoryStream encoded = new(); NativeArtifact.Write(program, encoded); encoded.Position = 0;
            await using NativeInterpreter interpreter = new(NativeArtifact.Read(encoded));
            try
            {
                NativeValue result = await interpreter.ExecuteAsync();
                Require(valid && result.Kind == NativeKind.Number && result.AsNumber() == 3,
                    $"Checked structural write ({valid}, O{level}) bypassed its selected physical String ABI: {result}");
            }
            catch (MsyncException error) when (!valid)
            {
                Require(error.Span is { } span && span.Start == operation.Source.Start && span.Length == operation.Source.Length,
                    "Checked structural write lost the original assignment source span");
            }
        }
    }
}

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

void CheckLiteralBits()
{
    HirBuilder hir = new();
    hir.Sources.Add(new("literal-bits.msync"));
    HirSourceRange source = new(new(0), 0, 0);
    var number = hir.Type(TypeRef.Number);
    var range = hir.Type(new TypeRef(TypeKind.Range, TypeRef.Number));
    long[] bits = [0, long.MinValue, 0x7ff8000000000001, 0x7ff8000000000002];
    HirNodeId[] values = bits.Select(bit => hir.Literal(new(HirLiteralKind.Number,
        Number: BitConverter.Int64BitsToDouble(bit)), number, source)).ToArray();
    var body = hir.Add(HirKind.Range, range, source, values);
    hir.Functions.Add(new("entry", [], [], range, body));
    NativeProgram program = SourceCompiler.LowerNative(hir.Freeze(new(0)), 0);
    NativeArtifact.Validate(program);
    Require(program.Constants.Where(value => value.Kind == NativeKind.Number)
        .Select(value => BitConverter.DoubleToInt64Bits(value.Number)).SequenceEqual(bits),
        "Literal interning merged signed zero or observable NaN payload bits");
}

void CheckEmptyFreshness()
{
    var hir = SourceCompiler.Analyze("let first = scope {}; let second = scope {}; [first, second]", "empty.msync");
    var program = SourceCompiler.LowerNative(hir, 0);
    NativeArtifact.Validate(program);
    Require(program.Functions.SelectMany(function => function.Instructions)
        .Count(instruction => instruction.Op == NativeOp.Allocate && program.Layouts[instruction.A].Fields.Count == 0) == 2,
        "Fresh empty namespace constructions were merged before identity analysis");
}

async Task CheckDependentShapeRejection()
{
    const string sourceName = "dependent-shape.msync";
    const string source = """
        let Model = decltype(scope { child: scope { count: 1 } });
        fn check(value: Scope) { let typed = value as Model; typed::child::count };
        check(scope { child: scope { count: "wrong" } })
        """;
    const string mutated = """
        let original = scope { child: scope { count: 2 } };
        let Model = decltype(original);
        let erased: Scope = original;
        let typed = erased as Model;
        original::child = scope { count: "wrong" };
        typed::child::count
        """;
    const string returned = """
        let original = scope { child: scope { count: 2 } };
        let Model = decltype(original);
        fn admit(value: Scope) { value as Model };
        let typed = admit(original);
        original::child = scope { count: "wrong" };
        fn read(value) { value::child::count };
        read(typed)
        """;
    foreach (string inputSource in new[] { source, mutated, returned })
        await RejectDependentShape(inputSource, sourceName);
}

async Task RejectDependentShape(string source, string sourceName)
{
    HirModule hir = SourceCompiler.Analyze(source, sourceName);
    using MemoryStream high = new();
    HirArtifact.Write(hir with { Sources = [.. hir.Sources.Select(item => item with { Text = null })] }, high);
    high.Position = 0;
    foreach (var (input, level) in new[] { hir, HirArtifact.Read(high) }
        .SelectMany(input => new[] { 0, 2 }.Select(level => (input, level))))
    {
        NativeProgram program = SourceCompiler.LowerNative(input, level);
        using MemoryStream artifact = new();
        NativeArtifact.WriteProduced(program, artifact); artifact.Position = 0;
        await using NativeInterpreter interpreter = new(NativeArtifact.Read(artifact));
        try { await interpreter.ExecuteAsync(); }
        catch (MsyncException error)
        {
            Require(error.Span is { } span && span.Source == sourceName
                && source.Substring(span.Start, span.Length).Contains("::child", StringComparison.Ordinal),
                "Dependent shape rejection lost its original member source span");
            continue;
        }
        throw new InvalidOperationException("A wrong nested scalar layout reached a typed dependent read");
    }
}

void CheckScopeAllocationContracts()
{
    Require((byte)HirKind.Scope == 29 && (byte)HirKind.QualifyOn == 40 && (byte)HirKind.AllocateScope == 41,
        "Appending layout-only allocation changed historical typed HIR node numbers");
    HirModule root = SourceCompiler.Analyze("$std::bool::identity(true)", "canonical-module-cells.msync");
    int allocation = Enumerable.Range(0, root.Nodes.Length).Single(index => root.Nodes[index].Kind == HirKind.AllocateScope);
    HirNode node = root.Nodes[allocation];
    Require(node.Children.Count == 0 && root.Scopes[node.Data].Fields.Length > 1,
        "Canonical module storage must bind its layout without dummy initializers");
    using MemoryStream high = new();
    HirArtifact.Write(root with { Sources = [.. root.Sources.Select(source => source with { Text = null })] }, high);
    high.Position = 0;
    HirModule decoded = HirArtifact.Read(high);
    Require(decoded.Nodes.Any(item => item.Kind == HirKind.AllocateScope)
        && HirPrinter.Print(decoded).Contains("AllocateScope", StringComparison.Ordinal),
        "Layout-only allocation lost its typed HIR operation or construction reference");
    RejectScope(root with { Nodes = root.Nodes.SetItem(allocation, node with { Kind = HirKind.Scope }) },
        "An initialized Scope accepted missing field initializers");
    RejectScope(root with
    {
        Nodes = root.Nodes.SetItem(allocation, node with { Children = new(root.Edges.Length, 1) }),
        Edges = root.Edges.Add(new(0))
    }, "AllocateScope accepted initializer children");
}

void RejectScope(HirModule module, string reason)
{
    bool writeRejected = false;
    try { HirValidation.Validate(module); }
    catch (MsyncException error) when (error.Message.Contains("semantic node expects", StringComparison.Ordinal)) { writeRejected = true; }
    Require(writeRejected, reason);
    using MemoryStream malformed = new();
    HirArtifact.WriteProduced(module, malformed);
    malformed.Position = 0;
    bool readRejected = false;
    try { HirValidation.Validate(HirArtifact.Read(malformed)); }
    catch (MsyncException error) when (error.Message.Contains("semantic node expects", StringComparison.Ordinal)) { readRejected = true; }
    Require(readRejected, reason + " after decoding");
}

async Task CheckSeparatedInitialization()
{
    HirModule original = SourceCompiler.Analyze("""
        let record = scope { count: 41, advance: fn { count = count + 1; count } };
        record::advance()
        """, "separated-namespace-initialization.msync");
    int scopeIndex = Enumerable.Range(0, original.Nodes.Length).Single(index => original.Nodes[index].Kind == HirKind.Scope);
    HirNode scope = original.Nodes[scopeIndex];
    HirNodeId bodyId = original.Functions[original.Entry.Value].Body;
    HirNode body = original.Node(bodyId);
    var children = original.Children(body).ToArray();
    var initializers = original.Children(scope).ToArray();
    var edges = original.Edges.Add(children[0]).AddRange(initializers).AddRange(children.Skip(1));
    HirModule separated = original with
    {
        Sources = [.. original.Sources.Select(source => source with { Text = null })],
        Edges = edges,
        Nodes = original.Nodes.SetItem(scopeIndex, scope with
        {
            Kind = HirKind.AllocateScope,
            Children = new(0, 0),
            Effects = HirEffects.WriteLocation | HirEffects.WriteMemory
        }).SetItem(bodyId.Value, body with { Children = new(original.Edges.Length, children.Length + initializers.Length) })
    };
    using MemoryStream high = new();
    HirArtifact.Write(separated, high);
    high.Position = 0;
    HirModule decoded = HirArtifact.Read(high);
    foreach (int level in new[] { 0, 2 })
    {
        NativeProgram program = SourceCompiler.LowerNative(decoded, level);
        using MemoryStream native = new();
        NativeArtifact.Write(program, native);
        native.Position = 0;
        await using NativeInterpreter interpreter = new(NativeArtifact.Read(native));
        Require((await interpreter.ExecuteAsync()).AsNumber() == 42,
            "Separated allocation/initialization detached a captured field from its namespace binding");
    }
}
