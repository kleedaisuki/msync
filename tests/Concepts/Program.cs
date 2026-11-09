using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

const string contracts = """
    concept Semigroup {
        type Self;
        combine: fn(left: Self, right: Self) -> Self,
    };
    concept Monoid {
        Semigroup,
        type Self;
        identity: Self,
    };
    scope Addition {
        identity: 0,
        combine: fn(left: Number, right: Number) -> Number { left + right },
    };
    fn apply(algebra: Monoid) -> Monoid { algebra };
    """;
int checks = 0;
foreach (int level in new[] { 0, 2, 3 })
{
    Module module = Compiler.Compile(contracts + "apply(Addition)::combine(2, 3)", "concepts.msync", level);
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    foreach (Module image in new[] { module, Artifact.Read(bytes), Binding.Bind(module, new(Environment.CurrentDirectory, Environment.CurrentDirectory)) })
    {
        Require(await Interpreter.ExecuteAsync(image) is NumberValue { Data: 5 }, "Composed algebra did not execute");
    }
    Reject(contracts + "apply(scope { identity: 0 })", "combine", level);
    Reject(contracts + "apply(scope { identity: 0, combine: fn(a: Number,b: Number) { true } })", "return", level);
    Reject(contracts + "let constrained: Monoid = Addition; constrained = scope { identity: 0 };", "combine", level);
    Reject(contracts + "let constrained: Monoid = Addition; Addition::combine = fn(a:Number,b:Number) { true };", "return", level);
    Reject("""concept C { type Self; identity: Self, combine: fn(a: Self,b: Self) -> Self }; fn use(x:C){x}; use(scope { Self: Scope, identity: scope { nested: scope { x: "" } }, combine: fn(a:Scope,b:Scope) { scope { nested: scope { wrong: true } } } });""", "associated field", level);
    var nullable = Compiler.Compile("concept C { item: Number }; fn use(value?: C) -> C? { value }; use(null)", "nullable-concept.msync", level);
    Require(await Interpreter.ExecuteAsync(nullable) is NullValue, "Nullable concept parameter/return");
    var generic = Compiler.Compile("concept Semigroup { type Self; combine: fn(a: Self,b: Self) -> Self }; concept Monoid { Semigroup, identity: Self }; fn use(value:Monoid) { value::combine(value::identity, 2) }; use(scope { identity: 0, combine: fn(a,b) { a+b } })", "associated-composition.msync", level);
    Require(await Interpreter.ExecuteAsync(generic) is NumberValue { Data: 2 }, "Inherited associated type and generic callable");
    Reject("concept C { type Self; identity: Self, combine: fn(a: Self,b: Self) -> Self }; fn use(x:C){x}; use(scope { Self: String, identity: 0, combine: fn(a,b) { a+b } });", "identity", level);
    var optionalBinding = Compiler.Compile("concept C { item: Number }; let value?: C = scope {item: 3}; value = null; value", "nullable-binding.msync", level);
    Require(await Interpreter.ExecuteAsync(optionalBinding) is NullValue, "Nullable concept binding mutation");
    var either = Compiler.Compile("concept Text { item: String }; concept Numeric { item: Number }; fn use(value: Text | Numeric) { value::item }; use(scope {item: 3})", "concept-union.msync", level);
    Require(await Interpreter.ExecuteAsync(either) is NumberValue { Data: 3 }, "Alternative structural concepts");
    var ranged = Compiler.Compile("concept C { item: Number }; fn use(values: Range<C>) { $std::ranges::size(values) }; use([scope {item: 1}, scope {item: 2}])", "range-concept.msync", level);
    Require(await Interpreter.ExecuteAsync(ranged) is NumberValue { Data: 2 }, "Range element structural concepts");
    Reject("concept C { item: Path }; { concept Local { value: Bool }; }; fn use(x: Local) { x }; use(scope {value:true});", "Unknown type", level);
}
CompilerSession session = new();
session.Compile("concept Named { item: Number, };");
session.AcceptSubmission();
session.Compile("fn use(value: Named) { value::item }; use(scope { item: 4 })");
Require(true, "Session concept declaration");
session.Compile("let checked: Named = scope { item: 3 };");
session.AcceptSubmission();
try
{
    session.Compile("checked = scope { other: true };");
    throw new InvalidOperationException("Session lost concept storage constraint");
}
catch (CompileException error) { Require(error.Diagnostic.Code == "E0214", "Session concept diagnostic"); }
session.Compile("checked = scope { item: 4 }; checked::item");
Require(true, "Successful submission after concept failure");
Console.WriteLine($"PASS {checks} concept contracts");

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
void Reject(string source, string fragment, int level)
{
    try { Compiler.Compile(source, "concept-error.msync", level); }
    catch (CompileException error)
    {
        Require(error.Diagnostic.Message.Contains(fragment, StringComparison.Ordinal), error.Diagnostic.Message);
        return;
    }
    throw new InvalidOperationException("Expected concept rejection: " + source);
}
