using Msync.Optimization;

int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
DefinitionSet Build(IEnumerable<int> tokens)
{
    DefinitionSet set = default;
    foreach (int token in tokens) set = set.Union(new(token));
    return set;
}
void Compare(DefinitionSet actual, IEnumerable<int> expected, string label)
{
    HashSet<int> oracle = [.. expected];
    Require(actual.Count == oracle.Count, label + ": count");
    Require(actual.Order().SequenceEqual(oracle.Order()), label + ": enumeration");
    foreach (int token in oracle) Require(actual.Contains(token), label + ": contains");
    Require(actual.Contains(int.MinValue) == oracle.Contains(int.MinValue), label + ": minimum token");
    Require(actual.Contains(int.MaxValue) == oracle.Contains(int.MaxValue), label + ": maximum token");
}

Compare(default, [], "empty");
Compare(new(int.MinValue), [int.MinValue], "negative boundary");
Compare(new(int.MaxValue), [int.MaxValue], "positive boundary");
Random random = new(451389);
for (int sample = 0; sample < 2_000; sample++)
{
    int[] a = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(-24, 25)).ToArray();
    int[] b = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(-24, 25)).ToArray();
    int[] c = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(-24, 25)).ToArray();
    DefinitionSet left = Build(a), right = Build(b), third = Build(c);
    DefinitionSet union = left.Union(right);
    Compare(union, a.Concat(b), "union");
    Compare(right.Union(left), union, "commutative");
    Compare(left.Union(left), a, "idempotent");
    Compare(union.Union(third), left.Union(right.Union(third)), "associative");
    Compare(left, a, "left snapshot");
    Compare(right, b, "right snapshot");
    Compare(third, c, "third snapshot");
}
DefinitionSet first = new(1), second = new(2);
for (int warmup = 0; warmup < 1_000; warmup++) first.Union(first);
long before = GC.GetAllocatedBytesForCurrentThread();
for (int iteration = 0; iteration < 10_000; iteration++)
{
    Require(first.Union(default).Count == 1, "empty identity");
    Require(default(DefinitionSet).Union(second).Contains(2), "singleton identity");
    Require(first.Union(first).Count == 1, "singleton idempotence");
}
Require(GC.GetAllocatedBytesForCurrentThread() == before, "empty/singleton union allocated");
DefinitionSet multiple = Build([1, 2, 3, 4, 5]), subset = Build([2, 4]);
for (int warmup = 0; warmup < 1_000; warmup++) multiple.Union(subset);
before = GC.GetAllocatedBytesForCurrentThread();
for (int iteration = 0; iteration < 10_000; iteration++)
{
    Require(multiple.Union(subset).Count == 5, "unchanged multiple union");
    Require(multiple.Union(new(3)).Count == 5, "existing singleton union");
}
Require(GC.GetAllocatedBytesForCurrentThread() == before, "unchanged union allocated");
Console.WriteLine($"DefinitionSet: {assertions} algebra, snapshot and allocation contracts passed.");
