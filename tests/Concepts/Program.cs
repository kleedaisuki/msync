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

    const string standard = """
        scope Operations {
            Self: Number,
            equal: fn(left: Number, right: Number) -> Bool { left == right },
        };
        concept Labeled {
            $std::concepts::Equality,
            label: String,
        };
        fn checked(value: $std::concepts::Equality)
            -> $std::concepts::Equality { value };
        """;
    var qualified = Compiler.Compile(standard + "checked(Operations)::equal(4, 4)", "standard-concepts.msync", level);
    using MemoryStream standardBytes = new();
    Artifact.Write(qualified, standardBytes);
    standardBytes.Position = 0;
    var executable = Binding.Bind(qualified, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
    using MemoryStream executableBytes = new();
    Artifact.Write(executable, executableBytes);
    executableBytes.Position = 0;
    foreach (Module image in new[] { qualified, Artifact.Read(standardBytes), Artifact.Read(executableBytes) })
        Require(await Interpreter.ExecuteAsync(image) is BoolValue { Data: true }, "Qualified concept across source and binary stages");
    var metadataOnly = Compiler.Compile("concept Local { $std::concepts::Equality, };", "concept-metadata.msync", level);
    Require(!metadataOnly.Functions.SelectMany(function => function.Instructions).Any(instruction => instruction.Op == OpCode.MakeScope),
        "A concept annotation allocated a runtime standard module");
    var conceptRange = Compiler.Compile(standard + "fn count(values: Range<$std::concepts::Equality>) { $std::ranges::size(values) }; count([Operations])", "qualified-range.msync", level);
    Require(await Interpreter.ExecuteAsync(conceptRange) is NumberValue { Data: 1 }, "Qualified Range element concept");
    var conceptUnion = Compiler.Compile(standard + "fn either(value: $std::concepts::Equality | String) { true }; either(Operations)", "qualified-union.msync", level);
    Require(await Interpreter.ExecuteAsync(conceptUnion) is BoolValue { Data: true }, "Qualified concept union");
    var lexical = Compiler.Compile(standard + "let std = scope { other: true }; checked(Operations)::equal(2, 2)", "qualified-lexical.msync", level);
    Require(await Interpreter.ExecuteAsync(lexical) is BoolValue { Data: true }, "Qualified function return resolved at definition, not caller");
    var fixedContract = Compiler.Compile(standard + "let target: $std::concepts::Equality = Operations; let std = scope {}; target = Operations; true", "qualified-storage.msync", level);
    Require(await Interpreter.ExecuteAsync(fixedContract) is BoolValue { Data: true }, "Stored concept contract retained lexical namespace");
    Reject(standard + "checked(scope { Self: Number, equal: fn(a: Number,b: Number) { 1 } })", "return", level);
    Reject("let std = scope {}; let value: $std::concepts::Equality = scope {};", "Unknown type", level);
    const string nullableResult = """
        concept Optional { type Item; value?: Item, };
        fn some(value?) { scope { Item: decltype(value), value?: value } };
        fn absent() -> Optional? { null };
        fn returned_absent() -> Optional? { return null; };
        fn present(value: Number) -> Optional? { some(value) };
        fn is_absent(value?: Optional) { value is null };
        fn read(value?: Optional) {
            if value is null { 0 } else { value::value }
        };
        let stored?: Optional = null;
        let initially_absent = is_absent(stored);
        stored = some(4);
        let stored_number = read(stored) == 4;
        stored = some("Klee");
        is_absent(absent()) and is_absent(returned_absent())
            and read(present(4)) == 4 and initially_absent
            and stored_number and read(stored) == "Klee"
        """;
    var preciseReturn = Compiler.Compile(nullableResult, "nullable-concept-results.msync", level);
    Require(await Interpreter.ExecuteAsync(preciseReturn) is BoolValue { Data: true }, "Nullable concept returns retained actual Null or carrier shape");
    foreach (string returned in new[] { "\"./converted\"", "return \"./converted\";" })
    {
        var converted = Compiler.Compile($"fn path() -> Path {{ {returned} }}; path()", "concept-return-conversion.msync", level);
        Require(await Interpreter.ExecuteAsync(converted) is PathValue, "Declared Path return still converted its String representation");
    }
    foreach (string returned in new[] { "./converted", "return ./converted;" })
    {
        var converted = Compiler.Compile($"fn text() -> String {{ {returned} }}; text()", "concept-return-conversion.msync", level);
        Require(await Interpreter.ExecuteAsync(converted) is StringValue, "Declared String return still converted its Path representation");
    }
    Reject("fn invalid() -> Number { ./path }; invalid()", "return type Number", level);
    Reject("fn invalid() -> Number { return ./path; }; invalid()", "return type Number", level);
    Reject("concept C { item: Number }; let value?: C = null; value = 3;", "Expected Scope?", level);
    const string inline = """
        concept Equal {
            equal: fn(left: typename Item, right: Item) -> Bool,
        };
        concept Sequence { items: Range<typename Element>, };
        concept Factory { make: fn() -> typename Item, };
        fn checked(value: Equal) { value::equal(2, 2) };
        fn sequence(value: Sequence) { $std::ranges::size(value::items) };
        fn made(value: Factory) { value::make() };
        checked(scope {
            equal: fn(left: Number, right: Number) { left == right },
        }) and sequence(scope { items: [1, 2] }) == 2
            and made(scope { make: fn { 3 } }) == 3
        """;
    var inlineConcept = Compiler.Compile(inline, "inline-associated-types.msync", level);
    using MemoryStream inlineBytes = new();
    Artifact.Write(inlineConcept, inlineBytes);
    inlineBytes.Position = 0;
    foreach (Module image in new[] { inlineConcept, Artifact.Read(inlineBytes), Binding.Bind(inlineConcept, new(Environment.CurrentDirectory, Environment.CurrentDirectory)) })
        Require(await Interpreter.ExecuteAsync(image) is BoolValue { Data: true }, "Inline associated types compose across parameter, Range and operation result");
    Reject("concept C { item: typename Item, other: typename Item, };", "declared twice", level);
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
