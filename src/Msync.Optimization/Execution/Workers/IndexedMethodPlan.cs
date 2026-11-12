using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Msync.Optimization.Execution;

/// <summary>A bound method over one admitted indexed storage owner.</summary>
public sealed record IndexedMethodPlan(NativeWorkerPlan Worker, ImmutableArray<IndexedMethodPlan.Access> Accesses,
    ImmutableArray<IndexedMethodPlan.CallGuard> Calls, int Storage, ImmutableArray<IndexedMethodPlan.Input> Inputs)
{
    public ImmutableArray<PrefixExit> Exits { get; init; }
    public int GuestResult { get; init; } = -1;
    public int SpillCount { get; init; }
    public bool WritesScalarStorage { get; init; }
    public bool UsesArenaRows { get; init; }
    public bool WritesArenaRows { get; init; }
    public bool ReturnsNull { get; init; }
    public int StorageCountAccess { get; init; } = -1;
    public int MaximumRows { get; init; } = int.MaxValue;
    public ImmutableArray<int> WritableParameters { get; init; }
    public readonly record struct PrefixExit(int Pc, ImmutableArray<PrefixLocal> Locals);
    public readonly record struct PrefixLocal(int Local, int Register, int Access = -1);
    // Accesses form a forward DAG: Member/Capture only refer to earlier
    // indices, and consumers evaluate each selected live projection once.
    public enum AccessKind : byte { Slot, Member, Capture, Count, ArenaRoot, ArenaSize, AdmittedScope }
    public readonly record struct Access(AccessKind Kind, int Receiver, int Operand);
    public readonly record struct CallGuard(int Callee, int Captures, int? Identity);
    public readonly record struct Input(int Access, NativeWorkerType Type, bool ShiftCount = false);
}

public readonly record struct IndexedMethodFailure(int Pc, InstructionEffect Effects);

internal enum BoundWorkerMode : byte { ArenaRead, ArenaPrefix, ArenaWrite, ArenaWritePrefix, VectorWrite, Scalar }

public static class IndexedMethodPreparation
{
    public static bool MayPrepare(FunctionCode body, bool includeEffectFrontier = false)
    {
        bool vector = false, loop = false;
        int arena = 0;
        for (int pc = 0; pc < body.Instructions.Count; pc++)
        {
            var op = body.Instructions[pc];
            vector |= op is { Op: OpCode.NativeCall, Text: "storage.vector_get" or "storage.vector_set" };
            loop |= op.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.JumpIfStaticReady && op.A <= pc;
            if (ExecutionArenaOperation.Bind(op) != ArenaOperation.None) arena++;
            if (arena >= 2 || loop && (vector || arena != 0))
                return includeEffectFrontier || !body.Instructions.Any(op => StopsReadonlyVersion(InstructionEffects.Effects(op)));
        }
        return false;
    }

    internal static bool StopsReadonlyVersion(InstructionEffect effect) =>
        (effect & (InstructionEffect.Write | InstructionEffect.Control | InstructionEffect.Capture)) != 0;

    /// <summary>Import from a bound image only; no source or HIR is consulted.</summary>
    public static bool TryPrepare(Module image, int function, [NotNullWhen(true)] out IndexedMethodPlan? plan,
        out IndexedMethodFailure failure) => TryPrepare(image, function, null, out plan, out failure);

    internal static bool TryPrepare(Module image, int function, Func<int, ExecutionTargets.Target?>? predicate,
        [NotNullWhen(true)] out IndexedMethodPlan? plan, out IndexedMethodFailure failure,
        bool allowPrefix = false)
    {
        plan = null;
        failure = new(-1, default);
        if (image.PreserveLocals || image.SessionDispatches is { Count: > 0 }) return false;
        if (!ExecutionRegionImporter.TryImport(image, function, out var graph, out _)) return false;
        var body = image.Functions[function];
        if (!body.Instructions.Any(op => ExecutionArenaOperation.Bind(op) != ArenaOperation.None))
            return allowPrefix && body.ReturnType == TypeRef.Null
                && new IndexedMethodImporter(image, graph, predicate, BoundWorkerMode.VectorWrite).Prepare(out plan, out failure);

        if (new IndexedMethodImporter(image, graph, predicate, BoundWorkerMode.ArenaRead).Prepare(out plan, out failure))
            return true;
        if (!allowPrefix) return false;
        if (body.ReturnType == TypeRef.Null
            && body.Instructions.Any(op => ExecutionArenaOperation.Writes(ExecutionArenaOperation.Bind(op))))
        {
            if (new IndexedMethodImporter(image, graph, predicate, BoundWorkerMode.ArenaWrite)
                    .Prepare(out plan, out failure)
                || new IndexedMethodImporter(image, graph, predicate, BoundWorkerMode.ArenaWritePrefix)
                    .Prepare(out plan, out failure)) return true;
        }
        return new IndexedMethodImporter(image, graph, predicate, BoundWorkerMode.ArenaPrefix).Prepare(out plan, out failure);
    }
}
