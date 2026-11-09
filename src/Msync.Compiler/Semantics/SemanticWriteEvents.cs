using System.Collections.Immutable;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed record SemanticWriteEvent(SemanticDeferredWrite? Write,
    ImmutableArray<int> Guards, int? InitializeSingleton = null,
    ImmutableArray<ImmutableArray<SemanticWriteEvent>> Branches = default);

internal readonly record struct SemanticWriteTarget(SemanticWriteKind Kind,
    HirLocationId Location = default, int Region = -1, string? Field = null);

internal sealed record SemanticConditionalFact(SemanticValue? WhenInitialized,
    SemanticValue? WhenNotInitialized);

internal sealed partial class SemanticContext
{
    public void ActivateWriteEvents()
    {
        if (WriteEvents is not null) return;
        WriteEvents = [];
        foreach (var location in CellWrites)
            if (Values.TryGetValue(location, out var value))
                WriteEvents.Add(new(new(SemanticWriteKind.Location, value, Location: location), ActiveSingletonGuards));
        foreach (var (region, field) in FieldWrites)
            if (Objects.TryGetValue(region, out var scope) && scope.Fields.TryGetValue(field, out var value))
                WriteEvents.Add(new(new(SemanticWriteKind.Field, value, Region: region, Field: field), ActiveSingletonGuards));
        foreach (var region in IndexedWrites)
            if (Indexed.TryGetValue(region, out var storage))
                WriteEvents.Add(new(new(SemanticWriteKind.Indexed, storage.Element, Region: region, IndexedStorage: storage), ActiveSingletonGuards));
    }

    public SemanticWriteTarget WriteTarget(SemanticDeferredWrite write)
    {
        if (write.Kind == SemanticWriteKind.Field && Objects.TryGetValue(write.Region, out var scope)
            && scope.Locations.TryGetValue(write.Field!, out var location))
            return new(SemanticWriteKind.Location, location);
        return new(write.Kind, write.Location, write.Region, write.Field);
    }

    public SemanticValue? ReadWriteTarget(SemanticWriteTarget target) => target.Kind switch
    {
        SemanticWriteKind.Location => Values.GetValueOrDefault(target.Location),
        SemanticWriteKind.Indexed => Indexed.GetValueOrDefault(target.Region)?.Element,
        _ => Objects.GetValueOrDefault(target.Region)?.Fields.GetValueOrDefault(target.Field!)
    };

    public void SetSingletonFact(SemanticWriteTarget target, int singleton, SemanticConditionalFact fact) =>
        SingletonWriteFacts[target] = SingletonWriteFacts.GetValueOrDefault(target,
            ImmutableDictionary<int, SemanticConditionalFact>.Empty).SetItem(singleton, fact);

    public SemanticValue FactWhenInitialized(int singleton, SemanticDeferredWrite write, SemanticValue current) =>
        SingletonWriteFacts.GetValueOrDefault(WriteTarget(write))?.GetValueOrDefault(singleton)?.WhenInitialized ?? current;

    public SemanticValue FactWhenNotInitialized(int singleton, SemanticDeferredWrite write, SemanticValue current) =>
        SingletonWriteFacts.GetValueOrDefault(WriteTarget(write))?.GetValueOrDefault(singleton)?.WhenNotInitialized ?? current;

    public void RecordWrite(SemanticDeferredWrite write)
    {
        var target = WriteTarget(write);
        if (SingletonWriteFacts.TryGetValue(target, out var facts))
        {
            // An unconditional replacement makes the two conditional values equal.
            // Nested unrelated guards invalidate this relation rather than inventing
            // a path correlation that was never established.
            var updated = ImmutableDictionary.CreateBuilder<int, SemanticConditionalFact>();
            foreach (var (singleton, fact) in facts)
                if (ActiveSingletonGuards.Contains(singleton))
                    updated.Add(singleton, fact with { WhenNotInitialized = write.After });
            SingletonWriteFacts[target] = updated.ToImmutable();
        }
        WriteEvents?.Add(new(write, ActiveSingletonGuards));
    }
}
