using Msync.Compiler;
using Msync.Core;
using Frontend = Msync.Compiler.Compiler;

internal static class SemanticChecks
{
    public static void Run()
    {
        List<string> failures = [];
        void Check(string name, Action action)
        {
            try { action(); Console.WriteLine($"semantic ok: {name}"); }
            catch (Exception error) { failures.Add($"{name}: {error.Message}"); }
        }
        Check("null admission", () =>
        {
            Reject("let value = null;");
            Accept("let value? = null;");
            Accept("let value: Null? = null;");
            Accept("let _ = null;");
            Reject("let value? = if true { ~/input } else { null }; let copy = value;");
            Reject("let value? = if true { ~/input } else { null }; value -> ~/output;");
        });
        Check("Null is excluded from Bool logic", () =>
        {
            foreach (string expression in new[] { "null == null", "null != true", "null and true", "true or null", "not null", "if null { true } else { false }" }) Reject(expression);
            AssertType(Accept("null is null"), TypeRef.Bool);
            AssertType(Accept("null is not null"), TypeRef.Bool);
        });
        Check("if null refinement", () =>
        {
            AssertType(Accept("let value? = if true { ~/input } else { null }; if value is null { false } else { value -> ~/output }"), TypeRef.Bool);
            AssertType(Accept("let value? = if true { ~/input } else { null }; if value? { false } else { value -> ~/output }"), TypeRef.Bool);
            AssertType(Accept("let value? = if true { ~/input } else { null }; if value is not null { value -> ~/output } else { false }"), TypeRef.Bool);
            Reject("let value? = if true { ~/input } else { null }; if value? { value -> ~/output } else { false }");
            AssertType(Accept("if true { true } else {}"), TypeRef.Bool.WithNull);
        });
        Check("match exhaustiveness and null refinement", () =>
        {
            AssertType(Accept("let value? = if true { ~/input } else { null }; match value { null => { false }, _ => { value -> ~/output } }"), TypeRef.Bool);
            AssertType(Accept("match true { true => { false }, false => { true } }"), TypeRef.Bool);
            Reject("match true { true => { false } }");
            Reject("let value? = if true { ~/input } else { null }; match value { Path => { true } }");
            Reject("match ~/input { <.+> => { true } }");
            AssertType(Accept("match decltype(~/input) { Path => { true } }"), TypeRef.Bool);
        });
        Check("generic specializations", () =>
        {
            var module = Accept("fn identity(value) { value }; identity(\"text\"); identity(~/input)");
            var instances = module.Functions.Where(function => function.Name == "identity").ToArray();
            Require(instances.Any(function => function.ReturnType == TypeRef.Path), "Missing Path specialization.");
            Require(instances.Any(function => function.ReturnType == TypeRef.String), "Missing String specialization.");
            Require(instances.All(function => function.ParameterTypes?.Count == 1), "Specializations must retain parameter types.");
            AssertType(module, TypeRef.Path);
        });
        Check("callback specialization", () =>
        {
            var module = Accept("let convert = fn(value) { value / child }; $std::ranges::transform([~/one, ~/two], convert)");
            AssertType(module, new(TypeKind.Range, TypeRef.Path));
            Require(module.Functions.SelectMany(function => function.Instructions).Any(instruction => instruction.Op == OpCode.SpecializeClosure), "Missing typed callback specialization opcode.");
            Reject("$std::ranges::filter([~/one], fn(value) { value })");
        });
        Check("recursive specialization", () =>
        {
            var module = Accept("fn recurse(value) { if value { true } else { recurse(true) } }; recurse(false)");
            AssertType(module, TypeRef.Bool);
            Require(module.Functions.Count(function => function.Name == "recurse") == 1, "Same-type recursion must reuse one specialization.");
            AssertType(Accept("fn countdown(value) { if value == 0 { true } else { countdown(value - 1) } }; countdown(4)"), TypeRef.Bool);
        });
        Check("unevaluated decltype", () =>
        {
            var module = Accept("let copyType = decltype(~/source -> ~/destination); let processType = decltype(~/tool(\"arg\")); copyType is Bool and processType is Process");
            Require(module.Functions[0].Instructions.All(instruction => instruction.Op is not (OpCode.Transfer or OpCode.LocalTransfer or OpCode.ProcessCall)), "decltype emitted an effect in main.");
            Require(module.Constants.OfType<TypeValue>().Any(value => value.Data == TypeRef.Bool), "Missing inferred transfer Bool type.");
            Require(module.Constants.OfType<TypeValue>().Any(value => value.Data == TypeRef.Process), "Missing inferred callable Path Process type.");
        });
        Check("return union", () =>
        {
            var module = Accept("fn choose(flag) { if flag { return \"yes\"; }; ~/fallback }; choose(true)");
            AssertType(module, TypeRef.Union(TypeRef.String, TypeRef.Path));
        });
        Check("explicit Path/String conversions", () =>
        {
            var module = Accept("let path: Path = \"relative.txt\"; let text: String = path; text");
            Require(module.Functions[0].Instructions.Any(instruction => instruction.Op == OpCode.Convert && instruction.Text == "Path"), "Missing String-to-Path Convert.");
            Require(module.Functions[0].Instructions.Any(instruction => instruction.Op == OpCode.Convert && instruction.Text == "String"), "Missing Path-to-String Convert.");
            var typedResult = Accept("fn path() -> Path { \"relative.txt\" }; path()");
            Require(typedResult.Functions.Where(function => function.Name == "path").SelectMany(function => function.Instructions).Any(instruction => instruction.Op == OpCode.Convert && instruction.Text == "Path"), "Explicit function return must materialize String-to-Path conversion.");
            var earlyResult = Accept("fn earlyPath() -> Path { return \"relative.txt\"; }; earlyPath()");
            var earlyCode = earlyResult.Functions.Single(function => function.Name == "earlyPath").Instructions;
            int firstReturn = earlyCode.ToList().FindIndex(instruction => instruction.Op == OpCode.Return);
            Require(firstReturn > 0 && earlyCode[firstReturn - 1] is { Op: OpCode.Convert, Text: "Path" }, "Explicit early return must convert before its Return opcode.");
            var typedParameter = Accept("fn accept(value: Path) { value }; accept(\"relative.txt\")");
            Require(typedParameter.Functions.Single(function => function.Name == "accept").Instructions.Any(instruction => instruction.Op == OpCode.Convert && instruction.Text == "Path"), "Typed parameters must materialize their implicit conversion.");
        });
        Check("lexical captures", () =>
        {
            var module = Accept("let root = ~/original; fn child(file) { root / file }; let root = ~/later; child(config.toml)");
            var function = module.Functions.Single(function => function.Name == "child");
            Require(function.CaptureCount == 1, "Capture only the referenced lexical root.");
            var main = module.Functions[0].Instructions;
            int closure = main.ToList().FindIndex(instruction => instruction.Op == OpCode.MakeClosure);
            int initialRoot = main.First(instruction => instruction.Op == OpCode.StoreLocal).A;
            Require(closure > 0 && main[closure - 1] is { Op: OpCode.LoadLocal } load && load.A == initialRoot, "Closure must load the original root binding, not a later shadow.");
        });
        Check("source-free binary roundtrip", () =>
        {
            const string marker = "UNIQUE_SOURCE_COMMENT_NOT_BYTECODE_80731";
            var original = Accept($"# {marker}\nfn copy(file) {{ file -> ~/output }}; copy(~/input)");
            using MemoryStream bytes = new();
            Artifact.Write(original, bytes);
            Require(!System.Text.Encoding.UTF8.GetString(bytes.ToArray()).Contains(marker, StringComparison.Ordinal), "Artifact serialized source text.");
            bytes.Position = 0;
            var restored = Artifact.Read(bytes);
            Require(original.Functions.Count == restored.Functions.Count, "Function table changed during roundtrip.");
            for (int index = 0; index < original.Functions.Count; index++)
            {
                Require(original.Functions[index].Instructions.SequenceEqual(restored.Functions[index].Instructions), "Instruction signature changed during binary roundtrip.");
                Require(original.Functions[index].ReturnType == restored.Functions[index].ReturnType, "Function result type changed during binary roundtrip.");
            }
        });
        if (failures.Count > 0) throw new InvalidOperationException("Semantic checks failed:\n" + string.Join("\n", failures));
    }
    public static void RunBranches()
    {
        List<string> failures = [];
        void Check(string name, Action action)
        {
            try { action(); Console.WriteLine($"branch semantic ok: {name}"); }
            catch (Exception error) { failures.Add($"{name}: {error.Message}"); }
        }
        Check("structural Scope if joins", () =>
        {
            const string definitions = "let selected = if true { scope { item: ~/left, flag: true, nested: scope { value: \"left\", }, leftOnly: true, } } else { scope { item: ~/right, flag: false, nested: scope { value: \"right\", }, rightOnly: false, } }; ";
            AssertType(Accept(definitions + "selected::nested::value"), TypeRef.String);
            AssertType(Accept(definitions + "selected::item"), TypeRef.Path);
            AssertType(Accept(definitions + "selected::flag"), TypeRef.Bool);
            Reject(definitions + "selected::leftOnly");
            Reject(definitions + "selected::rightOnly");
            AssertType(Accept("let selected = if true { scope { item: ~/left, } } else { scope { item: \"right\", } }; selected::item"), TypeRef.Union(TypeRef.Path, TypeRef.String));
        });
        Check("structural Scope match joins", () =>
        {
            const string definitions = "let selected = match true { true => { scope { item: ~/left, leftOnly: true, } }, false => { scope { item: ~/right, rightOnly: false, } } }; ";
            AssertType(Accept(definitions + "selected::item"), TypeRef.Path);
            Reject(definitions + "selected::leftOnly");
            Reject(definitions + "selected::rightOnly");
        });
        Check("same source function alias survives joins", () =>
        {
            var module = Accept("fn identity(value) { value }; let action = if true { identity } else { identity }; action(\"text\"); action(~/document)");
            AssertType(module, TypeRef.Path);
            Require(module.Functions.Count(function => function.Name == "identity") == 2, "Same callable joined alias must retain generic specialization.");
        });
        Check("multiple generic callable branch alternatives", () =>
        {
            var module = Accept("let action = if true { fn(value) { value } } else { fn(value) { value } }; action(\"text\"); action(~/document)");
            AssertType(module, TypeRef.Path);
            var mappings = Dispatches(module, OpCode.Call).ToArray();
            Require(mappings.Length == 2, "Both generic calls need their own dispatch mapping.");
            Require(mappings.All(mapping => mapping.Count == 2), "Each branch function must be in every dispatch.");
            Require(mappings[0].Keys.Order().SequenceEqual(mappings[1].Keys.Order()), "Dispatch must retain the original closure identities across argument types.");
            Require(mappings[0].Values.All(target => module.Functions[target].ReturnType == TypeRef.String), "String call dispatched to a wrong specialization.");
            Require(mappings[1].Values.All(target => module.Functions[target].ReturnType == TypeRef.Path), "Path call dispatched to a wrong specialization.");
            Require(!mappings[0].Values.Intersect(mappings[1].Values).Any(), "Different argument types must not reuse the wrong specialization targets.");
            var repeated = Accept("let action = if true { fn(value) { value } } else { fn(value) { value } }; action(\"first\"); action(\"second\")");
            AssertType(repeated, TypeRef.String);
            var repeatedMappings = Dispatches(repeated, OpCode.Call).ToArray();
            Require(repeatedMappings.Length == 2 && repeatedMappings[0].OrderBy(pair => pair.Key).SequenceEqual(repeatedMappings[1].OrderBy(pair => pair.Key)), "Repeated same-type calls should reuse their specialization dispatch.");
        });
        Check("match callable alternatives retain result union", () =>
        {
            var module = Accept("let action = match true { true => { fn() { \"text\" } }, false => { fn() { ~/document } } }; action()");
            AssertType(module, TypeRef.Union(TypeRef.Path, TypeRef.String));
            Require(Dispatches(module, OpCode.Call).Single().Count == 2, "Match function alternatives need typed dispatch.");
            Reject("let action = if true { fn(value) { value } } else { fn() { true } }; action(true)");
        });
        Check("Scope function members retain callable alternatives", () =>
        {
            var module = Accept("let selected = if true { scope { action: fn(value) { value }, } } else { scope { action: fn(value) { value }, } }; selected::action(\"text\"); selected::action(~/document)");
            AssertType(module, TypeRef.Path);
            Require(Dispatches(module, OpCode.Call).Count() == 2, "Structural Scope function members lost their callable dispatch.");
        });
        Check("generic source function receives action alternatives", () =>
        {
            var module = Accept("fn apply(action, value) { action(value) }; let action = if true { fn(value) { value } } else { fn(value) { value } }; apply(action, \"text\"); apply(action, ~/document)");
            AssertType(module, TypeRef.Path);
            var applications = module.Functions.Where(function => function.Name == "apply").ToArray();
            Require(applications.Length == 2, "Generic apply must specialize for the two value argument types.");
            Require(applications.Any(function => function.ReturnType == TypeRef.String) && applications.Any(function => function.ReturnType == TypeRef.Path), "Generic source calls lost branch return types.");
            Require(applications.All(function => function.Instructions.Any(instruction => instruction.Op == OpCode.Call && !string.IsNullOrEmpty(instruction.Text))), "Action alternatives lost dispatch inside generic source functions.");
            AssertType(Accept("fn apply(action, value) { action(value) }; let action = if true { $std::string::to_lowercase } else { $std::string::to_uppercase }; apply(action, \"Text\")"), TypeRef.String);
        });
        Check("callback alternatives across item types", () =>
        {
            var module = Accept("let action = if true { fn(value) { value / child } } else { fn(value) { value / other } }; let texts = $std::ranges::transform([\"one\"], action); $std::ranges::transform([~/one], action)");
            AssertType(module, new(TypeKind.Range, TypeRef.Path));
            var mappings = Dispatches(module, OpCode.SpecializeClosure).ToArray();
            Require(mappings.Length == 2 && mappings.All(mapping => mapping.Count == 2), "Callback branch alternatives need two complete specialization maps.");
            Require(mappings.SelectMany(mapping => mapping.Values).Any(target => module.Functions[target].ParameterTypes?.Single() == TypeRef.String), "String callback specialization missing.");
            Require(mappings.SelectMany(mapping => mapping.Values).Any(target => module.Functions[target].ParameterTypes?.Single() == TypeRef.Path), "Path callback specialization missing.");
            Reject("let predicate = if true { fn(value) { true } } else { fn(value) { value } }; $std::ranges::filter([~/one], predicate)");
        });
        Check("native Function aliases survive joins", () =>
        {
            var same = Accept("let lower = $std::string::to_lowercase; let action = if true { lower } else { lower }; action(\"TEXT\")");
            AssertType(same, TypeRef.String);
            var different = Accept("let action = if true { $std::string::to_lowercase } else { $std::string::to_uppercase }; action(\"Text\")");
            AssertType(different, TypeRef.String);
            Require(different.Constants.OfType<FunctionValue>().Any(value => value.NativeName == "string.to_lowercase") && different.Constants.OfType<FunctionValue>().Any(value => value.NativeName == "string.to_uppercase"), "Native branch identities missing from the binary value table.");
            Require(different.Functions.SelectMany(function => function.Instructions).All(instruction => instruction.Op != OpCode.ProcessCall), "A native function alias became a callable Path.");
            AssertType(Accept("let selected = if true { scope { action: $std::string::to_lowercase, } } else { scope { action: $std::string::to_uppercase, } }; selected::action(\"Text\")"), TypeRef.String);
            AssertType(Accept("let action = match true { true => { $std::string::to_lowercase }, false => { $std::string::to_uppercase } }; action(\"Text\")"), TypeRef.String);
            AssertType(Accept("let action = if true { $std::string::to_lowercase } else { $std::string::to_uppercase }; $std::ranges::transform([\"Text\"], action)"), new(TypeKind.Range, TypeRef.String));
            var mixed = Accept("let action = if true { fn(value) { value } } else { $std::string::to_uppercase }; action(\"Text\")");
            AssertType(mixed, TypeRef.String);
            Require(Dispatches(mixed, OpCode.Call).Single().Count == 1, "Mixed source/native calls must map the source closure while preserving native identity.");
        });
        if (failures.Count > 0) throw new InvalidOperationException("Branch semantic checks failed:\n" + string.Join("\n", failures));
    }
    private static IEnumerable<Dictionary<int, int>> Dispatches(Module module, OpCode opcode)
    {
        foreach (var instruction in module.Functions.SelectMany(function => function.Instructions).Where(instruction => instruction.Op == opcode && !string.IsNullOrEmpty(instruction.Text)))
        {
            Dictionary<int, int> mapping = [];
            foreach (string entry in instruction.Text!.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = entry.Split(':');
                Require(pair.Length == 2, $"Invalid specialization dispatch pair '{entry}'.");
                int source = int.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture);
                int target = int.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture);
                Require(source >= 0 && source < module.Functions.Count && target >= 0 && target < module.Functions.Count, "Dispatch references a nonexistent function.");
                Require(mapping.TryAdd(source, target), "Dispatch has conflicting entries for one closure.");
            }
            yield return mapping;
        }
    }
    private static Module Accept(string source) => Frontend.Compile(source, "semantic-check.msync", optimization: 0);
    private static void Reject(string source)
    {
        try { Accept(source); }
        catch (CompileException) { return; }
        throw new InvalidOperationException("Accepted invalid program: " + source);
    }
    private static void AssertType(Module module, TypeRef expected) => Require(module.Functions[module.EntryPoint].ReturnType == expected, $"Expected {expected}, inferred {module.Functions[module.EntryPoint].ReturnType}.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
