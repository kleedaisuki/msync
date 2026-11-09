using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

if (args is ["--check", var sourcePath])
{
    foreach (int level in new[] { 0, 2, 3 })
        Compiler.Compile(await File.ReadAllTextAsync(sourcePath), sourcePath, level);
    Console.WriteLine("PASS command policy source at O0/O2/O3");
    return;
}

int checks = 0;
const string resultRecords = """
    fn choose(flag: Bool) -> $std::concepts::Result {
        let payload = scope { code: 7, output: "failure" };
        if flag {
            $std::data::Result::ok(payload)
        } else {
            $std::data::Result::err(payload)
        }
    };
    let result = choose(false);
    let first = $std::data::Result::fold(result,
        fn(value) { value }, fn(error) { error });
    let second = $std::data::Result::get_or(choose(true), first);
    scope { first: first, second: second }
    """;
const string mutableParameter = """
    fn box(value?) {
        scope {
            read: fn { value },
            clear: fn { value = null; },
        }
    };
    let state = box(scope { code: 7 });
    let before? = state::read();
    state::clear();
    let after? = state::read();
    scope { before?: before, after?: after }
    """;

foreach (int level in new[] { 0, 2, 3 })
{
    foreach (Module image in Images(Compiler.Compile(resultRecords, "result-record-contracts.msync", level)))
    {
        var value = (ScopeValue)await Interpreter.ExecuteAsync(image);
        var first = (ScopeValue)value["first"];
        var second = (ScopeValue)value["second"];
        Require(first["code"] is NumberValue { Data: 7 } && first["output"] is StringValue { Data: "failure" }, "Error callback lost its record payload");
        Require(second["code"] is NumberValue { Data: 7 } && second["output"] is StringValue { Data: "failure" }, "Later success callback changed its record contract");
    }
    foreach (Module image in Images(Compiler.Compile(mutableParameter, "mutable-constructor-parameter.msync", level)))
    {
        var value = (ScopeValue)await Interpreter.ExecuteAsync(image);
        Require(value["before"] is ScopeValue record && record["code"] is NumberValue { Data: 7 }, "Read lost the original record");
        Require(value["after"] is NullValue, "An actual captured-parameter setter was incorrectly treated as read-only");
    }
    Reject("""
        let operation = fn { scope { code: 7 } };
        operation();
        operation = fn { scope { output: "different" } };
        operation()
        """, level, "Scope { output: String }", "Scope { code: Number }");
    Reject("""
        let operation = fn { scope { code: 7 } };
        operation();
        operation = fn { if exists ./callable-contract-absent { scope { code: 7 } } };
        operation()
        """, level, "Scope { code: Number }?", "requires Scope { code: Number }");
    Reject("""
        fn box(value?) {
            scope {
                read: fn { value },
                replace: fn { value = scope { other: 1 }; },
            }
        };
        let state = box(scope { code: 7 });
        state::read();
        state::replace();
        state::read()
        """, level, "Scope { other: Number }", "Scope { code: Number }");
}

Console.WriteLine($"PASS {checks} callable result contracts");

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

void Reject(string source, int level, string actual, string expected)
{
    try { Compiler.Compile(source, "incompatible-callable.msync", level); }
    catch (CompileException error)
    {
        Require(error.Diagnostic.Code == "E0213", "Wrong incompatible-callable diagnostic: " + error.Diagnostic.Message);
        Require(error.Diagnostic.Message.Contains(actual, StringComparison.Ordinal), "Diagnostic hid the actual callable result: " + error.Diagnostic.Message);
        Require(error.Diagnostic.Message.Contains(expected, StringComparison.Ordinal), "Diagnostic hid the required callable result: " + error.Diagnostic.Message);
        return;
    }
    throw new InvalidOperationException("Accepted an incompatible callable result");
}

static IEnumerable<Module> Images(Module module)
{
    yield return module;
    using MemoryStream relocatable = new();
    Artifact.Write(module, relocatable);
    relocatable.Position = 0;
    yield return Artifact.Read(relocatable);
    Module executable = Binding.Bind(module, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
    using MemoryStream bound = new();
    Artifact.Write(executable, bound);
    bound.Position = 0;
    yield return Artifact.Read(bound);
}
