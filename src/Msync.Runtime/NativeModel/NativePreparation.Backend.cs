using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed partial class NativePreparationState
{
    private NativeProgram RebuildImage(NativeProgram rewritten)
    {
        if (program.Backend is not { } backend || replacements.Count == 0 && slots.Count == function.Slots.Count)
            return rewritten;
        var functions = backend.Functions.ToArray();
        int entry = rewritten.EntryPoint;
        functions[entry] = trace is null ? NativeStandardBackendEmitter.EmitFunction(rewritten, entry)
            : trace.MeasureDetailed("compiler.prepare.image.function", (rewritten, entry, trace),
                static state => NativeStandardBackendEmitter.EmitFunction(state.rewritten, state.entry, state.trace),
                attributes: new Dictionary<string, string>
                {
                    ["function.name"] = function.Name,
                    ["function.id"] = entry.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }, metrics: _ => new Dictionary<string, ulong>
                {
                    ["source.instructions"] = (ulong)function.Instructions.Count,
                    ["emitted.instructions"] = (ulong)rewritten.Functions[entry].Instructions.Count,
                    ["replaced.sites"] = (ulong)replacements.Count,
                    ["added.slots"] = (ulong)(slots.Count - function.Slots.Count),
                    ["functions.preserved"] = (ulong)(functions.Length - 1),
                    ["connections.preserved"] = (ulong)backend.Connections.Count
                });
        return rewritten with { Backend = backend with { Functions = functions } };
    }
}
