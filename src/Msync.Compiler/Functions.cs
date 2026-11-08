using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private Info Function(Frame frame, FunctionExpr function)
    {
        var usedNames = Names(function.Body).ToHashSet(StringComparer.Ordinal);
        bool needsStandard = HasStandardReference(function.Body);
        if (needsStandard && !frame.Environment.ContainsKey("std") && !frame.Environment.ContainsKey("$std"))
        {
            Standard(frame, function.Span); frame.Emit(OpCode.Pop);
        }
        var captures = frame.Environment.Where(pair => (usedNames.Contains(pair.Key) || (pair.Key == "$std" && needsStandard)) && pair.Key != function.Name).ToArray();
        int id = functions.Count;
        functions.Add(new(function.Name ?? $"<lambda{nextTemplate++}>", function.Parameters.Count, captures.Length + function.Parameters.Count,
            [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, captures.Length));
        Template template = new(id, function, captures, frame.Source, frame.SourceName);
        templates.Add(template);
        EmitClosure(frame, template);
        return new(TypeRef.Function, template);
    }
    private void EmitClosure(Frame frame, Template template)
    {
        if (frame.SelfCaptureSlots.TryGetValue(template.Id, out var ownSlots))
        {
            foreach (int slot in ownSlots) frame.Emit(OpCode.LoadLocal, slot);
        }
        else
        {
            foreach (var capture in template.Captures)
            {
                if (capture.Value.Self is not null) EmitClosure(frame, capture.Value.Self);
                else frame.Emit(OpCode.LoadLocal, capture.Value.Slot);
            }
        }
        frame.Emit(OpCode.MakeClosure, template.Id, template.Captures.Count, span: template.Syntax.Span);
    }
    private Specialization Specialize(Frame caller, Template template, IReadOnlyList<Info> arguments, SourceSpan span)
    {
        if (arguments.Count != template.Syntax.Parameters.Count) throw Error(caller, $"Function expects {template.Syntax.Parameters.Count} arguments, got {arguments.Count}.", span);
        string key = string.Join(';', arguments.Select(argument => InfoKey(argument)));
        if (template.Specializations.TryGetValue(key, out var existing)) return existing;
        int id = template.Specializations.Count == 0 ? template.Id : functions.Count;
        if (id == functions.Count) functions.Add(functions[template.Id]);
        var asserted = template.Syntax.ReturnType is null ? null : ResolveType(caller, template.Syntax.ReturnType);
        Specialization specialization = new(id, new(asserted ?? new TypeRef(TypeKind.Never)));
        template.Specializations[key] = specialization;
        Frame frame = new(template.Source, template.SourceName) { ExpectedReturn = asserted };
        frame.SelfCaptureSlots[template.Id] = Enumerable.Range(0, template.Captures.Count).ToArray();
        foreach (var capture in template.Captures) frame.Environment[capture.Key] = new(frame.Allocate(), capture.Value.Info);
        for (int index = 0; index < arguments.Count; index++)
        {
            var parameter = template.Syntax.Parameters[index];
            var info = arguments[index] with { KnownBool = null };
            if (parameter.Type is not null)
            {
                var type = ResolveType(frame, parameter.Type);
                if (parameter.Nullable) type = type.WithNull;
                CheckConversion(caller, info.Type, type, span);
                if (!type.Accepts(info.Type))
                {
                    int parameterSlot = frame.Locals;
                    frame.Emit(OpCode.LoadLocal, parameterSlot);
                    Convert(frame, info.Type, type, span);
                    frame.Emit(OpCode.StoreLocal, parameterSlot);
                    info = info with { Type = type };
                }
            }
            else if (info.Type.Nullable && !parameter.Nullable)
                throw Error(caller, $"Nullable argument requires nullable parameter '{parameter.Name}?'.", span);
            frame.Environment[parameter.Name] = new(frame.Allocate(), info);
        }
        if (template.Syntax.Name is not null) frame.Environment[template.Syntax.Name] = new(-1, new(TypeRef.Function, template), template);
        var result = Block(frame, template.Syntax.Body);
        foreach (var returned in frame.Returns) result = Join(result, returned);
        if (asserted is not null) { CheckConversion(frame, result.Type, asserted, template.Syntax.Body.Span); Convert(frame, result.Type, asserted, template.Syntax.Body.Span); result = result with { Type = asserted }; }
        frame.Emit(OpCode.Return);
        specialization.Result = result;
        functions[id] = new(template.Syntax.Name ?? $"<lambda{template.Id}>", arguments.Count, frame.Locals, frame.Code.ToArray(), result.Type,
            template.Captures.Count, arguments.Select(info => info.Type).ToArray());
        return specialization;
    }

    private Info Call(Frame frame, CallExpr call)
    {
        Frame scratch = CloneFrame(frame);
        var result = CallEager(scratch, call);
        if (result.Type.Kind == TypeKind.Range)
        {
            EmitThunk(frame, scratch, result, "<range-call>");
            frame.Emit(OpCode.DeferRange, text: result.Type.Element?.ToString(), span: call.Span);
        }
        else
        {
            int offset = frame.Code.Count;
            frame.Code.AddRange(scratch.Code.Select(instruction => instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue
                ? instruction with { A = instruction.A + offset }
                : instruction.Op == OpCode.IteratorNext ? instruction with { B = instruction.B + offset } : instruction));
            frame.Locals = scratch.Locals;
            frame.Environment = scratch.Environment;
        }
        return result;
    }

    private Info CallEager(Frame frame, CallExpr call)
    {
        if (call.Callee is NameExpr { Name: "decltype" })
        {
            if (call.Arguments.Count != 1) throw Error(frame, "decltype expects one unevaluated expression.", call.Span);
            Frame scratch = CloneFrame(frame);
            var inferred = Expression(scratch, call.Arguments[0]);
            return TypeConstant(frame, inferred.Type, call.Span);
        }
        var callee = Expression(frame, call.Callee);
        var arguments = call.Arguments.Select(argument => Expression(frame, argument)).ToArray();
        return Invoke(frame, callee, arguments, call.Span);
    }
    private Info Invoke(Frame frame, Info callee, IReadOnlyList<Info> arguments, SourceSpan span)
    {
        if (callee.Type == TypeRef.Path || callee.Type == TypeRef.String)
        {
            foreach (var argument in arguments) Require(frame, argument.Type, new(TypeKind.String | TypeKind.Path | TypeKind.Number | TypeKind.Bool), span);
            frame.Emit(OpCode.ProcessCall, arguments.Count, span: span); return new(TypeRef.Process);
        }
        Require(frame, callee.Type, TypeRef.Function, span);
        if (callee.Function is not null)
        {
            var specialization = Specialize(frame, callee.Function, arguments, span);
            frame.Emit(OpCode.Call, arguments.Count, specialization.Id + 1, span: span);
            return specialization.Result;
        }
        if (callee.NativeName is not null) return InvokeNative(frame, callee.NativeName, arguments, span);
        if (callee.Alternatives is not null)
        {
            var dispatch = ResolveDispatch(frame, callee, arguments, span);
            frame.Emit(OpCode.Call, arguments.Count, text: dispatch.Mapping, span: span);
            return dispatch.Result;
        }
        throw Error(frame, "Function value has no inferred callable signature.", span);
    }
    private Info InvokeNative(Frame frame, string identity, IReadOnlyList<Info> arguments, SourceSpan span)
    {
        var signature = NativeSignatures.Get(identity);
        if ((!signature.Variadic && arguments.Count != signature.Parameters.Count) || (signature.Variadic && arguments.Count < signature.Parameters.Count))
            throw Error(frame, $"Native '{identity}' expects {(signature.Variadic ? "at least " : "")}{signature.Parameters.Count} arguments, got {arguments.Count}.", span);
        for (int index = 0; index < arguments.Count; index++)
        {
            var accepted = signature.Parameters.Count == 0 ? TypeRef.Any : signature.Parameters[Math.Min(index, signature.Parameters.Count - 1)];
            CheckConversion(frame, arguments[index].Type, accepted, span);
        }
        Info? callbackResult = null;
        int callbackIndex = arguments.Count - 1;
        if (callbackIndex >= 1 && arguments[0].Type.Kind == TypeKind.Range && arguments[callbackIndex].Type == TypeRef.Function)
        {
            var item = arguments[0].Element ?? new Info(arguments[0].Type.Element ?? TypeRef.Any);
            var callback = ResolveDispatch(frame, arguments[callbackIndex], [item], span);
            callbackResult = callback.Result;
            if (callback.Mapping.Length != 0) frame.Emit(OpCode.SpecializeClosure, text: callback.Mapping, span: span);
            if (identity is "ranges.filter" or "ranges.take_while" or "ranges.drop_while" or "ranges.any" or "ranges.any_of" or "ranges.all_of" or "ranges.none" or "ranges.none_of" or "ranges.find_if")
                Require(frame, callbackResult.Type, TypeRef.Bool, span);
        }
        var result = signature.ResultRule switch
        {
            NativeResultRule.FirstArgument when arguments.Count > 0 => arguments[0],
            NativeResultRule.RangeOfArguments => MakeRangeInfo(arguments),
            NativeResultRule.RangeItem when arguments.Count > 0 => arguments[0].Element ?? new(arguments[0].Type.Element ?? TypeRef.Any),
            NativeResultRule.Transform when callbackResult is not null => new(new(TypeKind.Range, callbackResult.Type), Element: callbackResult),
            _ => new Info(signature.Result)
        };
        frame.Emit(OpCode.Call, arguments.Count, span: span);
        return result;
    }
    private (Info Result, string Mapping) ResolveDispatch(Frame frame, Info callee, IReadOnlyList<Info> arguments, SourceSpan span)
    {
        List<string> mappings = []; Info? result = null;
        foreach (var candidate in CallableChoices(callee))
        {
            Info next;
            if (candidate.Function is not null)
            {
                var specialization = Specialize(frame, candidate.Function, arguments, span);
                mappings.Add($"{candidate.Function.Id}:{specialization.Id}");
                next = specialization.Result;
            }
            else if (candidate.NativeName is not null)
            {
                Frame scratch = CloneFrame(frame);
                next = InvokeNative(scratch, candidate.NativeName, arguments, span);
                foreach (var binding in scratch.Code.Where(instruction => instruction.Op == OpCode.SpecializeClosure)) frame.Code.Add(binding);
            }
            else throw Error(frame, "Function value has no inferred callable signature.", span);
            result = result is null ? next : Join(result, next);
        }
        return (result ?? new(new(TypeKind.Never)), string.Join(';', mappings.Distinct()));
    }
    private static IEnumerable<Info> CallableChoices(Info info) => info.Alternatives is null ? [info] : info.Alternatives;
    private static string InfoKey(Info info) => $"{info.Type}:{info.Function?.Id}:{info.NativeName}:{(info.Scope is null ? "" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(info.Scope))}:{string.Join(',', info.Alternatives?.Select(InfoKey) ?? [])}:{(info.Element is null ? "" : InfoKey(info.Element))}";


}
