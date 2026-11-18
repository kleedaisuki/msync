using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct NativeArgumentMove(NativeOffset Source, NativeOffset Destination, NativeType Type)
{
    internal readonly NativeRepresentation Representation = Type.Representation;
}

internal sealed record NativePhysicalCall(NativeArgumentMove[] Inputs, NativeArgumentMove[] Moves, NativeOffset? Window = null,
    NativeArgumentMove[]? Staged = null, NativeKind ScalarResult = NativeKind.Null)
{
    internal int Omitted => Inputs.Length - Moves.Length;
    internal bool HasMachineMoves { get; } = Window is null && Moves.Length is > 0 and <= 4
        && Moves.All(move => move.Representation is { Bytes: <= 16, References: <= 2 });
    internal int ReferenceWords { get; } = Moves.Sum(move => move.Representation.References);
}

internal readonly record struct NativeArgumentWindow(NativeAllocation Owner, NativeOffset Offset);

internal enum NativeArgumentPlacement : byte { Machine, Frame, Pending, HostReference }
