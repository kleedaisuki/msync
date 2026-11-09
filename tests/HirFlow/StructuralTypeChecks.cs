using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;

internal static class StructuralTypeChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool result, string message)
        {
            checks++;
            if (!result) throw new InvalidOperationException(message);
        }
        HirBuilder builder = new();
        SemanticArena arena = new(builder);
        HirFlowTypeAlgebra algebra = new(arena);
        HirTypeId number = arena.Type(TypeRef.Number), text = arena.Type(TypeRef.String), boolean = arena.Type(TypeRef.Bool);
        int first = arena.InternScopeLayout(new([new("first", number)]));
        int second = arena.InternScopeLayout(new([new("second", number)]));
        HirTypeId one = arena.Type(new HirType(TypeRef.Scope, Shape: first));
        HirTypeId two = arena.Type(new HirType(TypeRef.Scope, Shape: second));
        HirTypeId maybe = algebra.Union(arena.Type(TypeRef.Null), algebra.Union(one, two));
        FlowFact nullable = new(builder.Types[maybe.Value].RuntimeType, SemanticType: maybe);
        Check(algebra.Refine(nullable, TypeRef.Null, true).Type == TypeRef.Null,
            "A nullable wrapper retains its Null independently of structural alternatives");
        Check(algebra.Refine(nullable, TypeRef.Null, false).Type == TypeRef.Scope,
            "A negative Null edge retains both structural rows without Null");
        FlowFact TypeValue(HirTypeId type) => new(TypeRef.Type, SemanticType: arena.Type(TypeRef.Type), Denotations: new([type]));
        FlowFact a = TypeValue(number), b = TypeValue(text), c = TypeValue(boolean);
        Check(algebra.Join(algebra.Join(a, b), c) == algebra.Join(a, algebra.Join(b, c)),
            "Finite represented-type joins are associative");
        FlowFact represented = algebra.Join(a, b);
        Check(algebra.Refine(represented, TypeRef.Number, true).Denotations!.Types.SequenceEqual([number]),
            "Positive Type-value refinement filters represented types, not the runtime Type kind");
        Check(algebra.Refine(represented, TypeRef.Number, false).Denotations!.Types.SequenceEqual([text]),
            "Negative Type-value refinement retains the remaining represented types");
        HirLocationId cell = new(0);
        var domains = ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty.Add(cell,
            new(TypeRef.Number, TypeRef.Number, StorageSemanticType: number));
        FlowFact constrained = FlowFacts.Create(domains, algebra).Assign(cell,
            new(TypeRef.Any, SemanticType: arena.Type(TypeRef.Any))).Read(cell);
        Check(constrained.Type == TypeRef.Number && constrained.SemanticType == number,
            "An unresolved projection cannot widen an established numeric storage domain");
        HirTypeId mixed = algebra.Union(one, number);
        FlowFact numeric = FlowFacts.Create(domains, algebra).Assign(cell,
            new(builder.Types[mixed.Value].RuntimeType, SemanticType: mixed)).Read(cell);
        Check(numeric.SemanticType == number,
            "Storage intersection removes incompatible structural alternatives, not just runtime kind bits");
        HirTypeId range = arena.Type(new TypeRef(TypeKind.Range, TypeRef.Number));
        FlowFact view = new(builder.Types[range.Value].RuntimeType, SemanticType: range);
        FlowFact snapshot = view with { Eager = true };
        Check(FlowFact.Join(view, snapshot).Eager && algebra.Join(view, snapshot).Eager,
            "Possible snapshots retain eager evaluation across scalar and structural flow joins");
        FlowFact flags = new(TypeRef.Bool, Assigned: false, Boolean: true, Eager: true);
        Check((flags with { Boolean = null }) is { Assigned: false, Boolean: null, Eager: true },
            "Clearing a Boolean fact preserves assignment and evaluation policy");
        Check((flags with { Assigned = true }) is { Assigned: true, Boolean: true, Eager: true },
            "Assignment updates preserve Boolean and evaluation policy");
        Check((flags with { Eager = false }) is { Assigned: false, Boolean: true, Eager: false },
            "Evaluation policy updates preserve the other facts");
        Check(Unsafe.SizeOf<FlowFact>() <= Unsafe.SizeOf<PrePolicyFact>(),
            "Evaluation policy does not grow the scalar fact representation");
        return checks;
    }

    private readonly record struct PrePolicyFact(TypeRef Type, bool Assigned, bool? Boolean,
        HirTypeId? SemanticType, FlowCallableDomain? Callables, FlowDenotationDomain? Denotations);
}
