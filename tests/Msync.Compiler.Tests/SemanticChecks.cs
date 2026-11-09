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
            AssertType(Accept("if exists ./condition { true } else {}"), TypeRef.Bool.WithNull);
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
            int initialRoot = main.First(instruction => instruction.Op == OpCode.InitializeLocal).A;
            Require(closure > 0 && main[closure - 1] is { Op: OpCode.LoadCell } load && load.A == initialRoot, "Closure must capture the original root binding cell, not a later shadow.");
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
            const string definitions = "let selected = if exists ./condition { scope { item: ~/left, flag: true, nested: scope { value: \"left\", }, leftOnly: true, } } else { scope { item: ~/right, flag: false, nested: scope { value: \"right\", }, rightOnly: false, } }; ";
            AssertType(Accept(definitions + "selected::nested::value"), TypeRef.String);
            AssertType(Accept(definitions + "selected::item"), TypeRef.Path);
            AssertType(Accept(definitions + "selected::flag"), TypeRef.Bool);
            Reject(definitions + "selected::leftOnly");
            Reject(definitions + "selected::rightOnly");
            AssertType(Accept("let selected = if exists ./condition { scope { item: ~/left, } } else { scope { item: \"right\", } }; selected::item"), TypeRef.Union(TypeRef.Path, TypeRef.String));
        });
        Check("structural Scope match joins", () =>
        {
            const string definitions = "let selected = match exists ./condition { true => { scope { item: ~/left, leftOnly: true, } }, false => { scope { item: ~/right, rightOnly: false, } } }; ";
            AssertType(Accept(definitions + "selected::item"), TypeRef.Path);
            Reject(definitions + "selected::leftOnly");
            Reject(definitions + "selected::rightOnly");
        });
        Check("same source function alias survives joins", () =>
        {
            var module = Accept("fn identity(value) { value }; let action = if exists ./condition { identity } else { identity }; action(\"text\"); action(~/document)");
            AssertType(module, TypeRef.Path);
            Require(module.Functions.Count(function => function.Name == "identity") == 2, "Same callable joined alias must retain generic specialization.");
        });
        Check("multiple generic callable branch alternatives", () =>
        {
            var module = Accept("let action = if exists ./condition { fn(value) { value } } else { fn(value) { value } }; action(\"text\"); action(~/document)");
            AssertType(module, TypeRef.Path);
            var mappings = Dispatches(module, OpCode.Call).ToArray();
            Require(mappings.Length == 2, "Both generic calls need their own dispatch mapping.");
            Require(mappings.All(mapping => mapping.Count == 2), "Each branch function must be in every dispatch.");
            Require(mappings[0].Keys.Order().SequenceEqual(mappings[1].Keys.Order()), "Dispatch must retain the original closure identities across argument types.");
            Require(mappings[0].Values.All(target => module.Functions[target].ReturnType == TypeRef.String), "String call dispatched to a wrong specialization.");
            Require(mappings[1].Values.All(target => module.Functions[target].ReturnType == TypeRef.Path), "Path call dispatched to a wrong specialization.");
            Require(!mappings[0].Values.Intersect(mappings[1].Values).Any(), "Different argument types must not reuse the wrong specialization targets.");
            var repeated = Accept("let action = if exists ./condition { fn(value) { value } } else { fn(value) { value } }; action(\"first\"); action(\"second\")");
            AssertType(repeated, TypeRef.String);
            var repeatedMappings = Dispatches(repeated, OpCode.Call).ToArray();
            Require(repeatedMappings.Length == 2 && repeatedMappings[0].OrderBy(pair => pair.Key).SequenceEqual(repeatedMappings[1].OrderBy(pair => pair.Key)), "Repeated same-type calls should reuse their specialization dispatch.");
        });
        Check("match callable alternatives retain result union", () =>
        {
            var module = Accept("let action = match exists ./condition { true => { fn() { \"text\" } }, false => { fn() { ~/document } } }; action()");
            AssertType(module, TypeRef.Union(TypeRef.Path, TypeRef.String));
            Require(Dispatches(module, OpCode.Call).Single().Count == 2, "Match function alternatives need typed dispatch.");
            Reject("let action = if exists ./condition { fn(value) { value } } else { fn() { true } }; action(true)");
        });
        Check("Scope function members retain callable alternatives", () =>
        {
            var module = Accept("let selected = if exists ./condition { scope { action: fn(value) { value }, } } else { scope { action: fn(value) { value }, } }; selected::action(\"text\"); selected::action(~/document)");
            AssertType(module, TypeRef.Path);
            Require(Dispatches(module, OpCode.Call).Count() == 2, "Structural Scope function members lost their callable dispatch.");
        });
        Check("generic source function receives action alternatives", () =>
        {
            var module = Accept("fn apply(action, value) { action(value) }; let action = if exists ./condition { fn(value) { value } } else { fn(value) { value } }; apply(action, \"text\"); apply(action, ~/document)");
            AssertType(module, TypeRef.Path);
            var applications = module.Functions.Where(function => function.Name == "apply").ToArray();
            Require(applications.Length == 2, "Generic apply must specialize for the two value argument types.");
            Require(applications.Any(function => function.ReturnType == TypeRef.String) && applications.Any(function => function.ReturnType == TypeRef.Path), "Generic source calls lost branch return types.");
            Require(applications.All(function => function.Instructions.Any(instruction => instruction.Op == OpCode.Call && !string.IsNullOrEmpty(instruction.Text))), "Action alternatives lost dispatch inside generic source functions.");
            AssertType(Accept("fn apply(action, value) { action(value) }; let action = if exists ./condition { $std::string::to_lowercase } else { $std::string::to_uppercase }; apply(action, \"Text\")"), TypeRef.String);
        });
        Check("callback alternatives across item types", () =>
        {
            var module = Accept("let action = if exists ./condition { fn(value) { value / child } } else { fn(value) { value / other } }; let texts = $std::ranges::transform([\"one\"], action); $std::ranges::transform([~/one], action)");
            AssertType(module, new(TypeKind.Range, TypeRef.Path));
            var mappings = Dispatches(module, OpCode.SpecializeClosure).ToArray();
            Require(mappings.Length == 2 && mappings.All(mapping => mapping.Count == 2), "Callback branch alternatives need two complete specialization maps.");
            Require(mappings.SelectMany(mapping => mapping.Values).Any(target => module.Functions[target].ParameterTypes?.Single() == TypeRef.String), "String callback specialization missing.");
            Require(mappings.SelectMany(mapping => mapping.Values).Any(target => module.Functions[target].ParameterTypes?.Single() == TypeRef.Path), "Path callback specialization missing.");
            Reject("let predicate = if exists ./condition { fn(value) { true } } else { fn(value) { value } }; $std::ranges::filter([~/one], predicate)");
        });
        Check("native Function aliases survive joins", () =>
        {
            var same = Accept("let lower = $std::string::to_lowercase; let action = if exists ./condition { lower } else { lower }; action(\"TEXT\")");
            AssertType(same, TypeRef.String);
            var different = Accept("let action = if exists ./condition { $std::string::to_lowercase } else { $std::string::to_uppercase }; action(\"Text\")");
            AssertType(different, TypeRef.String);
            Require(different.Constants.OfType<FunctionValue>().Any(value => value.NativeName == "string.to_lowercase") && different.Constants.OfType<FunctionValue>().Any(value => value.NativeName == "string.to_uppercase"), "Native branch identities missing from the binary value table.");
            Require(different.Functions.SelectMany(function => function.Instructions).All(instruction => instruction.Op != OpCode.ProcessCall), "A native function alias became a callable Path.");
            AssertType(Accept("let selected = if exists ./condition { scope { action: $std::string::to_lowercase, } } else { scope { action: $std::string::to_uppercase, } }; selected::action(\"Text\")"), TypeRef.String);
            AssertType(Accept("let action = match exists ./condition { true => { $std::string::to_lowercase }, false => { $std::string::to_uppercase } }; action(\"Text\")"), TypeRef.String);
            AssertType(Accept("let action = if exists ./condition { $std::string::to_lowercase } else { $std::string::to_uppercase }; $std::ranges::transform([\"Text\"], action)"), new(TypeKind.Range, TypeRef.String));
            var mixed = Accept("let action = if exists ./condition { fn(value) { value } } else { $std::string::to_uppercase }; action(\"Text\")");
            AssertType(mixed, TypeRef.String);
            var call = mixed.Functions.SelectMany(function => function.Instructions).Single(instruction => instruction.Op == OpCode.Call);
            Require(call.B > 0 && mixed.Functions[call.B - 1].ParameterTypes?.SequenceEqual([TypeRef.String]) == true,
                "Mixed source/native calls must specialize the sole source closure for String.");
            Require(mixed.Constants.OfType<FunctionValue>().Any(value => value.NativeName == "string.to_uppercase"),
                "Mixed source/native calls must retain the native alternative's identity.");
        });
        if (failures.Count > 0) throw new InvalidOperationException("Branch semantic checks failed:\n" + string.Join("\n", failures));
    }
    public static void RunTypeSpecialization()
    {
        List<string> failures = [];
        void Check(string name, Action action)
        {
            try { action(); Console.WriteLine($"type specialization ok: {name}"); }
            catch (Exception error) { failures.Add($"{name}: {error.Message}"); }
        }
        Check("generic decltype conditional dispatch", () =>
        {
            var module = Accept("fn convert(value) { if decltype(value) is Path { value -> ./out } else { false } }; convert(true); convert(./input)");
            AssertType(module, TypeRef.Bool);
            var instances = module.Functions.Where(function => function.Name == "convert").ToArray();
            Require(instances.Length == 2, "Type-directed conversion must specialize Bool and Path arguments separately.");
            var boolean = instances.Single(function => function.ParameterTypes?.Single() == TypeRef.Bool);
            var path = instances.Single(function => function.ParameterTypes?.Single() == TypeRef.Path);
            Require(boolean.ReturnType == TypeRef.Bool && boolean.Instructions.All(instruction => instruction.Op is not (OpCode.Transfer or OpCode.LocalTransfer)), "The Bool specialization must not lower the invalid Path-transfer branch.");
            Require(path.ReturnType == TypeRef.Bool && path.Instructions.Any(instruction => instruction.Op is OpCode.Transfer or OpCode.LocalTransfer), "The Path specialization must lower its selected transfer.");
        });
        Check("generic decltype match dispatch", () =>
        {
            var module = Accept("fn viaMatch(value) { match decltype(value) { Path => { value -> ./out }, Bool => { not value }, _ => { false } } }; viaMatch(true); viaMatch(./input)");
            AssertType(module, TypeRef.Bool);
            var instances = module.Functions.Where(function => function.Name == "viaMatch").ToArray();
            Require(instances.Length == 2, "Type match must specialize both admitted argument types.");
            var boolean = instances.Single(function => function.ParameterTypes?.Single() == TypeRef.Bool);
            var path = instances.Single(function => function.ParameterTypes?.Single() == TypeRef.Path);
            Require(boolean.Instructions.Any(instruction => instruction.Op == OpCode.Not) && boolean.Instructions.All(instruction => instruction.Op is not (OpCode.Transfer or OpCode.LocalTransfer)), "The Bool type-match specialization must lower only boolean negation.");
            Require(path.Instructions.Any(instruction => instruction.Op is OpCode.Transfer or OpCode.LocalTransfer) && path.Instructions.All(instruction => instruction.Op != OpCode.Not), "The Path type-match specialization must not validate or lower boolean negation.");
        });
        Check("compile-time type conditions exclude invalid branches", () =>
        {
            AssertType(Accept("if decltype(./input) is Path { true } else { not ./input }"), TypeRef.Bool);
            AssertType(Accept("if decltype(true) is Path { true -> ./out } else { false }"), TypeRef.Bool);
            AssertType(Accept("match decltype(./input) { Bool => { not ./input }, Path => { true } }"), TypeRef.Bool);
        });
        Check("Bool argument values do not contaminate type specialization", () =>
        {
            var module = Accept("fn runtimeFlag(flag) { if flag { true } else { false } }; runtimeFlag(true); runtimeFlag(false)");
            var instances = module.Functions.Where(function => function.Name == "runtimeFlag").ToArray();
            Require(instances.Length == 1, "Both Bool values must reuse one type specialization.");
            var instructions = instances[0].Instructions;
            Require(instructions.Any(instruction => instruction.Op == OpCode.JumpIfFalse), "A Bool parameter was incorrectly folded using its first caller's value.");
            Require(instructions.Any(instruction => instruction.Op == OpCode.True) && instructions.Any(instruction => instruction.Op == OpCode.False), "A reused specialization must retain both runtime result branches.");
            var match = Accept("fn runtimeMatch(flag) { match flag { true => { ./truth }, false => { \"false\" } } }; runtimeMatch(true); runtimeMatch(false)");
            var matcher = match.Functions.Single(function => function.Name == "runtimeMatch");
            Require(matcher.ReturnType == TypeRef.Union(TypeRef.Path, TypeRef.String), "Bool pattern matching must retain both possible result types.");
            Require(matcher.Instructions.Any(instruction => instruction.Op == OpCode.JumpIfFalse), "A Bool match parameter was incorrectly folded using its first caller's value.");
        });
        Check("decltype calls and process operands remain unevaluated", () =>
        {
            var module = Accept("fn transfer(value) { value -> ./out }; let transferType = decltype(transfer(./input)); let processType = decltype(./bin/tool()); transferType is Bool and processType is Process");
            var main = module.Functions[module.EntryPoint].Instructions;
            Require(main.All(instruction => instruction.Op is not (OpCode.Transfer or OpCode.LocalTransfer or OpCode.ProcessCall or OpCode.Call)), "An unevaluated operand emitted a runtime call or effect in main.");
            Require(module.Constants.OfType<TypeValue>().Any(value => value.Data == TypeRef.Bool) && module.Constants.OfType<TypeValue>().Any(value => value.Data == TypeRef.Process), "Unevaluated effect operands lost their inferred types.");
        });
        Check("static type-match exhaustiveness", () =>
        {
            Reject("match decltype(true) { Path => { true } }");
            Reject("fn missing(value) { match decltype(value) { Path => { true } } }; missing(true)");
            AssertType(Accept("match decltype(./input) { Path => { true } }"), TypeRef.Bool);
            AssertType(Accept("match decltype(true) { Bool => { true } }"), TypeRef.Bool);
        });
        if (failures.Count > 0) throw new InvalidOperationException("Type specialization checks failed:\n" + string.Join("\n", failures));
    }
    public static void RunRegexUnions()
    {
        List<string> failures = [];
        void Check(string name, Action action)
        {
            try { action(); Console.WriteLine($"regex union ok: {name}"); }
            catch (Exception error) { failures.Add($"{name}: {error.Message}"); }
        }
        Check("nullable Path regex arm narrows to Path", () =>
        {
            var module = Accept("let value? = if exists ./condition { ./input } else { null }; match value { <.+> => { value -> ./out }, _ => { false } }");
            AssertType(module, TypeRef.Bool);
            Require(module.Functions[0].Instructions.Any(instruction => instruction.Op == OpCode.RegexMatch), "A nullable Path regex must retain a runtime text-domain predicate.");
            Require(module.Functions[0].Instructions.Any(instruction => instruction.Op is OpCode.Transfer or OpCode.LocalTransfer), "The matching nullable Path arm must lower its narrowed transfer.");
        });
        Check("mixed Bool and Path regex arm narrows to Path", () =>
        {
            var module = Accept("let value = if exists ./condition { ./input } else { false }; match value { <.+> => { value -> ./out }, _ => { false } }");
            AssertType(module, TypeRef.Bool);
            Require(module.Functions[0].Instructions.Any(instruction => instruction.Op == OpCode.RegexMatch), "A Bool|Path match needs a partial text-domain predicate.");
            Require(module.Functions[0].Instructions.Any(instruction => instruction.Op is OpCode.Transfer or OpCode.LocalTransfer), "A regex match arm must narrow Bool|Path before transfer typing.");
        });
        Check("Bool-only regex arm is statically impossible", () =>
        {
            var module = Accept("fn inspect(value: Bool) { match value { <.+> => { value -> ./out }, _ => { not value } } }; inspect(true); inspect(false)");
            AssertType(module, TypeRef.Bool);
            var function = module.Functions.Single(function => function.Name == "inspect");
            Require(function.Instructions.All(instruction => instruction.Op is not (OpCode.RegexMatch or OpCode.Transfer or OpCode.LocalTransfer)), "An impossible Bool-only regex arm must be omitted before invalid transfer typing.");
            Require(function.Instructions.Any(instruction => instruction.Op == OpCode.Not), "The possible fallback arm must remain executable.");
        });
        Check("regex exhaustiveness and BCL syntax validation", () =>
        {
            Reject("match ./input { <.+> => { true } }");
            Reject("let value? = if exists ./condition { ./input } else { null }; match value { <.+> => { true } }");
            Reject("match true { <.+> => { true } }");
            Reject("match ./input { <a(**> => { true }, _ => { false } }");
        });
        if (failures.Count > 0) throw new InvalidOperationException("Regex union checks failed:\n" + string.Join("\n", failures));
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
