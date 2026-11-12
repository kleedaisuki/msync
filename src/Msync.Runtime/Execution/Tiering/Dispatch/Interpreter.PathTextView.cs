using System.Diagnostics.CodeAnalysis;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private bool TryPathTextView<TFrame>(Module image, IReadOnlyList<Instruction> code, ref int pc,
        ref TFrame frame, ref OperandStack<TFrame> stack, CancellationToken cancellationToken)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        var operand = stack.PeekReference(0);
        if (operand is not (PathValue or StringValue)) return false;

        Instruction instruction = code[pc];
        var component = instruction.Text switch
        {
            "fs.filename" => NativePathComponent.Filename,
            "fs.stem" => NativePathComponent.Stem,
            _ => NativePathComponent.Extension,
        };
        if (!TryPathTextTail(image, code, pc, out int comparison, out Instruction? cast, out string? expected))
            return CompleteLexicalPath(image, instruction, operand, component, frame, ref stack, cancellationToken);

        NativePath path = operand is PathValue value
            ? NativeRegistry.LexicalTextProjection(value, OperatingSystem.IsWindows())
            : new(((StringValue)operand).Data, OperatingSystem.IsWindows());
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<char> text;
        using (var span = frame.BeginInlineNative(image, instruction))
        {
            span?.SetAttribute("native.path_result", "borrowed");
            text = path.BorrowComponent(component);
        }
        if (cast is not null)
        {
            frame.Visit(pc + 1, OpCode.Constant);
            frame.Visit(pc + 2, OpCode.NativeCall);
            using (frame.BeginInlineNative(image, cast)) { }
        }
        else frame.Visit(pc + 1, OpCode.Convert);

        frame.Visit(comparison - 1, OpCode.Constant);
        OpCode op = code[comparison].Op;
        frame.Visit(comparison, op);
        int order = TextSemantics.CompareOrdinal(text, expected);
        bool result = op switch
        {
            OpCode.Equal => order == 0,
            OpCode.NotEqual => order != 0,
            OpCode.Less => order < 0,
            OpCode.LessEqual => order <= 0,
            OpCode.Greater => order > 0,
            _ => order >= 0,
        };
        stack.Discard();
        stack.Add(result ? Value.True : Value.False);
        pc = comparison;
        return true;
    }

    private bool CompleteLexicalPath<TFrame>(Module image, Instruction instruction, Value operand, NativePathComponent component,
        TFrame frame, ref OperandStack<TFrame> stack, CancellationToken cancellationToken)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        using var span = frame.BeginInlineNative(image, instruction);
        span?.SetAttribute("native.path_result", "owned");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathValue result = operand is PathValue path
                ? NativeRegistry.MaterializeLexicalComponent(path, component, OperatingSystem.IsWindows())
                : NativeRegistry.MaterializeLexicalComponent((StringValue)operand, component, context, OperatingSystem.IsWindows());
            stack.Discard();
            stack.Add(result);
            return true;
        }
        catch (Exception error)
        {
            span?.Fail(error);
            if (error is not OperationCanceledException) ReportNativeFailure(instruction.Text!, error);
            throw;
        }
    }

    // A terminal String comparison consumes the lexical projection without
    // publishing a Path or String, or borrowing a view across suspension.
    private static bool TryPathTextTail(Module image, IReadOnlyList<Instruction> code, int pc,
        out int comparison, out Instruction? cast, [NotNullWhen(true)] out string? expected)
    {
        comparison = pc + 3;
        cast = null;
        expected = null;
        if (comparison >= code.Count) return false;
        if (code[pc + 1] is not { Op: OpCode.Convert, Text: "String" })
        {
            if (++comparison >= code.Count || code[pc + 1] is not { Op: OpCode.Constant } type
                || image.Constants[type.A] is not TypeValue typeValue || typeValue.Data != TypeRef.String
                || code[pc + 2] is not { Op: OpCode.NativeCall, A: 2, Text: "cast" } conversion)
                return false;
            cast = conversion;
        }

        if (code[comparison - 1] is not { Op: OpCode.Constant } constant
            || image.Constants[constant.A] is not StringValue literal
            || code[comparison].Op is not (OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual))
            return false;
        expected = literal.Data;
        return true;
    }
}
