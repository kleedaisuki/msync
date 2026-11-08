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
