using Msync.Compiler.Syntax;
using Msync.Core;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private Info Barrier(Frame frame, BarrierExpr barrier)
    {
        if (barrier.Release) frame.Emit(OpCode.Barrier, 1, span: barrier.Span);
        if (barrier.Value is null)
        {
            if (barrier.Acquire) frame.Emit(OpCode.Acquire, span: barrier.Span);
            frame.Emit(OpCode.Null);
            return new(TypeRef.Null);
        }

        if (barrier.Acquire) frame.Emit(OpCode.Barrier, 2, 1, span: barrier.Span);
        int start = frame.Code.Count;
        Info result = Expression(frame, barrier.Value);
        if (barrier.Release) ReleaseProcess(frame, barrier.Value, start, barrier.Acquire);
        if (!barrier.Acquire) return result;
        if (result.Type == TypeRef.Process) frame.Emit(OpCode.Acquire, 1, span: barrier.Span);
        frame.Emit(OpCode.Barrier, 2, span: barrier.Span);
        return result;
    }

    private static void ReleaseProcess(Frame frame, Expr expression, int start, bool acquire)
    {
        Expr value = expression is AssignmentExpr assignment ? assignment.Value : expression;
        if (value is not CallExpr) return;
        for (int index = frame.Code.Count - 1; index >= start; index--)
        {
            Instruction instruction = frame.Code[index];
            if (instruction.Op != OpCode.ProcessCall || instruction.Span is not { } span
                || span.Start != value.Span.Start || span.Length != value.Span.Length) continue;
            frame.Code[index] = instruction with { B = acquire ? 3 : 1 };
            return;
        }
    }
}
