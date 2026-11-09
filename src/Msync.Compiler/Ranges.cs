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
    private static Frame CloneFrame(Frame frame, string? source = null, string? sourceName = null)
    {
        Frame clone = new(source ?? frame.Source, sourceName ?? frame.SourceName, frame.Environment)
        {
            Locals = frame.Locals,
            Facts = frame.Facts,
            GlobalBindings = frame.GlobalBindings,
            Declarations = frame.Declarations,
            ScopeSites = frame.ScopeSites,
            Allocations = frame.Allocations,
            FieldOwners = frame.FieldOwners,
            ExpectedReturn = frame.ExpectedReturn,
            Concepts = frame.Concepts,
            ExpectedConceptReturn = frame.ExpectedConceptReturn,
            ExpectedTypeEnvironment = frame.ExpectedTypeEnvironment,
            ContextOwner = frame.ContextOwner,
            ContextIncludesEnd = frame.ContextIncludesEnd,
            EditorRecovery = frame.EditorRecovery,
            EditorProbe = frame.EditorProbe,
            EditorReportErrors = frame.EditorReportErrors,
            DeferredCaptures = frame.DeferredCaptures,
            DeferredReads = frame.DeferredReads,
            UnwrittenLocations = frame.UnwrittenLocations,
            EditorCallTrace = frame.EditorCallTrace
        };
        clone.AcquireRegions.AddRange(frame.AcquireRegions);
        clone.Iterators.AddRange(frame.Iterators);
        foreach (var pair in frame.SelfCaptureSlots) clone.SelfCaptureSlots[pair.Key] = pair.Value;
        return clone;
    }

    private void EmitThunk(Frame frame, Frame scratch, Info result, string name)
    {
        if (scratch.Code.FirstOrDefault(instruction => instruction.Op == OpCode.Jump && instruction.Text?.StartsWith(LoopLabelPrefix, StringComparison.Ordinal) == true) is { } exit)
            throw Error(scratch, "break cannot leave a deferred Range expression.", new(exit.Span!.Value.Start, exit.Span.Value.Length),
                help: "Place break in the enclosing for/loop body. Deferred Range expressions execute in their own invocation.", code: "E0207");
        int parentLocals = frame.Locals;
        var captured = scratch.Code.Where(instruction => instruction.Op is OpCode.LoadLocal or OpCode.LoadCell or OpCode.LoadStorage or OpCode.StoreLocal && instruction.A < parentLocals)
            .Select(instruction => instruction.A).Distinct().Order().ToArray();
        var slots = captured.Select((slot, index) => (slot, index)).ToDictionary(pair => pair.slot, pair => pair.index);
        int Remap(int slot) => slot < parentLocals ? slots[slot] : captured.Length + slot - parentLocals;
        var code = scratch.Code.Select(instruction => instruction.Op is OpCode.LoadLocal or OpCode.LoadCell or OpCode.LoadStorage or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.IteratorNext
            ? instruction with { A = Remap(instruction.A) } : instruction).ToList();
        code.Add(new(OpCode.Return));
        int id = functions.Count;
        functions.Add(new(name, 0, captured.Length + scratch.Locals - parentLocals, code.ToArray(), result.Type, captured.Length));
        foreach (int slot in captured) frame.Emit(OpCode.LoadCell, slot);
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
