namespace Msync.Core;

public sealed record FunctionCode(string Name, int ParameterCount, int LocalCount, IReadOnlyList<Instruction> Instructions,
    TypeRef ReturnType, int CaptureCount = 0, IReadOnlyList<TypeRef>? ParameterTypes = null);
public sealed record BasicBlock(int Function, int Start, int End, IReadOnlyList<int> Successors, bool HasEffects);
