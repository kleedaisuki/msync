namespace Msync.Core.Hpir;

public enum HpirWidth : byte { Void = 0, U8 = 1, U16 = 2, U32 = 4, U64 = 8 }

public enum HpirOp : byte
{
    Invalid,
    CopyU8, CopyU16, CopyU32, CopyU64,
    ConstU8, ConstU16, ConstU32, ConstU64,
    AddressOf, AddressOfStatic, AddPointer, OffsetPointer,
    AddF64, SubF64, MulF64, DivF64, RemF64, NegF64,
    EqF64, NeF64, LtF64, LeF64, GtF64, GeF64,
    AndU8, OrU8, XorU8, EqU8, EqU64,
    AddU64, SubU64, MulU64, AndU64, OrU64, XorU64, ShlU64, ShrU64, SarI64,
    LtU64, LeU64, LtI64, LeI64, SignExtendI8, SignExtendI16, SignExtendI32,
    AddressOfStaticCell, AddressOfGlobal, AddressOfTypeCell, Jump, Branch, Call, Return,
    OrdinalF64ToU64, ConvertI64ToF64, ConvertU8ToF64, BoolFromF64, AddressOfConnection
}

public enum HpirStage : byte { Relocatable, Executable }

public enum HpirReturnRoot : byte { None, Exact, Borrowed, AggregateLayout }

public sealed record HpirProgram(IReadOnlyList<HpirFunction> Functions, int EntryPoint)
{
    public IReadOnlyList<HpirLayout> Layouts { get; init; } = [];
    public IReadOnlyList<HpirGlobal> Globals { get; init; } = [];
    public IReadOnlyList<HpirImport> Imports { get; init; } = [];
    public IReadOnlyList<string> TextLiterals { get; init; } = [];
    public IReadOnlyList<HpirTypeLiteral> TypeLiterals { get; init; } = [];
    public IReadOnlyList<int> TypeCells { get; init; } = [];
    public int TextLayout { get; init; } = -1;
    public bool PathAnchors { get; init; }
    public BindingContext? Context { get; init; }
    public int OptimizationLevel { get; init; } = -1;
    public HpirStage Stage { get; init; } = HpirStage.Executable;
    public IReadOnlyList<HpirTransferInput> TransferInputs { get; init; } = [];
    public IReadOnlyList<HpirPreparedTransfer> PreparedTransfers { get; init; } = [];
    public IReadOnlyList<HpirContextCheck> ContextChecks { get; init; } = [];
    public IReadOnlyList<HpirIteratorSelection> IteratorSelections { get; init; } = [];
    internal IReadOnlyList<HpirSessionAllocation> SessionAllocations { get; init; } = [];
    internal IReadOnlyList<HpirSessionField> SessionRangeItems { get; init; } = [];
}

public sealed record HpirFunction(string Name, int FrameBytes, HpirWidth Result,
    IReadOnlyList<HpirParameter> Parameters, HpirInstruction[] Instructions,
    IReadOnlyList<HpirCall> Calls, IReadOnlyList<HpirSource> Sources)
{
    public IReadOnlyList<HpirRootMap> Safepoints { get; init; } = [];
}

public readonly record struct HpirParameter(int Offset, HpirWidth Width);

public readonly record struct HpirCall(int Target, IReadOnlyList<int> Arguments);

public readonly record struct HpirSource(int Instruction, SourceSpan Source);

// A nonnegative memory operand is a frame byte offset. Its bitwise complement
// denotes a dereference of the pointer stored at that offset. Branch targets
// and call table indices are not memory operands.
public readonly record struct HpirInstruction(HpirOp Op, int D = -1, int A = -1, int B = -1, long Immediate = 0);

public sealed record HpirLayout(int Size, int Alignment, IReadOnlyList<int> PointerOffsets)
{
    public IReadOnlyList<int> BorrowOffsets { get; init; } = [];
}

public readonly record struct HpirGlobal(int Declaration, int Layout);
public readonly record struct HpirTypeLiteral(TypeKind Kinds, int Element = -1);

public enum HpirService : byte { AllocateObject, MathF64, FormatF64, WriteText, Transfer, Fail, HasLayout,
    FutureLift, FutureDemand, ProcessStart, ProcessExit, ProcessSuccess,
    RangeSequence, RangeDrop, RangeTake, RangeOpen, ParseF64,
    ReadLineText, DemandText, TextIsNull, TextParseNumber, Word32, RangeDeferredSequence, String, Path, LiveTransfer,
    FutureMath, TextRangeSource, TextRangeOpen, TextRangeNext, TextRangeClose,
    RangeTransform, RangeTransformTake, RangeTransformOpen, RangeBufferGrow,
    // Keep the retired development import ID out of the active service table.
    ProcessStdout = (byte)RangeBufferGrow + 2, ProcessStderr, FileMetadata, RangeBound, TypeAccepts,
    FailDiagnostic, ReadFileText, LiveTransferFutureText,
    ParseBool, ParsePathBool, ParsePathF64, GuestNumber, SharedTransfer, ContextCheck, LiveTransferBoolText, Regex,
    PathSelection = (byte)Regex + 2, LiveTransferSelection, WriteFileText, RegexFind, ProcessSpawn, ProcessControl,
    ProcessTextReady, Work, TextRangeJoin, DirectorySource, DirectoryOpen, DirectoryNext, DirectoryClose,
    LiveTransferNumberText, RenameFile, FileBytesOpen, FileBytesChunk, TypeName, RangePackWords, AppendTextChunk, ReadStdinText,
    TextFormat, AllocateSessionScope, SessionRangeItem, LivePathBatch, ConcatTextPending }

public enum HpirFailure : byte { UnknownCallable, Unreachable, RangeStalled, RangeEmpty, RangeIndex, RangeIndexStalled }

public sealed record HpirImport(HpirService Service, int Binding, HpirWidth Result, HpirWidth[] Arguments)
{
    public HpirProcessArgument[] ProcessArguments { get; init; } = [];
}

public readonly record struct HpirRootMap(int Instruction, int[] RootOffsets)
{
    public int[] BorrowOffsets { get; init; } = [];
}

public static class HpirAbi
{
    public const int FrameAlignment = 8;
    public const int FrameHeaderBytes = 24;
}
