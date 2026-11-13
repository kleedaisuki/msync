namespace Msync.Core.Native;

public enum LegacyV6Op : byte
{
    Constant, Null, True, False, LoadLocal, StoreLocal, Pop, Dup,
    Jump, JumpIfFalse, JumpIfTrue, Call, Return, MakeClosure, NativeCall,
    LoadBuiltin, MakeRange, MakeScope, GetMember, Qualify, PathJoin, Transfer,
    Not, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, IsNull, IsType,
    RegexMatch, GetIterator, IteratorNext, Add, Subtract, Multiply, Divide,
    Negate, ProcessCall, SetMember, SpecializeClosure, Nop, SelectPath, Convert,
    LocalTransfer, MakeLazyRange, DeferRange, LoadCell, GetCapture, Barrier,
    Acquire, InitializeLocal, LoadStorage, Remainder, GetSingleton, SetSingleton,
    ExecutePlan, PlanResult, NewScope, LoadField, StoreField, AdmitScope,
    JumpIfStaticReady, PublishStatic, LoadStaticScope, LoadStaticField, StoreStaticField
}

public enum LegacyV6ValueKind : byte { Null, Bool, String, Number, Path, Connection, Type, Function, Namespace, Range }

public sealed record LegacyV6TypeValue(ushort Kinds, LegacyV6TypeValue? Element = null)
{
    internal TypeRef RuntimeType => new((TypeKind)Kinds, Element?.RuntimeType);
}

/// <summary>Closed portable data recipe; no legacy runtime object is instantiated.</summary>
public sealed record LegacyV6Value(LegacyV6ValueKind Kind, string? Text = null,
    double Number = 0, bool Boolean = false, NativePath? Path = null,
    NativeConnection? Connection = null, LegacyV6TypeValue? Type = null,
    int Function = -1, string? NativeName = null, bool HasEnvironment = false,
    int Layout = -1, IReadOnlyList<LegacyV6Value>? Items = null)
{
    public TypeKind SourceKind => Kind switch
    {
        LegacyV6ValueKind.Null => TypeKind.Null, LegacyV6ValueKind.Bool => TypeKind.Bool,
        LegacyV6ValueKind.String => TypeKind.String, LegacyV6ValueKind.Number => TypeKind.Number,
        LegacyV6ValueKind.Path => TypeKind.Path, LegacyV6ValueKind.Connection => TypeKind.Connection,
        LegacyV6ValueKind.Type => TypeKind.Type, LegacyV6ValueKind.Function => TypeKind.Function,
        LegacyV6ValueKind.Namespace => TypeKind.Scope, LegacyV6ValueKind.Range => TypeKind.Range,
        _ => throw new InvalidOperationException("Unknown legacy portable value recipe.")
    };
}

public sealed record LegacyV6CodeType(ushort Kinds, int Element, IReadOnlyList<int> Layouts);
public sealed record LegacyV6Field(string Name, int Type);
public sealed record LegacyV6Layout(IReadOnlyList<LegacyV6Field> Fields);
public readonly record struct LegacyV6Capture(int Type, bool Location);
public sealed record LegacyV6Signature(IReadOnlyList<int> Parameters, int Result,
    IReadOnlyList<LegacyV6Capture> Captures, IReadOnlyList<int> RequiredStatics, bool IsTemplate);
public sealed record LegacyV6Instruction(LegacyV6Op Op, int A, int B, string? Text = null, SourceSpan? Source = null);
public sealed record LegacyV6Function(string Name, int LocalCount, LegacyV6Signature Signature,
    IReadOnlyList<LegacyV6Instruction> Instructions);
public sealed record LegacyV6Projection(string Name, IReadOnlyList<NativeProjectionCase> Cases);
public sealed record LegacyV6Transfer(int Id, LegacyV6Value Source, NativePath Destination,
    PlannedTransferKind Kind, TransferDestinationMode Mode, IReadOnlyList<PlanResource> Reads,
    IReadOnlyList<PlanResource> Writes, IReadOnlyList<int> Predecessors, SourceSpan? Span);
public sealed record LegacyV6Plan(int Function, int Start, int End, int EliminatedTransfers,
    IReadOnlyList<int>? Results, IReadOnlyList<LegacyV6Transfer> Transfers, IReadOnlyList<ExecutionBatch> Batches);
public sealed record LegacyV6Module(IReadOnlyList<LegacyV6CodeType> Types,
    IReadOnlyList<LegacyV6Layout> Layouts, IReadOnlyList<int> Statics,
    IReadOnlyList<LegacyV6Projection> Projections, IReadOnlyList<LegacyV6Value> Constants,
    IReadOnlyList<LegacyV6Function> Functions, IReadOnlyList<LegacyV6Plan> Plans,
    int EntryPoint, ArtifactStage Stage, BindingContext? Context, int OptimizationLevel,
    ModuleSemantics Semantics);
