using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer(BoundProgram program, SemanticObservationBuilder? observations = null,
    SemanticInstanceCache? codeCache = null, SemanticSessionDispatch? sessionDispatch = null)
{
    private readonly SemanticArena hir = new(program.Hir);
    private readonly int boundNodeCount = program.Hir.Nodes.Count;
    private int nextRegion;

    internal SemanticContext FinalState => entryState ?? throw new InvalidOperationException("No semantic generation has been analyzed.");

    internal HirModule Analyze(SemanticContext? seed = null)
    {
        SemanticOutput output = new(program.Hir);
        SemanticContext context = new(program.Entry, output, seed);
        nextRegion = seed?.NextRegion ?? 0;
        entryState = context;
        SemanticValue body = Analyze(program.Body, context);
        foreach (var returned in context.Returns) body = Join(body, returned);
        if (entryPrelude.Count != 0)
        {
            entryPrelude.Add(body.Node);
            body = body with { Node = output.Add(HirKind.Block, body.Type,
                program.Node(program.Body).Source, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entryPrelude)) };
        }
        RefreshSessionCalls(context);
        output.SealFunction(program.Entry, new("<main>", [], [], body.Type, body.Node));
        context.NextRegion = nextRegion;
        return HirSealer.Seal(output.Freeze(program.Entry, program.IsSession) with
        { SessionDispatches = sessionDispatch?.Tables ?? [] });
    }

    private SemanticValue Analyze(HirNodeId id, SemanticContext context)
    {
        observations?.BeforeNode(id, context);
        HirNode node = program.Node(id);
        var children = program.Children(id);
        SemanticValue value = node.Kind switch
        {
            HirKind.Literal => Literal(node, context),
            HirKind.NativeFunction => NativeFunction(node, context),
            HirKind.ReadLocation or HirKind.ReadStorage => Read(id, node, context),
            HirKind.Declare => Declare(node, children, context),
            HirKind.Assign => Assign(node, children, context),
            HirKind.Discard => Discard(node, children, context),
            HirKind.Block => Block(node, children, context),
            HirKind.TemplateClosure => TemplateClosure(node, context),
            HirKind.Scope => Scope(node, children, context),
            HirKind.Range or HirKind.LazyRange => Range(node, children, context),
            HirKind.Call => Call(node, children, context),
            HirKind.If or HirKind.Match or HirKind.For or HirKind.Loop
                or HirKind.Break or HirKind.Return or HirKind.Barrier => Control(id, node, children, context),
            _ => Operation(id, node, children, context)
        };
        observations?.RecordNode(id, value, context);
        return value;
    }

    private SemanticValue Literal(HirNode node, SemanticContext context) =>
        new(Resolve(node.Type, context), Add(node, node.Type, [], context), hir.Literals[node.Data]);

    private SemanticValue NativeFunction(HirNode node, SemanticContext context) =>
        new(hir.Type(TypeRef.Function), Add(node, hir.Type(TypeRef.Function), [], context),
            Callables: [new(NativeName: node.Text)]);

    private SemanticValue Read(HirNodeId id, HirNode node, SemanticContext context)
    {
        if (StandardDeclaration(new(node.Data)) is { } imported)
        {
            SemanticValue initialized = imported.Kind == DeclarationKind.StandardRoot
                ? Standard(node, context) : EnsureModule(imported.Name, node.Source, context);
            HirLocationId target = context.Location(new(node.Data));
            context.Values[target] = initialized;
            context.Storage[target] = initialized.Type;
        }
        HirLocationId location = context.Location(new(node.Data));
        SemanticValue value = context.Values.TryGetValue(location, out var current)
            ? current : new(Resolve(hir.Locations[location.Value].StorageType, context), HirNodeId.None);
        value = FlowRead(id, location, value, context);
        if (StandardCell(location, context) is not null && StorageValue(location, context) is { } moduleValue)
            value = moduleValue;
        if (value.Scope is { } scope && context.Objects.TryGetValue(scope.Region, out var active))
        {
            if (active.Initializing) throw Error(node.Source, "A named Scope cannot be read before its initialization completes.", "E0211");
            value = value with { Scope = active };
        }
        value = RefreshIndexed(value, context);
        HirTypeId type = value.Type;
        return value with { Type = type, Node = Add(node with { Data = location.Value }, type, [], context), Location = location };
    }

    private SemanticValue Declare(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        SemanticValue value = Analyze(children[0], context);
        HirLocationId source = new(node.Data);
        HirLocationId location = context.Location(source);
        var definition = hir.Locations[source.Value];
        HirTypeId type = StorageTypeFor(location, value, context);
        if (!definition.AdmitsNull && Runtime(value).Nullable)
            throw SemanticDiagnosticFactory.NullAdmission(hir.Sources[node.Source.Source.Value],
                new(node.Source.Start, node.Source.Length), definition);
        Require(value, type, node.Source, allowImplicit: true, context);
        value = Convert(value, type, node.Source, context);
        SealStorage(location, type, context);
        context.Values[location] = value;
        UpdateStorageValue(location, value, context);
        var declaredNode = Add(node with { Data = location.Value }, hir.Type(TypeRef.Null), [value.Node], context);
        return new(hir.Type(TypeRef.Null), declaredNode);
    }

    private SemanticValue Assign(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        SemanticValue value = Analyze(children[^1], context);
        var valueSource = program.Node(children[^1]).Source;
        HirLocationId location = context.Location(new(node.Data));
        HirTypeId target = StorageType(location, context);
        try { Require(value, target, valueSource, allowImplicit: true, context); }
        catch (CompileException error)
        {
            var definition = hir.Locations[location.Value];
            SemanticDiagnosticFactory.FixedStorage(error, hir.Sources[definition.Source.Source.Value],
                definition, hir.Types[target.Value].RuntimeType);
            throw;
        }
        HirTypeId resolved = ResolveHirType(target, context);
        if (hir.Locations[location.Value].AdmitsNull) resolved = Nullable(resolved);
        if (resolved != target)
        {
            target = resolved;
            SealStorage(location, target, context);
        }
        value = Convert(value, target, valueSource, context);
        CheckSessionAssignment(location, value, valueSource, context);
        CheckClosedAssignment(location, value, valueSource, context);
        context.Values[location] = value;
        UpdateStorageValue(location, value, context);
        context.CellWrites.Add(location);
        context.RecordWrite(new(SemanticWriteKind.Location, value, Location: location));
        return new(hir.Type(TypeRef.Null), Add(node with { Data = location.Value }, hir.Type(TypeRef.Null), [value.Node], context));
    }

    private SemanticValue Discard(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        SemanticValue value = Analyze(children[0], context);
        return new(hir.Type(TypeRef.Null), Add(node, hir.Type(TypeRef.Null), [value.Node], context));
    }

    private SemanticValue Block(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        List<HirNodeId> result = [];
        SemanticValue value = new(hir.Type(TypeRef.Null), HirNodeId.None);
        foreach (var child in children)
        {
            value = Analyze(child, context);
            result.Add(value.Node);
        }
        return value with { Node = Add(node, value.Type, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(result), context) };
    }

    private HirNodeId Add(HirNode source, HirTypeId type, ReadOnlySpan<HirNodeId> children,
        SemanticContext context) => context.Publish
            ? context.Output!.Add(source.Kind, type, source.Source, children, source.Data, source.Text, source.Effects)
            : HirNodeId.None;

    private TypeRef Runtime(SemanticValue value) => hir.Types[value.Type.Value].RuntimeType;
    private HirTypeId Resolve(HirTypeId type, SemanticContext context)
    {
        while (context.Substitutions.TryGetValue(type, out var target) && target != type) type = target;
        return type;
    }
    private HirTypeId Nullable(HirTypeId type) => hir.Type(hir.Types[type.Value] with { RuntimeType = hir.Types[type.Value].RuntimeType.WithNull });
    private bool IsEditor => observations is not null;
    private bool Stable(HirLocationId location) => program.IsStable(location);

    private CompileException Error(HirSourceRange source, string message, string code = "E0200")
    {
        return SemanticDiagnosticFactory.Create(hir.Sources[source.Source.Value],
            new SourceSpan(source.Start, source.Length), message, code);
    }
}
