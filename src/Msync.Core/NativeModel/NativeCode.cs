namespace Msync.Core.Native;

public enum NativeOp : byte
{
    Nop,
    Constant,
    Copy,
    Move,
    Drop,
    FrameAddress,
    BindLocation,
    Allocate,
    AddressOffset,
    IndexAddress,
    CopyMemory,
    ClearMemory,
    ProjectAddress,
    Load,
    Store,
    Jump,
    Branch,
    Unary,
    Binary,
    Convert,
    Narrow,
    MakeFunction,
    EnvironmentAddress,
    AdaptFunction,
    Call,
    // C optionally names logical result element metadata; the result slot retains its physical Range ABI.
    CallNative,
    MakeRange,
    RangeFromMemory,
    OpenIterator = 30,
    NextIterator,
    CloseIterator,
    Transfer,
    ProcessCall,
    SelectPath,
    Barrier,
    Acquire,
    ExecutePlan,
    PlanResult,
    TestStatic,
    StaticAddress,
    PublishStatic,
    Return,
    Unreachable,
    MakeType,
    Select,
    MachineBinary = 48,
    MachineCompare = 49,
    MachineCast = 50,
    MachineIntrinsic = 51,
    MachineUnary = 54,
    FunctionComponent = 55,
    ComposeFunction = 56
}

public enum NativeUnary : byte { Not, Negate, IsNull, IsType, HasLayout, ValueType }

public enum NativeBinary : byte
{
    Add, Subtract, Multiply, Divide, Remainder, Power,
    Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
    RegexMatch, PathJoin, Qualify,
    BoolAnd, BoolOr
}

public enum NativeSlotRole : byte { Parameter, Capture, Local, Temporary }
public enum NativeCaptureMode : byte { Value, Location }
public enum NativeAllocationPlacement : byte { LogicalHeap, Frame, Eliminated }

public sealed record NativeSlot(int Type, NativeSlotRole Role = NativeSlotRole.Temporary,
    bool Escapes = false, string? Name = null);

public sealed record NativeCaptureContract(int Type, NativeCaptureMode Mode);

public sealed record NativeSignature(IReadOnlyList<int> Parameters, int Result,
    IReadOnlyList<NativeCaptureContract> Captures);

public sealed record NativeInstruction(NativeOp Op, int Result = -1, int A = -1,
    int B = -1, int C = -1, int Type = -1, long Immediate = 0,
    string? Symbol = null, IReadOnlyList<int>? Inputs = null, SourceSpan? Source = null);

public sealed record NativeFunctionCode(string Name, IReadOnlyList<NativeSlot> Slots,
    IReadOnlyList<NativeInstruction> Instructions, NativeSignature Signature,
    int ParameterCount = 0, int CaptureCount = 0, int IteratorCount = 0,
    bool IsTemplate = false)
{
    public NativeFunctionReflectionSignature? ReflectionSignature { get; init; }
    public NativeStoragePlan? Storage { get; init; }
    internal NativeSsaBody? SsaBody { get; init; }
}

[Flags]
public enum NativeSlotStorage : byte
{
    None = 0, Escapes = 1, PrivateArgument = 2, ReadOnlyParameter = 4,
    UnobservedInitialization = 8, UnreadParameter = 16, WindowProducer = 32
}
[Flags]
public enum NativeInstructionStorage : byte
{
    None = 0, FrameAlias = 1, NarrowLayout = 2, FrameAllocation = 4, OwnArguments = 16
}
public sealed record NativeStoragePlan(IReadOnlyList<NativeSlotStorage> Slots,
    IReadOnlyList<NativeInstructionStorage> Instructions)
{
    public IReadOnlyList<NativeCallStorage> Calls { get; init; } = [];
}
public sealed record NativeCallStorage(int Instruction, bool Snapshot, IReadOnlyList<bool> Borrowed)
{
    public int BorrowedCount { get; } = Borrowed.Count(value => value);
}

public sealed record NativeFunctionReflectionSignature(IReadOnlyList<TypeRef> Parameters, TypeRef Result);

// Namespace domains describe admitted referents, not the address carrier's storage.
// Direct backend offsets need physical producer/admission proof, not this domain alone.
public sealed partial record NativeCodeType(TypeKind Kind, int Element = -1,
    IReadOnlyList<int>? Layouts = null, int Pointee = -1);

public sealed record NativeCodeField(string Name, int Type);
public sealed record NativeCodeLayout(IReadOnlyList<NativeCodeField> Fields);
public readonly record struct NativeProjectionCase(int Layout, int Field);
public sealed record NativeProjection(IReadOnlyList<NativeProjectionCase> Cases);
public readonly record struct NativeStatic(int Layout, int Declaration);

public sealed partial record NativeConstant(NativeKind Kind, string? Text = null,
    double Number = 0, bool Boolean = false, TypeRef? Type = null,
    ConnectionKind ConnectionKind = ConnectionKind.Local)
{
    public NativePath? Path { get; init; }
    public int OriginId { get; init; } = -1;
    public int OriginOffset { get; init; }
}

public sealed record NativeAllocationPlan(int Instruction, int Layout,
    NativeAllocationPlacement Placement = NativeAllocationPlacement.LogicalHeap,
    int Function = 0);

public sealed record NativeProgram(IReadOnlyList<NativeConstant> Constants,
    IReadOnlyList<NativeFunctionCode> Functions, IReadOnlyList<NativeCodeType> Types,
    IReadOnlyList<NativeCodeLayout> Layouts, int EntryPoint = 0)
{
    public const string Representation = "native-v1";
    public IReadOnlyList<NativeProjection> Projections { get; init; } = [];
    public IReadOnlyList<NativeStatic> Statics { get; init; } = [];
    public IReadOnlyList<NativeAllocationPlan> Allocations { get; init; } = [];
    public NativeBackendCode? Backend { get; init; }
    public BindingContext? Context { get; init; }
    public IReadOnlyList<int>? SessionRoots { get; init; }
    // Live session metadata only. Each path orders lexical slots newest first.
    public IReadOnlyList<int[]>? SessionShadowPaths { get; init; }
    public IReadOnlyList<SessionDispatchTable>? SessionDispatches { get; init; }
    public IReadOnlyList<NativeExecutionPlan>? ExecutionPlans { get; init; }
    public ArtifactStage Stage { get; init; } = ArtifactStage.Relocatable;
    public ModuleSemantics Semantics { get; init; } = ModuleSemantics.Current;
    public int OptimizationLevel { get; init; } = 2;
    public bool PreserveLocals { get; init; }
}
