using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private sealed class PatternCoverage(SemanticAnalyzer owner, SemanticContext context,
        ImmutableArray<PatternCoverageTerm> arms, bool hasGuards)
    {
        private readonly Space root = new();
        private readonly Dictionary<Space, Sample> values = [];
        private readonly Dictionary<(Space, string), bool> predicates = [];
        private readonly Dictionary<HirTypeId, TypeRef> testedTypes = [];

        public int Partitions { get; private set; }

        private sealed class Space
        {
            public Dictionary<Projection, Space> Children { get; } = [];
            public HashSet<double> Numbers { get; } = [];
            public HashSet<string> Strings { get; } = new(StringComparer.Ordinal);
            public HashSet<int> Lengths { get; } = [];

            public Space Child(Projection key)
            {
                if (!Children.TryGetValue(key, out var item)) Children.Add(key, item = new());
                return item;
            }
        }

        private readonly record struct Projection(string? Field, int Index = 0);
        private readonly record struct Probe(Space Space, HirTypeId Type, string? Predicate = null, SemanticValue? Subject = null);
        private readonly record struct Sample(TypeKind Kind, HirTypeId Type, bool Boolean = false,
            double Number = 0, string? Text = null, int Length = -1, int Layout = -1, HirTypeId? Denotation = null,
            SemanticValue? Subject = null)
        {
            public string Label => Kind == TypeKind.Number && double.IsNaN(Number)
                ? "NaN" : Kind == TypeKind.Range && Length >= 0 ? $"Range of length {Length}"
                : new TypeRef(Kind).ToString();
        }

        public string? FindMissing(SemanticValue subject)
        {
            foreach (var arm in arms) Collect(root, arm);
            foreach (var sample in Samples(root, subject.Type, subject))
            {
                values[root] = sample;
                if (!Covered()) return sample.Label;
            }
            return null;
        }

        private static void Collect(Space space, PatternCoverageTerm term)
        {
            if (term.Left is { } left) Collect(space, left);
            if (term.Right is { } right) Collect(space, right);
            switch (term.Syntax)
            {
                case RelationPattern { Limit: NumberExpr n }: space.Numbers.Add(n.Value); break;
                case LiteralPattern { Value: NumberExpr n }: space.Numbers.Add(n.Value); break;
                case LiteralPattern { Value: UnaryExpr { Operand: NumberExpr n } unary }:
                    space.Numbers.Add(unary.Operator == "-" ? -n.Value : n.Value); break;
                case LiteralPattern { Value: StringExpr text }: space.Strings.Add(text.Value); break;
                case TuplePattern tuple:
                    space.Lengths.Add(tuple.Items.Count);
                    for (int index = 0; index < term.Items.Length; index++)
                        Collect(space.Child(new(null, index)), term.Items[index]);
                    break;
                case ScopePattern shape:
                    for (int index = 0; index < term.Items.Length; index++)
                        Collect(space.Child(new(shape.Fields[index].Name)), term.Items[index]);
                    break;
            }
        }

        private bool Covered()
        {
            Partitions++;
            Probe? request = null;
            foreach (var arm in arms)
                if (Matches(root, arm, ref request) == true) return true;
            if (request is not { } probe) return false;
            if (probe.Predicate is { } atom)
            {
                var key = (probe.Space, atom);
                predicates.Add(key, false);
                bool both = Covered();
                if (both)
                {
                    predicates[key] = true;
                    both = Covered();
                }
                predicates.Remove(key);
                return both;
            }
            foreach (var value in Samples(probe.Space, probe.Type, probe.Subject))
            {
                values.Add(probe.Space, value);
                bool covered = Covered();
                values.Remove(probe.Space);
                if (!covered) return false;
            }
            return true;
        }

        private IEnumerable<Sample> Samples(Space space, HirTypeId type, SemanticValue? subject = null)
        {
            var description = owner.hir.Types[type.Value];
            for (TypeKind kind = TypeKind.Null; kind <= TypeKind.Number; kind = (TypeKind)((int)kind << 1))
            {
                if ((description.RuntimeType.Kind & kind) == 0) continue;
                var sample = new Sample(kind, type, Subject: subject);
                switch (kind)
                {
                    case TypeKind.Bool:
                        yield return sample;
                        yield return sample with { Boolean = true };
                        break;
                    case TypeKind.Type when subject?.DenotationDomain is { } denotations:
                        foreach (var denotation in denotations.Types) yield return sample with { Denotation = denotation };
                        break;
                    case TypeKind.Scope when description.ScopeDomain.Count > 0:
                        for (int i = 0; i < description.ScopeDomain.Count; i++)
                        {
                            int layout = description.ScopeDomain[i];
                            var branch = subject?.BranchValue?.Arms.FirstOrDefault(item => item.Scope?.Layout == layout);
                            yield return sample with { Layout = layout, Subject = branch ?? subject };
                        }
                        break;
                    case TypeKind.Scope when !hasGuards && subject?.Scope is { Layout: >= 0, Dynamic: false } shape:
                        yield return sample with { Layout = shape.Layout };
                        break;
                    case TypeKind.Number when space.Numbers.Count != 0:
                        yield return sample with { Number = double.NaN };
                        yield return sample with { Number = double.NegativeInfinity };
                        yield return sample with { Number = double.PositiveInfinity };
                        foreach (double point in space.Numbers)
                        {
                            yield return sample with { Number = point };
                            yield return sample with { Number = double.BitIncrement(point) };
                            yield return sample with { Number = double.BitDecrement(point) };
                        }
                        break;
                    case TypeKind.String:
                        yield return sample;
                        foreach (var text in space.Strings) yield return sample with { Text = text };
                        break;
                    case TypeKind.Range when space.Lengths.Count != 0:
                        yield return sample with { Length = 0 };
                        foreach (int length in space.Lengths)
                        {
                            if (length > 0) yield return sample with { Length = length };
                            if (!space.Lengths.Contains(length + 1)) yield return sample with { Length = length + 1 };
                        }
                        break;
                    default: yield return sample; break;
                }
            }
        }

        private bool? Matches(Space space, PatternCoverageTerm term, ref Probe? request)
        {
            var sample = values[space];
            switch (term.Syntax)
            {
                case NamePattern or WildcardPattern: return true;
                case NullPattern: return sample.Kind == TypeKind.Null;
                case CapturePattern: return Matches(space, term.Left!, ref request);
                case NotPattern: return !Matches(space, term.Left!, ref request);
                case BinaryPattern binary:
                {
                    bool? left = Matches(space, term.Left!, ref request);
                    if (left == !binary.Conjunction) return left;
                    bool? right = Matches(space, term.Right!, ref request);
                    return binary.Conjunction ? left & right : left | right;
                }
                case TypePattern:
                    return TypeMatches(term.Type!.Value, sample);
                case RelationPattern { Limit: NumberExpr limit } relation:
                    if (sample.Kind != TypeKind.Number) return false;
                    return relation.Operation switch
                    {
                        "<" => sample.Number < limit.Value, "<=" => sample.Number <= limit.Value,
                        ">" => sample.Number > limit.Value, _ => sample.Number >= limit.Value
                    };
                case LiteralPattern { Value: BoolExpr boolean }:
                    return sample.Kind == TypeKind.Bool && sample.Boolean == boolean.Value;
                case LiteralPattern { Value: NumberExpr n }:
                    return sample.Kind == TypeKind.Number && sample.Number == n.Value;
                case LiteralPattern { Value: UnaryExpr { Operand: NumberExpr n } signed }:
                    return sample.Kind == TypeKind.Number && sample.Number == (signed.Operator == "-" ? -n.Value : n.Value);
                case LiteralPattern { Value: StringExpr text }:
                    if (sample.Kind == TypeKind.String) return sample.Text == text.Value;
                    if (sample.Kind != TypeKind.Path) return false;
                    return Predicate(space, sample.Type, "path:" + text.Value, ref request);
                case RegexPattern regex:
                    if (sample.Kind is not (TypeKind.String or TypeKind.Path)) return false;
                    return Predicate(space, sample.Type, "regex:" + regex.Pattern, ref request);
                case TuplePattern tuple:
                    return RangeMatches(space, term, tuple, sample, ref request);
                case ScopePattern shape:
                    return ScopeMatches(space, term, shape, sample, ref request);
                default: return null;
            }
        }

        private bool? TypeMatches(HirTypeId id, Sample sample)
        {
            if (!testedTypes.TryGetValue(id, out var tested))
            {
                tested = owner.hir.Types[owner.ResolveHirType(id, context).Value].RuntimeType;
                testedTypes.Add(id, tested);
            }
            if (sample.Denotation is { } denotation)
                return tested.Accepts(owner.hir.Types[denotation.Value].RuntimeType);
            if (sample.Kind == TypeKind.Type && tested.Kind != TypeKind.Any) return null;
            if ((tested.Kind & sample.Kind) == 0) return false;
            if (sample.Kind != TypeKind.Range || tested.Element is null) return true;
            var actual = owner.hir.Types[sample.Type.Value].RuntimeType.Element;
            if (actual is null) return null;
            return tested.Element.Accepts(actual) ? true
                : (tested.Element.Kind & actual.Kind) == 0 ? false : null;
        }

        private bool? RangeMatches(Space space, PatternCoverageTerm term, TuplePattern tuple, Sample sample, ref Probe? request)
        {
            if (sample.Kind != TypeKind.Range) return false;
            if (tuple.Prefix && tuple.Items.Count == 0) return true;
            if (sample.Length < tuple.Items.Count || !tuple.Prefix && sample.Length != tuple.Items.Count) return false;
            var description = owner.hir.Types[sample.Type.Value];
            var element = description.Element >= 0 ? new HirTypeId(description.Element)
                : owner.hir.Type(description.RuntimeType.Element ?? TypeRef.Any);
            bool? result = true;
            for (int index = 0; index < term.Items.Length; index++)
            {
                result &= ProjectionMatches(space.Child(new(null, index)), term.Items[index], element, ref request,
                    sample.Subject?.Element);
                if (result == false) break;
            }
            return result;
        }

        private bool? ScopeMatches(Space space, PatternCoverageTerm term, ScopePattern shape, Sample sample, ref Probe? request)
        {
            if (sample.Kind != TypeKind.Scope) return false;
            if (shape.Fields.Count == 0) return true;
            if (sample.Layout < 0) return null;
            var fields = owner.hir.ScopeLayouts[sample.Layout].Fields;
            bool? result = true;
            for (int index = 0; index < term.Items.Length; index++)
            {
                var name = shape.Fields[index].Name;
                var field = fields.FirstOrDefault(candidate => candidate.Name == name);
                if (field is null) return false;
                var type = field.AdmitsNull ? owner.Nullable(field.StorageType) : field.StorageType;
                SemanticValue? member = null;
                if (!hasGuards && sample.Subject?.Scope is { } known)
                    owner.TryScopeField(context.Objects.GetValueOrDefault(known.Region, known), name, context, out member);
                result &= ProjectionMatches(space.Child(new(name)), term.Items[index], type, ref request, member);
                if (result == false) break;
            }
            return result;
        }

        private bool? ProjectionMatches(Space space, PatternCoverageTerm term, HirTypeId type, ref Probe? request,
            SemanticValue? subject)
        {
            if (term.Syntax is WildcardPattern or NamePattern) return true;
            if (values.ContainsKey(space)) return Matches(space, term, ref request);
            request ??= new(space, type, Subject: subject);
            return null;
        }

        private bool? Predicate(Space space, HirTypeId type, string atom, ref Probe? request)
        {
            if (predicates.TryGetValue((space, atom), out bool value)) return value;
            request ??= new(space, type, atom);
            return null;
        }
    }
}
