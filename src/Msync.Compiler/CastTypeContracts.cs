using System.Collections.Immutable;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private Dictionary<StorageConstraint, (ImmutableDictionary<string, CastTypeContract> Contracts,
        ImmutableDictionary<string, DeferredTypeWrite> Writes)>? changedCastTargets;
    private int castContractEpoch;

    private void RecordCastTarget(Frame frame, StorageConstraint storage, TypeRef input, TypeRef target, SourceSpan span)
    {
        string key = frame.SourceName + ":" + span.Start + ":" + input;
        if (storage.CastContracts.ContainsKey(key)) return;
        var contract = new CastTypeContract(input, target, frame.Source, frame.SourceName, span);
        foreach (var write in storage.DeferredTypeWrites.Values)
            if (!CompatibleTarget(contract, write.Target))
                throw CastTargetChange(frame, contract, write.Target, write.Span, write.Source, write.SourceName);
        RememberCastTarget(storage);
        storage.CastContracts = storage.CastContracts.Add(key, contract);
        castContractEpoch++;
    }

    private void CheckCastTargetStore(Frame frame, StorageConstraint storage, Info value, SourceSpan span)
    {
        if (value.Type == TypeRef.Null && storage.Type?.Nullable == true) return;
        if (frame.DeferredCaptures && storage.Type?.WithoutNull == TypeRef.Type)
        {
            string key = frame.SourceName + ":" + span.Start;
            if (!storage.DeferredTypeWrites.ContainsKey(key))
            {
                RememberCastTarget(storage);
                storage.DeferredTypeWrites = storage.DeferredTypeWrites.Add(key, new(value.TypeValue, frame.Source, frame.SourceName, span));
            }
        }
        foreach (var contract in storage.CastContracts.Values)
        {
            if (!CompatibleTarget(contract, value.TypeValue)) throw CastTargetChange(frame, contract, value.TypeValue, span);
        }
    }

    private static bool CompatibleTarget(CastTypeContract contract, TypeRef? target) => target is not null
        && ExplicitConversions.Possible(contract.Input, target) && contract.Target.Accepts(target);

    private static CompileException CastTargetChange(Frame frame, CastTypeContract contract, TypeRef? replacement, SourceSpan span,
        string? source = null, string? sourceName = null)
    {
        var error = new CompileException(
                $"This Type value is used by deferred cast output fixed as {contract.Target}.", source ?? frame.Source, sourceName ?? frame.SourceName, span,
                label: replacement is not null ? $"changing its target to {replacement} changes the deferred result type" : "the new cast target is not statically known",
                help: "Keep a compatible target for this deferred operation, or create a new range with a separate Type binding.", code: "E0213");
        error.Diagnostic.Related.Add(new(contract.SourceName, contract.Source, contract.Span,
            $"this deferred cast established the {contract.Target} output contract"));
        return error;
    }

    private void RememberCastTarget(StorageConstraint storage) =>
        (changedCastTargets ??= []).TryAdd(storage, (storage.CastContracts, storage.DeferredTypeWrites));

    private void RestoreCastTargets(int epoch)
    {
        if (changedCastTargets is not null)
            foreach (var (storage, previous) in changedCastTargets)
            { storage.CastContracts = previous.Contracts; storage.DeferredTypeWrites = previous.Writes; }
        castContractEpoch = epoch;
    }
}
