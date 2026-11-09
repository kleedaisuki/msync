namespace Msync.Hir;

public sealed partial class HirBuilder
{
    private Transaction? transaction;

    // Source-generation acceptance/editor workspaces are transactional. Generic
    // inference queries have no writer and never begin an arena transaction.
    public Transaction BeginTransaction() => transaction = new(this, transaction);

    public void SetExports(IEnumerable<HirExport> values) => exports = [.. values];
    public void SetUnresolvedImports(IEnumerable<HirLocationId> values) => unresolvedImports = [.. values];

    public void SetFunction(HirFunctionId id, HirFunction function)
    {
        transaction?.RecordFunction(id.Value, Functions[id.Value]);
        Functions[id.Value] = function;
    }

    public void SetLocation(HirLocationId id, HirLocation location)
    {
        transaction?.RecordLocation(id.Value, Locations[id.Value]);
        Locations[id.Value] = location;
    }

    public sealed class Transaction : IDisposable
    {
        private readonly HirBuilder owner;
        private readonly Transaction? parent;
        private readonly int sources;
        private readonly int types;
        private readonly int locations;
        private readonly int functions;
        private readonly int templates;
        private readonly int nodes;
        private readonly int edges;
        private readonly int literals;
        private readonly int scopeLayouts;
        private readonly int signatures;
        private readonly int calls;
        private readonly int closures;
        private readonly int scopes;
        private readonly int fors;
        private readonly int matches;
        private readonly int barriers;
        private readonly int ranges;
        private readonly int adaptations;
        private readonly int templateClosures;
        private readonly int concepts;
        private readonly List<HirExport> exports;
        private readonly List<HirLocationId> unresolvedImports;
        private Dictionary<int, HirFunction>? functionWrites;
        private Dictionary<int, HirLocation>? locationWrites;
        private bool finished;

        internal Transaction(HirBuilder owner, Transaction? parent)
        {
            this.owner = owner;
            this.parent = parent;
            sources = owner.Sources.Count;
            types = owner.Types.Count;
            locations = owner.Locations.Count;
            functions = owner.Functions.Count;
            templates = owner.Templates.Count;
            nodes = owner.Nodes.Count;
            edges = owner.Edges.Count;
            literals = owner.Literals.Count;
            scopeLayouts = owner.ScopeLayouts.Count;
            signatures = owner.Signatures.Count;
            calls = owner.Calls.Count;
            closures = owner.Closures.Count;
            scopes = owner.Scopes.Count;
            fors = owner.Fors.Count;
            matches = owner.Matches.Count;
            barriers = owner.Barriers.Count;
            ranges = owner.Ranges.Count;
            adaptations = owner.Adaptations.Count;
            templateClosures = owner.TemplateClosures.Count;
            concepts = owner.Concepts.Count;
            exports = owner.exports;
            unresolvedImports = owner.unresolvedImports;
        }

        internal void RecordFunction(int index, HirFunction before)
        {
            if (index < functions) (functionWrites ??= []).TryAdd(index, before);
        }

        internal void RecordLocation(int index, HirLocation before)
        {
            if (index < locations) (locationWrites ??= []).TryAdd(index, before);
        }

        public void Commit()
        {
            if (finished) return;
            if (parent is not null)
            {
                if (functionWrites is not null)
                    foreach (var pair in functionWrites) parent.RecordFunction(pair.Key, pair.Value);
                if (locationWrites is not null)
                    foreach (var pair in locationWrites) parent.RecordLocation(pair.Key, pair.Value);
            }
            owner.transaction = parent;
            finished = true;
        }

        public void Dispose() => Rollback();

        public void Rollback()
        {
            if (finished) return;
            if (functionWrites is not null)
                foreach (var pair in functionWrites) owner.Functions[pair.Key] = pair.Value;
            if (locationWrites is not null)
                foreach (var pair in locationWrites) owner.Locations[pair.Key] = pair.Value;
            for (int index = types; index < owner.Types.Count; index++)
                owner.types.Remove(owner.Types[index]);
            owner.Sources.RemoveRange(sources, owner.Sources.Count - sources);
            owner.Types.RemoveRange(types, owner.Types.Count - types);
            owner.Locations.RemoveRange(locations, owner.Locations.Count - locations);
            owner.Functions.RemoveRange(functions, owner.Functions.Count - functions);
            owner.Templates.RemoveRange(templates, owner.Templates.Count - templates);
            owner.Nodes.RemoveRange(nodes, owner.Nodes.Count - nodes);
            owner.Edges.RemoveRange(edges, owner.Edges.Count - edges);
            owner.Literals.RemoveRange(literals, owner.Literals.Count - literals);
            owner.RewindScopeLayouts(scopeLayouts);
            owner.ScopeLayouts.RemoveRange(scopeLayouts, owner.ScopeLayouts.Count - scopeLayouts);
            owner.Signatures.RemoveRange(signatures, owner.Signatures.Count - signatures);
            owner.Calls.RemoveRange(calls, owner.Calls.Count - calls);
            owner.Closures.RemoveRange(closures, owner.Closures.Count - closures);
            owner.Scopes.RemoveRange(scopes, owner.Scopes.Count - scopes);
            owner.Fors.RemoveRange(fors, owner.Fors.Count - fors);
            owner.Matches.RemoveRange(matches, owner.Matches.Count - matches);
            owner.Barriers.RemoveRange(barriers, owner.Barriers.Count - barriers);
            owner.Ranges.RemoveRange(ranges, owner.Ranges.Count - ranges);
            owner.Adaptations.RemoveRange(adaptations, owner.Adaptations.Count - adaptations);
            owner.TemplateClosures.RemoveRange(templateClosures, owner.TemplateClosures.Count - templateClosures);
            owner.Concepts.RemoveRange(concepts, owner.Concepts.Count - concepts);
            owner.exports = exports;
            owner.unresolvedImports = unresolvedImports;
            owner.transaction = parent;
            finished = true;
        }
    }
}
