using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Msync.Runtime.Native;
using Msync.Core.Native;

int checks = 0;
void Check(bool condition, string reason)
{
    checks++;
    if (!condition) throw new InvalidOperationException(reason);
}
NativeValue Query(string operation, params TypeRef[] types) => MetaTypes.Evaluate(operation, types);
bool Predicate(string operation, params TypeRef[] types) => Query(operation, types).AsBool();
TypeRef Transform(string operation, params TypeRef[] types) => Query(operation, types).AsType();
NativeRuntimeContext context = new(new(WorkingDirectory: Environment.CurrentDirectory));
async Task<NativeValue> Native(string operation, params TypeRef[] types) => await NativeBuiltins.InvokeAsync("meta." + operation,
    types.Select(NativeValue.FromType).ToArray(), context,
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
    Check((await Native("is_same", type, type)).AsBool(), "native Type equality " + name);
    foreach (var (otherName, otherType) in categories)
        Check(Predicate("is_" + name, otherType) == (name == otherName), "category " + name + "/" + otherName);
}
Check(!(await Native("is_convertible", TypeRef.Number, TypeRef.String)).AsBool(), "runtime conversion relation");
Check((await Native("range_of", TypeRef.String)).AsType().Element == TypeRef.String,
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
Check(Query("element_type", TypeRef.String).Kind == NativeKind.Null, "non-Range element_type");
Check(Transform("element_type", new TypeRef(TypeKind.Range)) == TypeRef.Any, "unspecified Range element is Any");
Check(Transform("element_type", new TypeRef(TypeKind.Range | TypeKind.Null, TypeRef.String)) == TypeRef.String, "nullable Range element");
Check(Query("range_rank", new TypeRef(TypeKind.Range, new TypeRef(TypeKind.Range, TypeRef.Number))).AsNumber() == 2, "nested Range rank");
Check(Query("range_rank", TypeRef.String).AsNumber() == 0, "non-Range rank");
var alternatives = Query("alternatives", new TypeRef(TypeKind.Null | TypeKind.Path | TypeKind.Range, TypeRef.Number)).AsRange();
List<TypeRef> actualAlternatives = [];
await foreach (NativeValue value in alternatives.Items) actualAlternatives.Add(value.AsType());
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
        meta::is_union(Number or Null), meta::is_scalar(Path or String),
        meta::is_nullable(Number or Null), meta::is_same(Path, Path),
        meta::accepts(Path or String, Path), meta::is_assignable(String, Path),
        meta::is_convertible(Path, String), meta::is_castable(String, Number),
        meta::is_same(meta::add_null(Number), Number or Null),
        meta::is_same(meta::remove_null(Number or Null), Number),
        meta::is_same(meta::common_type(Path, String), Path or String),
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
    NativeProgram module = Compiler.CompileNative(source, "meta-type-values.msync", level);
    using MemoryStream bytes = new();
    NativeArtifact.Write(module, bytes); bytes.Position = 0;
    NativeProgram relocatable = NativeArtifact.Read(bytes);
    NativeProgram bound = NativePreparation.Prepare(relocatable);
    using MemoryStream executableBytes = new();
    NativeArtifact.Write(bound, executableBytes); executableBytes.Position = 0;
    NativeProgram executable = NativeArtifact.Read(executableBytes);
    Check(executable.Stage == ArtifactStage.Executable, "serialized executable stage");
    foreach (NativeProgram image in new[] { module, relocatable, executable })
    {
        await using NativeInterpreter interpreter = new(image);
        NativeValue value = await interpreter.ExecuteAsync();
        Check(value.Kind == NativeKind.Range, "trait program returns its observations");
        int count = 0;
        await foreach (NativeValue observation in value.AsRange().Items)
        {
            Check(observation.Kind == NativeKind.Bool && observation.AsBool(), "trait program false observation " + count + " at O" + level);
            count++;
        }
        Check(count == 29, "trait program observation count");
    }
}
try
{
    Compiler.CompileNative("$std::meta::is_number(3)", "meta-invalid.msync");
    throw new InvalidOperationException("A value was accepted where a Type is required");
}
catch (CompileException error) { Check(error.Diagnostic.Code == "E0203", "non-Type diagnostic"); }

const string finiteMatch = """
    fn render(T: Type) {
        match T {
            Number => { let value: T = 7; value as String },
            String => { let value: T = "text"; value as String }
        }
    };
    fn render_pair(pair) {
        let T = pair::Item;
        let value: T = pair::value;
        value as String
    };
    let io = $std::io;
    let input? = io::read_line();
    let tag = if input is null { Number } else { String };
    io::println(render(tag));
    let pair = match input is null {
        true => { scope { Item: Number, value: 19 } },
        false => { scope { Item: String, value: "stored" } }
    };
    io::println(match pair::Item {
        Number => { render_pair(pair) },
        String => { render_pair(pair) }
    });
    io::println(render_pair(pair));
    io::println(render_pair(match input is null {
        true => { scope { Item: Number, value: 23 } },
        false => { scope { Item: String, value: "inline" } }
    }));
    """;
foreach (int level in new[] { 0, 2 })
{
    var module = NativePreparation.Prepare(Compiler.CompileNative(finiteMatch, "finite-match.msync", level));
    using MemoryStream bytes = new();
    NativeArtifact.Write(module, bytes);
    bytes.Position = 0;
    var executable = NativeArtifact.Read(bytes);
    foreach (var (input, expected) in new[] { ("", "7\n19\n19\n23\n"), ("x\n", "text\nstored\nstored\ninline\n") })
    {
        using var reader = new StringReader(input);
        using var output = new StringWriter { NewLine = "\n" };
        await using var interpreter = new NativeInterpreter(executable, new(Output: output) { Input = reader });
        await interpreter.ExecuteAsync();
        Check(output.ToString() == expected, "finite match and its correlated payload at O" + level);
    }
}
try
{
    Compiler.CompileNative(finiteMatch[..finiteMatch.IndexOf("io::println(render(tag));", StringComparison.Ordinal)]
        + "match tag { Number => { 1 } };", "finite-match-incomplete.msync");
    throw new InvalidOperationException("A finite match omitted a reachable type");
}
catch (CompileException error)
{
    Check(error.Diagnostic.Code == "E0209", "missing denoted Type case");
}

const string forwardedWitness = """
    fn read_item(source) { source::Item };
    fn transform(view, original) {
        WRITE_FIRST
        let Item = read_item(original);
        WRITE_LAST
        let n: Item = 7;
        n
    };
    let a = scope { Item: Number };
    let b = scope { Item: Number };
    let view? = $std::ranges::first([a, b]);
    if view is null { 0 } else { transform(view, a) }
    """;
const string writeItem = "view::Item = String;";
var beforeStore = forwardedWitness.Replace("WRITE_FIRST", "").Replace("WRITE_LAST", writeItem);
await using (var interpreter = new NativeInterpreter(Compiler.CompileNative(beforeStore, "type-read-before-store.msync")))
    Check((await interpreter.ExecuteAsync()).AsNumber() == 7, "forwarded Type retains the value read before a later store");
try
{
    var afterStore = forwardedWitness.Replace("WRITE_FIRST", writeItem).Replace("WRITE_LAST", "");
    Compiler.CompileNative(afterStore, "type-read-after-store.msync");
    throw new InvalidOperationException("An aliased caller reused a stale forwarded Type witness");
}
catch (CompileException error)
{
    Check(error.Diagnostic.Code == "E0202", "stale forwarded Type is rejected at the annotation");
}

const string recursivePath = """
    fn make() { scope { Item: Number, next?: null } };
    fn write(node) {
        let next? = node::next;
        if next is not null {
            let following? = next::next;
            if following is not null { following::Item = String; };
        };
    };
    let self = make();
    self::next = self;
    write(self);
    let a = make();
    let b = make();
    let c = make();
    a::next = b;
    b::next = c;
    c::next = a;
    write(a);
    let T = c::Item;
    let result: T = "path";
    result
    """;
var recursiveHir = Compiler.Analyze(recursivePath, "recursive-effect-path.msync");
Check(recursiveHir.Functions.Count(function => function.Name == "write") == 1,
    "shared and distinct recursive edges reuse one specialization");
var recursiveImage = Compiler.LowerNative(recursiveHir);
await using (var interpreter = new NativeInterpreter(recursiveImage))
    Check((await interpreter.ExecuteAsync()).AsString() == "path", "recursive write binds the complete formal path");

const string writeBeforeFieldDemand = """
    fn alter(a, b) {
        let view? = $std::ranges::first([a, b]);
        if view is not null {
            view::Item = String;
            let T = a::Item;
            let n: T = 7;
            n
        } else { 0 }
    };
    let a = scope { Item: Number };
    let b = scope { Item: Number };
    alter(a, b)
    """;
try
{
    Compiler.CompileNative(writeBeforeFieldDemand, "type-write-before-field-demand.msync");
    throw new InvalidOperationException("The first field demand restored a stale entry Type");
}
catch (CompileException error)
{
    Check(error.Diagnostic.Code == "E0202", "a write before field demand invalidates the entry Type");
}

Console.WriteLine($"PASS {checks} closed-Type metaprogramming contracts");
