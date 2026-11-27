using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
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
            Reject("let value? = if exists ./unknown { ~/input } else { null }; let copy = value;");
            Reject("let value? = if exists ./unknown { ~/input } else { null }; value -> ~/output;");
        });
        Check("constant branch precision", () =>
        {
            AssertType(Accept("let value? = if true { ~/input } else { null }; let copy = value; copy"), TypeRef.Path);
            AssertType(Accept("let value? = if true { ~/input } else { null }; value -> ~/output"), TypeRef.Bool);
            AssertType(Accept("let value? = if true { ~/input } else { null }; if value? { value -> ~/output } else { false }"), TypeRef.Bool);
            AssertType(Accept("let value? = if true { ~/input } else { null }; match value { Path => { true } }"), TypeRef.Bool);
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
            Reject("let value? = if exists ./unknown { ~/input } else { null }; if value? { value -> ~/output } else { false }");
            AssertType(Accept("if exists ./condition { true } else {}"), TypeRef.Bool.WithNull);
        });
        Check("match exhaustiveness and null refinement", () =>
        {
            AssertType(Accept("let value? = if true { ~/input } else { null }; match value { null => { false }, _ => { value -> ~/output } }"), TypeRef.Bool);
            AssertType(Accept("match true { true => { false }, false => { true } }"), TypeRef.Bool);
            Reject("match true { true => { false } }");
            Reject("let value? = if exists ./unknown { ~/input } else { null }; match value { Path => { true } }");
            Reject("match ~/input { <.+> => { true } }");
            AssertType(Accept("match decltype(~/input) { Path => { true } }"), TypeRef.Bool);
        });
        Check("generic specializations", () =>
        {
            var module = Accept("fn identity(value) { value }; identity(\"text\"); identity(~/input)");
            var instances = module.Functions.Where(function => !function.IsTemplate && function.Name == "identity").ToArray();
            Require(instances.Any(function => Result(module, function) == TypeRef.Path), "Missing Path specialization.");
            Require(instances.Any(function => Result(module, function) == TypeRef.String), "Missing String specialization.");
            Require(instances.All(function => function.Signature.Parameters.Count == 1), "Specializations must retain parameter types.");
            AssertType(module, TypeRef.Path);
        });
        Check("callback specialization", () =>
        {
            var module = Accept("let convert = fn(value) { value / child }; $std::ranges::transform([~/one, ~/two], convert)");
            AssertType(module, new(TypeKind.Range, TypeRef.Path));
            Require(module.Functions.SelectMany(function => function.Instructions).Any(instruction => instruction.Op == NativeOp.AdaptFunction), "Missing typed callback specialization opcode.");
            Reject("$std::ranges::filter([~/one], fn(value) { value })");
        });
        Check("recursive specialization", () =>
        {
            var module = Accept("fn recurse(value) { if value { true } else { recurse(true) } }; recurse(false)");
            AssertType(module, TypeRef.Bool);
            Require(module.Functions.Count(function => !function.IsTemplate && function.Name == "recurse") == 1, "Same-type recursion must reuse one specialization.");
            AssertType(Accept("fn countdown(value) { if value == 0 { true } else { countdown(value - 1) } }; countdown(4)"), TypeRef.Bool);
        });
        Check("unevaluated decltype", () =>
        {
            var module = Accept("let copyType = decltype(~/source -> ~/destination); let processType = decltype(~/tool(\"arg\")); copyType is Bool and processType is Process");
            Require(module.Functions[0].Instructions.All(instruction => instruction.Op is not (NativeOp.Transfer or NativeOp.ProcessCall)), "decltype emitted an effect in main.");
            Require(module.Constants.Any(value => value.Kind == NativeKind.Type && value.Type == TypeRef.Bool), "Missing inferred transfer Bool type.");
            Require(module.Constants.Any(value => value.Kind == NativeKind.Type && value.Type == TypeRef.Process), "Missing inferred callable Path Process type.");
        });
        Check("return union", () =>
        {
            var module = Accept("fn choose(flag) { if flag { return \"yes\"; }; ~/fallback }; choose(true)");
            AssertType(module, TypeRef.Union(TypeRef.String, TypeRef.Path));
        });
        Check("explicit Path/String conversions", () =>
        {
            var module = Accept("let path: Path = \"relative.txt\"; let text: String = path; text");
            Require(module.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.Convert && RuntimeType(module, instruction.Type) == TypeRef.Path), "Missing String-to-Path Convert.");
            Require(module.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.Convert && RuntimeType(module, instruction.Type) == TypeRef.String), "Missing Path-to-String Convert.");
            var typedResult = Accept("fn path() -> Path { \"relative.txt\" }; path()");
            Require(typedResult.Functions.Where(function => !function.IsTemplate && function.Name == "path" && !function.IsTemplate).SelectMany(function => function.Instructions).Any(instruction => instruction.Op == NativeOp.Convert && RuntimeType(typedResult, instruction.Type) == TypeRef.Path), "Explicit function return must materialize String-to-Path conversion.");
            var earlyResult = Accept("fn earlyPath() -> Path { return \"relative.txt\"; }; earlyPath()");
            var earlyCode = earlyResult.Functions.Single(function => !function.IsTemplate && function.Name == "earlyPath").Instructions;
            int firstReturn = earlyCode.ToList().FindIndex(instruction => instruction.Op == NativeOp.Return);
            Require(firstReturn > 0 && earlyCode[firstReturn - 1].Op == NativeOp.Convert && RuntimeType(earlyResult, earlyCode[firstReturn - 1].Type) == TypeRef.Path, "Explicit early return must convert before its Return opcode.");
            var typedParameter = Accept("fn accept(value: Path) { value }; accept(\"relative.txt\")");
            Require(typedParameter.Functions.SelectMany(function => function.Instructions).Any(instruction => instruction.Op == NativeOp.Convert && RuntimeType(typedParameter, instruction.Type) == TypeRef.Path), "Typed parameters must materialize their implicit conversion.");
        });
        Check("lexical captures", () =>
        {
            var module = Accept("let root = ~/original; fn child(file) { root / file }; let root = ~/later; child(config.toml)");
            var function = module.Functions.Single(function => !function.IsTemplate && function.Name == "child");
            Require(function.CaptureCount == 1, "Capture only the referenced lexical root.");
            var main = module.Functions[0].Instructions;
            NativeInstruction closure = main.First(instruction => instruction.Op == NativeOp.MakeFunction && instruction.A >= 0
                && module.Functions[instruction.A].Name == "child");
            int initialRoot = module.Functions[module.EntryPoint].Slots.Select((slot, index) => (slot, index))
                .First(item => item.slot.Role == NativeSlotRole.Local && item.slot.Name == "root").index;
            Require(closure.Inputs is { Count: 1 } && OriginSlot(main, closure.Inputs[0]) == initialRoot,
                "Closure must capture the original root binding cell, not a later shadow.");
        });
        Check("source-free binary roundtrip", () =>
        {
            const string marker = "UNIQUE_SOURCE_COMMENT_NOT_BYTECODE_80731";
            var original = Accept($"# {marker}\nfn copy(file) {{ file -> ~/output }}; copy(~/input)");
            using MemoryStream bytes = new();
            NativeArtifact.Write(original, bytes);
            Require(!System.Text.Encoding.UTF8.GetString(bytes.ToArray()).Contains(marker, StringComparison.Ordinal), "Artifact serialized source text.");
            bytes.Position = 0;
            var restored = NativeArtifact.Read(bytes);
            Require(original.Functions.Count == restored.Functions.Count, "Function table changed during roundtrip.");
            for (int index = 0; index < original.Functions.Count; index++)
            {
                Require(SameInstructions(original.Functions[index].Instructions, restored.Functions[index].Instructions), "Instruction signature changed during binary roundtrip.");
                Require(Result(original, original.Functions[index]) == Result(restored, restored.Functions[index]), "Function result type changed during binary roundtrip.");
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
        Check("structural Object if joins", () =>
        {
            const string definitions = "let selected = if exists ./condition { object { item: ~/left, flag: true, nested: object { value: \"left\", }, leftOnly: true, } } else { object { item: ~/right, flag: false, nested: object { value: \"right\", }, rightOnly: false, } }; ";
            AssertType(Accept(definitions + "selected::nested::value"), TypeRef.String);
            AssertType(Accept(definitions + "selected::item"), TypeRef.Path);
            AssertType(Accept(definitions + "selected::flag"), TypeRef.Bool);
            Reject(definitions + "selected::leftOnly");
            Reject(definitions + "selected::rightOnly");
            AssertType(Accept("let selected = if exists ./condition { object { item: ~/left, } } else { object { item: \"right\", } }; selected::item"), TypeRef.Union(TypeRef.Path, TypeRef.String));
        });
        Check("structural Object match joins", () =>
        {
            const string definitions = "let selected = match exists ./condition { true => { object { item: ~/left, leftOnly: true, } }, false => { object { item: ~/right, rightOnly: false, } } }; ";
            AssertType(Accept(definitions + "selected::item"), TypeRef.Path);
            Reject(definitions + "selected::leftOnly");
            Reject(definitions + "selected::rightOnly");
        });
        Check("same source function alias survives joins", () =>
        {
            var module = Accept("fn identity(value) { value }; let action = if exists ./condition { identity } else { identity }; action(\"text\"); action(~/document)");
            AssertType(module, TypeRef.Path);
            Require(module.Functions.Count(function => !function.IsTemplate && function.Name == "identity") == 2, "Same callable joined alias must retain generic specialization.");
        });
        Check("multiple generic callable branch alternatives", () =>
        {
            var module = Accept("let action = if exists ./condition { fn(value) { value } } else { fn(value) { value } }; action(\"text\"); action(~/document)");
            AssertType(module, TypeRef.Path);
            var mappings = Dispatches(module, NativeOp.Call).ToArray();
            Require(mappings.Length == 2, "Both generic calls need their own dispatch mapping.");
            Require(mappings.All(mapping => mapping.Count == 2), "Each branch function must be in every dispatch.");
            Require(mappings[0].Keys.Order().SequenceEqual(mappings[1].Keys.Order()), "Dispatch must retain the original closure identities across argument types.");
            Require(mappings[0].Values.All(target => Result(module, module.Functions[target]) == TypeRef.String), "String call dispatched to a wrong specialization.");
            Require(mappings[1].Values.All(target => Result(module, module.Functions[target]) == TypeRef.Path), "Path call dispatched to a wrong specialization.");
            Require(!mappings[0].Values.Intersect(mappings[1].Values).Any(), "Different argument types must not reuse the wrong specialization targets.");
            var repeated = Accept("let action = if exists ./condition { fn(value) { value } } else { fn(value) { value } }; action(\"first\"); action(\"second\")");
            AssertType(repeated, TypeRef.String);
            var repeatedMappings = Dispatches(repeated, NativeOp.Call).ToArray();
            Require(repeatedMappings.Length == 2 && repeatedMappings[0].OrderBy(pair => pair.Key).SequenceEqual(repeatedMappings[1].OrderBy(pair => pair.Key)), "Repeated same-type calls should reuse their specialization dispatch.");
        });
        Check("match callable alternatives retain result union", () =>
        {
            var module = Accept("let action = match exists ./condition { true => { fn() { \"text\" } }, false => { fn() { ~/document } } }; action()");
            AssertType(module, TypeRef.Union(TypeRef.Path, TypeRef.String));
            Require(Dispatches(module, NativeOp.Call).Single().Count == 2, "Match function alternatives need typed dispatch.");
            Reject("let action = if exists ./condition { fn(value) { value } } else { fn() { true } }; action(true)");
        });
        Check("Object function members retain callable alternatives", () =>
        {
            var module = Accept("let selected = if exists ./condition { object { action: fn(value) { value }, } } else { object { action: fn(value) { value }, } }; selected::action(\"text\"); selected::action(~/document)");
            AssertType(module, TypeRef.Path);
            Require(Dispatches(module, NativeOp.Call).Count() == 2, "Structural Object function members lost their callable dispatch.");
        });
        Check("generic source function receives action alternatives", () =>
        {
            var module = Accept("fn apply(action, value) { action(value) }; let action = if exists ./condition { fn(value) { value } } else { fn(value) { value } }; apply(action, \"text\"); apply(action, ~/document)");
            AssertType(module, TypeRef.Path);
            var applications = module.Functions.Where(function => !function.IsTemplate && function.Name == "apply").ToArray();
            Require(applications.Length == 2, "Generic apply must specialize for the two value argument types.");
            Require(applications.Any(function => Result(module, function) == TypeRef.String) && applications.Any(function => Result(module, function) == TypeRef.Path), "Generic source calls lost branch return types.");
            Require(applications.All(function => function.Instructions.Any(instruction => instruction.Op == NativeOp.Call && !string.IsNullOrEmpty(instruction.Symbol))), "Action alternatives lost dispatch inside generic source functions.");
            AssertType(Accept("fn apply(action, value) { action(value) }; let action = if exists ./condition { $std::string::to_lowercase } else { $std::string::to_uppercase }; apply(action, \"Text\")"), TypeRef.String);
        });
        Check("callback alternatives across item types", () =>
        {
            var module = Accept("let action = if exists ./condition { fn(value) { value / child } } else { fn(value) { value / other } }; let texts = $std::ranges::transform([\"one\"], action); $std::ranges::transform([~/one], action)");
            AssertType(module, new(TypeKind.Range, TypeRef.Path));
            var mappings = Dispatches(module, NativeOp.AdaptFunction).ToArray();
            Require(mappings.Length == 2 && mappings.All(mapping => mapping.Count == 2), "Callback branch alternatives need two complete specialization maps.");
            Require(mappings.SelectMany(mapping => mapping.Values).Any(target => Parameters(module, module.Functions[target]).Single() == TypeRef.String), "String callback specialization missing.");
            Require(mappings.SelectMany(mapping => mapping.Values).Any(target => Parameters(module, module.Functions[target]).Single() == TypeRef.Path), "Path callback specialization missing.");
            Reject("let predicate = if exists ./condition { fn(value) { true } } else { fn(value) { value } }; $std::ranges::filter([~/one], predicate)");
        });
        Check("native Function aliases survive joins", () =>
        {
            var same = Accept("let lower = $std::string::to_lowercase; let action = if exists ./condition { lower } else { lower }; action(\"TEXT\")");
            AssertType(same, TypeRef.String);
            var different = Accept("let action = if exists ./condition { $std::string::to_lowercase } else { $std::string::to_uppercase }; action(\"Text\")");
            AssertType(different, TypeRef.String);
            Require(HasNative(different, "string.to_lowercase") && HasNative(different, "string.to_uppercase"), "Native branch identities missing from the binary value table.");
            Require(different.Functions.SelectMany(function => function.Instructions).All(instruction => instruction.Op != NativeOp.ProcessCall), "A native function alias became a callable Path.");
            AssertType(Accept("let selected = if exists ./condition { object { action: $std::string::to_lowercase, } } else { object { action: $std::string::to_uppercase, } }; selected::action(\"Text\")"), TypeRef.String);
            AssertType(Accept("let action = match exists ./condition { true => { $std::string::to_lowercase }, false => { $std::string::to_uppercase } }; action(\"Text\")"), TypeRef.String);
            AssertType(Accept("let action = if exists ./condition { $std::string::to_lowercase } else { $std::string::to_uppercase }; $std::ranges::transform([\"Text\"], action)"), new(TypeKind.Range, TypeRef.String));
            var mixed = Accept("let action = if exists ./condition { fn(value) { value } } else { $std::string::to_uppercase }; action(\"Text\")");
            AssertType(mixed, TypeRef.String);
            var call = mixed.Functions.SelectMany(function => function.Instructions).Single(instruction => instruction.Op == NativeOp.Call);
            var sources = FunctionDispatch.Targets(call.Symbol!).Values.ToArray();
            Require(sources.Length == 1 && Parameters(mixed, mixed.Functions[sources[0]]).SequenceEqual([TypeRef.String]),
                "Mixed source/native calls must specialize the sole source closure for String.");
            Require(HasNative(mixed, "string.to_uppercase"),
                "Mixed source/native calls must retain the native alternative's identity.");
            Reject("let action = fn(value) { value }; action == action");
            Reject("let action = if exists ./condition { fn(left, right) { left } } else { $std::string::to_uppercase }; action(\"text\")");
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
            var instances = module.Functions.Where(function => !function.IsTemplate && function.Name == "convert").ToArray();
            Require(instances.Length == 2, "Type-directed conversion must specialize Bool and Path arguments separately.");
            var boolean = instances.Single(function => Parameters(module, function).Single() == TypeRef.Bool);
            var path = instances.Single(function => Parameters(module, function).Single() == TypeRef.Path);
            Require(Result(module, boolean) == TypeRef.Bool && boolean.Instructions.All(instruction => instruction.Op != NativeOp.Transfer), "The Bool specialization must not lower the invalid Path-transfer branch.");
            Require(Result(module, path) == TypeRef.Bool && path.Instructions.Any(instruction => instruction.Op is NativeOp.Transfer), "The Path specialization must lower its selected transfer.");
        });
        Check("generic decltype match dispatch", () =>
        {
            var module = Accept("fn viaMatch(value) { match decltype(value) { Path => { value -> ./out }, Bool => { not value }, _ => { false } } }; viaMatch(true); viaMatch(./input)");
            AssertType(module, TypeRef.Bool);
            var instances = module.Functions.Where(function => !function.IsTemplate && function.Name == "viaMatch").ToArray();
            Require(instances.Length == 2, "Type match must specialize both admitted argument types.");
            var boolean = instances.Single(function => Parameters(module, function).Single() == TypeRef.Bool);
            var path = instances.Single(function => Parameters(module, function).Single() == TypeRef.Path);
            Require(boolean.Instructions.Any(instruction => IsNot(instruction)) && boolean.Instructions.All(instruction => instruction.Op != NativeOp.Transfer), "The Bool type-match specialization must lower only boolean negation.");
            Require(path.Instructions.Any(instruction => instruction.Op is NativeOp.Transfer) && path.Instructions.All(instruction => !IsNot(instruction)), "The Path type-match specialization must not validate or lower boolean negation.");
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
            var instances = module.Functions.Where(function => !function.IsTemplate && function.Name == "runtimeFlag").ToArray();
            Require(instances.Length == 1, "Both Bool values must reuse one type specialization.");
            var instructions = instances[0].Instructions;
            Require(instructions.Any(instruction => instruction.Op == NativeOp.Branch), "A Bool parameter was incorrectly folded using its first caller's value.");
            Require(instructions.Any(instruction => IsBoolean(module, instruction, true)) && instructions.Any(instruction => IsBoolean(module, instruction, false)), "A reused specialization must retain both runtime result branches.");
            var match = Accept("fn runtimeMatch(flag) { match flag { true => { ./truth }, false => { \"false\" } } }; runtimeMatch(true); runtimeMatch(false)");
            var matcher = match.Functions.Single(function => !function.IsTemplate && function.Name == "runtimeMatch");
            Require(Result(match, matcher) == TypeRef.Union(TypeRef.Path, TypeRef.String), "Bool pattern matching must retain both possible result types.");
            Require(matcher.Instructions.Any(instruction => instruction.Op == NativeOp.Branch), "A Bool match parameter was incorrectly folded using its first caller's value.");
        });
        Check("decltype calls and process operands remain unevaluated", () =>
        {
            var module = Accept("fn transfer(value) { value -> ./out }; let transferType = decltype(transfer(./input)); let processType = decltype(./bin/tool()); transferType is Bool and processType is Process");
            var main = module.Functions[module.EntryPoint].Instructions;
            Require(main.All(instruction => instruction.Op is not (NativeOp.Transfer or NativeOp.ProcessCall or NativeOp.Call)), "An unevaluated operand emitted a runtime call or effect in main.");
            Require(module.Constants.Any(value => value.Kind == NativeKind.Type && value.Type == TypeRef.Bool) && module.Constants.Any(value => value.Kind == NativeKind.Type && value.Type == TypeRef.Process), "Unevaluated effect operands lost their inferred types.");
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
            Require(module.Functions[0].Instructions.Any(instruction => IsRegex(instruction)), "A nullable Path regex must retain a runtime text-domain predicate.");
            Require(module.Functions[0].Instructions.Any(instruction => instruction.Op is NativeOp.Transfer), "The matching nullable Path arm must lower its narrowed transfer.");
        });
        Check("mixed Bool and Path regex arm narrows to Path", () =>
        {
            var module = Accept("let value = if exists ./condition { ./input } else { false }; match value { <.+> => { value -> ./out }, _ => { false } }");
            AssertType(module, TypeRef.Bool);
            Require(module.Functions[0].Instructions.Any(instruction => IsRegex(instruction)), "A Bool|Path match needs a partial text-domain predicate.");
            Require(module.Functions[0].Instructions.Any(instruction => instruction.Op is NativeOp.Transfer), "A regex match arm must narrow Bool|Path before transfer typing.");
        });
        Check("Bool-only regex arm is statically impossible", () =>
        {
            var module = Accept("fn inspect(value: Bool) { match value { <.+> => { value -> ./out }, _ => { not value } } }; inspect(true); inspect(false)");
            AssertType(module, TypeRef.Bool);
            var function = module.Functions.Single(function => !function.IsTemplate && function.Name == "inspect");
            Require(function.Instructions.All(instruction => !IsRegex(instruction) && instruction.Op != NativeOp.Transfer), "An impossible Bool-only regex arm must be omitted before invalid transfer typing.");
            Require(function.Instructions.Any(instruction => IsNot(instruction)), "The possible fallback arm must remain executable.");
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
    private static IEnumerable<Dictionary<int, int>> Dispatches(NativeProgram module, NativeOp opcode)
    {
        foreach (var instruction in module.Functions.SelectMany(function => function.Instructions).Where(instruction => instruction.Op == opcode && !string.IsNullOrEmpty(instruction.Symbol)))
        {
            Dictionary<int, int> mapping = [];
            foreach (string entry in instruction.Symbol!.Split(';', StringSplitOptions.RemoveEmptyEntries))
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
    private static NativeProgram Accept(string source) => Frontend.Compile(source, "semantic-check.msync", optimization: 0);
    private static void Reject(string source)
    {
        try { Accept(source); }
        catch (CompileException) { return; }
        throw new InvalidOperationException("Accepted invalid program: " + source);
    }
    private static void AssertType(NativeProgram module, TypeRef expected) => Require(Result(module, module.Functions[module.EntryPoint]) == expected, $"Expected {expected}, inferred {Result(module, module.Functions[module.EntryPoint])}.");
    private static TypeRef RuntimeType(NativeProgram program, int type)
    {
        NativeCodeType descriptor = program.Types[type];
        return new(descriptor.Kind, descriptor.Element < 0 ? null : RuntimeType(program, descriptor.Element));
    }

    private static TypeRef Result(NativeProgram program, NativeFunctionCode function) =>
        RuntimeType(program, function.Signature.Result);

    private static IEnumerable<TypeRef> Parameters(NativeProgram program, NativeFunctionCode function) =>
        function.Signature.Parameters.Select(type => RuntimeType(program, type));

    private static bool HasNative(NativeProgram program, string name) => program.Functions
        .SelectMany(function => function.Instructions)
        .Any(instruction => instruction.Op == NativeOp.MakeFunction && instruction.Symbol == name);

    private static bool IsNot(NativeInstruction instruction) =>
        instruction.Op == NativeOp.Unary && instruction.Immediate == (int)NativeUnary.Not;

    private static bool IsRegex(NativeInstruction instruction) =>
        instruction.Op == NativeOp.Binary && instruction.Immediate == (int)NativeBinary.RegexMatch;

    private static bool IsBoolean(NativeProgram program, NativeInstruction instruction, bool value) =>
        instruction.Op == NativeOp.Constant && program.Constants[instruction.A] is { Kind: NativeKind.Bool } constant
            && constant.Boolean == value;

    private static bool SameInstructions(IReadOnlyList<NativeInstruction> left, IReadOnlyList<NativeInstruction> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First with { Inputs = null } == pair.Second with { Inputs = null }
            && (pair.First.Inputs ?? []).SequenceEqual(pair.Second.Inputs ?? []));

    private static int OriginSlot(IReadOnlyList<NativeInstruction> instructions, int register)
    {
        HashSet<int> seen = [];
        while (seen.Add(register))
        {
            NativeInstruction? binding = instructions.FirstOrDefault(item => item.Op == NativeOp.BindLocation && item.B == register);
            if (binding is not null) return binding.A;
            NativeInstruction? definition = instructions.LastOrDefault(item => item.Result == register);
            if (definition?.Op == NativeOp.FrameAddress) return definition.A;
            if (definition?.Op is not (NativeOp.Copy or NativeOp.Move or NativeOp.Narrow)) break;
            register = definition.A;
        }
        return -1;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
