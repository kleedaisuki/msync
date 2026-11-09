using System.Collections.Immutable;
using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;

int checks = 0;
HirLocationId local = new(0), external = new(1), field = new(2), alias = new(3), boolean = new(4);
FlowMemoryRegion heap = new(0);
var domains = ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty
    .Add(local, new(TypeRef.Path.WithNull, TypeRef.Path.WithNull, StableForLifetime: true))
    .Add(external, new(TypeRef.Path.WithNull, TypeRef.Path.WithNull, ExternallyWritable: true))
    .Add(field, new(TypeRef.Path.WithNull, TypeRef.Path.WithNull, Memory: heap))
    .Add(alias, new(TypeRef.Path.WithNull, TypeRef.Path.WithNull, Memory: heap))
    .Add(boolean, new(TypeRef.Bool, TypeRef.Bool));
FlowFacts entry = FlowFacts.Create(domains);
FlowFacts guarded = entry.Refine(local, TypeRef.Null, false).Refine(external, TypeRef.Null, false)
    .Refine(field, TypeRef.Null, false).Refine(alias, TypeRef.Null, false);
Check(guarded.Read(local).Type == TypeRef.Path, "Immediate local edge is narrowed");
Check(guarded.Read(local, FlowReadKind.Deferred).Type == TypeRef.Path, "Lifetime-stable local retains its edge proof");
Check(guarded.Read(external).Type == TypeRef.Path, "Immediate external edge is narrowed");
Check(guarded.Read(external, FlowReadKind.Deferred).Type.Nullable, "Deferred writable capture uses legal domain");
Check(guarded.Read(field, FlowReadKind.Deferred).Type.Nullable, "Stable binding does not freeze heap fields");
FlowFacts changed = guarded.InvalidateMemory(heap);
Check(changed.Read(field).Type.Nullable && changed.Read(alias).Type.Nullable, "Region write invalidates possible field aliases");
Check(changed.Read(local).Type == TypeRef.Path && changed.Read(external).Type == TypeRef.Path,
    "Heap write does not erase unrelated lexical facts");
Check(guarded.InvalidateMemory().Read(external).Type.Nullable, "Unknown call invalidates externally writable locations");
Check(guarded.Read(field).Type == TypeRef.Path, "Control fork remains unchanged");
Check(!guarded.Refine(local, TypeRef.Null, true).Reachable, "Contradicting edge is unreachable");
Check(FlowFacts.Join(guarded.Unreachable(), guarded).SameFacts(guarded), "Unreachable predecessor does not widen facts");
Check(!entry.Assign(boolean, new(TypeRef.Bool, Boolean: true)).RefineBoolean(boolean, false).Reachable,
    "Contradicting Boolean edge is unreachable");

HirLocationId value = new(0);
FlowFacts initial = FlowFacts.Create(ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty
    .Add(value, new(TypeRef.Path.WithNull, TypeRef.Path.WithNull, InitiallyAssigned: false)));
FlowGraph loop = new([
    new([FlowOperation.Assign(value, TypeRef.Null)], [new(new(1))]),
    new([], [new(new(2)), new(new(3))]),
    new([FlowOperation.Assign(value, TypeRef.Path)], [new(new(1))]),
    new([], [])
], new(0));
FlowSolution solution = FlowSolver.Solve(loop, initial);
Check(solution.Inputs[3]!.Read(value).Type == TypeRef.Path.WithNull, "Loop reaches union fixed point");
Check(solution.Inputs[3]!.Read(value).Assigned, "Loop entry initialization is definite");
Check(solution.Transfers < 10, "Stable write sites converge without metadata identity churn");

FlowGraph diamond = new([
    new([], [new(new(1), [new(value, TypeRef.Null)]), new(new(2), [new(value, TypeRef.Null, false)])]),
    new([], [new(new(3))]),
    new([], [new(new(3))]),
    new([], [])
], new(0));
FlowSolution fork = FlowSolver.Solve(diamond, FlowFacts.Create(initialDomains()));
Check(fork.Inputs[1]!.Read(value).Type == TypeRef.Null, "Positive Null edge");
Check(fork.Inputs[2]!.Read(value).Type == TypeRef.Path, "Negative Null edge");
Check(fork.Inputs[3]!.Read(value).Type == TypeRef.Path.WithNull, "Diamond join restores alternatives");

TypeRef[] alternatives = [TypeRef.Null, TypeRef.Path, TypeRef.Bool, TypeRef.Number,
    TypeRef.Path.WithNull, TypeRef.Any, new(TypeKind.Range, TypeRef.Number)];
var universal = ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty.Add(value,
    new(TypeRef.Any, TypeRef.Any));
Random random = new(37);
for (int index = 0; index < 256; index++)
{
    FlowFacts first = FlowFacts.Create(universal).Assign(value, new(alternatives[random.Next(alternatives.Length)]));
    FlowFacts second = FlowFacts.Create(universal).Assign(value, new(alternatives[random.Next(alternatives.Length)]));
    FlowFacts third = FlowFacts.Create(universal).Assign(value, new(alternatives[random.Next(alternatives.Length)]));
    Check(FlowFacts.Join(first, first).SameFacts(first), "Join idempotence");
    Check(FlowFacts.Join(first, second).SameFacts(FlowFacts.Join(second, first)), "Join commutativity");
    Check(FlowFacts.Join(FlowFacts.Join(first, second), third).SameFacts(
        FlowFacts.Join(first, FlowFacts.Join(second, third))), "Join associativity");
}
checks += StructuredFlowChecks.Run();
checks += BoundWriteChecks.Run();
checks += StructuralTypeChecks.Run();
Console.WriteLine($"PASS {checks} semantic flow contracts");

ImmutableDictionary<HirLocationId, FlowLocationDomain> initialDomains() =>
    ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty.Add(value,
        new(TypeRef.Path.WithNull, TypeRef.Path.WithNull));

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
