using System.Collections.Immutable;

namespace Msync.Optimization;

internal static partial class IndexedMemoryOptimization
{
    private sealed class JoinEdge(ControlFlowEdge edge, State state)
    {
        internal readonly ControlFlowEdge Edge = edge;
        internal readonly State State = state;
        internal Dictionary<int, Atom>? Values;
        internal Dictionary<int, int>? Translations;
    }

    private sealed class Join(int block, JoinEdge[] edges, State entry)
    {
        internal readonly int Block = block;
        internal readonly JoinEdge[] Edges = edges;
        internal readonly int Cells = entry.Cells;
        internal readonly int Memory = entry.Memory;
        internal readonly ImmutableDictionary<string, int> Fields = entry.Fields;
        internal Dictionary<Access, Read?>? Available;
    }

    private sealed partial class Analysis
    {
        private Atom Merge(JoinEdge[] incoming, int start, int operand, Atom first, int index, bool stack, bool translate)
        {
            Atom Value(JoinEdge edge) => stack ? edge.State.Stack[index] : edge.State.Locals[index];
            if (incoming.Skip(1).All(edge => Value(edge) == first)) return first;
            var result = Unknown(start, operand, incoming.Any(edge => Value(edge).Storage));
            if (translate)
                foreach (JoinEdge edge in incoming)
                    (edge.Values ??= [])[result.Number] = Value(edge);
            return result;
        }

        private static void ForgetReads(State state)
        {
            state.Reads = ImmutableDictionary<Access, Read>.Empty;
            state.Join = null;
            state.Clobbers = null;
        }

        private void Kill(State state, Clobber write)
        {
            foreach (var entry in state.Reads)
                if (MayClobber(entry.Key, write)) state.Reads = state.Reads.Remove(entry.Key);
            if (state.Join is not null)
                state.Clobbers = (state.Clobbers ?? ImmutableHashSet<Clobber>.Empty).Add(write);
        }

        private bool MayClobber(Access access, Clobber write)
        {
            if (access.Operation == "storage.buffer_size" && (write.Region & IndexedRegion.Length) == 0) return false;
            if (access.Buffer != write.Buffer && freshBuffers is not null
                && freshBuffers.Contains(access.Buffer) && freshBuffers.Contains(write.Buffer)) return false;
            return (write.Region & IndexedRegion.Length) != 0 || !access.Index.Known
                || write.Index is not int exact || access.Index.Constant == exact;
        }

        private Read? Available(State state, Access access)
        {
            if (state.Reads.TryGetValue(access, out var read)) return read;
            if (state.Join is not { } join) return null;
            if (state.Clobbers is { } writes && writes.Any(write => MayClobber(access, write))) return null;
            var cache = join.Available ??= [];
            if (!cache.TryGetValue(access, out read))
            {
                read = ReadAtJoin(join, access);
                cache.Add(access, read);
            }
            return read;
        }

        private Read? ReadAtJoin(Join join, Access access)
        {
            var incoming = new Read[join.Edges.Length];
            for (int index = 0; index < incoming.Length; index++)
            {
                var edge = join.Edges[index];
                var projected = access with
                {
                    Buffer = Translate(join, edge, access.Buffer),
                    Index = access.Operation == "storage.buffer_get" && !access.Index.Known
                        ? Subscript(new(Translate(join, edge, access.Index.Number))) : access.Index
                };
                if (Available(edge.State, projected) is not { } read) return null;
                incoming[index] = read;
            }
            if (incoming.Skip(1).All(read => read == incoming[0])) return incoming[0];

            // Reuse an already materialized value phi before inserting copies.
            Atom result = default;
            for (int slot = 0; slot < join.Edges[0].State.Locals.Length; slot++)
            {
                int edge = 0;
                while (edge < incoming.Length && join.Edges[edge].State.Locals[slot] == incoming[edge].Result) edge++;
                if (edge != incoming.Length) continue;
                result = Merge(join.Edges, graph.Blocks[join.Block].Start, slot + 1, incoming[0].Result, slot,
                    stack: false, translate: false);
                break;
            }
            if (result.Number == 0) result = new(Define(), incoming.Any(read => read.Result.Storage));

            var inputs = new PhiInput[incoming.Length];
            for (int index = 0; index < inputs.Length; index++)
            {
                var edge = join.Edges[index];
                inputs[index] = new(edge.Edge, ExistingSource(edge.State, incoming[index]));
            }
            return new(-1, result, new(inputs));
        }

        private static Source ExistingSource(State state, Read read)
        {
            int slot = Array.IndexOf(state.Locals, read.Result);
            return slot >= 0 ? new(null, slot) : new(read);
        }

        private int Translate(Join join, JoinEdge edge, int value)
        {
            if (edge.Values?.TryGetValue(value, out var incoming) == true) return incoming.Number;
            Identity identity = definitions[value];
            if (identity.Kind != CellRead && identity.Kind < Operator) return value;
            var translated = edge.Translations ??= [];
            if (translated.TryGetValue(value, out int result)) return result;
            result = TranslateIdentity(join, edge, value, identity);
            translated.Add(value, result);
            return result;
        }

        private int TranslateIdentity(Join join, JoinEdge edge, int value, Identity identity)
        {
            int Operand(int number) => Translate(join, edge, number);
            if (identity.Kind == CellRead && identity.Right == join.Cells)
            {
                if (edge.Values?.TryGetValue(identity.Left, out Atom incoming) == true)
                    return Dereference(edge.State, incoming).Number;
                return Number(identity with { Left = Operand(identity.Left), Right = edge.State.Cells });
            }
            if (identity.Kind == MemberRead)
            {
                Identity stamp = definitions[identity.Right];
                string field = stamp.Name!;
                if (stamp.Left != join.Memory || stamp.Right != join.Cells || stamp.Third != join.Fields.GetValueOrDefault(field))
                    return value;
                int version = Number(new(MemberVersion, edge.State.Memory, edge.State.Cells,
                    edge.State.Fields.GetValueOrDefault(field), field));
                return Number(identity with { Left = Operand(identity.Left), Right = version });
            }
            var operation = (OpCode)(identity.Kind - Operator);
            if (operation is OpCode.Negate or OpCode.Not or OpCode.IsNull)
                return ExpressionNumber(identity with { Left = Operand(identity.Left) });
            if (operation is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder
                or OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual)
                return ExpressionNumber(identity with { Left = Operand(identity.Left), Right = Operand(identity.Right) });
            return value;
        }

        private int ExpressionNumber(Identity identity)
        {
            var operation = (OpCode)(identity.Kind - Operator);
            if (operation == OpCode.Negate && numbers.TryGetValue(identity.Left, out double input))
                return Literal(new NumberValue(-input)).Number;
            if (operation is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder)
                || !numbers.TryGetValue(identity.Left, out double left) || !numbers.TryGetValue(identity.Right, out double right))
                return Number(identity);
            // Phi translation can expose a literal already folded on the
            // predecessor. Compute the same binary64 instruction, not a
            // mathematical recurrence or reassociation.
            double result = operation switch
            {
                OpCode.Add => left + right,
                OpCode.Subtract => left - right,
                OpCode.Multiply => left * right,
                OpCode.Divide => left / right,
                _ => left % right
            };
            return Literal(new NumberValue(result)).Number;
        }
    }
}
