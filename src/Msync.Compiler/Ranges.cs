using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private Info Range(Frame frame, RangeExpr range)
    {
        TypeRef item = new(TypeKind.Never);
        Info? itemInfo = null;
        bool allPure = range.Items.All(PureValue);
        List<char> mask = [];
        foreach (var expression in range.Items)
        {
            if (PureValue(expression))
            {
                var pure = Expression(frame, expression);
                item = TypeRef.Union(item, pure.Type); itemInfo = itemInfo is null ? pure : Join(itemInfo, pure); mask.Add('0'); continue;
            }
            Frame scratch = CloneFrame(frame);
            var info = Expression(scratch, expression);
            item = TypeRef.Union(item, info.Type); itemInfo = itemInfo is null ? info : Join(itemInfo, info);
            EmitThunk(frame, scratch, info, "<range-item>"); mask.Add('1');
        }
        frame.Emit(allPure ? OpCode.MakeRange : OpCode.MakeLazyRange, range.Items.Count, text: allPure ? null : new string(mask.ToArray()), span: range.Span);
        return new(new(TypeKind.Range, item), Element: itemInfo);
    }
    private static bool PureValue(Expr expression) => expression switch
    {
        BoolExpr or NullExpr or StringExpr or NumberExpr or PathExpr or NameExpr or TypeExpr => true,
        BinaryExpr { Operator: "/" or "::" } binary => PureValue(binary.Left) && PureValue(binary.Right),
        UnaryExpr { Operator: "$" } unary => PureValue(unary.Operand),
        _ => false
    };
    private static Frame CloneFrame(Frame frame)
    {
        Frame clone = new(frame.Source, frame.SourceName, new(frame.Environment)) { Locals = frame.Locals, ExpectedReturn = frame.ExpectedReturn };
        foreach (var pair in frame.SelfCaptureSlots) clone.SelfCaptureSlots[pair.Key] = pair.Value;
        return clone;
    }

    private void EmitThunk(Frame frame, Frame scratch, Info result, string name)
    {
        int parentLocals = frame.Locals;
        var captured = scratch.Code.Where(instruction => instruction.Op == OpCode.LoadLocal && instruction.A < parentLocals)
            .Select(instruction => instruction.A).Distinct().Order().ToArray();
        var slots = captured.Select((slot, index) => (slot, index)).ToDictionary(pair => pair.slot, pair => pair.index);
        int Remap(int slot) => slot < parentLocals ? slots[slot] : captured.Length + slot - parentLocals;
        var code = scratch.Code.Select(instruction => instruction.Op is OpCode.LoadLocal or OpCode.StoreLocal or OpCode.IteratorNext
            ? instruction with { A = Remap(instruction.A) } : instruction).ToList();
        code.Add(new(OpCode.Return));
        int id = functions.Count;
        functions.Add(new(name, 0, captured.Length + scratch.Locals - parentLocals, code.ToArray(), result.Type, captured.Length));
        foreach (int slot in captured) frame.Emit(OpCode.LoadLocal, slot);
        frame.Emit(OpCode.MakeClosure, id, captured.Length);
    }

    private Info Pipe(Frame frame, BinaryExpr binary)
    {
        // Reorder through a temporary so calls keep one universal callee/argument convention.
        var input = Expression(frame, binary.Left);
        Require(frame, input.Type, new(TypeKind.Range), binary.Left.Span);
        int slot = frame.Allocate(); frame.Emit(OpCode.StoreLocal, slot);
        Info callee; IReadOnlyList<Expr> rest;
        if (binary.Right is CallExpr call) { callee = Expression(frame, call.Callee); rest = call.Arguments; }
        else { callee = Expression(frame, binary.Right); rest = []; }
        frame.Emit(OpCode.LoadLocal, slot);
        List<Info> arguments = [input];
        arguments.AddRange(rest.Select(expression => Expression(frame, expression)));
        return Invoke(frame, callee, arguments, binary.Span);
    }

    private static Info MakeRangeInfo(IReadOnlyList<Info> arguments)
    {
        var element = arguments.Count == 0 ? new Info(new(TypeKind.Never)) : arguments.Aggregate(Join);
        return new(new(TypeKind.Range, element.Type), Element: element);
    }


}
