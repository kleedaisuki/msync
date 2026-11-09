using System.Collections.Immutable;
using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;

internal static class BoundWriteChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool actual, string reason)
        {
            checks++;
            if (!actual) throw new InvalidOperationException(reason);
        }
        HirBuilder hir = new();
        hir.Sources.Add(new("closed.msync"));
        hir.Sources.Add(new("msync-stdlib:///0.8.0/source.msync"));
        HirTypeId type = hir.Type(TypeRef.Number);
        BoundProgram program = new(hir) { Entry = new(0) };
        HirLocationId Declare(string name, int position, DeclarationKind kind = DeclarationKind.Local,
            HirTemplateId? owner = null, HirSourceId? source = null, HirLocationId? captured = null)
        {
            HirLocationId id = new(hir.Locations.Count);
            HirSourceRange origin = new(source ?? new(0), position, 1);
            HirLocationKind runtimeKind = kind == DeclarationKind.Capture ? HirLocationKind.Import
                : kind == DeclarationKind.Field ? HirLocationKind.Field : HirLocationKind.Local;
            hir.Locations.Add(new(name, type, origin, new(0), runtimeKind, TemplateOwner: owner));
            program.Declare(new(id, name, kind, origin, Template: owner, CapturedFrom: captured));
            return id;
        }
        HirLocationId exported = Declare("same", 0);
        HirLocationId written = Declare("same", 1);
        HirLocationId captured = Declare("same", 1, DeclarationKind.Capture, new(0), captured: written);
        HirLocationId privateBlock = Declare("private", 2);
        HirLocationId module = Declare("public", 3, source: new(1));
        HirLocationId local = Declare("local", 4, owner: new(0), source: new(1));
        HirLocationId field = Declare("field", 5, DeclarationKind.Field);
        HirLocationId iteration = Declare("item", 6, DeclarationKind.Loop, new(0));
        HirLocationId pattern = Declare("matched", 7, DeclarationKind.Pattern, new(0));
        program.Exports = ImmutableDictionary<string, HirLocationId>.Empty.Add("same", exported);
        HirSourceRange span = new(new(0), 0, 1);
        HirNodeId literal = hir.Literal(new(HirLiteralKind.Number, Number: 3), type, span);
        HirNodeId store = hir.Add(HirKind.Assign, hir.Type(TypeRef.Null), span, [literal], captured.Value);
        program.Body = hir.Add(HirKind.Block, hir.Type(TypeRef.Null), span);
        program.Templates.Add(new(new(0), "setter", [], [captured], null, store, span));
        program.WrittenLocations.Add(program.Original(captured));
        program.StandardModules.Add("source", new("source", new(1), program.Body,
            ImmutableDictionary<string, HirLocationId>.Empty.Add("public", module), ImmutableDictionary<string, int>.Empty));
        Check(program.IsStable(exported), "Closed-root identity is stable despite a same-name writer");
        Check(!program.IsStable(written) && !program.IsStable(captured), "Capture writes reach the original declaration");
        program.IsSession = true;
        Check(!program.IsStable(exported), "Future REPL root submissions remain writable");
        Check(program.IsStable(privateBlock), "A private block inside a REPL remains a closed lifetime");
        Check(!program.IsStable(module), "Standard source exports remain externally writable");
        Check(program.IsStable(local), "A standard source function's private local can be stable");
        Check(!program.IsStable(field), "Scope field constancy is not inferred from absent source writes");
        Check(program.IsStable(iteration) && program.IsStable(pattern), "Loop/match declaration identities are private");
        Check(program.Original(captured) == written, "Capture identity resolves without physical layout");

        HirLocationId secondCapture = Declare("same", 1, DeclarationKind.Capture, new(1), captured: captured);
        HirNodeId secondStore = hir.Add(HirKind.Assign, hir.Type(TypeRef.Null), span, [literal], secondCapture.Value);
        program.Templates.Add(new(new(1), "setter2", [], [secondCapture], null, secondStore, span));
        Check(!program.IsStable(secondCapture), "Newly bound templates normalize transitive capture chains");
        return checks;
    }
}
