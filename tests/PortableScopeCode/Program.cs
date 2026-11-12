using Msync.Core;

const int Number = 0, String = 1, ScopeA = 2, BothScopes = 4, OpaqueScope = 5, Callable = 6, Null = 7;
CodeType[] types =
[
    new(TypeKind.Number), new(TypeKind.String), new(TypeKind.Scope, ScopeLayouts: [0]),
    new(TypeKind.Scope, ScopeLayouts: [1]), new(TypeKind.Scope, ScopeLayouts: [0, 1]),
    new(TypeKind.Scope), new(TypeKind.Function), new(TypeKind.Null)
];
CodeLayout[] layouts =
[
    new([new("x", Number), new("left", Number)]),
    new([new("right", String), new("x", Number)]),
    new([new("child", ScopeA)])
];
CodeFieldProjection[] projections =
[
    new("x", [new(0, 0), new(1, 1)]), new("x", [new(0, 0)]),
    new("x", []), new("child", [new(2, 0)])
];
int checks = 0;
Instruction[] joined =
[
    new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.IsNull), new(OpCode.JumpIfTrue, 7),
    new(OpCode.Constant, 0), new(OpCode.Constant, 0), new(OpCode.NewScope, 0, 20), new(OpCode.Jump, 10),
    new(OpCode.Constant, 1), new(OpCode.Constant, 0), new(OpCode.NewScope, 1, 21),
    new(OpCode.LoadField, 0), new(OpCode.Return)
];
Module choice = Image(joined);
foreach (var module in new[]
{
    choice, choice with
    {
        Stage = ArtifactStage.Executable, Context = new(Environment.CurrentDirectory, Environment.CurrentDirectory)
    }
})
{
    byte[] bytes = Encode(module);
    var decoded = Artifact.Read(new MemoryStream(bytes));
    Check(Artifact.FormatVersion == 6 && decoded.Stage == module.Stage
        && decoded.FieldProjections[0].Cases.SequenceEqual([new CodeFieldCase(0, 0), new(1, 1)])
        && decoded.CodeTypes[BothScopes].ScopeLayouts!.SequenceEqual([0, 1])
        && decoded.Functions[0].Instructions.SequenceEqual(joined),
        "source-free roundtrip preserves actual layout cases");
    Check(bytes.AsSpan().SequenceEqual(Encode(decoded)), "canonical portable bytecode");
}
Reject(Change(choice, 10, new(OpCode.LoadField, 1)), "no case for receiver layout");
var structural = Change(choice, 10, new(OpCode.LoadField, 2));
var decodedStructural = Artifact.Read(new MemoryStream(Encode(structural)));
Check(decodedStructural.FieldProjections[decodedStructural.Functions[0].Instructions[10].A].Cases.Count == 0,
    "An intentional structural projection remains checked when analysis knows the receiver layouts.");
Reject(choice with { FieldProjections = [new("x", [new(0, 1), new(1, 1)]), .. projections.Skip(1)] },
    "does not match its selected ordinal");
Reject(choice with { CodeTypes = [.. types, new CodeType(TypeKind.Scope, ScopeLayouts: [0, 0])] },
    "must be sorted and unique");
Reject(choice with { CodeTypes = [.. types, new CodeType(TypeKind.Range, Element: types.Length)] }, "Invalid portable code type");

FunctionCode opaque = new("opaque", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.LoadField, 2), new(OpCode.Return)],
    TypeRef.Number, ParameterTypes: [TypeRef.Scope]) { CodeSignature = new([OpaqueScope], Number, []) };
var withOpaque = choice with { Functions = [choice.Functions[0], opaque] };
_ = Artifact.Read(new MemoryStream(Encode(withOpaque)));
checks++;
var mixed = opaque with
{
    Instructions =
    [
        new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.IsNull), new(OpCode.JumpIfTrue, 7),
        new(OpCode.Constant, 0), new(OpCode.Constant, 0), new(OpCode.NewScope, 0), new(OpCode.Jump, 8),
        new(OpCode.LoadLocal, 0), new(OpCode.LoadField, 2), new(OpCode.Return)
    ]
};
_ = Artifact.Read(new MemoryStream(Encode(withOpaque with { Functions = [choice.Functions[0], mixed] })));
checks++;
Reject(withOpaque with { Functions = [choice.Functions[0], opaque with { Instructions = [new(OpCode.LoadLocal, 0), new(OpCode.LoadField, 1), new(OpCode.Return)] }] },
    "requires an admitted finite non-null Scope domain");
var admitted = opaque with
{
    Instructions = [new(OpCode.LoadLocal, 0), new(OpCode.AdmitScope, ScopeA), new(OpCode.LoadField, 1), new(OpCode.Return)]
};
_ = Artifact.Read(new MemoryStream(Encode(withOpaque with { Functions = [choice.Functions[0], admitted] })));
checks++;
Reject(withOpaque with { Functions = [choice.Functions[0], admitted with { Instructions = [new(OpCode.LoadLocal, 0),
    new(OpCode.AdmitScope, OpaqueScope), new(OpCode.LoadField, 1), new(OpCode.Return)] }] },
    "requires a finite non-null Scope domain");

FunctionCode direct = new("direct", 1, 1,
[
    new(OpCode.LoadLocal, 0), new(OpCode.Constant, 0), new(OpCode.Constant, 0), new(OpCode.NewScope, 0),
    new(OpCode.Call, 1, 3), new(OpCode.LoadField, 1), new(OpCode.Return)
], TypeRef.Number, ParameterTypes: [TypeRef.Function]) { CodeSignature = new([Callable], Number, []) };
FunctionCode typed = new("typed", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Scope,
    ParameterTypes: [TypeRef.Scope]) { CodeSignature = new([ScopeA], ScopeA, []) };
var directs = choice with { Functions = [choice.Functions[0], direct, typed] };
_ = Artifact.Read(new MemoryStream(Encode(directs)));
checks++;
Reject(directs with { Functions = [choice.Functions[0], direct with { Instructions =
    [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.Constant, 0), new(OpCode.NewScope, 1),
        new(OpCode.Call, 1, 3), new(OpCode.LoadField, 1), new(OpCode.Return)] }, typed] },
    "disagrees with portable storage type");

Instruction[] ready =
[
    new(OpCode.JumpIfStaticReady, 4, 0),
    new(OpCode.Constant, 0), new(OpCode.Constant, 0), new(OpCode.PublishStatic, 0),
    new(OpCode.LoadStaticField, 0, 0), new(OpCode.Return)
];
_ = Artifact.Read(new MemoryStream(Encode(Image(ready))));
checks++;
Reject(Image([new(OpCode.LoadStaticField, 0, 0), new(OpCode.Return)]), "not definitely initialized");
Reject(Image([new(OpCode.Constant, 1), new(OpCode.Constant, 0), new(OpCode.PublishStatic, 0),
    new(OpCode.LoadStaticField, 0, 0), new(OpCode.Return)]), "disagrees with portable storage type");
Reject(Image([new(OpCode.Constant, 0), new(OpCode.Constant, 0), new(OpCode.NewScope, 0),
    new(OpCode.NewScope, 2), new(OpCode.Constant, 1), new(OpCode.Constant, 0), new(OpCode.NewScope, 1),
    new(OpCode.StoreField, 3), new(OpCode.Return)]), "disagrees with portable storage type");
Reject(Image([new(OpCode.Constant, 0), new(OpCode.Constant, 0), new(OpCode.NewScope, 0),
    new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0, ScopeA + 1), new(OpCode.Pop),
    new(OpCode.Constant, 1), new(OpCode.Constant, 0), new(OpCode.NewScope, 1),
    new(OpCode.StoreLocal, 0), new(OpCode.Constant, 0), new(OpCode.Return)], locals: 1),
    "disagrees with portable storage type");

var template = new FunctionCode("template", 0, 1, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, CaptureCount: 1)
    { CodeSignature = new CodeSignature([], Null, [new(OpaqueScope, true)]) { IsTemplate = true } };
var body = new FunctionCode("specialized", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Number, CaptureCount: 1)
    { CodeSignature = new([], Number, [new(Number, true)]) };
var captureEntry = Image([new(OpCode.Null), new(OpCode.InitializeLocal, 0),
    new(OpCode.LoadCell, 0, Number + 1), new(OpCode.MakeClosure, 1, 1),
    new(OpCode.Constant, 0), new(OpCode.StoreLocal, 0), new(OpCode.Call, B: 3), new(OpCode.Return)], locals: 1);
var capture = captureEntry with { Functions = [captureEntry.Functions[0], template, body] };
_ = Artifact.Read(new MemoryStream(Encode(capture)));
checks++;
Reject(capture with { Functions = [captureEntry.Functions[0], template, body with { CodeSignature = new([], Number, [new(ScopeA, true)]) }] },
    "closure captured location disagrees");
Reject(Change(capture, 6, new(OpCode.Call)), "open closure template must be specialized");

var constant = new ScopeValue(new ScopeLayout(["x", "left"], 0), [new NumberValue(7), new NumberValue(8)]);
var constantCode = Image([new(OpCode.Constant, 0), new(OpCode.LoadField, 1), new(OpCode.Return)])
    with { Constants = [constant] };
var decodedConstant = (ScopeValue)Artifact.Read(new MemoryStream(Encode(constantCode))).Constants[0];
Check(decodedConstant.Layout.CodeLayoutId == 0 && decodedConstant["x"] is NumberValue { Data: 7 },
    "Scope constants carry a layout ID instead of field names");
var symbols = choice with
{
    Constants = [.. choice.Constants, new FunctionValue(0), new FunctionValue(0, [])]
};
var decodedSymbols = Artifact.Read(new MemoryStream(Encode(symbols))).Constants;
Check(decodedSymbols[^2] is FunctionValue { Captures: null }
    && decodedSymbols[^1] is FunctionValue { Captures.Count: 0 },
    "A portable closed code symbol does not acquire an empty closure environment on read");
Reject(constantCode with { Constants = [new ScopeValue(new ScopeLayout(["x", "left"], 0), [new StringValue("wrong"), new NumberValue(8)])] },
    "constant field 'x'");
byte[] oldWire = Encode(choice);
oldWire[8] = 5;
try { _ = Artifact.Read(new MemoryStream(oldWire)); throw new InvalidOperationException("old wire accepted"); }
catch (MsyncException error) when (error.Message.Contains("Unsupported msync bytecode format 5", StringComparison.Ordinal)) { checks++; }
Console.WriteLine($"PASS {checks} portable Scope layout/field/static codec and invalid-domain contracts");

Module Image(Instruction[] code, int locals = 0) =>
    new([new NumberValue(1), new StringValue("right")], [new("entry", 0, locals, code, TypeRef.Number)
        { CodeSignature = new([], Number, []) }])
    {
        CodeTypes = types, ScopeLayouts = layouts, StaticScopes = [new(0)], FieldProjections = projections
    };

static Module Change(Module module, int at, Instruction instruction)
{
    var code = module.Functions[0].Instructions.ToArray();
    code[at] = instruction;
    return module with { Functions = [module.Functions[0] with { Instructions = code }, .. module.Functions.Skip(1)] };
}

static byte[] Encode(Module module)
{
    using MemoryStream stream = new();
    Artifact.Write(module, stream);
    return stream.ToArray();
}

void Reject(Module module, string message)
{
    using MemoryStream stream = new();
    Artifact.WriteProduced(module, stream);
    stream.Position = 0;
    try { _ = Artifact.Read(stream); }
    catch (MsyncException error)
    {
        Check(error.Message.Contains(message, StringComparison.Ordinal), error.Message);
        return;
    }
    throw new InvalidOperationException($"Malformed portable code accepted: {message}");
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
