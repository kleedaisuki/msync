using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class RuntimeCarrierChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string reason)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(reason);
        }
        TypeRef never = new(TypeKind.Never);
        RuntimeContext context = new(new(WorkingDirectory: Environment.CurrentDirectory));
        async Task<Value> Native(string operation, params Value[] arguments) => await NativeRegistry.InvokeAsync(
            "storage." + operation, arguments, context,
            (_, _, _) => throw new InvalidOperationException("Storage operations do not invoke callbacks"));
        StorageValue owner = (StorageValue)await Native("vector_empty", new TypeValue(TypeRef.Number));
        StorageValue alias = owner;
        RangeValue before = (RangeValue)await Native("vector_snapshot", owner);
        HashSet<Value> identities = [owner];
        int originalHash = owner.GetHashCode();
        await Native("vector_push", alias, new NumberValue(4));
        Check(owner.ElementType == TypeRef.Number && owner.Type == new TypeRef(TypeKind.Storage, TypeRef.Number),
            "The typed constructor establishes a noniterable carrier");
        Check(await Native("vector_get", owner, new NumberValue(0)) is NumberValue { Data: 4 },
            "Aliases observe a shared storage owner");
        Check(before.ItemType == TypeRef.Number, "An empty snapshot retains its declared element type");
        Check(owner.GetHashCode() == originalHash && identities.Contains(owner), "Mutation does not change owner identity");
        RangeValue after = (RangeValue)await Native("vector_take", owner);
        Check(after.ItemType == TypeRef.Number, "The moved snapshot keeps its declared carrier");
        Check(await Native("vector_size", owner) is NumberValue { Data: 0 }, "Taking a snapshot empties the same owner");
        Check(owner.ElementType == TypeRef.Number, "Taking membership does not change the carrier");
        Check(await Count(after) == 1 && await Count(before) == 0, "Snapshots retain independent membership");

        StorageValue inserted = (StorageValue)await Native("vector_empty", new TypeValue(TypeRef.String));
        await Native("vector_insert", inserted, new NumberValue(0), new StringValue("text"));
        Check(await Native("vector_remove", inserted, new NumberValue(0)) is StringValue { Data: "text" },
            "Indexed insertion and removal share the same owner");
        StorageValue typed = (StorageValue)await Native("vector_empty", new TypeValue(TypeRef.Path));
        await Native("vector_push", typed, new StringValue("./item"));
        Check(await Native("vector_get", typed, new NumberValue(0)) is PathValue,
            "A string is converted into the selected Path carrier");

        var sharedItems = new RangeValue(Array.Empty<Value>(), TypeRef.Number).Items;
        RangeValue ordinary = new(sharedItems, TypeRef.Number);
        RangeValue equal = new(sharedItems, TypeRef.Number);
        RangeValue otherType = new(sharedItems, TypeRef.String);
        Check(ordinary == equal && ordinary.GetHashCode() == equal.GetHashCode(), "Equivalent Range descriptors retain equality");
        Check(ordinary != otherType, "Distinct Range descriptors remain distinct");
        ordinary.RetainedValues = [new StringValue("ownership")];
        Check(ordinary == equal && ordinary.GetHashCode() == equal.GetHashCode(), "Ownership roots do not affect value keys");

        ValueTask<Value> Front(RangeValue range) => NativeRegistry.InvokeAsync("ranges.front", [range], context,
            (_, _, _) => throw new InvalidOperationException("Front should consume its selected producer only"));
        var item = new ScopeValue(new Dictionary<string, Value>());
        var owned = RangeValue.TakeSnapshotOwnership([item], TypeRef.Scope);
        Check(ReferenceEquals(await Front(owned), item), "Snapshot head preserves object identity");
        Check(await Front(new RangeValue([Value.Null], TypeRef.Null)) is NullValue, "Null is a present snapshot head");
        int observed = 0;
        async IAsyncEnumerable<Value> Selected()
        {
            observed++;
            yield return Value.True;
            await Task.CompletedTask;
        }
        var changed = owned with { Items = Selected() };
        Check(changed.OwnedSnapshot is null && await Front(changed) is BoolValue { Data: true } && observed == 1,
            "A replaced producer must not inherit the old snapshot evidence");
        try
        {
            await Front(new RangeValue(Array.Empty<Value>(), never));
            throw new InvalidOperationException("An empty snapshot acquired a head");
        }
        catch (MsyncException error) { Check(error.Message.Contains("found no matching item", StringComparison.Ordinal), "Empty head diagnostic"); }

        const string source = """
            let owner = $std::storage::vector_empty(Number);
            let before = $std::storage::vector_snapshot(owner);
            let alias = owner;
            $std::storage::vector_push(alias, 4);
            object {
                items: owner,
                before: before,
                after: $std::storage::vector_snapshot(owner),
            }
            """;
        foreach (int level in new[] { 0, 2, 3 })
        {
            Module module = Compiler.Compile(source, "runtime-carrier.msync", level);
            foreach (Module image in new[] { module, RoundTrip(module), RoundTrip(Binding.Bind(module,
                new(Environment.CurrentDirectory, Environment.CurrentDirectory))) })
            {
                ScopeValue result = (ScopeValue)await Interpreter.ExecuteAsync(image);
                StorageValue items = (StorageValue)result["items"];
                RangeValue prior = (RangeValue)result["before"];
                RangeValue following = (RangeValue)result["after"];
                Check(items.ElementType == TypeRef.Number && items.Type.Kind == TypeKind.Storage, $"Actual runtime type O{level} {image.Stage}");
                Check(prior.ItemType == TypeRef.Number && following.ItemType == TypeRef.Number, $"Snapshot runtime types O{level} {image.Stage}");
                Check(await Count(following) == 1 && await Count(prior) == 0, $"Consumed snapshots O{level} {image.Stage}");
            }

            CompilerSession compiler = new(optimization: level);
            await using ExecutionSession execution = new();
            async Task<Value> Submit(string text)
            {
                Module submission = compiler.Compile(text);
                Value result = await execution.ExecuteAsync(submission);
                compiler.AcceptSubmission(execution.GetLocalValue);
                return result;
            }
            StorageValue live = (StorageValue)await Submit("let owner = $std::storage::vector_empty(Number); owner");
            RangeValue priorSubmission = (RangeValue)await Submit("let saved = $std::storage::vector_snapshot(owner); saved");
            StorageValue afterSubmission = (StorageValue)await Submit("let alias = owner; $std::storage::vector_push(alias, 8); owner");
            Check(ReferenceEquals(live, afterSubmission) && live.ElementType == TypeRef.Number,
                $"A REPL alias preserves the existing typed owner O{level}");
            Check(priorSubmission.ItemType == TypeRef.Number && await Count(priorSubmission) == 0,
                $"An earlier REPL snapshot remains empty O{level}");
            try
            {
                _ = compiler.Compile("$std::storage::vector_push(owner, true);");
                throw new InvalidOperationException("A fixed vector accepted a different payload type");
            }
            catch (CompileException) { checks++; }
        }
        return checks;
    }

    private static async Task<int> Count(RangeValue value)
    {
        int count = 0;
        await foreach (Value _ in value.Items) count++;
        return count;
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }
}
