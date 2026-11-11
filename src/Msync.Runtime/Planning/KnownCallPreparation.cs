using Msync.Core;

namespace Msync.Runtime;

public static partial class ContextualBackend
{
    private sealed partial class Preparation
    {
        private bool KnownClosure(Instruction instruction)
        {
            if (instruction.B != 0) return Stop();
            // Preserve residual construction: callable equality includes capture identity.
            Push(new(new FunctionValue(instruction.A, []), pc, pc));
            return true;
        }

        private bool KnownCall(Instruction instruction)
        {
            KnownValue[] inputs = PeekMany(instruction.A + 1);
            if (instruction.B < 0 || inputs[0].Value is not FunctionValue closure
                || inputs.Skip(1).Any(input => !input.Known)) return Stop();
            int? target = KnownCalls.Target(closure, instruction);
            if (closure.NativeName is null && instruction.Text is not null && target is null) return Stop();
            var evaluated = new KnownCalls(module, context, filesystem, ResolveKnownCapture, ReadKnownMember, Builtin)
                .Run(closure, inputs.Skip(1).Select(input => input.Value!).ToArray(), target, cancellationToken);
            if (evaluated is null) return Stop();
            if (evaluated.Copies.Count == 0)
            {
                if (!KnownEvaluation.Portable(evaluated.Value)) return Stop();
                PopMany(inputs.Length);
                return PushComputed(evaluated.Value, inputs);
            }
            bool discarded = pc + 1 < code.Count && code[pc + 1].Op == OpCode.Pop;
            bool observed = !discarded && evaluated.Value is KnownCopyResult;
            if (!discarded && !observed) return Stop();
            var expanded = new TransferGraph();
            for (int index = 0; index < evaluated.Copies.Count; index++)
            {
                var copy = evaluated.Copies[index];
                foreach (var source in SourcePaths(copy.Source))
                    if (expanded.NamespaceMayChange(filesystem.Resource(source))) return Stop();
                bool result = observed && evaluated.Value is KnownCopyResult selected && selected.Copy == index;
                if (!AddTransfer(copy.Source, copy.Destination, copy.Span, result, pc, expanded)) return Stop();
            }
            foreach (var node in expanded.Nodes) graph.Add(node);
            PopMany(inputs.Length);
            sites.Add(new(pc, inputs, observed));
            if (observed)
            {
                continuation = pc + 1;
                Flush();
                return false;
            }
            if (discarded) pc++;
            return true;
        }

        private Value? ResolveKnownCapture(Value value)
        {
            if (value is not CellValue cell) return value;
            if (cellValues is not null && cellValues.TryGetValue(cell, out var current)) return current;
            if (incomingLocals is not null)
                for (int index = 0; index < incomingLocals.Count && index < locals.Length; index++)
                    if (ReferenceEquals(IncomingCell(index), cell)) return locals[index].Value;
            return cell.Data;
        }

        private CellValue? IncomingCell(int slot) => incomingLocals is not null && slot < incomingLocals.Count
            && freshLocals?.Contains(slot) != true ? incomingLocals[slot] as CellValue : null;

        private void SetKnownCell(CellValue cell, Value? value) =>
            (cellValues ??= new(ReferenceEqualityComparer.Instance))[cell] = value;

        private void SetKnownLocal(int slot, Value? value, bool initialize)
        {
            if (IncomingCell(slot) is { } cell)
            {
                if (initialize) (freshLocals ??= []).Add(slot);
                else SetKnownCell(cell, value);
            }
            locals[slot] = new(value);
        }
    }
}
