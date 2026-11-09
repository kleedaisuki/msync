namespace Msync.Core;

public readonly record struct SourceSpan(string Source, int Start, int Length);
public sealed record Instruction(OpCode Op, int A = 0, int B = 0, string? Text = null, SourceSpan? Span = null);
