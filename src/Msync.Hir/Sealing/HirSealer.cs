using System.Collections.Immutable;
using Msync.Core;

namespace Msync.Hir;

public sealed record HirSealingResult(HirModule Module, ImmutableArray<HirNodeId> NodeMap);

/// <summary>Retains finalized semantic bodies, discarding abandoned binding/query construction.</summary>
public static class HirSealer
{
    public static HirModule Seal(HirModule module) => new Compaction(module).Run(includeMap: false).Module;

    public static HirSealingResult Compact(HirModule module) => new Compaction(module).Run(includeMap: true);

    private sealed class Arena(int length, string name)
    {
        private readonly bool[] live = new bool[length];
        public int[] Map { get; private set; } = [];
        public int Count { get; private set; }
        public bool Complete => Count == live.Length;

        public bool Mark(int id)
        {
            if ((uint)id >= (uint)live.Length)
                throw new MsyncException($"Invalid Typed HIR: {name} identity {id} is outside its arena.");
            if (live[id]) return false;
            live[id] = true;
            Count++;
            return true;
        }

        public void Number()
        {
            Map = new int[live.Length];
            int next = 0;
            for (int i = 0; i < live.Length; i++) Map[i] = live[i] ? next++ : -1;
        }

        public ImmutableArray<T> Copy<T>(ImmutableArray<T> input, Func<T, T>? rewrite = null)
        {
            if (Complete && rewrite is null) return input.IsDefault ? [] : input;
            var result = ImmutableArray.CreateBuilder<T>(Count);
            for (int i = 0; i < live.Length; i++)
                if (live[i]) result.Add(rewrite is null ? input[i] : rewrite(input[i]));
            return result.MoveToImmutable();
        }
    }

    private sealed class Compaction(HirModule module)
    {
        private readonly Arena nodes = new(module.Nodes.Length, "node");
        private readonly Arena literals = new(module.Literals.Length, "literal");
        private readonly Arena calls = new(module.Calls.Length, "call");
        private readonly Arena closures = new(module.Closures.Length, "closure");
        private readonly Arena templates = new(module.TemplateClosures.Length, "template closure");
        private readonly Arena scopes = new(module.Scopes.Length, "scope construction");
        private readonly Arena fors = new(module.Fors.Length, "for");
        private readonly Arena matches = new(module.Matches.Length, "match");
        private readonly Arena barriers = new(module.Barriers.Length, "barrier");
        private readonly Arena ranges = new(module.Ranges.Length, "range");
        private readonly Arena adaptations = new(module.Adaptations.Length, "function adaptation");
        private readonly Stack<HirNodeId> pending = new();
        private int edgeCount;

        private Arena[] Arenas => [nodes, literals, calls, closures, templates, scopes, fors, matches, barriers, ranges, adaptations];

        public HirSealingResult Run(bool includeMap)
        {
            foreach (var function in module.Functions) { Add(function.Body); Captures(function.Captures); }
            foreach (var template in module.Templates) { Add(template.Body); Captures(template.Captures); }
            while (pending.TryPop(out var id)) Visit(module.Nodes[id.Value]);
            Arena[] arenas = Arenas;
            bool unchanged = arenas.All(arena => arena.Complete) && CanonicalEdges();
            if (unchanged && !includeMap) return new(module, []);
            foreach (var arena in arenas) arena.Number();
            ImmutableArray<HirNodeId> map = [];
            if (includeMap)
            {
                var nodeMap = ImmutableArray.CreateBuilder<HirNodeId>(nodes.Map.Length);
                foreach (int id in nodes.Map) nodeMap.Add(new(id));
                map = nodeMap.MoveToImmutable();
            }
            return new(unchanged ? module : Rewrite(), map);
        }

        private bool CanonicalEdges()
        {
            int offset = 0;
            foreach (var node in module.Nodes)
            {
                if (node.Children.Start != offset) return false;
                offset += node.Children.Count;
            }
            return offset == module.Edges.Length;
        }

        private void Add(HirNodeId id)
        {
            if (nodes.Mark(id.Value)) pending.Push(id);
        }

        private void Captures(ImmutableArray<HirCapture> captures)
        {
            foreach (var capture in captures.AsSpan())
                if (capture.Mode == HirCaptureMode.Value) Add(capture.SourceValue);
        }

        private void Visit(HirNode node)
        {
            var children = node.Children;
            if (children.Start < 0 || children.Count < 0 || children.Start > module.Edges.Length - children.Count)
                throw new MsyncException("Invalid Typed HIR: live node child range is outside the edge arena.");
            edgeCount += children.Count;
            foreach (var child in module.Children(node)) Add(child);
            switch (node.Kind)
            {
                case HirKind.Literal: literals.Mark(node.Data); break;
                case HirKind.Call: calls.Mark(node.Data); break;
                case HirKind.Closure:
                    if (closures.Mark(node.Data)) Captures(module.Closures[node.Data].Captures);
                    break;
                case HirKind.TemplateClosure:
                    if (templates.Mark(node.Data)) Captures(module.TemplateClosures[node.Data].Captures);
                    break;
                case HirKind.Scope: scopes.Mark(node.Data); break;
                case HirKind.For: fors.Mark(node.Data); break;
                case HirKind.Match:
                    if (matches.Mark(node.Data)) MatchDependencies(module.Matches[node.Data]);
                    break;
                case HirKind.Barrier: barriers.Mark(node.Data); break;
                case HirKind.Range: case HirKind.LazyRange: ranges.Mark(node.Data); break;
                case HirKind.AdaptFunction: adaptations.Mark(node.Data); break;
            }
        }

        private void MatchDependencies(HirMatch match)
        {
            foreach (var arm in match.Arms)
            {
                Add(arm.Value);
                if (arm.Pattern.Kind == HirPatternKind.Literal) literals.Mark(arm.Pattern.Data);
            }
        }

        private HirNodeId Node(HirNodeId id) => new(nodes.Map[id.Value]);
        private ImmutableArray<HirCapture> RewriteCaptures(ImmutableArray<HirCapture> captures)
        {
            if (captures.IsDefaultOrEmpty) return captures;
            var result = ImmutableArray.CreateBuilder<HirCapture>(captures.Length);
            foreach (var capture in captures)
                result.Add(capture.Mode == HirCaptureMode.Value ? capture with { SourceValue = Node(capture.SourceValue) } : capture);
            return result.MoveToImmutable();
        }

        private HirModule Rewrite()
        {
            var edges = ImmutableArray.CreateBuilder<HirNodeId>(edgeCount);
            var rewrittenNodes = nodes.Copy(module.Nodes, node => RewriteNode(node, edges));
            return module with
            {
                Nodes = rewrittenNodes, Edges = edges.MoveToImmutable(),
                Functions = [.. module.Functions.Select(function => function with { Body = Node(function.Body), Captures = RewriteCaptures(function.Captures) })],
                Templates = [.. module.Templates.Select(template => template with { Body = Node(template.Body), Captures = RewriteCaptures(template.Captures) })],
                Literals = literals.Copy(module.Literals), Calls = calls.Copy(module.Calls),
                Closures = closures.Copy(module.Closures, closure => closure with { Captures = RewriteCaptures(closure.Captures) }),
                TemplateClosures = templates.Copy(module.TemplateClosures, closure => closure with { Captures = RewriteCaptures(closure.Captures) }),
                Scopes = scopes.Copy(module.Scopes), Fors = fors.Copy(module.Fors),
                Matches = matches.Copy(module.Matches, RewriteMatch), Barriers = barriers.Copy(module.Barriers),
                Ranges = ranges.Copy(module.Ranges), Adaptations = adaptations.Copy(module.Adaptations)
            };
        }

        private HirMatch RewriteMatch(HirMatch match) => new([.. match.Arms.Select(arm => arm with
        {
            Value = Node(arm.Value),
            Pattern = arm.Pattern.Kind == HirPatternKind.Literal
                ? arm.Pattern with { Data = literals.Map[arm.Pattern.Data] } : arm.Pattern
        })]);

        private HirNode RewriteNode(HirNode node, ImmutableArray<HirNodeId>.Builder edges)
        {
            var children = new HirEdgeRange(edges.Count, node.Children.Count);
            foreach (var child in module.Children(node)) edges.Add(Node(child));
            int data = node.Kind switch
            {
                HirKind.Literal => literals.Map[node.Data], HirKind.Call => calls.Map[node.Data],
                HirKind.Closure => closures.Map[node.Data], HirKind.TemplateClosure => templates.Map[node.Data],
                HirKind.Scope => scopes.Map[node.Data], HirKind.For => fors.Map[node.Data],
                HirKind.Match => matches.Map[node.Data], HirKind.Barrier => barriers.Map[node.Data],
                HirKind.Range or HirKind.LazyRange => ranges.Map[node.Data],
                HirKind.AdaptFunction => adaptations.Map[node.Data], _ => node.Data
            };
            return node with { Children = children, Data = data };
        }
    }
}
