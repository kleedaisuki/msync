using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private sealed class FunctionNameOrigin
        {
            internal string? ContextualName;
            internal readonly List<FunctionNameOrigin> Parents = [];
        }
        private readonly Dictionary<int, FunctionNameOrigin> functionNameOrigins = [];
        private readonly List<(bool Prelude, int PC, FunctionNameOrigin Origin)> functionContextSites = [];

        private FunctionNameOrigin NameOrigin(int slot)
        {
            if (!functionNameOrigins.TryGetValue(slot, out var origin))
                functionNameOrigins.Add(slot, origin = new());
            return origin;
        }

        private void CopyFunctionOrigin(int target, int source) => NameOrigin(target).Parents.Add(NameOrigin(source));

        private void ConstantFunctionOrigin(int slot, NativeConstant value)
        {
            if (value.Kind != NativeKind.String || value.Text is not { } name ||
                !name.StartsWith("fs.", StringComparison.Ordinal) || !NativeSignatures.TryGet(name, out var signature) ||
                signature.Effect != NativeEffect.Read) return;
            NameOrigin(slot).ContextualName = name;
        }

        private void RecordFunctionContext(AggregateSlots fields, bool inPrelude)
        {
            var origin = NameOrigin(fields.Slots[2]);
            functionContextSites.Add((inPrelude, inPrelude ? prelude.Count : code.Count, origin));
        }

        private void ApplyFunctionContextMarkers()
        {
            // Existing contextual binding is keyed by the literal fs builtin's
            // origin. Leaf SSA moves that origin; it does not infer callable names.
            Dictionary<FunctionNameOrigin, string?> resolved = [];
            foreach (var site in functionContextSites)
            {
                if (!resolved.TryGetValue(site.Origin, out string? name))
                    resolved.Add(site.Origin, name = ContextualName(site.Origin));
                if (name is null) continue;
                var instructions = site.Prelude ? prelude : code;
                instructions[site.PC] = instructions[site.PC] with { Symbol = name };
            }
        }

        private static string? ContextualName(FunctionNameOrigin root)
        {
            HashSet<FunctionNameOrigin> seen = [];
            List<FunctionNameOrigin> pending = [root];
            while (pending.Count != 0)
            {
                var current = pending[^1];
                pending.RemoveAt(pending.Count - 1);
                if (!seen.Add(current)) continue;
                if (current.ContextualName is { } name) return name;
                pending.AddRange(current.Parents);
            }
            return null;
        }
    }
}
