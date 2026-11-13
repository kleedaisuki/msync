using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime.Native;
using Msync.Optimization;

internal static class NativeStaticFieldLoweringChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        foreach (bool session in new[] { false, true })
        foreach (bool byValue in new[] { false, true })
        foreach (bool beforeBinding in new[] { false, true })
        foreach (int repetitions in new[] { -1, 0, 1, 2 })
        {
            HirModule module = Build(byValue, beforeBinding, repetitions);
            if (session) module = module with { IsSession = true, Exports = [new("count", new(0))] };
            using MemoryStream high = new();
            HirArtifact.Write(module, high); high.Position = 0;
            HirModule decoded = HirArtifact.Read(high);
            foreach (int level in new[] { 0, 1, 2, 3 })
            {
                var lowered = NativeHirLowerer.Lower(decoded, new(level));
                NativeProgram program = NativeOptimizer.Optimize(lowered.Program, level);
                using MemoryStream native = new();
                if (!session) { NativeArtifact.Write(program, native); native.Position = 0; program = NativeArtifact.Read(native); }
                await using NativeInterpreter runtime = new(program);
                double expected = (beforeBinding ? 1 : 10) * 100 + (repetitions > 0 ? 20 : 10);
                double actual = (await runtime.ExecuteAsync()).AsNumber();
                if (session) check(runtime.GetLocalValue(lowered.Layout.Slot(decoded.Entry, new(0)))?.AsNumber() == expected % 100,
                    $"Persistent field alias lost its canonical binding at O{level}.");
                check(actual == expected,
                    $"Static field alias changed: value={byValue}, prebind={beforeBinding}, repetitions={repetitions}, O{level}: {actual} != {expected}");
            }
        }
    }

    private static HirModule Build(bool byValue, bool beforeBinding, int repetitions)
    {
        HirBuilder hir = new();
        hir.Sources.Add(new("static-field-bindings.msync"));
        HirSourceRange source = new(new(0), 0, 1);
        HirTypeId number = hir.Type(TypeRef.Number), none = hir.Type(TypeRef.Null);
        HirTypeId scope = hir.Type(TypeRef.Scope), callable = hir.Type(TypeRef.Function);
        HirTypeId boolean = hir.Type(TypeRef.Bool);
        HirLocationId field = Location("count", number, 0, HirLocationKind.Field);
        HirLocationId saved = Location("saved", callable, 0);
        HirLocationId captured = Location("count", number, 1);
        HirLocationId item = Location("item", number, 0);
        HirCapture capture = new(captured, byValue ? HirCaptureMode.Value : HirCaptureMode.Location,
            byValue ? new(-1) : field, byValue ? Read(field) : HirNodeId.None);
        hir.Functions.Add(new("entry", [], [], number, HirNodeId.None));
        hir.Functions.Add(new("snapshot", [], [capture], number, Read(captured)));
        hir.Closures.Add(new(new(1), [capture]));
        hir.Calls.Add(new(HirCallKind.Function, new(1)));
        hir.ScopeLayouts.Add(new([new("count", number)]));
        int construction = hir.AddScope(new(0, [field]));
        HirNodeId closure = Node(HirKind.Closure, callable);
        HirNodeId[] initializers = beforeBinding ? [Declare(field, Number(10))] : [Declare(field, Number(10)), Declare(saved, closure)];
        HirNodeId initialized = Node(HirKind.Scope, scope, initializers, construction);
        HirNodeId rebind = Block(Node(HirKind.AllocateScope, scope, data: construction), Declare(field, Number(20)));
        HirNodeId control;
        if (repetitions is 0 or 1)
            control = Node(HirKind.If, none,
                [hir.Literal(new(HirLiteralKind.Bool, Boolean: repetitions == 1), boolean, source), rebind, Null()]);
        else
        {
            hir.Ranges.Add(new([]));
            hir.Fors.Add(new(new(0), item));
            HirNodeId range = Node(HirKind.Range, hir.Type(new TypeRef(TypeKind.Range, TypeRef.Number)), repetitions < 0 ? [] : [Number(0), Number(1)]);
            control = Node(HirKind.For, none, [range, rebind]);
        }
        List<HirNodeId> body = [];
        if (beforeBinding) { body.Add(Declare(field, Number(1))); body.Add(Declare(saved, closure)); }
        body.Add(initialized);
        body.Add(control);
        HirNodeId call = Node(HirKind.Call, number, [Read(saved)]);
        body.Add(Node(HirKind.Binary, number,
            [Node(HirKind.Binary, number, [call, Number(100)], (int)HirBinaryOperator.Multiply), Read(field)], (int)HirBinaryOperator.Add));
        hir.Functions[0] = hir.Functions[0] with { Body = Block([.. body]) };
        return hir.Freeze(new(0));

        HirLocationId Location(string name, HirTypeId type, int owner, HirLocationKind kind = HirLocationKind.Local)
        {
            HirLocationId result = new(hir.Locations.Count);
            hir.Locations.Add(new(name, type, source, new(owner), kind)); return result;
        }
        HirNodeId Node(HirKind kind, HirTypeId type, HirNodeId[]? children = null, int data = 0) =>
            hir.Add(kind, type, source, children ?? [], data);
        HirNodeId Number(double value) => hir.Literal(new(HirLiteralKind.Number, Number: value), number, source);
        HirNodeId Null() => hir.Literal(new(HirLiteralKind.Null), none, source);
        HirNodeId Read(HirLocationId location) => Node(HirKind.ReadLocation, hir.Locations[location.Value].StorageType, data: location.Value);
        HirNodeId Declare(HirLocationId location, HirNodeId value) => Node(HirKind.Declare, none, [value], location.Value);
        HirNodeId Block(params HirNodeId[] children) => Node(HirKind.Block,
            children.Length == 0 ? none : hir.Nodes[children[^1].Value].Type, children);
    }
}
