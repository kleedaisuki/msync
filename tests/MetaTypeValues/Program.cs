using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

int checks = 0;
void Check(bool condition, string reason)
{
    checks++;
    if (!condition) throw new InvalidOperationException(reason);
}
Value Query(string operation, params TypeRef[] types) => MetaTypes.Evaluate(operation, types);
bool Predicate(string operation, params TypeRef[] types) => ((BoolValue)Query(operation, types)).Data;
TypeRef Transform(string operation, params TypeRef[] types) => ((TypeValue)Query(operation, types)).Data;
RuntimeContext context = new(new(WorkingDirectory: Environment.CurrentDirectory));
async Task<Value> Native(string operation, params TypeRef[] types) => await NativeRegistry.InvokeAsync("meta." + operation,
    types.Select(type => (Value)new TypeValue(type)).ToArray(), context,
    (_, _, _) => throw new InvalidOperationException("Type traits must not invoke callbacks"));

var categories = new (string Name, TypeRef Type)[]
{
    ("null", TypeRef.Null), ("bool", TypeRef.Bool), ("number", TypeRef.Number),
    ("string", TypeRef.String), ("path", TypeRef.Path), ("range", new TypeRef(TypeKind.Range)),
    ("function", TypeRef.Function), ("scope", TypeRef.Scope), ("process", TypeRef.Process),
    ("connection", TypeRef.Connection), ("type", TypeRef.Type), ("never", new TypeRef(TypeKind.Never))
};
foreach (var (name, type) in categories)
{
    Check(await Native("is_same", type, type) is BoolValue { Data: true }, "native Type equality " + name);
    foreach (var (otherName, otherType) in categories)
        Check(Predicate("is_" + name, otherType) == (name == otherName), "category " + name + "/" + otherName);
}
Check(await Native("is_convertible", TypeRef.Number, TypeRef.String) is BoolValue { Data: false }, "runtime conversion relation");
Check(await Native("range_of", TypeRef.String) is TypeValue { Data.Element: var nativeElement } && nativeElement == TypeRef.String,
    "runtime type transformation");
Check(!Predicate("is_number", TypeRef.Number.WithNull), "nullable Number is not the Number category");
Check(Predicate("is_scalar", new TypeRef(TypeKind.Path | TypeKind.String)), "a text union contains only scalars");
Check(!Predicate("is_scalar", TypeRef.Null), "Null is not a scalar");
Check(!Predicate("is_scalar", TypeRef.Number.WithNull), "a nullable scalar contains Null");
Check(!Predicate("is_scalar", new TypeRef(TypeKind.Never)), "Never is not a scalar");
Check(Predicate("is_union", TypeRef.Number.WithNull), "Null counts as a union alternative");
Check(!Predicate("is_union", new TypeRef(TypeKind.Range, TypeRef.Number.WithNull)), "a union inside Range is not an outer union");

TypeRef[] domain = categories.Select(entry => entry.Type).Concat(
[
    TypeRef.Number.WithNull, TypeRef.Path.WithNull, TypeRef.String.WithNull,
    new TypeRef(TypeKind.Path | TypeKind.String), new TypeRef(TypeKind.Range, TypeRef.String),
    new TypeRef(TypeKind.Range, TypeRef.Number), new TypeRef(TypeKind.Range, TypeRef.Path),
    new TypeRef(TypeKind.Range | TypeKind.Null, TypeRef.Number), TypeRef.Any
]).ToArray();
foreach (TypeRef first in domain)
{
    Check(Transform("add_null", first).Nullable, "add_null " + first);
    Check(!Transform("remove_null", first).Nullable, "remove_null " + first);
    Check(Transform("remove_null", Transform("add_null", first)) == first.WithoutNull, "nullable roundtrip " + first);
    Check(Transform("range_of", first) == new TypeRef(TypeKind.Range, first), "range_of " + first);
    Check(Predicate("is_nullable", first) == first.Nullable, "is_nullable " + first);
    foreach (TypeRef second in domain)
    {
        Check(Predicate("is_same", first, second) == (first == second), "is_same " + first + "/" + second);
        Check(Predicate("accepts", first, second) == first.Accepts(second), "accepts " + first + "/" + second);
        Check(Predicate("is_assignable", first, second) == Predicate("is_convertible", second, first), "assignment direction");
        TypeRef common = Transform("common_type", first, second);
        Check(common.Accepts(first) && common.Accepts(second), "common type retains both alternatives");
        Check(common == Transform("common_type", second, first), "common type is commutative");
    }
}
Check(Predicate("is_convertible", TypeRef.Path, TypeRef.String), "Path implicitly converts to String");
Check(Predicate("is_convertible", TypeRef.String, TypeRef.Path), "String implicitly converts to Path");
Check(!Predicate("is_convertible", TypeRef.Number, TypeRef.String), "Number requires explicit String conversion");
Check(!Predicate("is_convertible", TypeRef.Path.WithNull, TypeRef.String), "implicit conversion preserves Null admission");
Check(Predicate("is_convertible", TypeRef.Path.WithNull, TypeRef.String.WithNull), "nullable text conversion");
Check(!Predicate("is_convertible", new TypeRef(TypeKind.Range, TypeRef.Number), new TypeRef(TypeKind.Range, TypeRef.String)), "no implicit element conversion");
Check(!Predicate("is_convertible", new TypeRef(TypeKind.Range | TypeKind.Path, TypeRef.Number), new TypeRef(TypeKind.Range | TypeKind.String, TypeRef.String)), "text union cannot hide an incompatible Range element");
Check(Predicate("is_castable", TypeRef.String, TypeRef.Number), "String has a legal explicit Number conversion");
Check(!Predicate("is_castable", TypeRef.String, TypeRef.Process), "String cannot be cast to Process");
Check(Predicate("is_castable", new TypeRef(TypeKind.Range, TypeRef.Path), new TypeRef(TypeKind.Range, TypeRef.String)), "explicit Range element conversion");
Check(Query("element_type", TypeRef.String) is NullValue, "non-Range element_type");
Check(Transform("element_type", new TypeRef(TypeKind.Range)) == TypeRef.Any, "unspecified Range element is Any");
Check(Transform("element_type", new TypeRef(TypeKind.Range | TypeKind.Null, TypeRef.String)) == TypeRef.String, "nullable Range element");
Check(((NumberValue)Query("range_rank", new TypeRef(TypeKind.Range, new TypeRef(TypeKind.Range, TypeRef.Number)))).Data == 2, "nested Range rank");
Check(((NumberValue)Query("range_rank", TypeRef.String)).Data == 0, "non-Range rank");
var alternatives = (RangeValue)Query("alternatives", new TypeRef(TypeKind.Null | TypeKind.Path | TypeKind.Range, TypeRef.Number));
List<TypeRef> actualAlternatives = [];
await foreach (Value value in alternatives.Items) actualAlternatives.Add(((TypeValue)value).Data);
Check(actualAlternatives.SequenceEqual(new[] { TypeRef.Null, TypeRef.Path, new TypeRef(TypeKind.Range, TypeRef.Number) }), "alternatives retain Range schema and stable order");

const string source = """
    let meta = $std::meta;
    let element? = meta::element_type(Range<String>);
    let missing? = meta::element_type(Number);
    [
        meta::is_null(Null), meta::is_bool(Bool), meta::is_number(Number),
        meta::is_string(String), meta::is_path(Path), meta::is_range(Range),
        meta::is_function(Function), meta::is_scope(Scope),
        meta::is_process(Process), meta::is_connection(Connection),
        meta::is_type(Type), meta::is_never(meta::remove_null(Null)),
        meta::is_union(Number | Null), meta::is_scalar(Path | String),
        meta::is_nullable(Number | Null), meta::is_same(Path, Path),
        meta::accepts(Path | String, Path), meta::is_assignable(String, Path),
        meta::is_convertible(Path, String), meta::is_castable(String, Number),
        meta::is_same(meta::add_null(Number), Number | Null),
        meta::is_same(meta::remove_null(Number | Null), Number),
        meta::is_same(meta::common_type(Path, String), Path | String),
        meta::is_same(meta::range_of(Number), Range<Number>),
        if element is null { false } else { meta::is_same(element, String) },
        missing is null,
        meta::range_rank(Range<Range<Number>>) == 2,
        meta::type_name(Number) == "Number",
        $std::ranges::size(meta::alternatives(Any)) == 11
    ]
    """;
foreach (int level in new[] { 0, 2, 3 })
{
    Module module = Compiler.Compile(source, "meta-type-values.msync", level);
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes); bytes.Position = 0;
    Module relocatable = Artifact.Read(bytes);
    Module bound = Binding.Bind(relocatable, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
    using MemoryStream executableBytes = new();
    Artifact.Write(bound, executableBytes); executableBytes.Position = 0;
    Module executable = Artifact.Read(executableBytes);
    Check(executable.Stage == ArtifactStage.Executable, "serialized executable stage");
    foreach (Module image in new[] { module, relocatable, executable })
    {
        Value value = await Interpreter.ExecuteAsync(image);
        Check(value is RangeValue, "trait program returns its observations");
        int count = 0;
        await foreach (Value observation in ((RangeValue)value).Items)
        {
            Check(observation is BoolValue { Data: true }, "trait program false observation " + count + " at O" + level);
            count++;
        }
        Check(count == 29, "trait program observation count");
    }
}
try
{
    Compiler.Compile("$std::meta::is_number(3)", "meta-invalid.msync");
    throw new InvalidOperationException("A value was accepted where a Type is required");
}
catch (CompileException error) { Check(error.Diagnostic.Code == "E0203", "non-Type diagnostic"); }
Console.WriteLine($"PASS {checks} closed-Type metaprogramming contracts");
