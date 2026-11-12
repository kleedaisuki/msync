using Msync.Core;

if (args.Length is not (1 or 2))
    throw new ArgumentException("Expected output .mso path and optional fixture name.");

string fixture = args.Length == 2 ? args[1] : "carry-pair";
if (fixture != "carry-pair")
{
    using var effectOutput = File.Create(args[0]);
    Artifact.Write(EffectReader(fixture), effectOutput);
    return;
}

const int count = 0, index = 1, saved = 2, oldFirst = 3, oldSecond = 4;
const int number = 0, zero = 1, three = 2, second = 3, one = 4, text = 5;

List<Instruction> code =
[
    new(OpCode.NativeCall, Text: "io.read_line"),
    new(OpCode.Constant, number),
    new(OpCode.NativeCall, 2, Text: "cast"),
    new(OpCode.StoreLocal, count),
    new(OpCode.Constant, zero),
    new(OpCode.StoreLocal, index),
    new(OpCode.Constant, zero),
    new(OpCode.StoreLocal, saved),
    new(OpCode.Constant, zero),
    new(OpCode.StoreLocal, oldFirst),
    new(OpCode.Constant, zero),
    new(OpCode.StoreLocal, oldSecond),
    new(OpCode.Constant, three),
    new(OpCode.Constant, second),
];
int header = code.Count;
code.AddRange(
[
    new(OpCode.LoadLocal, index),
    new(OpCode.LoadLocal, count),
    new(OpCode.Less),
]);
int exitBranch = code.Count;
code.Add(new(OpCode.JumpIfFalse));
code.AddRange(
[
    // The pair [a,b] stays live while the loop predicate and counter execute.
    new(OpCode.StoreLocal, saved),
    new(OpCode.LoadLocal, saved),
    new(OpCode.Dup),
    new(OpCode.LoadLocal, index),
    new(OpCode.Add),
    // Overwrite saved with b+i while the preceding load of b is still live.
    new(OpCode.StoreLocal, saved),
    new(OpCode.StoreLocal, oldSecond),
    new(OpCode.StoreLocal, oldFirst),
    // Subtract the snapshot of b from saved, giving i. The next pair is [b, a+i+3].
    new(OpCode.LoadLocal, oldSecond),
    new(OpCode.LoadLocal, oldFirst),
    new(OpCode.LoadLocal, saved),
    new(OpCode.LoadLocal, oldSecond),
    new(OpCode.Subtract),
    new(OpCode.Add),
    new(OpCode.Constant, three),
    new(OpCode.Add),
    new(OpCode.LoadLocal, index),
    new(OpCode.Constant, one),
    new(OpCode.Add),
    new(OpCode.StoreLocal, index),
    new(OpCode.Jump, header),
]);
code[exitBranch] = code[exitBranch] with { A = code.Count };
code.AddRange(
[
    // a-3*b detects both a reordered exit and a common error in both carried values.
    new(OpCode.Constant, three),
    new(OpCode.Multiply),
    new(OpCode.Subtract),
    new(OpCode.Constant, text),
    new(OpCode.NativeCall, 2, Text: "cast"),
    new(OpCode.NativeCall, 1, Text: "io.println"),
    new(OpCode.Pop),
    new(OpCode.Null),
    new(OpCode.Return),
]);
Value[] constants =
[
    new TypeValue(TypeRef.Number), new NumberValue(0), new NumberValue(3), new NumberValue(-2),
    new NumberValue(1), new TypeValue(TypeRef.String),
];
Module module = new(constants, [new("carry-pair", 0, 5, code.ToArray(), TypeRef.Null)]);
using var output = File.Create(args[0]);
Artifact.Write(module, output);

static Module EffectReader(string fixture)
{
    if (fixture is not ("tail-call" or "tail-cell" or "tail-switch" or "tail-native"))
        throw new ArgumentException($"Unknown artifact fixture '{fixture}'.");
    bool direct = fixture == "tail-native";
    const int count = 0, index = 1, value = 2, reader = 3;
    const int number = 0, zero = 1, seven = 2, marker = 3, half = 4, quarter = 5, one = 6, text = 7, transition = 8;
    List<Instruction> code =
    [
        new(OpCode.NativeCall, Text: "io.read_line"),
        new(OpCode.Constant, number),
        new(OpCode.NativeCall, 2, Text: "cast"),
        new(OpCode.StoreLocal, count),
        new(OpCode.Constant, zero),
        new(OpCode.StoreLocal, index),
        new(OpCode.Constant, seven),
        new(OpCode.StoreLocal, value),
    ];
    if (fixture == "tail-cell")
    {
        // The ordinary numeric operations read the location's current Value.
        // The native tail may not guard the Cell as if it were an owned Number.
        code.Add(new(OpCode.LoadCell, value));
        code.Add(new(OpCode.Pop));
    }
    if (!direct)
    {
        code.Add(new(OpCode.MakeClosure, 1));
        code.Add(new(OpCode.StoreLocal, reader));
    }
    // The numeric suffix must leave this non-Number prefix in place.
    code.Add(new(OpCode.Constant, marker));
    int header = code.Count;
    code.AddRange([new(OpCode.LoadLocal, index), new(OpCode.LoadLocal, count), new(OpCode.Less)]);
    int exit = code.Count;
    code.Add(new(OpCode.JumpIfFalse));
    if (fixture == "tail-switch")
    {
        // After native code is installed, make the same site fail its ownership guard.
        code.AddRange([new(OpCode.LoadLocal, index), new(OpCode.Constant, transition), new(OpCode.NotEqual)]);
        int skip = code.Count;
        code.AddRange([new(OpCode.JumpIfTrue), new(OpCode.LoadCell, value), new(OpCode.Pop)]);
        code[skip] = code[skip] with { A = code.Count };
    }
    if (direct) code.Add(new(OpCode.NativeCall, Text: "io.read_line"));
    else code.AddRange([new(OpCode.LoadLocal, reader), new(OpCode.Call)]);
    code.AddRange(
    [
        // The read result itself is discarded; only the surrounding numeric state is needed.
        new(OpCode.Pop),
        new(OpCode.LoadLocal, value),
        new(OpCode.LoadLocal, index),
        new(OpCode.Add),
        new(OpCode.Constant, half),
        new(OpCode.Multiply),
        new(OpCode.Constant, quarter),
        new(OpCode.Subtract),
        new(OpCode.StoreLocal, value),
        new(OpCode.LoadLocal, index),
        new(OpCode.Constant, one),
        new(OpCode.Add),
        new(OpCode.StoreLocal, index),
        new(OpCode.Jump, header),
    ]);
    code[exit] = code[exit] with { A = code.Count };
    code.AddRange(
    [
        new(OpCode.NativeCall, 1, Text: "io.println"),
        new(OpCode.Pop),
        // Reading the next input detects a lost or replayed canonical effect.
        new(OpCode.NativeCall, Text: "io.read_line"),
        new(OpCode.NativeCall, 1, Text: "io.println"),
        new(OpCode.Pop),
        new(OpCode.LoadLocal, value),
        new(OpCode.Constant, text),
        new(OpCode.NativeCall, 2, Text: "cast"),
        new(OpCode.NativeCall, 1, Text: "io.println"),
        new(OpCode.Pop),
        new(OpCode.Null),
        new(OpCode.Return),
    ]);
    Value[] constants =
    [
        new TypeValue(TypeRef.Number), new NumberValue(0), new NumberValue(7), new StringValue("opaque prefix"),
        new NumberValue(.5), new NumberValue(.25), new NumberValue(1), new TypeValue(TypeRef.String),
        new NumberValue(81),
    ];
    FunctionCode entry = new(fixture, 0, direct ? 3 : 4, code.ToArray(), TypeRef.Null);
    FunctionCode effect = new("reader", 0, 0, [new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.Return)], TypeRef.String.WithNull);
    return new(constants, direct ? [entry] : [entry, effect]);
}
