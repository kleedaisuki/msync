using System.Collections.Immutable;
using Msync.Compiler.Semantics;
using Msync.Hir;

internal static class CallableIdentityChecks
{
    internal static void Run()
    {
        int checks = 0;
        var comparer = SemanticCallableIdentity.Instance;
        SemanticValue one = new(new(0), HirNodeId.None, new(HirLiteralKind.Number, Number: 1));
        SemanticValue two = one with { Literal = new(HirLiteralKind.Number, Number: 2) };
        SemanticCallable Closure(int cell, SemanticValue hint, HirCaptureMode mode = HirCaptureMode.Location) =>
            new(new(1), Captures: [new(new(cell), hint, mode)]);
        var first = Closure(7, one);
        var refreshed = Closure(7, two);
        Check(comparer.Equals(first, refreshed), "A location capture identifies a cell, not its current hint.");
        Check(comparer.GetHashCode(first) == comparer.GetHashCode(refreshed), "Equal environments have equal hashes.");
        Check(!comparer.Equals(first, Closure(8, one)), "Different captured cells remain different environments.");
        Check(!comparer.Equals(Closure(7, one, HirCaptureMode.Value), Closure(7, two, HirCaptureMode.Value)),
            "By-value snapshots retain distinct values.");
        Check(comparer.Equals(new(new(1)), new(new(1), Captures: [])), "Default and empty captures are equivalent.");
        Check(comparer.Equals(new(NativeName: "storage.buffer_size"),
            new(NativeName: "storage.buffer_size", Captures: [])), "Native identity does not depend on empty storage.");
        Check(!comparer.Equals(new(NativeName: "storage.buffer_size"), new(NativeName: "storage.buffer_get")),
            "Different registered natives remain different.");
        SemanticCallable[] repeated = Enumerable.Range(0, 384).Select(index => Closure(7, index % 2 == 0 ? one : two)).ToArray();
        Check(repeated.Distinct(comparer).Count() == 1, "Repeated joins are idempotent for one live closure environment.");
        var mixed = repeated.Append(Closure(8, one)).Distinct(comparer).ToArray();
        Check(mixed.Length == 2, "Canonicalization does not erase a genuine alternative environment.");
        Check(mixed.Concat(mixed).Distinct(comparer).Count() == mixed.Length, "Union of a canonical domain with itself is idempotent.");
        SemanticScope a = new(1, 0, ImmutableDictionary<string, SemanticValue>.Empty,
            ImmutableDictionary<string, HirLocationId>.Empty);
        SemanticValue objectA = one with { Literal = null, Scope = a };
        SemanticValue objectB = objectA with { Scope = a with { Region = 2 } };
        Check(!comparer.Equals(Closure(7, objectA, HirCaptureMode.Value), Closure(7, objectB, HirCaptureMode.Value)),
            "By-value object references preserve object identity.");
        Console.WriteLine($"PASS {checks} callable identity contracts");

        void Check(bool valid, string reason)
        {
            checks++;
            if (!valid) throw new InvalidOperationException(reason);
        }
    }
}
