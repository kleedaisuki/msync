namespace Msync.Compiler.Semantics;

// Completed entries describe formal inputs and effects, never caller locations
// or runtime resources. A source generation owns only the promises it adds.
internal sealed class SemanticInstanceCache
{
    private readonly Dictionary<string, SemanticAnalyzer.FunctionInstance> instances = new(StringComparer.Ordinal);
    private AdditionLease? additions;

    public AdditionLease BeginAdditions() => additions = new(this, additions);

    public bool TryGetValue(string key, out SemanticAnalyzer.FunctionInstance instance) =>
        instances.TryGetValue(key, out instance!);

    public void Add(string key, SemanticAnalyzer.FunctionInstance instance)
    {
        instances.Add(key, instance);
        additions?.Record(key);
    }

    public void Remove(string key) => instances.Remove(key);

    internal sealed class AdditionLease(SemanticInstanceCache owner, AdditionLease? parent) : IDisposable
    {
        private List<string>? keys;
        private bool finished;

        internal void Record(string key) => (keys ??= []).Add(key);

        public void Commit()
        {
            if (keys is not null && parent is not null)
                foreach (string key in keys) parent.Record(key);
            owner.additions = parent;
            finished = true;
        }

        public void Dispose()
        {
            if (finished) return;
            if (keys is not null)
                foreach (string key in keys) owner.instances.Remove(key);
            owner.additions = parent;
            finished = true;
        }
    }
}
