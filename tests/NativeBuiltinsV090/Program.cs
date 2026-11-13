using System.Globalization;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

internal static class Program
{
    private static readonly HashSet<string> exercised = new(StringComparer.Ordinal);
    private static readonly string root = FindRoot();
    private static readonly string work = Path.Combine(root, ".temp", "native-builtins-v090", Guid.NewGuid().ToString("N"));
    private static readonly StringWriter output = new(CultureInfo.InvariantCulture);
    private static readonly NativeRuntimeContext context = new(new ExecutionOptions(work, Output: output, Error: output)
    { Input = new StringReader("first\nsecond\n") });
    private static int checks;

    private static async Task Main()
    {
        Directory.CreateDirectory(work);
        NativeHostIO.Attach(context);
        await ScalarAndText();
        await Ranges();
        await MetadataAndFormatting();
        await ActualFilesAndDirectoryCapture();
        await FailureAndDemandBoundaries();
        Check(exercised.Count >= 100, "coverage records actual builtin calls");
        Console.WriteLine($"PASS native builtin bindings: {checks} assertions, {exercised.Count} operations; workspace {work}");
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "msync.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run from the repository.");
    }

    private static async ValueTask<NativeValue> Call(string operation, params NativeValue[] args)
    {
        exercised.Add(operation);
        return await NativeBuiltins.InvokeAsync(operation, args, context, Invoke, CancellationToken.None);
    }

    private static ValueTask<NativeValue> Invoke(NativeValue callback, IReadOnlyList<NativeValue> args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(callback.AsFunction().Function switch
        {
            0 => NativeValue.FromNumber(args[0].AsNumber() * 2),
            1 => NativeValue.FromBool(args[0].AsNumber() % 2 == 0),
            2 => NativeValue.FromBool(args[0].AsNumber() < args[1].AsNumber()),
            3 => NativeValue.FromNumber(args[0].AsNumber() + args[1].AsNumber()),
            _ => throw new InvalidOperationException("Unknown checked callback")
        });
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal(NativeValue value, double expected, string message) => Check(
        value.Kind == NativeKind.Number && (double.IsNaN(expected) ? double.IsNaN(value.AsNumber())
            : BitConverter.DoubleToUInt64Bits(value.AsNumber()) == BitConverter.DoubleToUInt64Bits(expected)), message);
    private static void Equal(NativeValue value, string expected, string message) => Check(value.Kind == NativeKind.String && value.AsString() == expected, message);
    private static void Equal(NativeValue value, bool expected, string message) => Check(value.Kind == NativeKind.Bool && value.AsBool() == expected, message);
    private static NativeValue Range(params NativeValue[] values) => NativeValue.FromRange(NativeRange.Snapshot(values,
        values.Aggregate(new TypeRef(TypeKind.Never), static (type, value) => TypeRef.Union(type, value.Type))));
    private static async Task<double[]> Numbers(NativeValue value)
    {
        List<double> result = [];
        await foreach (NativeValue item in value.AsRange().Items) result.Add(item.AsNumber());
        return result.ToArray();
    }
    private static async Task<string[]> Strings(NativeValue value)
    {
        List<string> result = [];
        await foreach (NativeValue item in value.AsRange().Items) result.Add(item.AsString());
        return result.ToArray();
    }
    private static async Task Sequence(NativeValue value, params double[] expected) => Check(
        (await Numbers(value)).SequenceEqual(expected), "checked numeric sequence");

    private static async Task ScalarAndText()
    {
        foreach ((string name, bool expected) in new[] { ("identity", true), ("negate", false) }) Equal(await Call("bool." + name, true), expected, name);
        foreach ((string name, bool expected) in new[] { ("both", false), ("either", true), ("xor", true), ("implies", false) })
            Equal(await Call("bool." + name, true, false), expected, name);
        foreach (Msync.Core.NativeSignature signature in NativeSignatures.All.Where(s => s.Name.StartsWith("math.", StringComparison.Ordinal)))
        {
            NativeValue[] args = signature.Parameters.Select((_, index) => NativeValue.FromNumber(index == 0 ? 0.5 : index == 1 ? 0.25 : 2)).ToArray();
            NativeValue value = await Call(signature.Name, args);
            Check(signature.Result.Accepts(value.Type), signature.Name + " result type");
        }
        Equal(await Call("math.round", -2.5), -3, "round away from zero");
        Equal(await Call("math.round_even", 2.5), 2, "round ties even");
        Equal(await Call("math.nextafter", 0.0, -1.0), double.BitDecrement(0), "nextafter");
        Equal(await Call("math.copysign", 0.0, -1.0), -0.0, "negative zero");
        foreach (Msync.Core.NativeSignature signature in NativeSignatures.All.Where(s => s.Name.StartsWith("bits.", StringComparison.Ordinal)))
        {
            NativeValue[] args = signature.Parameters.Select((_, index) => NativeValue.FromNumber(index == 0 ? 0xf0f0a55a : 7)).ToArray();
            Check((await Call(signature.Name, args)).Kind == NativeKind.Number, signature.Name);
        }
        Equal(await Call("bits.bit_xor", 0xf0f0a55a, 0x0f0f5aa5), uint.MaxValue, "xor words");
        Equal(await Call("string.length", "a😀ß"), 3, "Unicode scalar length");
        Equal(await Call("string.utf8_size", "a😀ß"), 7, "UTF8 byte length");
        Equal(await Call("string.char_at", "a😀ß", 1), "😀", "scalar indexing");
        Equal(await Call("string.substring", "a😀ß", 1, 1), "😀", "scalar substring");
        Equal(await Call("string.to_uppercase", "straße"), "STRASSE", "Unicode full uppercase");
        Equal(await Call("string.to_lowercase", "ABC"), "abc", "lowercase");
        Equal(await Call("string.contains", "abc", "b"), true, "contains");
        Equal(await Call("string.starts_with", "abc", "ab"), true, "starts_with");
        Equal(await Call("string.ends_with", "abc", "bc"), true, "ends_with");
        Equal(await Call("string.concat", "a", "b"), "ab", "concat");
        Equal(await Call("string.trim", " a "), "a", "trim");
        Equal(await Call("string.trim_start", " a "), "a ", "trim_start");
        Equal(await Call("string.trim_end", " a "), " a", "trim_end");
        Equal(await Call("string.replace", "abab", "a", "x"), "xbxb", "replace");
        Equal(await Call("string.empty", ""), true, "empty");
        Equal(await Call("string.equal", "a", "a"), true, "equal");
        Equal(await Call("string.is_ascii", "😀"), false, "ascii");
        Equal(await Call("string.find", "a😀b😀", "😀"), 1, "find scalar offset");
        Equal(await Call("string.rfind", "a😀b😀", "😀"), 3, "rfind scalar offset");
        Equal(await Call("string.compare", "a", "b"), -1, "compare");
        Equal(await Call("string.repeat", "ab", 3), "ababab", "repeat");
        Check((await Strings(await Call("string.chars", "a😀ß"))).SequenceEqual(["a", "😀", "ß"]), "chars");
        Check((await Strings(await Call("string.lines", "a\r\nb\nc"))).SequenceEqual(["a", "b", "c"]), "lines");
        Check((await Strings(await Call("string.split_whitespace", " a\t😀\n b "))).SequenceEqual(["a", "😀", "b"]), "words");
        Check((await Strings(await Call("string.split", "a::b::", "::"))).SequenceEqual(["a", "b", ""]), "split");
        Equal(await Call("string.join", Range("a", "😀", "b"), ":"), "a:😀:b", "join");
        Equal(await Call("cast", "-0", TypeRef.Number), -0.0, "cast signed zero");
        Equal(await Call("cast", "0xff", TypeRef.Number), 255, "cast radix");
        Equal(await Call("cast", true, TypeRef.Number), 1, "cast bool");
        Equal(await Call("cast", 0.0, TypeRef.Bool), false, "cast bool zero");
        NativeValue endpoint = await Call("cast", "ssh://alias", TypeRef.Connection);
        Check(endpoint.AsConnection() == new NativeConnection(ConnectionKind.Ssh, "alias"), "connection descriptor without authentication");
        NativeValue path = await Call("cast", "./relative", TypeRef.Path);
        Check(path.AsPath().WorkingDirectory == work, "Path captures directory");
        Equal(await Call("cast", path, TypeRef.String), "./relative", "Path raw conversion");
        Check((await Call("path.identity", path)).AsPath() == path.AsPath(), "path identity");
        Check((await Call("path.join", path, "next")).AsPath().Raw == "./relative/next", "symbolic join");
        Check((await Call("path.qualify", endpoint, path)).AsPath().Connection == endpoint.AsConnection(), "endpoint qualification preserves provenance");
    }

    private static async Task Ranges()
    {
        NativeValue values = Range(1, 2, 3, 4);
        await Sequence(await Call("ranges.take", values, 2), 1, 2);
        await Sequence(await Call("ranges.drop", values, 2), 3, 4);
        await Sequence(await Call("ranges.stride", values, 2), 1, 3);
        await Sequence(await Call("ranges.step", values, 3), 1, 4);
        await Sequence(await Call("ranges.reverse", values), 4, 3, 2, 1);
        await Sequence(await Call("ranges.unique", Range(1, 1, 2, 1)), 1, 2);
        await Sequence(await Call("ranges.transform", values, new NativeFunction(0)), 2, 4, 6, 8);
        await Sequence(await Call("ranges.filter", values, new NativeFunction(1)), 2, 4);
        await Sequence(await Call("ranges.take_while", Range(2, 4, 5, 6), new NativeFunction(1)), 2, 4);
        await Sequence(await Call("ranges.drop_while", Range(2, 4, 5, 6), new NativeFunction(1)), 5, 6);
        Equal(await Call("ranges.front", values), 1, "front"); Equal(await Call("ranges.first", values), 1, "first");
        Equal(await Call("ranges.back", values), 4, "back"); Equal(await Call("ranges.last", values), 4, "last");
        Equal(await Call("ranges.at", values, 2), 3, "at"); Equal(await Call("ranges.size", values), 4, "size");
        Equal(await Call("ranges.find", values, 3), 3, "find"); Equal(await Call("ranges.find_if", values, new NativeFunction(1)), 2, "find_if");
        Equal(await Call("ranges.empty", values), false, "empty"); Equal(await Call("ranges.contains", values, 3), true, "contains");
        foreach (string name in new[] { "any", "any_of", "all_of", "none", "none_of" })
            Equal(await Call("ranges." + name, values, new NativeFunction(1)), name is "any" or "any_of", name);
        await Sequence(await Call("ranges.of", 2, 3), 2, 3); await Sequence(await Call("ranges.single", 7), 7);
        await Sequence(await Call("ranges.empty_of", TypeRef.Number));
        await Sequence(await Call("ranges.repeat", 4, 3), 4, 4, 4); await Sequence(await Call("ranges.sequence", 1, 6, 2), 1, 3, 5);
        await Sequence(await Call("ranges.concat", Range(1, 2), Range(3, 4)), 1, 2, 3, 4);
        await Sequence(await Call("ranges.flatten", Range(Range(1, 2), Range(3))), 1, 2, 3);
        await Sequence(await Call("ranges.scan", values, 0, new NativeFunction(3)), 1, 3, 6, 10);
        await Sequence(await Call("ranges.sort", Range(4, 2, 1, 3)), 1, 2, 3, 4);
        await Sequence(await Call("ranges.sort_with", Range(4, 2, 1, 3), new NativeFunction(2)), 1, 2, 3, 4);
        Equal(await Call("ranges.less", 1, 2), true, "natural comparator");
        Check((await Call("ranges.identity", values)).AsRange() == values.AsRange(), "identity retains producer");
        Check((await Call("ranges.all", values)).AsRange() == values.AsRange(), "all retains producer");
        foreach ((string operation, double[] expected) in new[] {
            ("merge", new double[] { 1, 2, 3, 3, 5, 6 }), ("set_union", [1, 2, 3, 5, 6]),
            ("set_intersection", [3]), ("set_difference", [1, 5]), ("set_symmetric_difference", [1, 2, 5, 6]) })
        {
            await Sequence(await Call("ranges." + operation, Range(1, 3, 5), Range(2, 3, 6)), expected);
            await Sequence(await Call("ranges." + operation + "_with", Range(1, 3, 5), Range(2, 3, 6), new NativeFunction(2)), expected);
        }
        NativeValue zipped = await Call("ranges.zip", values, Range("a", "b"));
        int pairs = 0;
        await foreach (NativeValue pair in zipped.AsRange().Items)
        { Equal(pair.AsAddress().Field(0).Load(), ++pairs, "zip first"); Check(pair.AsAddress().Field(1).Load().Kind == NativeKind.String, "zip second"); }
        Check(pairs == 2, "zip stops at shortest");
        NativeValue indexed = await Call("ranges.enumerate_from", Range("a", "b"), 7);
        int position = 7;
        await foreach (NativeValue pair in indexed.AsRange().Items) Equal(pair.AsAddress().Field(0).Load(), position++, "enumerate_from");
        foreach (string name in new[] { "chunk", "slide" })
        {
            List<double[]> windows = [];
            await foreach (NativeValue window in (await Call("ranges." + name, values, 3)).AsRange().Items) windows.Add(await Numbers(window));
            Check(windows.Count == 2 && windows[0].SequenceEqual([1, 2, 3]), name);
        }
        int evaluations = 0;
        NativeRange deferred = NativeRangeDemand.Defer(token =>
        { token.ThrowIfCancellationRequested(); evaluations++; return ValueTask.FromResult(NativeRange.Snapshot([1, 2], TypeRef.Number)); }, TypeRef.Number);
        await Sequence(await Call("ranges.materialize", await Call("ranges.take", deferred, 0)));
        Check(evaluations == 0, "zero demand does not execute producer");
        Equal(await Call("ranges.size", deferred), 2, "first demand"); Equal(await Call("ranges.size", deferred), 2, "repeated demand");
        Check(evaluations == 1, "transparent producer evaluated once");
        NativeValue unique = await Call("ranges.unique", Range(0.0, -0.0, double.NaN,
            BitConverter.UInt64BitsToDouble(0x7ff8000000000042)));
        Check((await Numbers(unique)).Length == 2, "container equality retains numeric equality, not bitwise storage comparison");
    }

    private static async Task MetadataAndFormatting()
    {
        Equal(await Call("meta.is_range", new TypeRef(TypeKind.Range, TypeRef.Number)), true, "meta range");
        Equal(await Call("meta.is_union", TypeRef.Number.WithNull), true, "meta union");
        Equal(await Call("meta.is_scalar", TypeRef.Connection), true, "meta scalar");
        Equal(await Call("meta.is_same", TypeRef.Path, TypeRef.Path), true, "same type");
        Equal(await Call("meta.accepts", TypeRef.Number.WithNull, TypeRef.Null), true, "type admission");
        Equal(await Call("meta.is_convertible", TypeRef.Path, TypeRef.String), true, "implicit conversion");
        Equal(await Call("meta.is_castable", TypeRef.String, TypeRef.Number), true, "explicit conversion");
        Check((await Call("meta.add_null", TypeRef.Number)).AsType() == TypeRef.Number.WithNull, "add null");
        Check((await Call("meta.remove_null", TypeRef.Number.WithNull)).AsType() == TypeRef.Number, "remove null");
        Check((await Call("meta.common_type", TypeRef.Path, TypeRef.String)).AsType().Kind == (TypeKind.Path | TypeKind.String), "common type");
        Check((await Call("meta.common_type_of", TypeRef.Path, TypeRef.String, TypeRef.Null)).AsType().Nullable, "common variadic type");
        Check((await Call("meta.range_of", TypeRef.Number)).AsType().Element == TypeRef.Number, "range_of");
        Check((await Call("meta.element_type", new TypeRef(TypeKind.Range, TypeRef.Number))).AsType() == TypeRef.Number, "element_type");
        Equal(await Call("meta.range_rank", new TypeRef(TypeKind.Range, new(TypeKind.Range, TypeRef.Number))), 2, "range rank");
        Equal(await Call("meta.type_name", TypeRef.Number), "Number", "type name");
        Check((await Call("meta.alternatives", TypeRef.Number.WithNull)).AsRange().SnapshotValues?.Count == 2, "alternatives");
        NativeValue operation = new NativeFunction(-1, NativeName: "math.floor");
        Equal(await Call("meta.parameter_count", operation), 1, "signature arity");
        Equal(await Call("meta.is_variadic", operation), false, "signature variadic");
        Check((await Call("meta.parameter_types", operation)).AsRange().SnapshotValues?.Count == 1, "signature parameters");
        Check((await Call("meta.signature_of", operation)).Kind == NativeKind.Address, "native signature namespace");
        Equal(await Call("meta.is_invocable", operation, TypeRef.Number), true, "invocable query");
        Equal(await Call("meta.is_invocable_r", TypeRef.Number, operation, TypeRef.Number), true, "result query");
        Check((await Call("meta.invoke_result", operation, TypeRef.Number)).AsType() == TypeRef.Number, "invoke result");
        Check((await Call("meta.try_invoke_result", operation, TypeRef.Connection)).Kind == NativeKind.Null, "invalid query no execution");
        NativeLayout layout = new([new NativeField("number", NativeType.Of(NativeKind.Number)), new NativeField("name", NativeType.Of(NativeKind.String))]);
        NativeAllocation namespaceStorage = new(layout);
        namespaceStorage.Field(0).Store(42); namespaceStorage.Field(1).Store("Klee");
        NativeValue scope = namespaceStorage.Address;
        Equal(await Call("meta.has_member", scope, "name"), true, "member existence");
        Check((await Call("meta.member_type", scope, "name")).AsType() == TypeRef.String, "member type");
        Equal(await Call("meta.member_count", scope), 2, "member count");
        Check((await Strings(await Call("meta.member_names", scope))).SequenceEqual(["name", "number"]), "member order");
        Check((await Call("meta.members_of", scope)).AsRange().SnapshotValues?.Count == 2, "member descriptors");
        Equal(await Call("text.format", "{0:#x} {1:>5}", 42, "hi"), "0x2a    hi", "numeric and Unicode-invariant format");
        Equal(await Call("text.format_fields", "{name}:{number}", scope), "Klee:42", "selected field formatting");
        NativeValue chunks = await Call("text.append_chunk", Range("a", "b"), "c");
        Check((await Strings(chunks)).SequenceEqual(["a", "b", "c"]), "persistent chunk producer");
        Equal(await Call("regex.is_match", "abc12", "[0-9]+"), true, "regex match");
        Equal(await Call("regex.escape", ".*"), "\\.\\*", "regex escape");
        Equal(await Call("regex.replace", "a1b2", "[0-9]", "x"), "axbx", "regex replace");
        Check((await Strings(await Call("regex.split", "a1b2", "[0-9]"))).SequenceEqual(["a", "b", ""]), "regex split");
        Check((await Call("regex.find", "😀abc", "abc")).AsAddress().Field(1).Load().AsNumber() == 1, "regex scalar offset");
        int matches = 0; await foreach (NativeValue unused in (await Call("regex.matches", "a1b2", "[0-9]")).AsRange().Items) matches++;
        Check(matches == 2, "regex rows");
        Check((await Strings(await Call("shell.tail_lines", Range("a", "b", "c"), 2))).SequenceEqual(["b", "c"]), "tail lines");
        Check((await Strings(await Call("shell.unique_lines", Range("a", "a", "b", "a")))).SequenceEqual(["a", "b", "a"]), "adjacent unique lines");
        Check((await Call("shell.process_table")).AsRange().SnapshotValues is { Count: > 0 }, "actual process table");
    }

    private static async Task ActualFilesAndDirectoryCapture()
    {
        string key = "MSYNC_NATIVE_BINDING_" + Guid.NewGuid().ToString("N");
        await Call("environment.set", key, "value"); Equal(await Call("environment.get", key), "value", "actual environment");
        Check((await Call("environment.variables")).AsRange().SnapshotValues is { Count: > 0 }, "eager environment snapshot");
        await Call("environment.unset", key); Check((await Call("environment.get", key)).Kind == NativeKind.Null, "unset");
        foreach (string name in new[] { "operating_system", "newline", "path_separator" }) Check((await Call("environment." + name)).Kind == NativeKind.String, name);
        Check((await Call("environment.home_directory")).Kind == NativeKind.Path, "home");
        Check((await Call("environment.working_directory")).AsPath().Raw == work, "session cwd");
        await Call("io.write_text", "one.txt", "payload"); Equal(await Call("io.read_text", "one.txt"), "payload", "real atomic publication");
        NativeRuntimeContext captured = context.CaptureDirectory();
        string child = Path.Combine(work, "child"); Directory.CreateDirectory(child);
        await Call("environment.set_working_directory", NativeValue.FromPath(new NativePathValue(child)));
        Check(captured.WorkingDirectory == work && context.WorkingDirectory == child, "directory capture does not reanchor");
        Check(Environment.CurrentDirectory != child, "session cwd never changes process cwd");
        Check(captured.TransferAsync is not null, "captured host callback");
        await captured.TransferAsync!(NativeValue.FromPath(new("one.txt", captured.WorkingDirectory)), new("copied.txt", captured.WorkingDirectory), captured, CancellationToken.None);
        Check(File.ReadAllText(Path.Combine(work, "copied.txt")) == "payload" && !File.Exists(Path.Combine(child, "copied.txt")), "host callback uses passed captured context");
        await Call("io.println", "visible"); Check(output.ToString().Contains("visible", StringComparison.Ordinal), "injected output stream");
    }

    private static async Task FailureAndDemandBoundaries()
    {
        NativeBuiltinOperation floor = NativeBuiltins.Resolve("math.floor");
        Check(ReferenceEquals(floor, NativeBuiltins.Resolve("math.floor")), "native symbols resolve once without caller state");
        Equal(await NativeBuiltins.InvokeProducedAsync(floor, [1.9], context, Invoke), 1, "certified native call uses prebound handler");
        await Reject("math.floor", [], "expects 1");
        await Reject("text.format", [], "at least 1");
        await Reject("regex.is_match", ["x", "["], "regex.is_match:");
        NativeLayout layout = new([new NativeField("selected", NativeType.Of(NativeKind.Number)),
            new NativeField("deferred", NativeType.Of(NativeKind.Range))]);
        NativeAllocation allocation = new(layout);
        int calls = 0;
        NativeRange deferred = NativeRangeDemand.Defer(_ =>
        { calls++; throw new InvalidOperationException("unselected producer must not execute"); }, TypeRef.String);
        allocation.Field(0).Store(7);
        allocation.Field(1).Store(deferred);
        Equal(await Call("text.format_fields", "{selected}", allocation.Address), "7", "selected formatting does not inspect an unrelated producer");
        Check(calls == 0, "formatting has no hidden enumeration");
        await Reject("text.format_fields", ["{missing}", allocation.Address], "Scope has no member 'missing'");
        TaskCompletionSource<NativeProcess> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeProcess process = new(-1, "", "") { Completion = pending.Task };
        Equal(await Call("text.format", "{}", process), "Process(deferred)", "display does not wait for process completion");
        Check(!pending.Task.IsCompleted, "formatting leaves the pending process untouched");
        await Sequence(await Call("cast", Range("1", "2"), new TypeRef(TypeKind.Range, TypeRef.Number)), 1, 2);
        NativeRuntimeContext preview = new(new ExecutionOptions(work, DryRun: true, Output: output, Error: output));
        NativeHostIO.Attach(preview);
        NativeValue emptyArgs = Range();
        NativeValue simulated = await NativeBuiltins.InvokeAsync("process.spawn", [NativeValue.FromPath(new("definitely-not-a-real-program")), emptyArgs], preview, Invoke);
        Check(simulated.Kind == NativeKind.Process && simulated.AsProcess().ExitCode == 0, "dryrun preserves Process result without launch");
    }

    private static async Task Reject(string operation, NativeValue[] args, string message)
    {
        try { await Call(operation, args); }
        catch (MsyncException error)
        {
            Check(error.Message.Contains(message, StringComparison.Ordinal), operation + " retains the actual diagnostic: " + error.Message);
            return;
        }
        throw new InvalidOperationException(operation + " unexpectedly succeeded");
    }
}
