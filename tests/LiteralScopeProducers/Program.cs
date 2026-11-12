using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

const string hitMetric = "literal_factories";
int checks = 0;
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "literal-scope-producers", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await SourceAndArtifacts();
    await OpaqueFailureRemainsDeferred();
    await ConcurrentFirstWrites();
    await RetainedSession();
    await LiteralScopeWitnessChecks.Run(root);
    Console.WriteLine($"PASS: {checks} literal Scope producer contracts.");
}
finally { Directory.Delete(root, recursive: true); }

async Task SourceAndArtifacts()
{
    using Stream fixture = typeof(Program).Assembly.GetManifestResourceStream("literal-demand.msync")
        ?? throw new InvalidOperationException("Missing literal demand fixture.");
    using StreamReader reader = new(fixture);
    string source = reader.ReadToEnd();
    Module compiled = Compiler.Compile(source, "literal-scope-demand", 2);
    string portableFile = Path.Combine(root, "literal.mso");
    Artifact.Write(compiled, portableFile);
    Module portable = Artifact.Read(portableFile, formBlocks: false);
    Module bound = Binding.Bind(portable, new(root, root));
    string executableFile = Path.Combine(root, "literal.mexe");
    Artifact.Write(bound, executableFile);
    Check(portable.Stage == ArtifactStage.Relocatable, "portable artifact remains independently readable");
    Check(Artifact.Read(executableFile).Stage == ArtifactStage.Executable, "bound artifact remains independently readable");
    foreach ((string route, Module image) in new[]
    {
        ("source", compiled),
        ("object", portable),
        ("executable", Artifact.Read(executableFile, formBlocks: false)),
    })
    {
        RuntimeProfile profile = new(instructionSites: true);
        ConcurrentQueue<RuntimeTimelineRecord> events = new();
        using StringWriter output = new();
        var options = new ExecutionOptions(WorkingDirectory: root, Output: output)
        {
            Profile = profile,
            Timeline = new(events.Enqueue, "root")
        };
        ScopeValue result = (ScopeValue)await Interpreter.ExecuteAsync(image, options);
        ScopeValue first = (ScopeValue)result["first"];
        ScopeValue alias = (ScopeValue)result["alias"];
        ScopeValue fresh = (ScopeValue)result["fresh"];
        Check(ReferenceEquals(first, alias) && alias["x"] is NumberValue { Data: 9 }, $"{route}: aliases see first write");
        Check(!ReferenceEquals(first, fresh) && fresh["x"] is NumberValue { Data: 1 }
            && fresh["label"] is StringValue { Data: "literal" }, $"{route}: repeated enumeration creates a fresh identity");
        Check(result["before_tail"] is NumberValue { Data: 0 } && result["after_tail"] is NumberValue { Data: 1 }
            && result["calls"] is NumberValue { Data: 2 }, $"{route}: an effectful suffix is neither prefetched nor cached");
        Check(((ScopeValue)result["tail"])["x"] is NumberValue { Data: 1 }
            && ((ScopeValue)result["next_tail"])["x"] is NumberValue { Data: 2 }, $"{route}: demanded suffix sees live captures");
        Check(output.ToString() == string.Join(Environment.NewLine, ["9", "1", "literal", "0", "1", "1", "2", "2", ""]),
            $"{route}: user output retains demand order");

        var samples = profile.Drain();
        var fast = samples.Where(sample => sample.Name == "vm.function" && sample.Metrics.GetValueOrDefault(hitMetric) != 0).ToArray();
        Check(fast is [var recipe] && recipe.Metrics[hitMetric] == 4 && recipe.Metrics["calls"] == 0,
            $"{route}: all four literal pulls bypass physical frames");
        Check(fast[0].Metrics["instructions"] == 16, $"{route}: logical constructor opcodes are preserved");
        string function = fast[0].Attributes["function.id"];
        var sites = samples.Where(sample => sample.Name == "vm.instruction"
            && sample.Attributes["function.id"] == function).ToArray();
        Check(sites.Length == 4 && sites.All(sample => sample.Metrics["hits"] == 4),
            $"{route}: per-PC constructor counts are preserved");
        var spans = events.Where(record => record.Name == "vm.literal_scope").ToArray();
        Check(spans.Length == 4 && spans.All(record => record.DurationNanoseconds >= 0 && record.Outcome == "ok"),
            $"{route}: each recipe pull retains a timed diagnostic span");
        SourceSpan constructor = image.Functions[int.Parse(function, System.Globalization.CultureInfo.InvariantCulture)]
            .Instructions.Single(instruction => instruction.Op == OpCode.NewScope).Span
            ?? throw new InvalidOperationException("Expected literal constructor provenance.");
        Check(spans.All(record => record.Attributes.GetValueOrDefault("source.file") == constructor.Source
            && record.Metrics.GetValueOrDefault("source.offset") == (ulong)constructor.Start
            && record.Metrics.GetValueOrDefault("source.length") == (ulong)constructor.Length),
            $"{route}: recipe spans retain the literal's original source coordinates");
    }
}

async Task OpaqueFailureRemainsDeferred()
{
    const int Number = 0, Boolean = 1, Any = 2, Scope = 3;
    SourceSpan fieldSpan = new("lazy-opaque-field.msync", 17, 13);
    foreach (bool demanded in new[] { false, true })
    {
        List<Instruction> entry = [new(OpCode.MakeClosure, 1), new(OpCode.MakeLazyRange, 1, Text: "1")];
        if (!demanded)
            entry.AddRange([new(OpCode.Constant, 0), new(OpCode.NativeCall, 2, Text: "ranges.take"),
                new(OpCode.NativeCall, 1, Text: "ranges.empty")]);
        else
            entry.Add(new(OpCode.NativeCall, 1, Text: "ranges.front"));
        entry.Add(new(OpCode.Return));

        Module portable = new([new NumberValue(0)],
            [
                new("main", 0, 0, entry, demanded ? TypeRef.Any : TypeRef.Bool)
                    { CodeSignature = new([], demanded ? Any : Boolean, []) },
                new("opaque factory", 0, 0,
                    [new(OpCode.MakeClosure, 2), new(OpCode.NewScope, 0, 17), new(OpCode.Call, 1, 3), new(OpCode.Return)],
                    TypeRef.Any) { CodeSignature = new([], Any, []) },
                new("opaque read", 1, 1,
                    [new(OpCode.LoadLocal, 0), new(OpCode.LoadField, 0, Span: fieldSpan), new(OpCode.Return)],
                    TypeRef.Any, ParameterTypes: [TypeRef.Scope]) { CodeSignature = new([Scope], Any, []) }
            ], OptimizationLevel: 0)
        {
            CodeTypes = [new(TypeKind.Number), new(TypeKind.Bool), new(TypeKind.Any), new(TypeKind.Scope)],
            ScopeLayouts = [new([])],
            FieldProjections = [new("missing", [])]
        };

        if (!demanded)
        {
            var malformed = portable with
            {
                ScopeLayouts = [new([new("x", Number), new("x", Number)])],
                Functions = [portable.Functions[0], new("malformed literal", 0, 0,
                    [new(OpCode.Constant, 0), new(OpCode.Constant, 0), new(OpCode.NewScope, 0), new(OpCode.Return)],
                    TypeRef.Scope) { CodeSignature = new([], Scope, []) }]
            };
            using MemoryStream bytes = new();
            Artifact.WriteProduced(malformed, bytes);
            bytes.Position = 0;
            try
            {
                _ = Artifact.Read(bytes);
                throw new InvalidOperationException("A duplicate layout was admitted for a zero-demand Range.");
            }
            catch (MsyncException error)
            {
                Check(error.Message.Contains("duplicate field name", StringComparison.Ordinal),
                    "even an unselected duplicate layout must fail at artifact intake");
            }
        }

        Module image = RoundTrip(Binding.Bind(RoundTrip(portable), new(root, root)));
        RuntimeProfile profile = new();
        try
        {
            Value result = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Profile = profile });
            Check(!demanded && result is BoolValue { Data: true }, "zero demand never runs an opaque failing projection");
        }
        catch (MsyncException error) when (demanded)
        {
            Check(error.Span == fieldSpan && error.Message == "Scope has no member 'missing'",
                "a selected opaque projection retains its original diagnostic and span");
        }
        Check(Hits(profile.Drain()) == 0, "the opaque factory is never mistaken for a literal recipe");
    }
}

async Task ConcurrentFirstWrites()
{
    const int count = 2048;
    const int extra = 3;
    string source = $$"""
        let r = $std::ranges;
        let seed = [scope { first: 0, second: 0, label: "constant" }];
        r::materialize(r::transform(r::iota(0, {{count + extra}}), fn(_index: Number) { r::front(seed) }))
        """;
    RuntimeProfile profile = new();
    var image = Compiler.Compile(source, "literal-scope-first-writes", 2);
    RangeValue returned = (RangeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Profile = profile });
    IReadOnlyList<Value> snapshot = returned.OwnedSnapshot ?? throw new InvalidOperationException("Expected private materialized rows.");
    ScopeValue[] rows = snapshot.Cast<ScopeValue>().ToArray();
    Check(rows.Length == count + extra && rows.Distinct(ReferenceEqualityComparer.Instance).Count() == rows.Length,
        "all pulls produce independent identities before writes");
    Check(Hits(profile.Drain()) == (ulong)rows.Length, "all concurrent cases exercise actual literal producers");

    using Barrier firstWrites = new(2);
    Task first = Task.Factory.StartNew(() => Write("first", 1, numeric: true), TaskCreationOptions.LongRunning);
    Task second = Task.Factory.StartNew(() => Write("second", 2, numeric: false), TaskCreationOptions.LongRunning);
    await Task.WhenAll(first, second);
    Check(rows.Take(count).All(row => row["first"] is NumberValue { Data: 1 }
        && row["second"] is NumberValue { Data: 2 } && row["label"] is StringValue { Data: "constant" }),
        "concurrent first writes detach once without losing a slot or altering another field");

    ScopeValue cellRow = rows[^3];
    int firstOffset = cellRow.Layout.GetOffset("first");
    CellValue cell = new(new NumberValue(6));
    Check(!cellRow.TryGetFieldCell(firstOffset, out _), "literal template starts without a captured field");
    cellRow.SetField(firstOffset, cell);
    cellRow.SetNumber(firstOffset, 11);
    Check(cellRow.TryGetFieldCell(firstOffset, out CellValue installed) && ReferenceEquals(cell, installed)
        && cell.Data is NumberValue { Data: 11 }, "first detach preserves the installed Cell and later writes reach it");
    cell.Data = new NumberValue(12);
    Check(cellRow["first"] is NumberValue { Data: 12 }, "later captured writes remain visible through the Scope");

    ScopeValue original = rows[^2];
    ScopeValue copy = original with { AllocationSite = original.AllocationSite + 1 };
    copy.SetMember("first", new NumberValue(3));
    original.SetNumber(original.Layout.GetOffset("second"), 4);
    Check(!ReferenceEquals(original, copy) && copy.AllocationSite == original.AllocationSite + 1,
        "host record copying retains a distinct identity and new provenance");
    Check(original["first"] is NumberValue { Data: 3 } && copy["second"] is NumberValue { Data: 4 },
        "host record copy and original share mutable fields, independently of other template instances");
    Check(rows[^1]["first"] is NumberValue { Data: 0 } && rows[^1]["second"] is NumberValue { Data: 0 }
        && rows[^1]["label"] is StringValue { Data: "constant" }, "writes do not alter another instance's shared template");

    void Write(string name, double value, bool numeric)
    {
        for (int i = 0; i < count; i++)
        {
            Rendezvous();
            var row = rows[i];
            if (numeric)
                row.SetNumber(row.Layout.GetOffset(name), value);
            else
                row.SetMember(name, new NumberValue(value));
            Rendezvous();
        }
    }

    void Rendezvous()
    {
        if (!firstWrites.SignalAndWait(TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("Concurrent first-write worker did not rendezvous.");
    }
}

async Task RetainedSession()
{
    CompilerSession compiler = new(optimization: 2);
    RuntimeProfile profile = new(instructionSites: true);
    await using ExecutionSession execution = new(new(WorkingDirectory: root) { Profile = profile });
    await Submit("""
        let live = 1;
        let old = [scope { x: 7, label: "old" }, scope { x: live, label: "live" }];
        """);
    ScopeValue first = (ScopeValue)await Submit("$std::ranges::front(old)");
    first.SetMember("x", new NumberValue(9));
    await Submit("""
        live = 42;
        let later = [scope { x: 99, label: "later" }];
        """);
    ScopeValue fresh = (ScopeValue)await Submit("$std::ranges::front(old)");
    ScopeValue later = (ScopeValue)await Submit("$std::ranges::front(later)");
    ScopeValue captured = (ScopeValue)await Submit("$std::ranges::at(old, 1)");
    Check(!ReferenceEquals(first, fresh) && first["x"] is NumberValue { Data: 9 }
        && fresh["x"] is NumberValue { Data: 7 } && fresh["label"] is StringValue { Data: "old" },
        "an old Range still creates a fresh literal across accepted session images");
    Check(later["x"] is NumberValue { Data: 99 } && later["label"] is StringValue { Data: "later" },
        "later session producers do not use stale code-image recipes");
    Check(captured["x"] is NumberValue { Data: 42 }, "a retained deferred element reads the current shared capture");
    Check(Hits(profile.Drain()) == 0, "session images use canonical deferred execution");

    async Task<Value> Submit(string source)
    {
        Module module = compiler.Compile(source);
        try
        {
            Value result = await execution.ExecuteAsync(module);
            compiler.AcceptSubmission(execution.GetLocalValue);
            return result;
        }
        catch
        {
            compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
            throw;
        }
    }
}

static ulong Hits(IEnumerable<RuntimeProfileSample> samples) =>
    samples.Where(sample => sample.Name == "vm.function")
        .Aggregate(0UL, (sum, sample) => sum + sample.Metrics.GetValueOrDefault(hitMetric));

static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes, formBlocks: false);
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
