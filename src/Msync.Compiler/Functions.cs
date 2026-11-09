using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private readonly Dictionary<Template, Specialization> activeSpecializations = [];
    private readonly Dictionary<FunctionExpr, List<Template>> functionSites = new(ReferenceEqualityComparer.Instance);
    private Info DeclareFunction(Frame frame, FunctionDeclaration declaration)
    {
        int slot = frame.Allocate();
        var origin = DeclarationOrigin(frame, declaration.Function.NameSpan);
        Binding binding = new(slot, new(TypeRef.Function), Origin: origin, Constraint: TypeRef.Function);
        var site = (declaration.Function.NameSpan?.Start ?? declaration.Span.Start, declaration.Name);
        if (frame.Declarations.TryGetValue(site, out var saved)) binding = binding with { Identity = saved.Identity, Storage = saved.Storage };
        else frame.Declarations.Add(site, binding);
        frame.Bind(declaration.Name, binding);
        frame.Emit(OpCode.Null);
        frame.Emit(OpCode.InitializeLocal, slot);
        var info = Function(frame, declaration.Function, binding);
        frame.Emit(OpCode.StoreLocal, slot);
        Update(frame, binding, info);
        if (origin is not null) CaptureDefinition(frame, origin.Span, [origin]);
        frame.Emit(OpCode.Null);
        return new(TypeRef.Null);
    }

    private Info Function(Frame frame, FunctionExpr function, Binding? declaration = null)
    {
        if (!frame.EditorRecovery) ValidateGlobalImports(frame, function);
        var usedNames = Names(function.Body).ToHashSet(StringComparer.Ordinal);
        if (function.Parameters.Any(parameter => ReferencesStandardConcept(parameter.Type)) || ReferencesStandardConcept(function.ReturnType))
            usedNames.Add("std");
        CaptureStandardDependencies(frame, function.Body, usedNames);
        var captures = Captures(frame, usedNames, declaration is null ? function.Name : null);
        if (functionSites.TryGetValue(function, out var atSite))
        {
            var saved = atSite.FirstOrDefault(candidate => candidate.Captures.Count == captures.Length &&
                candidate.Captures.Zip(captures).All(pair => pair.First.Key == pair.Second.Key && ReferenceEquals(pair.First.Value.Identity, pair.Second.Value.Identity)));
            if (saved is not null)
            {
                if (declaration is not null) Update(frame, declaration, new(TypeRef.Function, saved));
                EmitClosure(frame, saved);
                return new(TypeRef.Function, saved);
            }
        }
        int id = functions.Count;
        functions.Add(new(function.Name ?? $"<lambda{nextTemplate++}>", function.Parameters.Count, captures.Length + function.Parameters.Count,
            [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, captures.Length));
        Template template = new(id, function, captures, frame.Source, frame.SourceName) { Concepts = frame.Concepts };
        templates.Add(template);
        if (atSite is null) functionSites.Add(function, atSite = []);
        atSite.Add(template);
        if (declaration is not null) Update(frame, declaration, new(TypeRef.Function, template));
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
            foreach (int slot in ownSlots) frame.Emit(OpCode.LoadCell, slot);
        }
        else
        {
            foreach (var capture in template.Captures)
            {
                if (capture.Value.Self is not null) EmitClosure(frame, capture.Value.Self);
                else frame.Emit(OpCode.LoadCell, capture.Value.Slot);
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
        if (activeSpecializations.TryGetValue(template, out var active)
            && active.ParameterTypes.SequenceEqual(arguments.Select(argument => argument.Type))) return active;
        if (caller.EditorProbe)
        {
            if (!probeSpecializations!.TryGetValue(template, out specializations)) probeSpecializations[template] = specializations = [];
        }
        var captures = template.Captures.Select(pair => new KeyValuePair<string, Binding>(pair.Key,
            pair.Value with { Info = caller.DeferredCaptures ? DeferredCapture(caller, pair.Value) : CaptureInfo(Current(caller, pair.Value), caller.EditorRecovery, caller.EditorProbe) })).ToArray();
        var reads = ReadNames(template.Syntax.Body).ToHashSet(StringComparer.Ordinal);
        // A write-only capture's old callable identity cannot affect this body.
        // Including it would create unbounded specializations for recursive stores.
        string CaptureKey(Info info) => info.Type + ":" + info.TypeValue + ":" + (info.Scope is null ? "" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(info.Scope.Identity));
        string key = (caller.DeferredCaptures ? "deferred|" : "") + string.Join(';', arguments.Select(argument => InfoKey(argument))) + "|" + string.Join(';', captures
            .Select(pair => !reads.Contains(pair.Key) ? pair.Value.Info.Type.ToString() : caller.DeferredCaptures ? CaptureKey(pair.Value.Info) : InfoKey(pair.Value.Info)));
        if (castContractEpoch != 0) key = castContractEpoch + "/" + key;
        if (specializations.TryGetValue(key, out var existing)) return existing;
        if (activeEditorSpecializations?.Contains(template.Syntax) == true)
        {
            var recursiveContext = CloneFrame(caller);
            recursiveContext.Concepts = template.Concepts;
            var guarded = template.Syntax.ReturnType is null ? UnknownInfo : ConceptInfo(recursiveContext, template.Syntax.ReturnType);
            return new(-1, guarded, arguments.Select(argument => argument.Type).ToArray());
        }
        int id = specializations.Count == 0 && !caller.EditorProbe && !template.Instance ? template.Id : functions.Count;
        if (id == functions.Count) functions.Add(functions[template.Id]);
        var annotationFrame = CloneFrame(caller);
        annotationFrame.Concepts = template.Concepts;
        if (ReferencesStandardConcept(template.Syntax.ReturnType))
            annotationFrame.Environment = template.Captures.ToImmutableDictionary(StringComparer.Ordinal);
        var asserted = template.Syntax.ReturnType is null ? null : ResolveType(annotationFrame, template.Syntax.ReturnType);
        Specialization specialization = new(id, new(asserted ?? new TypeRef(TypeKind.Never)), arguments.Select(info => info.Type).ToArray());
        specializations[key] = specialization;
        var environment = editorDefinitions is not null && editorDefinitions.TryGetValue(template, out var definition) ? definition.Environment : null;
        Frame frame = new(template.Source, template.SourceName, environment)
        {
            ExpectedReturn = asserted,
            Concepts = template.Concepts,
            ExpectedConceptReturn = template.Syntax.ReturnType,
            Facts = caller.Facts,
            FieldOwners = caller.FieldOwners,
            ExpectedReturnSpan = template.Syntax.ReturnType?.Span,
            ContextOwner = template.Syntax.Body.Span,
            ContextIncludesEnd = template.Syntax.Body.EditorIncomplete,
            EditorRecovery = caller.EditorRecovery,
            EditorProbe = caller.EditorProbe,
            EditorReportErrors = caller.EditorReportErrors,
            DeferredCaptures = caller.DeferredCaptures,
            EditorCallTrace = caller.EditorRecovery ? new(caller.SourceName, caller.Source, span, template.Syntax.Parameters, specialization.ParameterTypes, caller.EditorCallTrace) : null
        };
        frame.SelfCaptureSlots[template.Id] = Enumerable.Range(0, template.Captures.Count).ToArray();
        foreach (var capture in captures)
        {
            frame.Bind(capture.Key, capture.Value with { Slot = frame.Allocate(), Self = null });
            frame.Facts = frame.Facts.SetItem(capture.Value.Identity, capture.Value.Info);
            if (caller.DeferredCaptures) InstallCaptureFacts(frame, capture.Value.Info);
        }
        frame.GlobalBindings = frame.Environment;
        for (int index = 0; index < arguments.Count; index++)
        {
            var parameter = template.Syntax.Parameters[index];
            var argument = arguments[index];
            var info = argument with { KnownBool = null, KnownConnection = null, KnownPath = null, KnownText = null, Element = ForgetEndpoint(argument.Element) };
            if (parameter.Type is not null)
            {
                CheckConcept(frame, info, parameter.Nullable ? parameter.Type with { Nullable = true } : parameter.Type, argumentExpressions?[index].Span ?? span);
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
            var origin = DeclarationOrigin(frame, new(parameter.Span.Start, parameter.Name.Length));
            var parameterConstraint = parameter.Type is null ? StorageType(info.Type, parameter.Nullable) : ResolveType(frame, parameter.Type);
            if (parameter.Nullable && parameterConstraint is not null) parameterConstraint = parameterConstraint.WithNull;
            frame.Bind(parameter.Name, new(frame.Allocate(), info, Origin: origin, AdmitsNull: parameter.Nullable || parameterConstraint?.Nullable == true,
                Constraint: parameterConstraint) { IsParameter = true });
            CaptureParameter(frame, new(parameter.Span.Start, parameter.Name.Length));
            if (parameter.Type is not null) AttachConcept(frame, frame.Environment[parameter.Name].Storage, parameter.Nullable ? parameter.Type with { Nullable = true } : parameter.Type);
            if (origin is not null) CaptureDefinition(frame, origin.Span, [origin]);
        }
        if (template.Syntax.Name is not null && !frame.Environment.ContainsKey(template.Syntax.Name)) frame.Bind(template.Syntax.Name, new(-1, new(TypeRef.Function, template), template,
            DeclarationOrigin(frame, template.Syntax.NameSpan)));
        if (caller.EditorReportErrors) editorCalledTemplates?.Add(template);
        CaptureContext(frame, template.Syntax.Body.Span.Start);
        Info result;
        activeEditorSpecializations?.Add(template.Syntax);
        activeSpecializations[template] = specialization;
        try
        {
            result = Block(frame, template.Syntax.Body);
            foreach (var returned in frame.Returns) result = Join(result, returned);
            if (template.Syntax.ReturnType is { } resultAnnotation) CheckConcept(frame, result, resultAnnotation, template.Syntax.Body.Span);
            if (asserted is not null)
            {
                CheckReturnConversion(frame, result.Type, asserted, template.Syntax.Body.Span);
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
        finally { activeEditorSpecializations?.Remove(template.Syntax); activeSpecializations.Remove(template); }
        frame.Emit(OpCode.Return);
        result = Current(frame, result);
        specialization.Result = result;
        specialization.Effects = frame.Facts.Where(pair => !caller.Facts.TryGetValue(pair.Key, out var previous) || !ReferenceEquals(previous, pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        functions[id] = new(template.Syntax.Name ?? $"<lambda{template.Id}>", arguments.Count, frame.Locals, frame.Code.ToArray(), result.Type,
            template.Captures.Count, specialization.ParameterTypes);
        return specialization;
    }

    private Info Call(Frame frame, CallExpr call)
    {
        Frame scratch = CloneFrame(frame);
        var result = CallEager(scratch, call);
        if (result.Type.Kind == TypeKind.Range && !result.EagerRange)
        {
            if (!scratch.DeferredCaptures)
            {
                scratch = CloneFrame(frame);
                scratch.DeferredCaptures = true;
                result = CallEager(scratch, call);
            }
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
            frame.Facts = scratch.Facts;
            MergeControlExits(frame, scratch);
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
            ApplyEffects(frame, specialization);
            var result = InstantiateResult(frame, specialization.Result, callee.Function, arguments, span);
            return RecordMutableCall(frame, callee, arguments, span, argumentExpressions, result);
        }
        if (callee.NativeName is not null)
        {
            var result = InvokeNative(frame, callee.NativeName, arguments, span, argumentExpressions);
            return RecordMutableCall(frame, callee, arguments, span, argumentExpressions, result);
        }
        if (callee.Alternatives is not null)
        {
            var dispatch = ResolveDispatch(frame, callee, arguments, span);
            frame.Emit(OpCode.Call, arguments.Count, text: dispatch.Mapping, span: span);
            return RecordMutableCall(frame, callee, arguments, span, argumentExpressions, dispatch.Result);
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
        ValidateNativeRange(frame, signature, arguments, span);
        Info? callbackResult = null;
        int callbackIndex = arguments.Count - 1;
        if (callbackIndex >= 1 && arguments[0].Type.Kind == TypeKind.Range && arguments[callbackIndex].Type == TypeRef.Function)
        {
            bool deferred = frame.DeferredCaptures;
            frame.DeferredCaptures |= signature.Result.Kind == TypeKind.Range;
            try { callbackResult = NativeCallback(frame, signature, arguments, span, argumentExpressions); }
            finally { frame.DeferredCaptures = deferred; }
        }
        var result = signature.ResultRule switch
        {
            NativeResultRule.FirstArgument when arguments.Count > 0 => arguments[0],
            NativeResultRule.RangeOfArguments => MakeRangeInfo(arguments),
            NativeResultRule.RangeItem when arguments.Count > 0 => arguments[0].Element ?? new(arguments[0].Type.Element ?? TypeRef.Any),
            NativeResultRule.Transform when callbackResult is not null => new(new(TypeKind.Range, callbackResult.Type), Element: callbackResult),
            _ => ExtendedNativeResult(frame, signature, arguments, callbackResult, span, argumentExpressions)
        };
        frame.Emit(OpCode.Call, arguments.Count, span: span);
        return result with { EagerRange = (signature.Behavior.GetValueOrDefault() & NativeBehavior.EagerRange) != 0 };
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
    // Scope/callable identities specialize storage shapes. Scalar
    // and Range-item literal values do not belong to the type-shared argument frame.
    private static Info? ForgetEndpoint(Info? info)
    {
        if (info is null) return null;
        var element = ForgetEndpoint(info.Element);
        return info.KnownConnection is null && info.KnownPath is null && info.KnownText is null && ReferenceEquals(info.Element, element) ? info
            : info with { KnownConnection = null, KnownPath = null, KnownText = null, Element = element };
    }
    private static Info CaptureInfo(Info info, bool projection = false, bool acceptedOnly = false) => info with
    {
        KnownBool = null,
        KnownConnection = projection ? info.KnownConnection : null,
        KnownPath = projection && (!acceptedOnly || info.KnownPath is { WorkingDirectory: not null }
            or { Connection.Kind: not ConnectionKind.Local } || info.KnownPath is { } path && System.IO.Path.IsPathFullyQualified(path.NativeText)) ? info.KnownPath : null,
        KnownText = projection ? info.KnownText : null,
        Element = projection ? info.Element : ForgetEndpoint(info.Element)
    };
    private static string InfoKey(Info info) => InfoKey(info, InfoKey);
    private static string InfoKey(Info info, Func<Info, string> nestedKey) => $"{info.Type}:{info.TypeValue}:{info.EagerRange}:{(info.Function is null ? "" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(info.Function))}:{info.NativeName}:{(info.Scope is null ? "" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(info.Scope))}:{string.Join(',', info.Alternatives?.Select(nestedKey) ?? [])}:{(info.Element is null ? "" : nestedKey(info.Element))}";


}
