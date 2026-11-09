using System.Globalization;
using Msync.Core;

namespace Msync.Hir;

public static class HirLowerer
{
    public static HirLoweringResult Lower(HirModule module, HirLoweringOptions? options = null,
        HirLoweringSession? session = null)
    {
        if (!module.UnresolvedImports.IsDefaultOrEmpty)
        {
            var unresolved = module.UnresolvedImports.Where(id => session is null
                || !session.Locations.ContainsKey(new(module.Locations[id.Value].Owner, id))).ToArray();
            if (unresolved.Length != 0)
                throw new InvalidOperationException("HIR requires an active session containing imports: "
                    + string.Join(", ", unresolved.Select(id => module.Locations[id.Value].Name)) + ".");
        }
        return new Lowering(module, options ?? new(), session ?? new()).Run();
    }

    private sealed class Lowering(HirModule hir, HirLoweringOptions options, HirLoweringSession layout)
    {
        private readonly List<Value> constants = [];
        private readonly Dictionary<Value, int> constantIndices = new(ValueRepresentation.Comparer);
        private readonly Dictionary<HirFunctionId, FunctionCode> functions = [];
        private readonly List<HirLocationId>?[] locations = new List<HirLocationId>?[hir.Functions.Length];
        private readonly HashSet<HirLocationId>? sessionRoots = hir.IsSession
            ? hir.Exports.Select(export => export.Location).ToHashSet() : null;

        internal HirLoweringResult Run()
        {
            ReserveCallables();
            for (int index = 0; index < hir.Locations.Length; index++)
            {
                var location = hir.Locations[index];
                if (location.Owner.Value < 0 || location.TemplateOwner is not null) continue;
                (locations[location.Owner.Value] ??= []).Add(new(index));
            }
            for (int index = 0; index < hir.Functions.Length; index++)
            {
                HirFunctionId id = new(index);
                functions.Add(id, new FunctionLowering(this, id, hir.Functions[index]).Run());
            }
            FunctionCode[] code = new FunctionCode[layout.NextCallable];
            foreach (var (id, function) in functions) code[layout.Functions[id]] = function;
            for (int index = 0; index < hir.Templates.Length; index++)
            {
                HirTemplateId id = new(index);
                var template = hir.Templates[index];
                code[layout.Templates[id]] = new(template.Name, template.Parameters.Length,
                    template.Parameters.Length + (template.Captures.IsDefault ? 0 : template.Captures.Length),
                    [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null,
                    template.Captures.IsDefault ? 0 : template.Captures.Length);
            }
            if (code.Any(function => function is null))
                throw new InvalidOperationException("A persistent HIR module must retain its prior callable definitions.");
            var result = new Module(constants.ToArray(), code, layout.Functions[hir.Entry],
                OptimizationLevel: options.OptimizationLevel, PreserveLocals: hir.IsSession || options.PreserveLocals)
            {
                SessionRoots = hir.IsSession ? hir.Exports.Select(export => Slot(hir.Entry, export.Location)).Distinct().ToArray() : null
            };
            return new(result, options.RetainLayout ? layout.Layout : HirLoweringLayout.Empty);
        }

        private void ReserveCallables()
        {
            for (int index = 0; index < hir.Functions.Length; index++)
            {
                HirFunctionId id = new(index);
                if (layout.Functions.ContainsKey(id)) continue;
                int slot = layout.NextCallable++;
                layout.Functions.Add(id, slot); layout.Callables.Add(slot, HirCallableId.From(id));
            }
            for (int index = 0; index < hir.Templates.Length; index++)
            {
                HirTemplateId id = new(index);
                if (layout.Templates.ContainsKey(id)) continue;
                int slot = layout.NextCallable++;
                layout.Templates.Add(id, slot); layout.Callables.Add(slot, HirCallableId.From(id));
            }
        }

        internal int Constant(Value value)
        {
            if (constantIndices.TryGetValue(value, out int index)) return index;
            index = constants.Count; constants.Add(value); constantIndices.Add(value, index); return index;
        }
        internal int Callable(HirCallableId id) => id.IsTemplate
            ? layout.Templates[new(id.Value)] : layout.Functions[new(id.Value)];
        internal int Function(HirFunctionId id) => layout.Functions[id];
        internal int Slot(HirFunctionId function, HirLocationId location) => layout.Locations[new(function, location)];
        internal bool SessionRoot(HirLocationId location) => sessionRoots?.Contains(location) == true;
        internal int Site(int construction)
        {
            if (layout.AllocationSites.TryGetValue(construction, out int site)) return site;
            int singleton = hir.Scopes[construction].Singleton;
            if (singleton < 0 || !layout.SingletonSites.TryGetValue(singleton, out site))
            {
                site = layout.NextAllocation++;
                if (singleton >= 0) layout.SingletonSites.Add(singleton, site);
            }
            layout.AllocationSites.Add(construction, site); return site;
        }
        internal Core.SourceSpan? Source(HirSourceRange range) => range.Source.Value < 0 ? null
            : new(hir.Sources[range.Source.Value].Name, range.Start, range.Length);
        internal TypeRef Type(HirTypeId id) => hir.RuntimeType(id);
        internal HirModule Hir => hir;
        internal HirLoweringSession Layout => layout;
        internal IReadOnlyList<HirLocationId> Locations(HirFunctionId function) => locations[function.Value] ?? (IReadOnlyList<HirLocationId>)Array.Empty<HirLocationId>();
    }

    private sealed class FunctionLowering(Lowering owner, HirFunctionId id, HirFunction function)
    {
        private readonly List<Instruction> code = [];
        private readonly List<LoopState> loops = [];
        private readonly List<int> iterators = [];
        private readonly List<string> acquireRegions = [];
        private int locals;
        private int nextRegion;
        private HirModule Hir => owner.Hir;
        private sealed record LoopState(HirLoopId Id, bool For, int IteratorDepth, int AcquireDepth)
        {
            public List<int> Breaks { get; } = [];
        }

        internal FunctionCode Run()
        {
            PrepareLocations();
            if (function.IsOpen)
                throw new InvalidOperationException($"Open function '{function.Name}' is not a finalized callable body.");
            Emit(function.Body);
            Add(OpCode.Return);
            owner.Layout.LocalCounts[id] = locals;
            return new(function.Name, function.Parameters.Length, locals, code.ToArray(), owner.Type(function.Result),
                function.Captures.Length, function.Parameters.Select(parameter => owner.Type(Hir.Locations[parameter.Value].StorageType)).ToArray());
        }

        private void PrepareLocations()
        {
            locals = owner.Layout.LocalCounts.GetValueOrDefault(id);
            for (int index = 0; index < function.Captures.Length; index++) FixedSlot(function.Captures[index].Target, index);
            for (int index = 0; index < function.Parameters.Length; index++) FixedSlot(function.Parameters[index], function.Captures.Length + index);
            foreach (var locationId in owner.Locations(id))
            {
                var key = new HirLocationSlot(id, locationId);
                if (owner.Layout.Locations.ContainsKey(key)) continue;
                owner.Layout.Locations.Add(key, locals++);
            }
        }
        private void FixedSlot(HirLocationId location, int slot)
        {
            var key = new HirLocationSlot(id, location);
            if (owner.Layout.Locations.TryGetValue(key, out int previous) && previous != slot)
                throw new InvalidOperationException("A persistent callable changed its capture/parameter location layout.");
            owner.Layout.Locations[key] = slot; locals = Math.Max(locals, slot + 1);
        }
        private int Slot(int location) => owner.Slot(id, new(location));
        private int Temporary() => locals++;
        private int Add(OpCode op, int a = 0, int b = 0, string? text = null, HirSourceRange? source = null)
        {
            code.Add(new(op, a, b, text, source is { } range ? owner.Source(range) : null)); return code.Count - 1;
        }
        private void Patch(int at, int target) => code[at] = code[at] with { A = target };
        private HirNodeId Child(HirNode node, int index) => Hir.Edges[node.Children.Start + index];
        private void AllChildren(HirNode node)
        {
            foreach (var child in Hir.Children(node)) Emit(child);
        }

        private void Emit(HirNodeId expression)
        {
            if (expression.IsNone) { Add(OpCode.Null); return; }
            var node = Hir.Node(expression);
            switch (node.Kind)
            {
                case HirKind.Error: throw new InvalidOperationException("An erroneous semantic body cannot be lowered.");
                case HirKind.Literal: Literal(Hir.Literals[node.Data], node.Source); break;
                case HirKind.TypeValue: Add(OpCode.Constant, owner.Constant(new TypeValue(owner.Type(new(node.Data)))), source: node.Source); break;
                case HirKind.NativeFunction: Add(OpCode.Constant, owner.Constant(new FunctionValue(-1, NativeName: node.Text!)), source: node.Source); break;
                case HirKind.Builtin: Add(OpCode.LoadBuiltin, text: node.Text, source: node.Source); break;
                case HirKind.ReadLocation: Add(OpCode.LoadLocal, Slot(node.Data), source: node.Source); break;
                case HirKind.ReadStorage: Add(OpCode.LoadStorage, Slot(node.Data), source: node.Source); break;
                case HirKind.Declare: Declare(node); break;
                case HirKind.Assign: Emit(Child(node, 0)); Add(OpCode.StoreLocal, Slot(node.Data), source: node.Source); Add(OpCode.Null); break;
                case HirKind.Discard: Emit(Child(node, 0)); Add(OpCode.Pop); Add(OpCode.Null); break;
                case HirKind.Block: Block(node); break;
                case HirKind.If: Conditional(node); break;
                case HirKind.Match: Match(node); break;
                case HirKind.For: For(node); break;
                case HirKind.Loop: Loop(node); break;
                case HirKind.Break: Break(node); break;
                case HirKind.Return: Return(node); break;
                case HirKind.Unary: Unary(node); break;
                case HirKind.Binary: Binary(node); break;
                case HirKind.Coerce: case HirKind.Convert: Conversion(node); break;
                case HirKind.IsNull: Emit(Child(node, 0)); Add(OpCode.IsNull, source: node.Source); break;
                case HirKind.IsType: Emit(Child(node, 0)); Add(OpCode.Constant, owner.Constant(new TypeValue(owner.Type(new(node.Data))))); Add(OpCode.IsType, source: node.Source); break;
                case HirKind.RegexMatch: Emit(Child(node, 0)); Add(OpCode.RegexMatch, text: node.Text, source: node.Source); break;
                case HirKind.Call: Call(node); break;
                case HirKind.Closure: Closure(Hir.Closures[node.Data], node.Source); break;
                case HirKind.TemplateClosure: TemplateClosure(Hir.TemplateClosures[node.Data], node.Source); break;
                case HirKind.AdaptFunction: Adapt(node); break;
                case HirKind.Scope: Scope(node); break;
                case HirKind.MemberRead: Emit(Child(node, 0)); Add(OpCode.GetMember, Math.Max(0, node.Data + 1), text: node.Text, source: node.Source); break;
                case HirKind.MemberAssign: AllChildren(node); Add(OpCode.SetMember, Math.Max(0, node.Data + 1), text: node.Text, source: node.Source); break;
                case HirKind.Range: AllChildren(node); Add(OpCode.MakeRange, node.Children.Count, source: node.Source); break;
                case HirKind.LazyRange: LazyRange(node); break;
                case HirKind.DeferRange: Emit(Child(node, 0)); Add(OpCode.DeferRange, source: node.Source); break;
                case HirKind.Qualify: Qualify(node); break;
                case HirKind.PathJoin: AllChildren(node); Add(OpCode.PathJoin, source: node.Source); break;
                case HirKind.SelectPath: Emit(Child(node, 0)); Add(OpCode.SelectPath, text: node.Text, source: node.Source); break;
                case HirKind.Transfer: AllChildren(node); Add(OpCode.Transfer, source: node.Source); break;
                case HirKind.Barrier: Barrier(node); break;
                default: throw new InvalidOperationException($"Unsupported semantic node {node.Kind}.");
            }
        }
        private void Literal(HirLiteral literal, HirSourceRange source)
        {
            switch (literal.Kind)
            {
                case HirLiteralKind.Null: Add(OpCode.Null, source: source); return;
                case HirLiteralKind.Bool: Add(literal.Boolean ? OpCode.True : OpCode.False, source: source); return;
            }
            Value value = literal.Kind switch
            {
                HirLiteralKind.Number => new NumberValue(literal.Number),
                HirLiteralKind.String => new StringValue(literal.Text!),
                HirLiteralKind.Path => new PathValue(literal.Text!),
                HirLiteralKind.Connection => new ConnectionValue(literal.ConnectionKind, literal.Text ?? ""),
                HirLiteralKind.Type => new TypeValue(literal.Type!),
                _ => throw new InvalidOperationException($"Invalid literal kind {literal.Kind}.")
            };
            Add(OpCode.Constant, owner.Constant(value), source: source);
        }
        private void Declare(HirNode node)
        {
            // Session roots already own uninitialized storage. Capturing it is legal,
            // but only the completed initializer may publish a value into that cell.
            if (!owner.SessionRoot(new(node.Data)))
            {
                Add(OpCode.Null); Add(OpCode.InitializeLocal, Slot(node.Data), source: node.Source);
            }
            Emit(Child(node, 0)); Add(OpCode.StoreLocal, Slot(node.Data), source: node.Source); Add(OpCode.Null);
        }
        private void Block(HirNode node)
        {
            if (node.Children.Count == 0) { Add(OpCode.Null, source: node.Source); return; }
            for (int index = 0; index < node.Children.Count; index++)
            {
                Emit(Child(node, index));
                if (index + 1 < node.Children.Count) Add(OpCode.Pop);
            }
        }
        private void Conditional(HirNode node)
        {
            Emit(Child(node, 0)); int otherwise = Add(OpCode.JumpIfFalse, source: node.Source);
            Emit(Child(node, 1)); int done = Add(OpCode.Jump);
            Patch(otherwise, code.Count); Emit(Child(node, 2)); Patch(done, code.Count);
        }
        private void Unary(HirNode node)
        {
            Emit(Child(node, 0));
            Add((HirUnaryOperator)node.Data == HirUnaryOperator.Not ? OpCode.Not : OpCode.Negate, source: node.Source);
        }
        private void Binary(HirNode node)
        {
            var operation = (HirBinaryOperator)node.Data;
            Emit(Child(node, 0));
            if (operation is HirBinaryOperator.And or HirBinaryOperator.Or)
            {
                Add(OpCode.Dup);
                int skip = Add(operation == HirBinaryOperator.And ? OpCode.JumpIfFalse : OpCode.JumpIfTrue);
                Add(OpCode.Pop); Emit(Child(node, 1)); Patch(skip, code.Count); return;
            }
            Emit(Child(node, 1));
            if (operation == HirBinaryOperator.Power) { Add(OpCode.NativeCall, 2, text: "math.pow", source: node.Source); return; }
            Add(operation switch
            {
                HirBinaryOperator.Add => OpCode.Add, HirBinaryOperator.Subtract => OpCode.Subtract,
                HirBinaryOperator.Multiply => OpCode.Multiply, HirBinaryOperator.Divide => OpCode.Divide,
                HirBinaryOperator.Remainder => OpCode.Remainder, HirBinaryOperator.Equal => OpCode.Equal,
                HirBinaryOperator.NotEqual => OpCode.NotEqual, HirBinaryOperator.Less => OpCode.Less,
                HirBinaryOperator.LessEqual => OpCode.LessEqual, HirBinaryOperator.Greater => OpCode.Greater,
                HirBinaryOperator.GreaterEqual => OpCode.GreaterEqual,
                _ => throw new InvalidOperationException($"Invalid binary operation {operation}.")
            }, source: node.Source);
        }
        private void Conversion(HirNode node)
        {
            Emit(Child(node, 0));
            var target = owner.Type(new(node.Data));
            if (node.Kind == HirKind.Coerce) Add(OpCode.Convert, text: target.WithoutNull.Kind.ToString(), source: node.Source);
            else { Add(OpCode.Constant, owner.Constant(new TypeValue(target))); Add(OpCode.NativeCall, 2, text: "cast", source: node.Source); }
        }
        private void Call(HirNode node)
        {
            var call = Hir.Calls[node.Data];
            Emit(Child(node, 0));
            if (call.Kind == HirCallKind.Native) Add(OpCode.Pop);
            for (int index = 1; index < node.Children.Count; index++) Emit(Child(node, index));
            int count = node.Children.Count - 1;
            if (call.Kind == HirCallKind.Native) Add(OpCode.NativeCall, count, text: call.NativeName, source: node.Source);
            else if (call.Kind == HirCallKind.Path) Add(OpCode.ProcessCall, count, call.Scheduling, source: node.Source);
            else Add(OpCode.Call, count, call.Kind == HirCallKind.Function ? owner.Function(call.Function) + 1 : 0,
                text: call.Targets.IsDefaultOrEmpty ? null : Dispatch(call.Targets), source: node.Source);
        }
        private string Dispatch(System.Collections.Immutable.ImmutableArray<HirDispatchTarget> targets) =>
            string.Join(';', targets.Select(target => owner.Callable(target.Identity).ToString(CultureInfo.InvariantCulture)
                + ":" + owner.Function(target.Target).ToString(CultureInfo.InvariantCulture)));
        private void Capture(HirCapture capture)
        {
            if (capture.Mode == HirCaptureMode.Location) Add(OpCode.LoadCell, Slot(capture.SourceLocation.Value));
            else Emit(capture.SourceValue);
        }
        private void Closure(HirClosure closure, HirSourceRange source)
        {
            foreach (var capture in closure.Captures) Capture(capture);
            Add(OpCode.MakeClosure, owner.Function(closure.Function), closure.Captures.Length, source: source);
        }
        private void TemplateClosure(HirTemplateClosure closure, HirSourceRange source)
        {
            foreach (var capture in closure.Captures) Capture(capture);
            Add(OpCode.MakeClosure, owner.Layout.Templates[closure.Template], closure.Captures.Length, source: source);
        }
        private void Adapt(HirNode node)
        {
            Emit(Child(node, 0)); var targets = Hir.Adaptations[node.Data].Targets;
            if (targets.Length == 1) Add(OpCode.SpecializeClosure, owner.Function(targets[0].Target), source: node.Source);
            else Add(OpCode.SpecializeClosure, text: Dispatch(targets), source: node.Source);
        }
        private void Scope(HirNode node)
        {
            var scope = Hir.Scopes[node.Data]; int site = owner.Site(node.Data);
            if (scope.Self is { } self && !owner.SessionRoot(self))
            { Add(OpCode.Null); Add(OpCode.InitializeLocal, Slot(self.Value)); }
            int initialized = -1;
            if (scope.Singleton >= 0)
            {
                Add(OpCode.GetSingleton, site, source: node.Source); Add(OpCode.Dup); Add(OpCode.IsNull);
                initialized = Add(OpCode.JumpIfFalse); Add(OpCode.Pop);
            }
            foreach (var initializer in Hir.Children(node)) { Emit(initializer); Add(OpCode.Pop); }
            foreach (var field in scope.Fields) Add(OpCode.LoadStorage, Slot(field.Value));
            Add(OpCode.MakeScope, scope.Fields.Length, site,
                string.Join('\0', Hir.ScopeLayouts[scope.Layout].Fields.Select(field => field.Name)), node.Source);
            if (scope.Singleton >= 0) { Add(OpCode.SetSingleton, site, source: node.Source); Patch(initialized, code.Count); }
            if (scope.Self is { } target) { Add(OpCode.Dup); Add(OpCode.StoreLocal, Slot(target.Value)); }
        }
        private void LazyRange(HirNode node)
        {
            AllChildren(node);
            var deferred = Hir.Ranges[node.Data].Deferred;
            var itemType = owner.Type(node.Type).Element ?? TypeRef.Any;
            Add(OpCode.MakeLazyRange, node.Children.Count, owner.Constant(new TypeValue(itemType)) + 1,
                new string(deferred.Select(item => item ? '1' : '0').ToArray()), node.Source);
        }
        private void Qualify(HirNode node)
        {
            Emit(Child(node, 0)); int path = Temporary(); Add(OpCode.InitializeLocal, path);
            Emit(Child(node, 1)); Add(OpCode.LoadLocal, path); Add(OpCode.Qualify, source: node.Source);
        }
        private void For(HirNode node)
        {
            var loop = Hir.Fors[node.Data];
            Emit(Child(node, 0)); Add(OpCode.GetIterator); int iterator = Temporary(); Add(OpCode.InitializeLocal, iterator);
            int begin = code.Count; int next = Add(OpCode.IteratorNext, iterator, source: node.Source);
            var context = new LoopState(loop.Loop, true, iterators.Count, acquireRegions.Count);
            loops.Add(context); iterators.Add(iterator);
            Add(OpCode.InitializeLocal, Slot(loop.Item.Value)); Emit(Child(node, 1)); Add(OpCode.Pop); Add(OpCode.Jump, begin);
            int end = code.Count; code[next] = code[next] with { B = end };
            FinishLoop(context, end); Add(OpCode.Null, source: node.Source);
        }
        private void Loop(HirNode node)
        {
            var context = new LoopState(new(node.Data), false, iterators.Count, acquireRegions.Count);
            loops.Add(context); int begin = code.Count;
            Emit(Child(node, 0)); Add(OpCode.Pop); Add(OpCode.Jump, begin);
            FinishLoop(context, code.Count);
        }
        private void FinishLoop(LoopState loop, int end)
        {
            foreach (int branch in loop.Breaks) Patch(branch, end);
            loops.RemoveAt(loops.Count - 1);
            iterators.RemoveRange(loop.IteratorDepth, iterators.Count - loop.IteratorDepth);
        }
        private void Break(HirNode node)
        {
            var loop = loops.Last(loop => loop.Id.Value == node.Data);
            if (!loop.For) { if (node.Children.Count == 0) Add(OpCode.Null); else Emit(Child(node, 0)); }
            CloseIterators(loop.IteratorDepth); CloseAcquire(loop.AcquireDepth);
            loop.Breaks.Add(Add(OpCode.Jump, source: node.Source)); Add(OpCode.Null);
        }
        private void Return(HirNode node)
        {
            if (node.Children.Count == 0) Add(OpCode.Null); else Emit(Child(node, 0));
            CloseIterators(0); CloseAcquire(0); Add(OpCode.Return, source: node.Source); Add(OpCode.Null);
        }
        private void CloseIterators(int depth)
        {
            for (int index = iterators.Count - 1; index >= depth; index--)
            { Add(OpCode.LoadLocal, iterators[index]); Add(OpCode.NativeCall, 1, text: "_core.iterator_close"); Add(OpCode.Pop); }
        }
        private void CloseAcquire(int depth)
        {
            for (int index = acquireRegions.Count - 1; index >= depth; index--)
                Add(OpCode.Barrier, 2, text: acquireRegions[index]);
        }
        private void Barrier(HirNode node)
        {
            var barrier = Hir.Barriers[node.Data];
            if (barrier.Release) Add(OpCode.Barrier, 1, source: node.Source);
            if (node.Children.Count == 0)
            { if (barrier.Acquire) Add(OpCode.Acquire, source: node.Source); Add(OpCode.Null); return; }
            string? region = barrier.Acquire ? (++nextRegion).ToString(CultureInfo.InvariantCulture) : null;
            if (region is not null) { Add(OpCode.Barrier, 2, 1, region, node.Source); acquireRegions.Add(region); }
            Emit(Child(node, 0));
            if (region is not null)
            {
                acquireRegions.RemoveAt(acquireRegions.Count - 1);
                if (owner.Type(node.Type) == TypeRef.Process) Add(OpCode.Acquire, 1, source: node.Source);
                Add(OpCode.Barrier, 2, text: region, source: node.Source);
            }
        }
        private void Match(HirNode node)
        {
            Emit(Child(node, 0)); int value = Temporary(); Add(OpCode.InitializeLocal, value);
            List<int> exits = [];
            foreach (var arm in Hir.Matches[node.Data].Arms)
            {
                Pattern(arm.Pattern, value, node.Source); int next = Add(OpCode.JumpIfFalse);
                if (arm.Pattern.Binding is { } binding) { Add(OpCode.LoadLocal, value); Add(OpCode.InitializeLocal, Slot(binding.Value)); }
                Emit(arm.Value); exits.Add(Add(OpCode.Jump)); Patch(next, code.Count);
            }
            Add(OpCode.Null);
            foreach (int exit in exits) Patch(exit, code.Count);
        }
        private void Pattern(HirPattern pattern, int value, HirSourceRange source)
        {
            if (pattern.Kind == HirPatternKind.Any) { Add(OpCode.True); return; }
            Add(OpCode.LoadLocal, value);
            switch (pattern.Kind)
            {
                case HirPatternKind.Null: Add(OpCode.IsNull); break;
                case HirPatternKind.Literal: Literal(Hir.Literals[pattern.Data], source); Add(OpCode.Equal); break;
                case HirPatternKind.Type: Add(OpCode.Constant, owner.Constant(new TypeValue(owner.Type(new(pattern.Data))))); Add(OpCode.IsType); break;
                case HirPatternKind.Regex: Add(OpCode.RegexMatch, text: pattern.Text); break;
                default: throw new InvalidOperationException($"Unknown pattern {pattern.Kind}.");
            }
        }
    }
}
