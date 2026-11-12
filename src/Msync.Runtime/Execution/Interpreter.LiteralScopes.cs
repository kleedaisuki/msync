using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private static readonly ConditionalWeakTable<Module, LiteralScopeRecipes> literalScopeImages = new();

    private static LiteralScopeProducer?[]? PlanLiteralScopes(Module image, IReadOnlyList<Value> values, string? mask,
        out bool allLiteral)
    {
        LiteralScopeRecipes? recipes = null;
        LiteralScopeProducer?[]? plan = null;
        int remaining = values.Count;
        for (int i = 0; i < values.Count; i++)
        {
            if (mask is not null && mask[i] != '1') continue;
            if (values[i] is not FunctionValue { NativeName: null, Captures: null or { Count: 0 } } function
                || (uint)function.Function >= (uint)image.Functions.Count) continue;
            recipes ??= literalScopeImages.GetValue(image, static bound => new(bound));
            if (recipes.Get(function.Function) is { } producer)
            {
                (plan ??= new LiteralScopeProducer?[values.Count])[i] = producer;
                remaining--;
            }
        }
        allLiteral = remaining == 0;
        return plan;
    }

    private LiteralScopeItems? ReadonlyLiteralItems(Module image, RangeValue range) =>
        range.Resolve is null && range.Selection is null && range.Items is LiteralScopeItems items && items.OwnedBy(this, image)
            ? items : null;

    // A record copy can replace RangeValue.Items. The proof belongs to the
    // enumerable itself, not to the enclosing Range's metadata.
    private sealed class LiteralScopeItems(Interpreter owner, Module image, LiteralScopeProducer?[] rows,
        CancellationToken producerToken, IAsyncEnumerable<Value> items) : IAsyncEnumerable<Value>
    {
        internal int Count => rows.Length;
        internal CancellationToken ProducerToken => producerToken;
        internal ScopeLiteralTemplate Template(int index) => rows[index]!.Template;
        internal int FunctionId(int index) => rows[index]!.FunctionId;
        internal bool OwnedBy(Interpreter interpreter, Module currentImage) =>
            ReferenceEquals(owner, interpreter) && ReferenceEquals(image, currentImage);

        public IAsyncEnumerator<Value> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            items.GetAsyncEnumerator(cancellationToken);
    }

    private ScopeValue PullLiteralScope(Module image, LiteralScopeProducer producer)
    {
        var scope = context.Timeline is { } timeline ? ObserveLiteralScope(timeline, producer) : producer.Template.Create();
        profile?.LiteralScope(image, producer.FunctionId);
        return scope;
    }

    private static ScopeValue ObserveLiteralScope(RuntimeTimeline timeline, LiteralScopeProducer producer)
    {
        using var span = timeline.Begin("vm.literal_scope");
        span.SetAttribute("function.id", producer.FunctionId.ToString(CultureInfo.InvariantCulture));
        span.SetSource(producer.Source);
        return producer.Template.Create();
    }

    private sealed record LiteralScopeProducer(int FunctionId, ScopeLiteralTemplate Template, SourceSpan? Source);

    private sealed class LiteralScopeRecipes(Module image)
    {
        private readonly ConcurrentDictionary<int, LiteralScopeProducer?> functions = new();

        public LiteralScopeProducer? Get(int functionId) => functions.GetOrAdd(functionId,
            static (id, recipes) => recipes.Build(id), this);

        private LiteralScopeProducer? Build(int functionId)
        {
            var function = image.Functions[functionId];
            var code = function.Instructions;
            if (function.ParameterCount != 0 || function.CaptureCount != 0 || function.LocalCount != 0
                || code.Count < 2 || code[^1].Op != OpCode.Return
                || code[^2] is not { Op: OpCode.NewScope } make)
                return null;

            var layout = BoundScopePlaces.For(image).Layout(make.A);
            if (layout.Count != code.Count - 2) return null;
            Value[] fields = new Value[layout.Count];
            for (int i = 0; i < fields.Length; i++)
            {
                var instruction = code[i];
                Value? value = instruction.Op switch
                {
                    OpCode.Constant when (uint)instruction.A < (uint)image.Constants.Count => image.Constants[instruction.A],
                    OpCode.Null => Value.Null,
                    OpCode.True => Value.True,
                    OpCode.False => Value.False,
                    _ => null
                };
                if (value is null) return null;
                fields[i] = value;
            }

            return ScopeLiteralTemplate.TryCreate(layout, fields, make.B) is { } template
                ? new(functionId, template, make.Span) : null;
        }
    }
}
