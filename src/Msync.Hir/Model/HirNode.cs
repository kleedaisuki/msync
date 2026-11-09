namespace Msync.Hir;

[Flags]
public enum HirEffects : ushort
{
    None = 0, ReadLocation = 1, WriteLocation = 2, ReadMemory = 4,
    WriteMemory = 8, ReadExternal = 16, WriteExternal = 32, Invoke = 64,
    Enumerate = 128, MayFail = 256, Synchronize = 512, Spawn = 1024
}

public enum HirKind : byte
{
    Error, Literal, TypeValue, TypeOf, NativeFunction, Builtin, ReadLocation, ReadStorage,
    Declare, Assign, Discard, Block, If, Match, For, Loop, Break, Return,
    Unary, Binary, Coerce, Convert, IsNull, IsType, RegexMatch, Call, Closure, TemplateClosure, AdaptFunction,
    Scope, MemberRead, MemberAssign, Range, LazyRange, DeferRange,
    Qualify, PathJoin, SelectPath, Transfer, Barrier
}

public enum HirUnaryOperator : byte { Not, Negate }
public enum HirBinaryOperator : byte
{
    Add, Subtract, Multiply, Divide, Remainder, Power,
    Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, And, Or
}

// Data refers to the semantic table selected by Kind, never a bytecode offset.
public readonly record struct HirNode(HirKind Kind, HirTypeId Type,
    HirSourceRange Source, HirEdgeRange Children, int Data = 0,
    string? Text = null, HirEffects Effects = HirEffects.None);
