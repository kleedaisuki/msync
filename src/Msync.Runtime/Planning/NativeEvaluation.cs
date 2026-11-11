using Msync.Core;

namespace Msync.Runtime;

internal static class NativeEvaluation
{
    internal static bool CanEnumerate(NativeSignature signature, IReadOnlyList<Value> arguments)
    {
        if ((signature.Behavior.GetValueOrDefault() & NativeBehavior.Enumerates) == 0) return true;
        // Native purity does not cover a Range's captured producer. Only an
        // explicit membership snapshot can be consumed without invoking it.
        foreach (var argument in arguments)
            if (argument is RangeValue range && (range.Resolve is not null || range.Selection is not null
                || range.SnapshotValues is null)) return false;
        return true;
    }

    internal static bool DefersFileRead(string? name) => name is "fs.directory_iterator"
        or "fs.recursive_directory_iterator" or "io.read_lines" or "io.read_bytes";
}
