using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

var programs = new (string Name, string Source, string Expected)[]
{
    ("late-scalar", """
        fn values() {
            let cell? = null;
            let values = [fn { cell }];
            cell = 42;
            values
        };
        let get = $std::ranges::first(values());
        let value? = get();
        if value is null { "missing" } else { value as String }
        """, "42"),
    ("late-record", """
        fn values() {
            let cell? = null;
            let values = [fn { cell }];
            cell = scope { value: "record" };
            values
        };
        let get = $std::ranges::first(values());
        let value? = get();
        if value is null { "missing" } else { value::value }
        """, "record"),
    ("alternative-closures", """
        fn values() {
            let cell? = null;
            let values = [fn { cell + 1 }, fn { cell + 2 }];
            cell = 42;
            values
        };
        let values = values();
        let first = $std::ranges::first(values);
        let second = $std::ranges::last(values);
        let left? = first();
        let right? = second();
        if left is null or right is null { "missing" }
        else { ((left + right) as String) }
        """, "87"),
    ("independent-cells", """
        fn create(initial: Number) {
            let cell? = null;
            let getters = [fn { cell }];
            cell = initial;
            scope {
                getters: getters,
                set: fn(value: Number) { cell = value; },
            }
        };
        let first = create(1);
        let second = create(2);
        first::set(9);
        let first_get = $std::ranges::first(first::getters);
        let second_get = $std::ranges::first(second::getters);
        let left? = first_get();
        let right? = second_get();
        if left is null or right is null { "missing" }
        else { ((left + right) as String) }
        """, "11"),
    ("borrowed-record", """
        fn pass(values: Range) { values };
        let record = scope { flag: false };
        let values = pass([record]);
        let selected = $std::ranges::first(values);
        selected::flag = true;
        if record::flag { "shared" } else { "lost" }
        """, "shared"),
    ("borrowed-callback-result", """
        fn pass(callback: Function) { [callback()] };
        let record = scope { flag: false };
        let getter = fn { record };
        let values = pass(getter);
        let selected = $std::ranges::first(values);
        selected::flag = true;
        if record::flag { "shared" } else { "lost" }
        """, "shared"),
};

int checks = 0;
foreach (int level in new[] { 0, 2, 3 })
{
    foreach (var program in programs)
    {
        Console.WriteLine($"CASE {program.Name} O{level}");
        Module module = Compiler.Compile(program.Source, program.Name + ".msync", level);
        foreach (Module image in Images(module))
        {
            Value value = await Interpreter.ExecuteAsync(image);
            Require(value is StringValue text && text.Data == program.Expected,
                $"{program.Name} O{level}: expected {program.Expected}, received {value}");
        }
    }
    const string unsafeUnion = """
        let current: Range | String = [1];
        fn identity(value: Range | String) { value };
        let values = identity(current);
        current = "changed";
        $std::ranges::size(values)
        """;
    try { Compiler.Compile(unsafeUnion, "unsafe-live-union.msync", level); }
    catch (CompileException error)
    {
        Require(error.Diagnostic.Code == "E0203", "Wrong diagnostic for a non-Range live capture: " + error.Diagnostic.Message);
        continue;
    }
    throw new InvalidOperationException("A live union capture was frozen as a Range");
}
Console.WriteLine($"PASS {checks} late captured Range contracts");

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

static IEnumerable<Module> Images(Module module)
{
    yield return module;
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    yield return Artifact.Read(bytes);
    Module bound = Binding.Bind(module, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
    using MemoryStream executable = new();
    Artifact.Write(bound, executable);
    executable.Position = 0;
    yield return Artifact.Read(executable);
}
