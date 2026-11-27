using Msync.Optimization;
using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

MethodInfo pass = typeof(Optimizer).Assembly.GetType("Msync.Optimization.AggregateArgumentSpecialization")!
    .GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic)!;
Module Apply(Module module, int level = 2) => (Module)pass.Invoke(null, [module, level])!;
int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

Module Source(string source) => Compiler.Compile(source, "aggregate-argument-test", 0);
Module RoundTrip(Module module)
{
    using MemoryStream bytes = new(); Artifact.Write(module, bytes); bytes.Position = 0;
    return Artifact.Read(bytes);
}

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "aggregate-arguments-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    byte[] payload = [0, 255, 13, 10, 0xe4, 0xb8, 0xad];
    await File.WriteAllBytesAsync(Path.Combine(root, "source-input"), payload);
    async Task<string> Run(Module module, string directory)
    {
        using StringWriter errors = new();
        try
        {
            Value result = await Interpreter.ExecuteAsync(module with { OptimizationLevel = 0 }, new(WorkingDirectory: directory, Error: errors));
            return result.Type + ":" + result + "|" + errors;
        }
        catch (MsyncException error) { return "error:" + error.Message + ":" + error.Span; }
    }

    async Task<Module> Compare(string name, string source, bool split)
    {
        return await CompareModule(name, Source(source), split);
    }

    async Task<Module> CompareModule(string name, Module before, bool split)
    {
        Module after = Apply(before);
        Check(after.Functions.Any(function => function.Name.Contains("$sra", StringComparison.Ordinal)) == split, name + ": specialization applicability");
        string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
        string expected = await Run(before, directory);
        var files = Directory.EnumerateFiles(directory).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
        foreach (Module candidate in new[] { after, RoundTrip(after), RoundTrip(Binding.Bind(after with { OptimizationLevel = 0 }, new(directory, directory))) })
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Check(await Run(candidate, directory) == expected, name + ": selected value or error changed");
            var actual = Directory.EnumerateFiles(directory).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
            Check(files.Count == actual.Count, name + ": output count changed");
            foreach (var (file, bytes) in files) Check(actual.TryGetValue(file, out var found) && found.AsSpan().SequenceEqual(bytes), name + ": output bytes changed");
        }
        return after;
    }

    Module copy = await Compare("copy", "fn copy(doc, destination: Path) { doc::file -> destination; }; let doc = object { file: \"payload\", unused: 7 }; copy(doc, ./output); exists ./output", true);
    FunctionCode specialized = copy.Functions.First(function => function.Name.Contains("$sra", StringComparison.Ordinal));
    Check(!specialized.Instructions.Any(instruction => instruction.Op == OpCode.LoadField), "Aggregate member lookup survived in clone");
    Check(specialized.ParameterTypes![0] == TypeRef.String
        && specialized.CodeSignature is { } typed && copy.CodeTypes[typed.Parameters[0]].Kind == TypeKind.String,
        "Clone input did not become the field's portable String payload");
    Check(specialized.Instructions.Any(instruction => instruction.Op == OpCode.Return), "Invocation owner return was removed");
    await Compare("path-copy", "fn copy(doc, destination: Path) { doc::file -> destination; }; let doc = object { file: ../source-input }; copy(doc, ./output);", true);
    Check(File.ReadAllBytes(Path.Combine(root, "path-copy", "output")).AsSpan().SequenceEqual(payload), "Path field lost actual file bytes");
    await Compare("argument-effects", "fn write(doc, ignored) { \"started\" -> ./log; doc::value }; let doc = object { value: \"before\" }; fn change() { doc::value = \"after\"; true }; write(doc, change()) -> ./result;", true);
    await Compare("argument-rebind", "fn write(doc, ignored) { \"started\" -> ./log; doc::value }; let doc = object { value: \"before\" }; fn change() { doc = object { value: \"replacement\" }; true }; write(doc, change()) -> ./result;", true);
    await Compare("branch-true", "fn copy(doc, destination) { if doc::enabled { doc::file -> destination; } else { \"disabled\" -> destination; }; }; let doc = object { file: \"chosen\", enabled: true }; copy(doc, ./output);", true);
    await Compare("branch-false", "fn copy(doc, destination) { if doc::enabled { doc::file -> destination; } else { \"disabled\" -> destination; }; }; let doc = object { enabled: false, file: \"unchosen\" }; copy(doc, ./output);", true);
    await Compare("mutating-callee", "fn mutate(doc) { doc::value = \"changed\"; doc::value }; let doc = object { value: \"before\" }; mutate(doc) -> ./result;", false);
    await Compare("escaped-return", "fn identity(doc) { doc }; let doc = object { value: \"before\" }; identity(doc)::value -> ./result;", false);
    await Compare("captured-parameter", "fn invoke(doc) { let get = fn { doc::value }; get() }; let doc = object { value: \"captured\" }; invoke(doc) -> ./result;", false);
    await Compare("escaped-callback", "fn write(doc) { doc::file -> ./output; }; let original = write; let doc = object { file: \"kept\" }; write(doc); original(object { file: \"callback\" });", true);
    await Compare("eager-range", "fn write(doc) { doc::files -> ./; }; let doc = object { files: [\"a\", \"b\"] }; write(doc);", true);
    await Compare("eager-path-range", "fn write(doc) { doc::files -> ./; }; let doc = object { files: [../source-input] }; write(doc);", true);
    Check(File.ReadAllBytes(Path.Combine(root, "eager-path-range", "source-input")).AsSpan().SequenceEqual(payload), "Eager Path range field lost bytes");
    await Compare("deferred-mutation", "fn read(doc, stream) { let before = doc::value; for item in stream { }; doc::value }; let doc = object { value: \"before\" }; let stream = $std::ranges::transform([1], fn(item) { doc::value = \"after\"; item }); read(doc, stream) -> ./result;", false);
    await Compare("aliased-write", "fn read(doc, alias) { alias::value = \"after\"; doc::value }; let doc = object { value: \"before\" }; read(doc, doc) -> ./result;", false);

    const int Any = 0, Scope = 1, String = 2, PathType = 3;
    Module missing = new([new ScopeValue(new ScopeLayout(["other"], 0), [new StringValue("unused")]),
        new StringValue("started"), new PathValue("log")],
    [
        new FunctionCode("main", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Call, 1, 2),
            new(OpCode.Return)], TypeRef.Any) { CodeSignature = new([], Any, []) },
        new FunctionCode("read", 1, 1, [new(OpCode.Constant, 1), new(OpCode.Constant, 2), new(OpCode.Transfer),
            new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.LoadField), new(OpCode.Return)], TypeRef.Any,
            ParameterTypes: [TypeRef.Scope]) { CodeSignature = new([Scope], Any, []) }
    ], OptimizationLevel: 0)
    {
        CodeTypes = [new(TypeKind.Any), new(TypeKind.Scope), new(TypeKind.String), new(TypeKind.Path)],
        ScopeLayouts = [new([new("other", String)])],
        FieldProjections = [new("missing", [])]
    };
    await CompareModule("missing-after-write", missing, false);
    Module native = missing with
    {
        Constants = [new ScopeValue(new ScopeLayout(["missing"], 0), [new PathValue("file")]),
            new FunctionValue(-1, NativeName: "ranges.single")],
        ScopeLayouts = [new([new("missing", PathType)])],
        Functions =
        [
            missing.Functions[0] with { Instructions = [new(OpCode.Constant, 1), new(OpCode.Constant, 0), new(OpCode.Call, 1, 2),
                new(OpCode.NativeCall, 1, Text: "ranges.front"), new(OpCode.Return)] },
            missing.Functions[1] with { Instructions = [new(OpCode.LoadLocal, 0), new(OpCode.LoadField), new(OpCode.Return)] }
        ]
    };
    await CompareModule("native-identity", native, false);
    Check((await Run(native, root)).StartsWith("error:A direct specialization requires a source Function", StringComparison.Ordinal),
        "A known native callable cannot use the unrelated direct source target");
    var nativeCode = native.Functions[0].Instructions.ToArray();
    nativeCode[2] = nativeCode[2] with { B = 0 };
    var ordinaryNative = native with { Functions = [native.Functions[0] with { Instructions = nativeCode }, native.Functions[1]] };
    await CompareModule("ordinary-native", ordinaryNative, false);
    Check(await Interpreter.ExecuteAsync(ordinaryNative) is ScopeValue nativeValue && nativeValue["missing"] is PathValue { Raw: "file" },
        "A native call kept its actual Object argument rather than the projected source callee's field");

    Module control = Source("fn copy(doc) { doc::file -> ./out; }; let doc = object { file: \"plain\" }; copy(doc);");
    Check(ReferenceEquals(Apply(control, 1), control), "O1 enables aggregate specialization");
    Module session = control with { PreserveLocals = true };
    Check(ReferenceEquals(Apply(session), session), "Persistent session entry ABI changed");
    Module once = Apply(control), twice = Apply(once);
    Check(twice.Functions.Count == once.Functions.Count, "Repeated optimization keeps cloning the same call");
    Module product = Compiler.Compile("fn copy(doc, destination: Path) { doc::file -> destination; }; copy(object { file: \"product\" }, ./output);", "aggregate-product", 2);
    Check(product.Functions.Any(function => function.Name.Contains("$sra", StringComparison.Ordinal)), "Public O2 compiler did not integrate aggregate specialization");
    Check(!product.Functions.SelectMany(function => function.Instructions).Any(instruction => instruction.Op == OpCode.NewScope), "Public O2 compiler retained private aggregate construction");
    string productRoot = Path.Combine(root, "product"); Directory.CreateDirectory(productRoot);
    await Run(product, productRoot);
    Check(await File.ReadAllTextAsync(Path.Combine(productRoot, "output")) == "product", "Public O2 compiler changed effectful helper bytes");
    Module escaped = Compiler.Compile("fn copy(doc) { doc::file -> ./output; }; copy(object { file: \"escaped\" }); object { callback: copy }", "aggregate-escaping", 2);
    string escapedRoot = Path.Combine(root, "escaped-product"); Directory.CreateDirectory(escapedRoot);
    foreach (var image in new[] { escaped, RoundTrip(escaped), RoundTrip(Binding.Bind(escaped, new(escapedRoot, escapedRoot))) })
    {
        Value returned = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: escapedRoot));
        Check(returned is ScopeValue scope && scope["callback"] is FunctionValue { NativeName: null } callback
            && image.Functions[callback.Function] is { Name: "copy", ParameterCount: 1, CodeSignature.IsTemplate: true },
            "The escaping closure lost its original generic ABI or exposed the projected clone");
        Check(await File.ReadAllTextAsync(Path.Combine(escapedRoot, "output")) == "escaped", "Public O2 escaped callback output changed");
        File.Delete(Path.Combine(escapedRoot, "output"));
    }
    Console.WriteLine($"PASS: {assertions} aggregate argument, owner, effect, alias and binary contracts");
}
finally { Directory.Delete(root, recursive: true); }
