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
        if (needsStandard) usedNames.Add("$std");
        var captures = Captures(frame, usedNames, function.Name);
        int id = functions.Count;
        functions.Add(new(function.Name ?? $"<lambda{nextTemplate++}>", function.Parameters.Count, captures.Length + function.Parameters.Count,
            [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, captures.Length));
        Template template = new(id, function, captures, frame.Source, frame.SourceName);
        templates.Add(template);
        if (editorDefinitions is not null && IsCurrentSource(frame))
        {
            editorDefinitions[template] = (frame.Environment, frame.Locals);
            editorDefinitionOrder!.Add(template);
        }
        EmitClosure(frame, template);
        Info info = new(TypeRef.Function, template);
        if (function.NameSpan is { } name) Observe(frame, name, info, function.Name);
        return info;
    }
    private static KeyValuePair<string, Binding>[] Captures(Frame frame, IEnumerable<string> names, string? self)
    {
        List<KeyValuePair<string, Binding>> captures = [];
        foreach (string name in names)
            if (name != self && frame.Environment.TryGetValue(name, out var binding)) captures.Add(new(name, binding));
        return captures.OrderBy(pair => pair.Value.Slot).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
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
    private Specialization Specialize(Frame caller, Template template, IReadOnlyList<Info> arguments, SourceSpan span,
        IReadOnlyList<Expr>? argumentExpressions = null)
    {
        if (arguments.Count != template.Syntax.Parameters.Count)
        {
            var error = Error(caller, $"Function expects {template.Syntax.Parameters.Count} arguments, got {arguments.Count}.", span,
                label: $"{arguments.Count} arguments supplied",
                help: template.Syntax.Parameters.Count == 0 ? "Call this function with empty parentheses and no arguments."
                    : $"Supply arguments for: {string.Join(", ", template.Syntax.Parameters.Select(parameter => parameter.Name))}.", code: "E0204");
            error.Diagnostic.Related.Add(new(template.SourceName, template.Source, template.Syntax.NameSpan ?? template.Syntax.Span,
                "function declared here"));
            throw error;
        }
        var specializations = template.Specializations;
        if (caller.EditorProbe)
        {
            if (!probeSpecializations!.TryGetValue(template, out specializations)) probeSpecializations[template] = specializations = [];
        }
        string key = string.Join(';', arguments.Select(argument => InfoKey(argument)));
        if (specializations.TryGetValue(key, out var existing)) return existing;
        if (activeEditorSpecializations?.Contains(template.Syntax) == true)
        {
            var guarded = template.Syntax.ReturnType is null ? UnknownInfo : new Info(ResolveType(caller, template.Syntax.ReturnType));
            return new(-1, guarded, arguments.Select(argument => argument.Type).ToArray());
        }
        int id = specializations.Count == 0 && !caller.EditorProbe ? template.Id : functions.Count;
        if (id == functions.Count) functions.Add(functions[template.Id]);
        var asserted = template.Syntax.ReturnType is null ? null : ResolveType(caller, template.Syntax.ReturnType);
        Specialization specialization = new(id, new(asserted ?? new TypeRef(TypeKind.Never)), arguments.Select(info => info.Type).ToArray());
        specializations[key] = specialization;
        var environment = editorDefinitions is not null && editorDefinitions.TryGetValue(template, out var definition) ? definition.Environment : null;
        Frame frame = new(template.Source, template.SourceName, environment)
        {
            ExpectedReturn = asserted,
            ContextOwner = template.Syntax.Body.Span,
            ContextIncludesEnd = template.Syntax.Body.EditorIncomplete,
            EditorRecovery = caller.EditorRecovery,
            EditorProbe = caller.EditorProbe,
            EditorReportErrors = caller.EditorReportErrors,
            EditorCallTrace = caller.EditorRecovery ? new(caller.SourceName, caller.Source, span, template.Syntax.Parameters, specialization.ParameterTypes, caller.EditorCallTrace) : null
        };
        frame.SelfCaptureSlots[template.Id] = Enumerable.Range(0, template.Captures.Count).ToArray();
        foreach (var capture in template.Captures) frame.Bind(capture.Key, new(frame.Allocate(), capture.Value.Info));
        for (int index = 0; index < arguments.Count; index++)
        {
            var parameter = template.Syntax.Parameters[index];
            var argument = arguments[index];
            var info = argument with { KnownBool = null, KnownConnection = null, KnownPath = null, KnownText = null, Element = ForgetEndpoint(argument.Element) };
            if (parameter.Type is not null)
            {
                var type = ResolveType(frame, parameter.Type);
                if (parameter.Nullable) type = type.WithNull;
                try { CheckConversion(caller, info.Type, type, argumentExpressions?[index].Span ?? span); }
                catch (CompileException error)
                {
                    error.Diagnostic.Related.Add(new(template.SourceName, template.Source, parameter.Span,
                        $"parameter '{parameter.Name}' requires {type}"));
                    throw;
                }
                if (!type.Accepts(info.Type))
                {
                    int parameterSlot = frame.Locals;
                    frame.Emit(OpCode.LoadLocal, parameterSlot);
                    Convert(frame, info.Type, type, span);
                    frame.Emit(OpCode.StoreLocal, parameterSlot);
                    info = Retype(info, type);
                }
            }
            else if (info.Type.Nullable && !parameter.Nullable)
            {
                var error = Error(caller, $"Nullable argument requires nullable parameter '{parameter.Name}?'.", argumentExpressions?[index].Span ?? span,
                    label: $"this argument may be Null ({info.Type})",
                    help: "Handle Null before the call, or add '?' after the parameter name if the function handles Null.", code: "E0201");
                error.Diagnostic.Related.Add(new(template.SourceName, template.Source, parameter.Span,
                    $"parameter '{parameter.Name}' does not admit Null"));
                throw error;
            }
            Observe(frame, new(parameter.Span.Start, parameter.Name.Length), info, parameter.Name);
            frame.Bind(parameter.Name, new(frame.Allocate(), info));
        }
        if (template.Syntax.Name is not null) frame.Bind(template.Syntax.Name, new(-1, new(TypeRef.Function, template), template));
        if (caller.EditorReportErrors) editorCalledTemplates?.Add(template);
        CaptureContext(frame, template.Syntax.Body.Span.Start);
        Info result;
        activeEditorSpecializations?.Add(template.Syntax);
        try
        {
            result = Block(frame, template.Syntax.Body);
            foreach (var returned in frame.Returns) result = Join(result, returned);
            if (asserted is not null)
            {
                CheckConversion(frame, result.Type, asserted, template.Syntax.Body.Span);
                Convert(frame, result.Type, asserted, template.Syntax.Body.Span);
                result = Retype(result, asserted);
            }
        }
        catch (CompileException error)
        {
            error.Diagnostic.Related.Add(new(caller.SourceName, caller.Source, span,
                arguments.Count == 0 ? "while checking this call"
                    : $"while checking this call with {string.Join(", ", template.Syntax.Parameters.Zip(arguments, (p, a) => $"{p.Name}: {a.Type}"))}"));
            throw;
        }
        finally { activeEditorSpecializations?.Remove(template.Syntax); }
        frame.Emit(OpCode.Return);
        specialization.Result = result;
        functions[id] = new(template.Syntax.Name ?? $"<lambda{template.Id}>", arguments.Count, frame.Locals, frame.Code.ToArray(), result.Type,
            template.Captures.Count, specialization.ParameterTypes);
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
        return Invoke(frame, callee, arguments, call.Span, call.Arguments);
    }
    private Info Invoke(Frame frame, Info callee, IReadOnlyList<Info> arguments, SourceSpan span,
        IReadOnlyList<Expr>? argumentExpressions = null)
    {
        if (callee.Type == TypeRef.Path || callee.Type == TypeRef.String)
        {
            for (int index = 0; index < arguments.Count; index++)
                Require(frame, arguments[index].Type, new(TypeKind.String | TypeKind.Path | TypeKind.Number | TypeKind.Bool), argumentExpressions?[index].Span ?? span);
            frame.Emit(OpCode.ProcessCall, arguments.Count, span: span); return new(TypeRef.Process);
        }
        Require(frame, callee.Type, TypeRef.Function, span);
        if (callee.Function is not null)
        {
            var specialization = Specialize(frame, callee.Function, arguments, span, argumentExpressions);
            frame.Emit(OpCode.Call, arguments.Count, specialization.Id + 1, span: span);
            return specialization.Result;
        }
        if (callee.NativeName is not null) return InvokeNative(frame, callee.NativeName, arguments, span, argumentExpressions);
        if (callee.Alternatives is not null)
        {
            var dispatch = ResolveDispatch(frame, callee, arguments, span);
            frame.Emit(OpCode.Call, arguments.Count, text: dispatch.Mapping, span: span);
            return dispatch.Result;
        }
        throw Error(frame, "Function value has no inferred callable signature.", span,
            help: "Use a concrete fn value or preserve its signature through a generic binding.", code: "E0204");
    }
    private Info InvokeNative(Frame frame, string identity, IReadOnlyList<Info> arguments, SourceSpan span,
        IReadOnlyList<Expr>? argumentExpressions = null)
    {
        var signature = NativeSignatures.Get(identity);
        if ((!signature.Variadic && arguments.Count != signature.Parameters.Count) || (signature.Variadic && arguments.Count < signature.Parameters.Count))
            throw Error(frame, $"Native '{identity}' expects {(signature.Variadic ? "at least " : "")}{signature.Parameters.Count} arguments, got {arguments.Count}.", span,
                help: $"Expected argument types: ({string.Join(", ", signature.Parameters)}).", code: "E0204");
        for (int index = 0; index < arguments.Count; index++)
        {
            var accepted = signature.Parameters.Count == 0 ? TypeRef.Any : signature.Parameters[Math.Min(index, signature.Parameters.Count - 1)];
            CheckConversion(frame, arguments[index].Type, accepted, argumentExpressions?[index].Span ?? span);
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
    // Scope/callable identities already specialize their immutable captures. Scalar
    // and Range-item literal values do not belong to the type-shared argument frame.
    private static Info? ForgetEndpoint(Info? info)
    {
        if (info is null) return null;
        var element = ForgetEndpoint(info.Element);
        return info.KnownConnection is null && info.KnownPath is null && info.KnownText is null && ReferenceEquals(info.Element, element) ? info
            : info with { KnownConnection = null, KnownPath = null, KnownText = null, Element = element };
    }
    private static string InfoKey(Info info) => $"{info.Type}:{info.Function?.Id}:{info.NativeName}:{(info.Scope is null ? "" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(info.Scope))}:{string.Join(',', info.Alternatives?.Select(InfoKey) ?? [])}:{(info.Element is null ? "" : InfoKey(info.Element))}";


}
