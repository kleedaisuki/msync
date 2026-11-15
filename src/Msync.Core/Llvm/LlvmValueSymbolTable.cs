// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueSymbolTable.h/.cpp.
namespace Msync.Core.Llvm;

public sealed class LlvmValueSymbolTable
{
    private readonly Dictionary<string, LlvmValueName> values = new(StringComparer.Ordinal);
    private readonly int maxNameSize;
    private uint lastUnique;

    public LlvmValueSymbolTable(int maxNameSize = -1) => this.maxNameSize = maxNameSize;
    public IEnumerable<LlvmValueName> Entries => values.Values;
    private static string Key(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    public LlvmValue? Lookup(ReadOnlySpan<byte> name) =>
        values.GetValueOrDefault(Key(Truncate(name)))?.Value;

    private ReadOnlySpan<byte> Truncate(ReadOnlySpan<byte> name) =>
        maxNameSize > -1 && name.Length > maxNameSize ? name[..Math.Max(1, maxNameSize)] : name;

    internal LlvmValueName CreateValueName(ReadOnlySpan<byte> name, LlvmValue value)
    {
        name = Truncate(name);
        var entry = new LlvmValueName(name, value);
        if (values.TryAdd(Key(name), entry)) return entry;
        return MakeUniqueName(value, name);
    }

    private LlvmValueName MakeUniqueName(LlvmValue value, ReadOnlySpan<byte> name)
    {
        int baseSize = name.Length;
        bool appendDot = value is LlvmGlobalValue global &&
            !(global.Parent is { } module && module.TargetTriple.IsNVPTX());
        while (true)
        {
            byte[] suffix = System.Text.Encoding.ASCII.GetBytes(
                (appendDot ? "." : "") + (++lastUnique).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (maxNameSize > -1 && baseSize + suffix.Length > maxNameSize)
            {
                baseSize -= baseSize + suffix.Length - maxNameSize;
                System.Diagnostics.Debug.Assert(baseSize >= 0);
                continue;
            }
            byte[] candidate = new byte[baseSize + suffix.Length];
            name[..baseSize].CopyTo(candidate);
            suffix.CopyTo(candidate, baseSize);
            var entry = new LlvmValueName(candidate, value);
            if (values.TryAdd(Key(candidate), entry)) return entry;
        }
    }

    internal void ReinsertValue(LlvmValue value)
    {
        var entry = value.GetValueName()!;
        if (values.TryAdd(Key(entry.Key.Span), entry)) return;
        value.SetValueName(MakeUniqueName(value, entry.Key.Span));
    }

    internal void RemoveValueName(LlvmValueName entry) => values.Remove(Key(entry.Key.Span));
}
