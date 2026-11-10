using System.Reflection;
using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;

internal static class KeySchemaChecks
{
    internal static void Run()
    {
        int checks = 0;
        var program = new SourceBinder().Bind("let first = scope { value: 1 }; let second = scope { value: 2 }; fn make(value) { fn { value } }; let left = make(1); let right = make(2);", "key-schemas.msync");
        var analyzer = new SemanticAnalyzer(program);
        analyzer.Analyze();
        var state = analyzer.FinalState;
        var keyMethod = typeof(SemanticAnalyzer).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "InstanceKey" && method.GetParameters().Length == 5);
        var regionField = typeof(SemanticAnalyzer).GetField("nextRegion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        SemanticValue Value(string name) => state.Values[program.Declarations.Single(item => item.Name == name && item.Kind == DeclarationKind.Local).Location];
        string Key(SemanticAnalyzer owner, SemanticContext context, IReadOnlyList<SemanticValue> values, bool direct,
            IReadOnlyList<SemanticValue>? captures = null) =>
            (string)keyMethod.Invoke(owner, [new HirTemplateId(0), values, captures ?? Array.Empty<SemanticValue>(), context, direct])!;
        string Compare(IReadOnlyList<SemanticValue> values, bool exact = true)
        {
            int before = (int)regionField.GetValue(analyzer)!;
            string direct = Key(analyzer, state, values, true);
            if (exact) Check((int)regionField.GetValue(analyzer)! == before, "Exact graphs do not materialize formal regions.");
            string normalized = Key(analyzer, state, values, false);
            Check(direct == normalized, $"Readonly and materialized keys disagree: {direct} != {normalized}");
            return direct;
        }
        string aliased = Compare([Value("first"), Value("first")]);
        string disjoint = Compare([Value("first"), Value("second")]);
        Check(aliased != disjoint, "Must-alias and separate object inputs retain distinct keys.");
        Check(disjoint == Compare([Value("second"), Value("first")]), "Allocation identities and runtime constants are not key components.");
        string sharedClosure = Compare([Value("left"), Value("left")]);
        string distinctClosures = Compare([Value("left"), Value("right")]);
        Check(sharedClosure != distinctClosures, "Distinct captured environments are not merged.");
        Check(Compare([Value("left")]) == Compare([Value("right")]), "Equivalent single-environment schemas share a key.");
        var snapshot = Value("left") with { Callables = [Value("left").Callables[0] with
        { Captures = [Value("left").Callables[0].Captures[0] with { Mode = HirCaptureMode.Value }] }] };
        Compare([snapshot], exact: false);
        var dynamic = Value("first") with { Scope = Value("first").Scope! with { Region = -1, Dynamic = true } };
        Compare([dynamic], exact: false);
        var item = Value("first").Scope!.Fields["value"];
        var rangeType = program.Hir.Type(new HirType(new(TypeKind.Range, TypeRef.Number), Element: item.Type.Value));
        state.Indexed[7000] = new(item, item.Type);
        Compare([new(rangeType, HirNodeId.None, Element: item, IndexedRegion: 7000)], exact: false);

        var listProgram = new SourceBinder().Bind("let actual = $std::containers::List::new([1, 2, 3]); $std::containers::List::size(actual)", "list-key-schema.msync");
        var listAnalyzer = new SemanticAnalyzer(listProgram);
        listAnalyzer.Analyze();
        var listState = listAnalyzer.FinalState;
        var list = listState.Values[listProgram.Declarations.Single(item => item.Name == "actual").Location];
        int listBefore = (int)regionField.GetValue(listAnalyzer)!;
        string listDirect = Key(listAnalyzer, listState, [list], true);
        Check((int)regionField.GetValue(listAnalyzer)! == listBefore, "The real List result takes the readonly graph path.");
        Check(listDirect == Key(listAnalyzer, listState, [list], false), "Real List readonly/materialized schemas agree.");
        var containers = listState.Values[listProgram.StandardLocations["containers"]].Scope!;
        containers = listState.Objects.GetValueOrDefault(containers.Region, containers);
        var listClass = containers.Fields["List"].Scope!;
        listClass = listState.Objects.GetValueOrDefault(listClass.Region, listClass);
        var constructor = listClass.Fields["new"].Callables.Single();
        var enrich = typeof(SemanticAnalyzer).GetMethod("EnrichFutureDomains", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var environment = constructor.Captures.Select(capture => (SemanticValue)enrich.Invoke(listAnalyzer,
            [listState.Values.GetValueOrDefault(capture.Source, capture.Value), listState, capture.Source, true])!).ToArray();
        SemanticValue[] input = [list.Scope!.Fields["items"] with { EagerRange = false }];
        int classBefore = (int)regionField.GetValue(listAnalyzer)!;
        string classDirect = Key(listAnalyzer, listState, input, true, environment);
        Check((int)regionField.GetValue(listAnalyzer)! == classBefore, "The actual List constructor capture environment takes the readonly path.");
        Check(classDirect == Key(listAnalyzer, listState, input, false, environment), "Actual List constructor capture keys match normalization.");
        Console.WriteLine($"PASS {checks} readonly/normalized key schema properties");

        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
