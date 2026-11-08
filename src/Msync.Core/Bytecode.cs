namespace Msync.Core;

public enum ArtifactStage : byte { Relocatable, Executable }
[Flags]
public enum ModuleSemantics : byte { Legacy = 0, MutableLocations = 1, DirectionalScheduling = 2, Current = MutableLocations | DirectionalScheduling }
public enum OpCode : byte
{
    Constant, Null, True, False, LoadLocal, StoreLocal, Pop, Dup,
    Jump, JumpIfFalse, JumpIfTrue, Call, Return, MakeClosure, NativeCall,
    LoadBuiltin, MakeRange, MakeScope, GetMember, Qualify, PathJoin, Transfer,
    Not, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, IsNull, IsType,
    RegexMatch, GetIterator, IteratorNext, Add, Subtract, Multiply, Divide,
    Negate, ProcessCall, SetMember, SpecializeClosure, Nop, SelectPath, Convert, LocalTransfer, MakeLazyRange, DeferRange,
    LoadCell, GetCapture, Barrier, Acquire, InitializeLocal, LoadStorage
}

public readonly record struct SourceSpan(string Source, int Start, int Length);
public sealed record Instruction(OpCode Op, int A = 0, int B = 0, string? Text = null, SourceSpan? Span = null);
public sealed record FunctionCode(string Name, int ParameterCount, int LocalCount, IReadOnlyList<Instruction> Instructions,
    TypeRef ReturnType, int CaptureCount = 0, IReadOnlyList<TypeRef>? ParameterTypes = null);
public sealed record BasicBlock(int Function, int Start, int End, IReadOnlyList<int> Successors, bool HasEffects);
public sealed record BindingContext(string WorkingDirectory, string HomeDirectory, string? SourceFile = null);
public sealed record Module(IReadOnlyList<Value> Constants, IReadOnlyList<FunctionCode> Functions, int EntryPoint = 0,
    ArtifactStage Stage = ArtifactStage.Relocatable, BindingContext? Context = null, int OptimizationLevel = 2,
    IReadOnlyList<BasicBlock>? Blocks = null, ModuleSemantics Semantics = ModuleSemantics.Current, bool PreserveLocals = false);

public sealed class MsyncException : Exception
{
    public SourceSpan? Span { get; }
    public MsyncException(string message, SourceSpan? span = null, Exception? inner = null) : base(message, inner) => Span = span;
}
