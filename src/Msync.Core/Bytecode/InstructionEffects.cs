using System.Runtime.CompilerServices;

namespace Msync.Core;

public static class InstructionEffects
{
    private sealed record ScopeNames(bool HasDuplicates);
    private static readonly ConditionalWeakTable<string, ScopeNames> scopeNames = new();

    public static InstructionEffect Effects(Instruction instruction) => instruction.Op switch
    {
        OpCode.NativeCall => NativeEffects(instruction.Text),
        OpCode.Call when instruction.B < 0 => InstructionEffect.Read | InstructionEffect.Write
            | InstructionEffect.Invoke | InstructionEffect.MayFail | InstructionEffect.Diverge,
        OpCode.Call => InstructionEffect.Invoke | InstructionEffect.MayFail | InstructionEffect.Diverge,
        OpCode.ProcessCall => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.Invoke | InstructionEffect.MayFail,
        OpCode.Transfer or OpCode.LocalTransfer => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.MayFail,
        OpCode.ExecutePlan => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.MayFail,
        OpCode.PlanResult => InstructionEffect.Read | InstructionEffect.MayFail,
        OpCode.GetIterator or OpCode.IteratorNext => InstructionEffect.Read | InstructionEffect.Invoke | InstructionEffect.MayFail,
        OpCode.GetSingleton => InstructionEffect.Read,
        OpCode.SetSingleton => InstructionEffect.Write,
        OpCode.SetMember => InstructionEffect.Write | InstructionEffect.MayFail,
        OpCode.MakeScope when ScopeConstructionMayFail(instruction) => InstructionEffect.MayFail,
        OpCode.GetMember or OpCode.Qualify or OpCode.Convert or OpCode.RegexMatch => InstructionEffect.MayFail,
        OpCode.SpecializeClosure when instruction.A < 0 => InstructionEffect.Read | InstructionEffect.MayFail,
        OpCode.SpecializeClosure => InstructionEffect.MayFail,
        OpCode.LoadCell => InstructionEffect.Capture,
        OpCode.GetCapture => InstructionEffect.MayFail,
        OpCode.Barrier or OpCode.Acquire => InstructionEffect.Control | InstructionEffect.Invoke,
        _ => InstructionEffect.None
    };

    public static bool ScopeConstructionMayFail(Instruction instruction)
    {
        if (instruction.A < 2 || instruction.Text is not { } names) return false;
        if (instruction.A > 4)
            return scopeNames.GetValue(names, static names => AnalyzeScopeNames(names)).HasDuplicates;
        // Most source-owned scopes have only a few fields. Compare their slices
        // directly; large standard namespaces use the linear set path above.
        var source = names.AsSpan();
        for (var start = 0; start < source.Length;)
        {
            var separator = source[start..].IndexOf('\0');
            var end = separator < 0 ? source.Length : start + separator;
            var member = source[start..end];
            for (var next = end + 1; member.Length > 0 && next < source.Length;)
            {
                var nextSeparator = source[next..].IndexOf('\0');
                var nextEnd = nextSeparator < 0 ? source.Length : next + nextSeparator;
                if (member.SequenceEqual(source[next..nextEnd])) return true;
                next = nextEnd + 1;
            }
            start = end + 1;
        }
        return false;
    }

    private static ScopeNames AnalyzeScopeNames(string names)
    {
        // Rewrites retain immutable operand text even when they create new
        // instructions. The weak key lets this syntax fact die with its code.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (!seen.Add(name)) return new(true);
        return new(false);
    }

    // Enumeration is an invocation of the range's deferred source and callbacks,
    // not merely a read of the adapter object returned by a pure native function.
    public static InstructionEffect NativeEffects(string? name)
    {
        if (name is null || !NativeSignatures.TryGet(name, out var signature))
            return InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.Invoke | InstructionEffect.MayFail;
        NativeBehavior behavior = signature.Behavior.GetValueOrDefault();
        var effects = signature.Effect switch
        {
            NativeEffect.Read => InstructionEffect.Read | InstructionEffect.MayFail,
            NativeEffect.Write => InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.MayFail,
            _ => InstructionEffect.None
        };
        if ((behavior & NativeBehavior.MayFail) != 0) effects |= InstructionEffect.MayFail;
        if ((behavior & NativeBehavior.Enumerates) != 0) effects |= InstructionEffect.Read | InstructionEffect.Invoke | InstructionEffect.MayFail;
        if ((behavior & NativeBehavior.Invokes) != 0) effects |= InstructionEffect.Invoke | InstructionEffect.MayFail;
        return effects;
    }

}
