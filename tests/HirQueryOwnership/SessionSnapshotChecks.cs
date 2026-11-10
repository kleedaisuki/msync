using Msync.Compiler.Semantics;
using Msync.Hir;

internal static class SessionSnapshotChecks
{
    internal static int Run()
    {
        int checks = 0;
        SemanticFacts<int, int> parent = new();
        parent[0] = 1;
        parent[7] = 5;
        SemanticFacts<int, int> query = new(parent);
        query[0] = 2;
        int notifications = 0;
        query.ObserveChanges((_, _) => notifications++);
        query.Detach();
        Check(notifications == 0, "Detaching a fact view does not publish logical writes.");
        parent[0] = 3;
        parent[7] = 9;
        Check(query[0] == 2 && query[7] == 5, "A published fact root freezes inherited and own values.");
        query[0] = 4;
        Check(parent[0] == 3 && query[0] == 4 && notifications == 1,
            "Later writes retain local ownership and change notifications.");
        SemanticFacts<int, int> sibling = new(parent);
        Check(sibling[7] == 9, "Ordinary query forks still borrow current parent facts.");
        for (int generation = 0; generation < 10_000; generation++)
        {
            SemanticFacts<int, int> next = new(query);
            next[0] = generation;
            next.Detach();
            query = next;
        }
        Check(query.Count == 2 && query[0] == 9_999 && query[7] == 5,
            "Long publication histories preserve old unchanged facts without an overlay chain.");

        SemanticFacts<int, SemanticValue> numbers = new();
        numbers[0] = Number(0);
        numbers.Detach();
        foreach (long bits in new[] { unchecked((long)0x8000000000000000),
            0x7ff8000000000042L, 0x7ff8000000000043L, 0x3ff0000000000000L, 0x3ff0000000000000L })
        {
            SemanticFacts<int, SemanticValue> next = new(numbers);
            SemanticValue assigned = Number(bits);
            next[0] = assigned;
            next.Detach();
            SemanticValue actual = next[0];
            Check(BitConverter.DoubleToInt64Bits(actual.Literal!.Value.Number) == bits,
                "Publication preserves signed zero and exact NaN payloads.");
            Check(ReferenceEquals(actual, assigned),
                "Publication retains the assigned fact identity even when records compare equal.");
            numbers = next;
        }

        SemanticContext accepted = new(new(0));
        HirLocationId location = new(3);
        accepted.Storage[location] = new(1);
        accepted.Values[location] = new(new(1), HirNodeId.None);
        SemanticContext pending = accepted.QueryFork();
        pending.Storage[location] = new(2);
        pending.DetachFacts();
        accepted.Storage[location] = new(4);
        accepted.Values[location] = new(new(4), HirNodeId.None);
        Check(pending.Storage[location] == new HirTypeId(2)
            && pending.Values[location].Type == new HirTypeId(1),
            "Publication detaches every inherited semantic table, not only current values.");
        SemanticContext editor = pending.QueryFork();
        editor.Storage[location] = new(5);
        Check(pending.Storage[location] == new HirTypeId(2),
            "Queries after publication cannot mutate accepted fixed-storage facts.");
        return checks;

        static SemanticValue Number(long bits) => new(new(1), HirNodeId.None,
            new(HirLiteralKind.Number, Number: BitConverter.Int64BitsToDouble(bits)));

        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks++;
        }
    }
}
