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
        RangeValue buffer = (RangeValue)await Native("buffer_new", new RangeValue(Array.Empty<Value>(), never));
        RangeValue alias = buffer;
        RangeValue before = (RangeValue)await Native("buffer_snapshot", buffer);
        HashSet<Value> identities = [buffer];
        int originalHash = buffer.GetHashCode();
        await Native("buffer_push", alias, new NumberValue(4));
        Check(buffer.ItemType == TypeRef.Number && buffer.Type == new TypeRef(TypeKind.Range, TypeRef.Number), "First write resolves the runtime carrier");
        Check(alias.ItemType == TypeRef.Number && ReferenceEquals(alias, buffer), "Aliases observe one carrier solution");
        Check(before.ItemType == never, "A prior empty snapshot keeps its carrier");
        Check(buffer.GetHashCode() == originalHash && identities.Contains(buffer), "Carrier inference preserves hashed identities");
        RangeValue after = (RangeValue)await Native("buffer_snapshot", buffer);
        Check(after.ItemType == TypeRef.Number, "Snapshot publishes the solved carrier");
        await Native("buffer_clear", buffer);
        Check(buffer.ItemType == TypeRef.Number, "Clearing does not reopen a solved carrier");
        Check(await Count(after) == 1 && await Count(before) == 0, "Snapshots retain independent membership");

        RangeValue inserted = (RangeValue)await Native("buffer_new", new RangeValue(Array.Empty<Value>(), never));
        await Native("buffer_insert", inserted, new NumberValue(0), new StringValue("text"));
        Check(inserted.ItemType == TypeRef.String, "An insertion can resolve an empty carrier");
        RangeValue typed = (RangeValue)await Native("buffer_new", new RangeValue(Array.Empty<Value>(), TypeRef.Path));
        await Native("buffer_push", typed, new PathValue("./item"));
        Check(typed.ItemType == TypeRef.Path, "A supplied carrier does not change");

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
            let buffer = $std::storage::Buffer::new([]);
            let before = $std::storage::Buffer::snapshot(buffer);
            let alias = buffer;
            $std::storage::Buffer::push(alias, 4);
            scope {
                items: buffer,
                before: before,
                after: $std::storage::Buffer::snapshot(buffer),
            }
            """;
        foreach (int level in new[] { 0, 2, 3 })
        {
            Module module = Compiler.Compile(source, "runtime-carrier.msync", level);
            foreach (Module image in new[] { module, RoundTrip(module), RoundTrip(Binding.Bind(module,
                new(Environment.CurrentDirectory, Environment.CurrentDirectory))) })
            {
                ScopeValue result = (ScopeValue)await Interpreter.ExecuteAsync(image);
                RangeValue items = (RangeValue)result["items"];
                RangeValue prior = (RangeValue)result["before"];
                RangeValue following = (RangeValue)result["after"];
                Check(items.ItemType == TypeRef.Number && items.Type.Element == TypeRef.Number, $"Actual runtime type O{level} {image.Stage}");
                Check(prior.ItemType == never && following.ItemType == TypeRef.Number, $"Snapshot runtime types O{level} {image.Stage}");
                Check(await Count(following) == 1 && await Count(prior) == 0, $"Consumed snapshots O{level} {image.Stage}");
            }

            CompilerSession compiler = new(optimization: level);
            ExecutionSession execution = new();
            async Task<Value> Submit(string text)
            {
                Module submission = compiler.Compile(text);
                Value result = await execution.ExecuteAsync(submission);
                compiler.AcceptSubmission(execution.GetLocalValue);
                return result;
            }
            RangeValue live = (RangeValue)await Submit("let buffer = $std::storage::Buffer::new([]); buffer");
            RangeValue priorSubmission = (RangeValue)await Submit("let saved = $std::storage::Buffer::snapshot(buffer); saved");
            RangeValue afterSubmission = (RangeValue)await Submit("let alias = buffer; $std::storage::Buffer::push(alias, 8); buffer");
            Check(ReferenceEquals(live, afterSubmission) && live.ItemType == TypeRef.Number,
                $"An already created REPL Range receives its carrier O{level}");
            Check(priorSubmission.ItemType == never && await Count(priorSubmission) == 0,
                $"An earlier REPL snapshot remains empty O{level}");
            try
            {
                _ = compiler.Compile("$std::storage::Buffer::push(buffer, true);");
                throw new InvalidOperationException("A solved buffer accepted a different payload type");
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
