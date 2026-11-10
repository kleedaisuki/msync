namespace Msync.Core;

public enum OpCode : byte
{
    Constant, Null, True, False, LoadLocal, StoreLocal, Pop, Dup,
    Jump, JumpIfFalse, JumpIfTrue, Call, Return, MakeClosure, NativeCall,
    LoadBuiltin, MakeRange, MakeScope, GetMember, Qualify, PathJoin, Transfer,
    Not, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, IsNull, IsType,
    RegexMatch, GetIterator, IteratorNext, Add, Subtract, Multiply, Divide,
    Negate, ProcessCall, SetMember, SpecializeClosure, Nop, SelectPath, Convert, LocalTransfer, MakeLazyRange, DeferRange,
    LoadCell, GetCapture, Barrier, Acquire, InitializeLocal, LoadStorage, Remainder, GetSingleton, SetSingleton,
    ExecutePlan, PlanResult
}
