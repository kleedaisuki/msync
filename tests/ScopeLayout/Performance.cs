using System.Diagnostics;
using Msync.Core;

internal static class Performance
{
    private static object? retained;

    internal static void Run()
    {
        ScopeLayout layout = ScopeLayout.Parse("path\0enabled\0operation");
        Value[] payload = [new PathValue("./file"), Value.True, new FunctionValue(0)];
        const int count = 50_000;
        foreach (var (name, create) in new (string, Func<object>)[]
        {
            ("dictionary-view-baseline", () => new OldScope(new Dictionary<string, Value> { [layout.Names[0]] = payload[0], [layout.Names[1]] = payload[1], [layout.Names[2]] = payload[2] })),
            ("shared-layout", () => new ScopeValue(layout, [payload[0], payload[1], payload[2]]))
        })
        {
            for (int warmup = 0; warmup < 3; warmup++) Measure(create, count);
            var samples = Enumerable.Range(0, 9).Select(_ => Measure(create, count)).ToArray();
            Console.WriteLine($"{name}: {samples.Select(item => item.Elapsed).Order().ElementAt(4):F3} ms / {samples.Select(item => item.Bytes).Order().ElementAt(4):N0} B for {count:N0} objects");
        }
        OldScope old = new(new Dictionary<string, Value> { [layout.Names[0]] = payload[0], [layout.Names[1]] = payload[1], [layout.Names[2]] = payload[2] });
        ScopeValue compact = new(layout, payload);
        foreach (var (name, read) in new (string, Func<object>)[]
        {
            ("dictionary-name-read", () => old.Get("path")),
            ("layout-name-read", () => compact.Members["path"]),
            ("layout-index-read", () => compact.GetField(0)),
            ("layout-guarded-index-read", () => compact.IsField(0, "path") ? compact.GetField(0) : compact.Members["path"])
        })
        {
            for (int warmup = 0; warmup < 3; warmup++) Measure(read, count * 10);
            var samples = Enumerable.Range(0, 9).Select(_ => Measure(read, count * 10)).ToArray();
            Console.WriteLine($"{name}: {samples.Select(item => item.Elapsed).Order().ElementAt(4):F3} ms / {samples.Select(item => item.Bytes).Order().ElementAt(4):N0} B for {count * 10:N0} reads");
        }
        GC.KeepAlive(retained);
    }

    private static (double Elapsed, long Bytes) Measure(Func<object> create, int count)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int index = 0; index < count; index++) retained = create();
        return (Stopwatch.GetElapsedTime(start).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    private sealed class OldScope(Dictionary<string, Value> members)
    {
        private readonly Dictionary<string, Value> fields = members;
        private readonly IReadOnlyDictionary<string, Value> view = new OldView(members);
        public int AllocationSite { get; init; }
        internal Value Get(string name) => view[name];
        public override string ToString() => fields.Count + string.Join(',', view.Keys) + AllocationSite;
    }

    private sealed class OldView(IReadOnlyDictionary<string, Value> members) : IReadOnlyDictionary<string, Value>
    {
        public int Count => members.Count;
        public IEnumerable<string> Keys => members.Keys;
        public IEnumerable<Value> Values => members.Values.Select(Read);
        public Value this[string name] => Read(members[name]);
        public bool ContainsKey(string name) => members.ContainsKey(name);
        public bool TryGetValue(string name, out Value value)
        {
            if (members.TryGetValue(name, out var stored)) { value = Read(stored); return true; }
            value = Value.Null; return false;
        }
        public IEnumerator<KeyValuePair<string, Value>> GetEnumerator() => members.Select(pair => KeyValuePair.Create(pair.Key, Read(pair.Value))).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        private static Value Read(Value value) => value is CellValue cell ? cell.Data : value;
    }
}
