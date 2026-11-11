using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public sealed record RuntimeProfileSample(string Name, Dictionary<string, string> Attributes, Dictionary<string, ulong> Metrics);

public sealed class RuntimeProfile(bool instructionSites = false)
{
    private readonly ConcurrentDictionary<Module, ImageCounts> images = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<Module, ImageIdentity> identities = new();
    private int nextImage;

    internal Frame Enter(Module image, int functionId, RuntimeFrameTrace? trace)
    {
        ImageCounts counts = Image(image);
        FunctionCounts function = counts.GetFunction(functionId);
        Interlocked.Increment(ref function.Calls);
        return new(this, function, trace);
    }

    internal void Native(Module image, string name, bool fast = false)
    {
        NativeCounts counts = Image(image).Natives.GetOrAdd(name, static _ => new());
        Interlocked.Increment(ref counts.Calls);
        if (fast) Interlocked.Increment(ref counts.Fast);
    }

    private ImageCounts Image(Module image) => images.GetOrAdd(image, static (module, profile) =>
        new(module, profile.ImageId(module), profile.InstructionSites), this);

    internal int ImageId(Module image) =>
        identities.GetValue(image, _ => new(Interlocked.Increment(ref nextImage))).Id;

    private bool InstructionSites { get; } = instructionSites;

    // Drain after guest execution joins. Weak image IDs survive across drains
    // without retaining replaced session images.
    public IReadOnlyList<RuntimeProfileSample> Drain()
    {
        List<RuntimeProfileSample> samples = [];
        foreach (ImageCounts image in images.Values.OrderBy(image => image.Id))
        {
            for (int functionId = 0; functionId < image.Functions.Length; functionId++)
                if (image.Functions[functionId] is { } function) CollectFunction(image, functionId, function, samples);
            foreach (var (name, count) in image.Natives.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var attributes = image.Attributes();
                attributes["native.name"] = name;
                samples.Add(new("vm.native", attributes, new()
                {
                    ["calls"] = (ulong)count.Calls,
                    ["fast_calls"] = (ulong)count.Fast
                }));
            }
        }
        images.Clear();
        return samples;
    }

    private static void CollectFunction(ImageCounts image, int functionId, FunctionCounts function, List<RuntimeProfileSample> samples)
    {
        var attributes = image.FunctionAttributes(functionId);
        ulong total = 0;
        for (int index = 0; index < function.Opcodes.Length; index++)
        {
            long count = function.Opcodes[index];
            if (count == 0) continue;
            total += (ulong)count;
            samples.Add(new("vm.opcode", new(attributes) { ["opcode"] = ((OpCode)index).ToString() },
                new() { ["hits"] = (ulong)count }));
        }
        samples.Add(new("vm.function", attributes, new()
        {
            ["calls"] = (ulong)function.Calls,
            ["instructions"] = total,
            ["number_boxes"] = (ulong)function.Boxes
        }));
        if (function.Sites is { } sites)
            for (int pc = 0; pc < sites.Length; pc++)
                if (sites[pc].Hits != 0) CollectSite(image, functionId, pc, sites[pc], attributes, samples);
        if (function.Targets is { } targets)
            foreach (var (target, count) in targets.OrderBy(pair => pair.Key.Pc).ThenBy(pair => pair.Key.Image)
                .ThenBy(pair => pair.Key.Function))
            {
                samples.Add(new("vm.call", new(attributes)
                {
                    ["instruction.pc"] = Number(target.Pc),
                    ["target.image.id"] = Number(target.Image),
                    ["target.function.id"] = Number(target.Function),
                    ["target.function.name"] = target.Name
                }, new() { ["hits"] = (ulong)count.Hits }));
            }
    }

    private static void CollectSite(ImageCounts image, int functionId, int pc, SiteCounts site,
        Dictionary<string, string> function, List<RuntimeProfileSample> samples)
    {
        Instruction instruction = image.Module.Functions[functionId].Instructions[pc];
        Dictionary<string, string> attributes = new(function)
        {
            ["instruction.pc"] = Number(pc),
            ["opcode"] = instruction.Op.ToString()
        };
        if (instruction.Span is { } span)
        {
            attributes["source.file"] = span.Source;
            attributes["source.offset"] = Number(span.Start);
            attributes["source.length"] = Number(span.Length);
        }
        if (instruction.Op == OpCode.NativeCall && instruction.Text is { } name) attributes["native.name"] = name;
        samples.Add(new("vm.instruction", attributes, new() { ["hits"] = (ulong)site.Hits }));
        if (site.Sequential != 0) samples.Add(Edge(attributes, pc + 1, site.Sequential));
        if (site.Target != 0)
            samples.Add(Edge(attributes, instruction.Op == OpCode.IteratorNext ? instruction.B : instruction.A, site.Target));
    }

    private static RuntimeProfileSample Edge(Dictionary<string, string> attributes, int target, long hits) =>
        new("vm.edge", new(attributes) { ["target.pc"] = Number(target) }, new() { ["hits"] = (ulong)hits });

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record ImageIdentity(int Id);

    private sealed class ImageCounts(Module module, int id, bool instructionSites)
    {
        public Module Module { get; } = module;
        public int Id { get; } = id;
        public FunctionCounts?[] Functions { get; } = new FunctionCounts?[module.Functions.Count];
        public ConcurrentDictionary<string, NativeCounts> Natives { get; } = new(StringComparer.Ordinal);

        public FunctionCounts GetFunction(int functionId)
        {
            ref FunctionCounts? slot = ref Functions[functionId];
            var current = Volatile.Read(ref slot);
            if (current is not null) return current;
            var created = new FunctionCounts(instructionSites ? Module.Functions[functionId].Instructions.Count : null);
            return Interlocked.CompareExchange(ref slot, created, null) ?? created;
        }

        public Dictionary<string, string> Attributes() => new()
        {
            ["image.id"] = Number(Id),
            ["artifact.stage"] = Module.Stage.ToString(),
            ["optimization.level"] = Number(Module.OptimizationLevel)
        };

        public Dictionary<string, string> FunctionAttributes(int functionId)
        {
            var result = Attributes();
            result["function.id"] = Number(functionId);
            result["function.name"] = Module.Functions[functionId].Name;
            return result;
        }
    }

    internal sealed class FunctionCounts(int? sites)
    {
        public readonly long[] Opcodes = new long[byte.MaxValue + 1];
        public readonly SiteCounts[]? Sites = sites is { } count ? new SiteCounts[count] : null;
        public readonly ConcurrentDictionary<CallTarget, CallCount>? Targets = sites is null ? null : new();
        public long Calls;
        public long Boxes;
    }

    internal struct SiteCounts
    {
        public long Hits;
        public long Sequential;
        public long Target;
    }

    internal readonly record struct CallTarget(int Pc, int Image, int Function, string Name);

    internal sealed class CallCount
    {
        public long Hits;
    }

    internal sealed class NativeCounts
    {
        public long Calls;
        public long Fast;
    }

    internal interface IFrame
    {
        void Visit(int pc, OpCode op);
        void Call(int pc, Module image, int target);
        void Box();
        double Power(double left, double right, Module image, Instruction instruction);
        RuntimeTimelineScope? BeginInlineNative(Module image, Instruction instruction);
        ValueTask<T> Observe<T>(ValueTask<T> pending, int pc, Instruction instruction, string reason);
        ValueTask Observe(ValueTask pending, int pc, Instruction instruction, string reason);
        Task<T> Observe<T>(Task<T> pending, int pc, Instruction instruction, string reason);
    }

    internal readonly struct DisabledFrame : IFrame
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit(int pc, OpCode op) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Call(int pc, Module image, int target) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Box() { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Power(double left, double right, Module image, Instruction instruction) => NativeRegistry.Power(left, right);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RuntimeTimelineScope? BeginInlineNative(Module image, Instruction instruction) => null;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<T> Observe<T>(ValueTask<T> pending, int pc, Instruction instruction, string reason) => pending;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask Observe(ValueTask pending, int pc, Instruction instruction, string reason) => pending;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Task<T> Observe<T>(Task<T> pending, int pc, Instruction instruction, string reason) => pending;
    }

    internal struct Frame(RuntimeProfile? owner, FunctionCounts? counters, RuntimeFrameTrace? trace) : IFrame
    {
        private int previousBranch = -1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Box()
        {
            if (counters is not null) Interlocked.Increment(ref counters.Boxes);
        }

        public readonly double Power(double left, double right, Module image, Instruction instruction)
        {
            owner?.Native(image, "math.pow", fast: true);
            return trace is null ? NativeRegistry.Power(left, right) : trace.Power(left, right, instruction.Span);
        }

        public readonly RuntimeTimelineScope? BeginInlineNative(Module image, Instruction instruction)
        {
            owner?.Native(image, instruction.Text!, fast: true);
            return trace?.BeginInlineNative(instruction);
        }

        public readonly ValueTask<T> Observe<T>(ValueTask<T> pending, int pc, Instruction instruction, string reason) =>
            trace is null ? pending : trace.Observe(pending, reason, pc, instruction);

        public readonly ValueTask Observe(ValueTask pending, int pc, Instruction instruction, string reason) =>
            trace is null ? pending : trace.Observe(pending, reason, pc, instruction);

        public readonly Task<T> Observe<T>(Task<T> pending, int pc, Instruction instruction, string reason) =>
            trace is null ? pending : trace.Observe(pending, reason, pc, instruction);

        public void Visit(int pc, OpCode op)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.Opcodes[(byte)op]);
            if (counters.Sites is not { } sites) return;
            Interlocked.Increment(ref sites[pc].Hits);
            if (previousBranch >= 0)
            {
                ref SiteCounts from = ref sites[previousBranch];
                if (pc == previousBranch + 1) Interlocked.Increment(ref from.Sequential);
                else Interlocked.Increment(ref from.Target);
            }
            previousBranch = op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext ? pc : -1;
        }

        public readonly void Call(int pc, Module image, int target)
        {
            if (counters?.Targets is not { } targets) return;
            var key = new CallTarget(pc, owner!.Image(image).Id, target, image.Functions[target].Name);
            Interlocked.Increment(ref targets.GetOrAdd(key, static _ => new()).Hits);
        }
    }
}
