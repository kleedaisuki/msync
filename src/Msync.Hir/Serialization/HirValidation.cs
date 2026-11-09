using Msync.Core;

namespace Msync.Hir;

public static class HirValidation
{
    public static void Validate(HirModule module)
    {
        Check(module);
        var entry = module.Functions[module.Entry.Value];
        if (entry.IsOpen || entry.Body.IsNone) Fail("entry must select a closed function body");
        ClosedBodies(module);
    }

    private static void Check(HirModule module)
    {
        Id(module.Entry.Value, module.Functions.Length, "entry function");
        foreach (var type in module.Types)
        {
            RuntimeType(type.RuntimeType);
            Optional(type.Shape, module.ScopeLayouts.Length, "type shape");
            Optional(type.Signature, module.Signatures.Length, "type signature");
            Optional(type.Element, module.Types.Length, "type element");
            Optional(type.Concept, module.Concepts.Length, "type concept");
            foreach (var alternative in type.Alternatives.OrEmpty()) Id(alternative.Value, module.Types.Length, "type alternative");
            if (type.TypeParameter < -1) Fail("type parameter identity is negative");
        }
        foreach (var location in module.Locations)
        {
            Id(location.StorageType.Value, module.Types.Length, "location storage type");
            Source(module, location.Source);
            if (!Enum.IsDefined(location.Kind)) Fail("unknown location kind");
            Optional(location.Owner.Value, module.Functions.Length, "location owner");
            if (location.TemplateOwner is { } template) Id(template.Value, module.Templates.Length, "location template owner");
        }
        foreach (var function in module.Functions)
        {
            if (function.IsOpen) Fail("finalized function table retains an open inference reservation");
            Id(function.Result.Value, module.Types.Length, "function result type");
            Id(function.Body.Value, module.Nodes.Length, "function body");
            foreach (var parameter in function.Parameters) Id(parameter.Value, module.Locations.Length, "function parameter");
            Captures(module, function.Captures);
            if (function.Template is { } template) Id(template.Value, module.Templates.Length, "function template");
        }
        foreach (var template in module.Templates)
        {
            Id(template.Body.Value, module.Nodes.Length, "template semantic body");
            Source(module, template.Source);
            foreach (var parameter in template.Parameters) Id(parameter.Value, module.Locations.Length, "template parameter");
            Captures(module, template.Captures);
            if (template.ResultAnnotation is { } result) Id(result.Value, module.Types.Length, "template result annotation");
            foreach (var type in template.TypeParameterTypes.OrEmpty()) Id(type.Value, module.Types.Length, "template type parameter");
            if (!template.TypeParameterTypes.IsDefaultOrEmpty && template.TypeParameterTypes.Length != template.TypeParameters.Length)
                Fail("template parameter names and type identities disagree");
        }
        foreach (var layout in module.ScopeLayouts)
            foreach (var field in layout.Fields) Id(field.StorageType.Value, module.Types.Length, "field storage type");
        foreach (var signature in module.Signatures)
        {
            Id(signature.Result.Value, module.Types.Length, "signature result");
            foreach (var parameter in signature.Parameters) Id(parameter.Value, module.Types.Length, "signature parameter");
        }
        var sessionDispatches = module.SessionDispatches.OrEmpty();
        for (int index = 0; index < sessionDispatches.Length; index++)
        {
            var dispatch = sessionDispatches[index];
            if (!module.IsSession) Fail("session dispatch requires a session module");
            if (dispatch.Slot != index) Fail("session dispatch slot disagrees with its stable session table position");
            foreach (var parameter in dispatch.Parameters) Id(parameter.Value, module.Types.Length, "session parameter type");
            Id(dispatch.Result.Value, module.Types.Length, "session result type");
            Targets(module, dispatch.Targets);
        }
        foreach (var call in module.Calls)
        {
            if (!Enum.IsDefined(call.Kind)) Fail("unknown call kind");
            if (call.Kind == HirCallKind.Function && call.SessionSlot < 0) ClosedFunction(module, call.Function.Value, "call target");
            else Optional(call.Function.Value, module.Functions.Length, "call function");
            if (call.Kind == HirCallKind.Native && string.IsNullOrEmpty(call.NativeName)) Fail("native call has no native identity");
            Targets(module, call.Targets);
            SessionSlot(call.SessionSlot, sessionDispatches.Length);
        }
        foreach (var closure in module.Closures)
        {
            Id(closure.Function.Value, module.Functions.Length, "closure function");
            Captures(module, closure.Captures);
        }
        foreach (var closure in module.TemplateClosures)
        {
            Id(closure.Template.Value, module.Templates.Length, "closure template");
            Captures(module, closure.Captures);
        }
        HashSet<HirAllocationSiteId> sites = [];
        foreach (var scope in module.Scopes)
        {
            if (scope.Site is not { Value: >= 0 } site || !sites.Add(site))
                Fail("scope construction has a missing or duplicate allocation-site identity");
            Id(scope.Layout, module.ScopeLayouts.Length, "constructed scope layout");
            if (scope.Fields.Length != module.ScopeLayouts[scope.Layout].Fields.Length) Fail("scope field locations disagree with its layout");
            foreach (var field in scope.Fields) Id(field.Value, module.Locations.Length, "scope field location");
            if (scope.Self is { } self) Id(self.Value, module.Locations.Length, "scope self location");
            if (scope.Singleton < -1) Fail("scope singleton identity is negative");
        }
        foreach (var loop in module.Fors)
        {
            if (loop.Loop.Value < 0) Fail("for loop identity is negative");
            Id(loop.Item.Value, module.Locations.Length, "for item location");
        }
        foreach (var match in module.Matches)
            foreach (var arm in match.Arms)
            {
                Id(arm.Value.Value, module.Nodes.Length, "match arm value");
                if (!Enum.IsDefined(arm.Pattern.Kind)) Fail("unknown match pattern kind");
                if (arm.Pattern.Kind == HirPatternKind.Literal) Id(arm.Pattern.Data, module.Literals.Length, "match literal");
                if (arm.Pattern.Kind == HirPatternKind.Type) Id(arm.Pattern.Data, module.Types.Length, "match type");
                if (arm.Pattern.Kind == HirPatternKind.Regex && arm.Pattern.Text is null) Fail("regex pattern text is missing");
                if (arm.Pattern.Binding is { } binding) Id(binding.Value, module.Locations.Length, "match binding");
            }
        foreach (var adaptation in module.Adaptations)
        {
            Targets(module, adaptation.Targets);
            SessionSlot(adaptation.SessionSlot, sessionDispatches.Length);
        }
        foreach (var concept in module.Concepts)
        {
            Source(module, concept.Source);
            foreach (int parent in concept.Bases) Id(parent, module.Concepts.Length, "base concept");
            foreach (var type in concept.TypeParameters) Id(type.Value, module.Types.Length, "associated type");
            foreach (var member in concept.Members)
            {
                Id(member.Type.Value, module.Types.Length, "concept member type");
                Optional(member.Signature, module.Signatures.Length, "concept member signature");
                Source(module, member.Source);
            }
        }
        foreach (var literal in module.Literals)
        {
            if (!Enum.IsDefined(literal.Kind)) Fail("unknown literal kind");
            if (!Enum.IsDefined(literal.ConnectionKind)) Fail("unknown connection kind");
            if (literal.Kind is HirLiteralKind.String or HirLiteralKind.Path or HirLiteralKind.Connection && literal.Text is null)
                Fail("text literal payload is missing");
            if (literal.Type is { } type) RuntimeType(type);
            else if (literal.Kind == HirLiteralKind.Type) Fail("Type literal payload is missing");
        }
        foreach (var export in module.Exports) Id(export.Location.Value, module.Locations.Length, "export location");
        foreach (var import in module.UnresolvedImports.OrEmpty()) Id(import.Value, module.Locations.Length, "unresolved imported location");
        foreach (var edge in module.Edges) Id(edge.Value, module.Nodes.Length, "node edge");
        foreach (var node in module.Nodes) Node(module, node);
        AcyclicNodes(module);
    }

    private static void SessionSlot(int slot, int count)
    {
        if (slot < -1 || slot >= count) Fail("session dispatch slot is unresolved");
    }

    private static void Node(HirModule module, HirNode node)
    {
        if (!Enum.IsDefined(node.Kind)) Fail("unknown semantic node kind");
        if ((node.Effects & ~(HirEffects.ReadLocation | HirEffects.WriteLocation | HirEffects.ReadMemory | HirEffects.WriteMemory
            | HirEffects.ReadExternal | HirEffects.WriteExternal | HirEffects.Invoke | HirEffects.Enumerate
            | HirEffects.MayFail | HirEffects.Synchronize | HirEffects.Spawn)) != 0) Fail("unknown semantic effect flags");
        Id(node.Type.Value, module.Types.Length, "node type");
        Source(module, node.Source);
        if (node.Children.Start < 0 || node.Children.Count < 0 || node.Children.Start > module.Edges.Length - node.Children.Count)
            Fail("node child range is outside the edge arena");
        int count = node.Children.Count;
        switch (node.Kind)
        {
            case HirKind.Literal: Id(node.Data, module.Literals.Length, "literal node payload"); Arity(count, 0, 0); break;
            case HirKind.TypeValue: Id(node.Data, module.Types.Length, "type value payload"); Arity(count, 0, 0); break;
            case HirKind.NativeFunction or HirKind.Builtin:
                if (node.Text is null) Fail("named node payload is missing"); Arity(count, 0, 0); break;
            case HirKind.ReadLocation or HirKind.ReadStorage: Id(node.Data, module.Locations.Length, "read location"); Arity(count, 0, 0); break;
            case HirKind.Declare or HirKind.Assign: Id(node.Data, module.Locations.Length, "assigned location"); Arity(count, 1, 1); break;
            case HirKind.Discard or HirKind.IsNull or HirKind.DeferRange: Arity(count, 1, 1); break;
            case HirKind.If: Arity(count, 3, 3); break;
            case HirKind.Match: Id(node.Data, module.Matches.Length, "match node"); Arity(count, 1, 1); break;
            case HirKind.For: Id(node.Data, module.Fors.Length, "for node"); Arity(count, 2, 2); break;
            case HirKind.Loop: if (node.Data < 0) Fail("loop identity is negative"); Arity(count, 1, 1); break;
            case HirKind.Break: if (node.Data < 0) Fail("break loop identity is negative"); Arity(count, 0, 1); break;
            case HirKind.Return: Arity(count, 0, 1); break;
            case HirKind.Unary: if (!Enum.IsDefined((HirUnaryOperator)node.Data)) Fail("unknown unary operator"); Arity(count, 1, 1); break;
            case HirKind.Binary: if (!Enum.IsDefined((HirBinaryOperator)node.Data)) Fail("unknown binary operator"); Arity(count, 2, 2); break;
            case HirKind.Coerce or HirKind.Convert: Id(node.Data, module.Types.Length, "conversion target"); Arity(count, 1, 1); break;
            case HirKind.IsType:
                Arity(count, 1, 2);
                if (count == 1 || node.Data >= 0) Id(node.Data, module.Types.Length, "type test target");
                else if (node.Data != -1) Fail("dynamic type test has an invalid target tag");
                break;
            case HirKind.TypeOf: Arity(count, 1, 1); break;
            case HirKind.RegexMatch or HirKind.SelectPath: if (node.Text is null) Fail("pattern text is missing"); Arity(count, 1, 1); break;
            case HirKind.Call: Id(node.Data, module.Calls.Length, "call node"); Arity(count, 1, int.MaxValue); break;
            case HirKind.Closure: Id(node.Data, module.Closures.Length, "closure node"); Arity(count, 0, 0); break;
            case HirKind.TemplateClosure: Id(node.Data, module.TemplateClosures.Length, "template closure node"); Arity(count, 0, 0); break;
            case HirKind.AdaptFunction: Id(node.Data, module.Adaptations.Length, "function adaptation"); Arity(count, 1, 1); break;
            case HirKind.Scope:
                Id(node.Data, module.Scopes.Length, "scope node");
                int fields = module.Scopes[node.Data].Fields.Length; Arity(count, fields, fields + 1); break;
            case HirKind.MemberRead: if (node.Text is null) Fail("member name is missing"); Arity(count, 1, 1); break;
            case HirKind.MemberAssign: if (node.Text is null) Fail("member name is missing"); Arity(count, 2, 2); break;
            case HirKind.Range or HirKind.LazyRange:
                Id(node.Data, module.Ranges.Length, "range node");
                if (!module.Ranges[node.Data].Deferred.IsDefaultOrEmpty && module.Ranges[node.Data].Deferred.Length != count)
                    Fail("range item modes disagree with the item expressions");
                break;
            case HirKind.Qualify or HirKind.PathJoin or HirKind.Transfer: Arity(count, 2, 2); break;
            case HirKind.Barrier: Id(node.Data, module.Barriers.Length, "barrier node"); Arity(count, 0, 1); break;
        }
    }

    private static void Captures(HirModule module, System.Collections.Immutable.ImmutableArray<HirCapture> captures)
    {
        foreach (var capture in captures.OrEmpty())
        {
            Id(capture.Target.Value, module.Locations.Length, "capture target");
            if (!Enum.IsDefined(capture.Mode)) Fail("unknown capture mode");
            if (capture.Mode == HirCaptureMode.Location) Id(capture.SourceLocation.Value, module.Locations.Length, "captured location");
            else Id(capture.SourceValue.Value, module.Nodes.Length, "captured value expression");
        }
    }

    private static void Targets(HirModule module, System.Collections.Immutable.ImmutableArray<HirDispatchTarget> targets)
    {
        foreach (var target in targets.OrEmpty())
        {
            Id(target.Identity.Value, target.Identity.IsTemplate ? module.Templates.Length : module.Functions.Length, "dispatch identity");
            ClosedFunction(module, target.Target.Value, "dispatch target");
        }
    }

    private static void ClosedFunction(HirModule module, int id, string name)
    {
        Id(id, module.Functions.Length, name);
        if (module.Functions[id].IsOpen) Fail(name + " is an open function identity");
    }

    private static void Source(HirModule module, HirSourceRange span)
    {
        if (span.Source.Value == -1 && span.Start == 0 && span.Length == 0) return;
        Id(span.Source.Value, module.Sources.Length, "source identity");
        if (span.Start < 0 || span.Length < 0) Fail("source range is negative");
        if (module.Sources[span.Source.Value].Text is { } text && span.Start > text.Length - span.Length)
            Fail("source range exceeds its owning source text");
    }

    private static void RuntimeType(TypeRef type)
    {
        for (TypeRef? current = type; current is not null; current = current.Element)
            if ((current.Kind & ~TypeKind.Any) != 0) Fail("unknown runtime type flags");
    }

    private static void Arity(int actual, int minimum, int maximum)
    {
        if (actual < minimum || actual > maximum) Fail($"semantic node expects {minimum}..{maximum} children, found {actual}");
    }

    private static void Optional(int id, int count, string name) { if (id != -1) Id(id, count, name); }
    private static void Id(int id, int count, string name) { if ((uint)id >= (uint)count) Fail($"{name} {id} is outside its arena of {count}"); }
    private static void Fail(string message) => throw new MsyncException("Invalid typed HIR: " + message + ".");

    private static void AcyclicNodes(HirModule module)
    {
        byte[] state = new byte[module.Nodes.Length];
        Stack<(int Node, bool Exit)> pending = [];
        for (int root = 0; root < state.Length; root++)
        {
            if (state[root] != 0) continue;
            pending.Push((root, false));
            while (pending.TryPop(out var item))
            {
                if (item.Exit) { state[item.Node] = 2; continue; }
                if (state[item.Node] == 1) Fail("semantic expression children contain a cycle");
                if (state[item.Node] == 2) continue;
                state[item.Node] = 1;
                pending.Push((item.Node, true));
                HirNode node = module.Nodes[item.Node];
                foreach (var child in module.Children(node)) pending.Push((child.Value, false));
                if (node.Kind == HirKind.Match)
                    foreach (var arm in module.Matches[node.Data].Arms) pending.Push((arm.Value.Value, false));
                if (node.Kind is HirKind.Closure or HirKind.TemplateClosure)
                {
                    var captures = node.Kind == HirKind.Closure ? module.Closures[node.Data].Captures
                        : module.TemplateClosures[node.Data].Captures;
                    foreach (var capture in captures.OrEmpty())
                        if (capture.Mode == HirCaptureMode.Value) pending.Push((capture.SourceValue.Value, false));
                }
            }
        }
    }

    private static void ClosedBodies(HirModule module)
    {
        bool[] visited = new bool[module.Nodes.Length];
        Stack<HirNodeId> pending = [];
        foreach (var function in module.Functions)
        {
            if (function.IsOpen) continue;
            pending.Push(function.Body);
            while (pending.TryPop(out var id))
            {
                if (visited[id.Value]) continue;
                visited[id.Value] = true;
                var node = module.Node(id);
                if (node.Kind is HirKind.Error or HirKind.TypeOf)
                    Fail($"closed function retains unresolved semantic {node.Kind}");
                if (node.Kind == HirKind.IsType && node.Children.Count == 2
                    && module.RuntimeType(module.Node(module.Children(node)[1]).Type) != Msync.Core.TypeRef.Type)
                    Fail("dynamic type-test target must have Type type");
                foreach (var child in module.Children(node)) pending.Push(child);
                if (node.Kind == HirKind.Match)
                    foreach (var arm in module.Matches[node.Data].Arms) pending.Push(arm.Value);
                if (node.Kind is HirKind.Closure or HirKind.TemplateClosure)
                {
                    var captures = node.Kind == HirKind.Closure ? module.Closures[node.Data].Captures
                        : module.TemplateClosures[node.Data].Captures;
                    foreach (var capture in captures.OrEmpty())
                        if (capture.Mode == HirCaptureMode.Value) pending.Push(capture.SourceValue);
                }
            }
        }
    }
}

internal static class HirArrayExtensions
{
    internal static System.Collections.Immutable.ImmutableArray<T> OrEmpty<T>(this System.Collections.Immutable.ImmutableArray<T> value) =>
        value.IsDefault ? [] : value;
}
