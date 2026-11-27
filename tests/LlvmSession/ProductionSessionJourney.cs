using System.Text;
using System.Text.Json;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class ProductionSessionJourney
{
    internal static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        List<object> receipts = [];
        foreach (int optimization in new[] { 0, 2 })
        {
            string work = Path.Combine(output, "O" + optimization);
            Directory.CreateDirectory(work);
            string gate = Path.Combine(work, "release-future");
            if (File.Exists(gate)) File.Delete(gate);
            await using PublicSession session = new(work, optimization);
            try
            {
                await session.Submit("""
                    let value = 7;
                    fn read() { value };
                    fn set(next: Number) { value = next; };
                    let record = object { value: 1 };
                    let alias = record;
                    null
                    """);
                await session.RuntimeFailure("""
                    let before = 11;
                    alias::value = 4;
                    let failed = "not-a-number" as Number;
                    let after = 5;
                    null
                    """);
                session.BindingsPresent("before", "value", "alias");
                session.BindingsAbsent("failed", "after");
                await session.Number("before + record::value + read()", 22);

                await session.RuntimeFailure("let value = \"not-a-number\" as Number; null");
                session.BindingsPresent("value");
                await session.Number("value + read()", 14);
                await session.Number("value = 9; alias::value = 5; read() + record::value", 14);

                await session.Submit("""
                    let child = release /bin/sh("-c", "while [ ! -f ./release-future ]; do sleep 0.01; done; printf pending-value");
                    let pending = child::stdout;
                    null
                    """);
                session.Pending("pending");
                await session.RuntimeFailure("""
                    let pendingAlias = pending;
                    let ready = 13;
                    let brokenPending = "not-a-number" as Number;
                    let afterPending = 17;
                    null
                    """);
                session.BindingsPresent("pendingAlias", "ready", "pending");
                session.BindingsAbsent("brokenPending", "afterPending");
                session.Pending("pending");
                session.Pending("pendingAlias");
                await session.Number("ready + read()", 22);
                File.WriteAllText(gate, "release");
                await session.Submit("$std::io::write_text(./future.txt, pendingAlias); null");
                Require(File.ReadAllBytes(Path.Combine(work, "future.txt")).AsSpan()
                    .SequenceEqual(Encoding.UTF8.GetBytes("pending-value")),
                    "Production reconciliation lost an initialized pending root or demanded it during acceptance.");
                await session.Number("let value = 100; read() + value", 109);
                await session.Number("set(6); read() + value + record::value", 111);
                session.CheckPrimaryPath();
                receipts.Add(new { optimization, session.Submissions, session.Accepted, session.ReconciledFailures,
                    session.ExpectedNumbers, pendingRootsObserved = 3, checkedFiles = 1 });
            }
            finally
            {
                File.WriteAllText(gate, "release");
            }
        }
        string receipt = JsonSerializer.Serialize(new { status = "passed", path = "production-compiler-session", receipts });
        File.WriteAllText(Path.Combine(output, "receipt.json"), receipt);
        Console.WriteLine(receipt);
    }

    private sealed class PublicSession : IAsyncDisposable
    {
        private readonly string work;
        private readonly string sourceName;
        private readonly CompilerSession compiler;
        private readonly NativeExecutionSession execution;
        private readonly List<CompilationObservation> observations = [];
        private NativeProgram? currentProgram;
        internal int Submissions { get; private set; }
        internal int Accepted { get; private set; }
        internal int ReconciledFailures { get; private set; }
        internal int ExpectedNumbers { get; private set; }

        internal PublicSession(string work, int optimization)
        {
            this.work = work;
            sourceName = Path.Combine(work, "production-session.msync");
            compiler = new(sourceName, optimization, new CompilationTrace(observations.Add, includeOptimizerPasses: true));
            execution = new(new ExecutionOptions(WorkingDirectory: work));
        }

        internal async Task<NativeValue> Submit(string source)
        {
            string directory = Path.Combine(work, (++Submissions).ToString("D2"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "source.msync"), source);
            int traceStart = observations.Count;
            NativeProgram program;
            try { program = compiler.Compile(source); }
            catch
            {
                compiler.ReconcileSubmission(_ => false);
                throw;
            }
            currentProgram = program;
            Require(program.Backend is not null && program.PreserveLocals,
                "Production CompilerSession must provide its executable target and persistent roots directly.");
            try
            {
                NativeValue result = await execution.ExecuteAsync(program);
                compiler.AcceptSubmission(execution.GetLocalValue);
                Accepted++;
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
                {
                    outcome = "accepted", kind = result.Kind.ToString(),
                    number = result.Kind == NativeKind.Number ? result.AsNumber() : (double?)null,
                    bindings = compiler.BindingNames.Order(StringComparer.Ordinal).ToArray()
                }));
                return result;
            }
            catch (MsyncException error)
            {
                compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
                ReconciledFailures++;
                File.WriteAllText(Path.Combine(directory, "diagnostic.txt"), error.ToString());
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
                {
                    outcome = "reconciled-runtime-failure",
                    bindings = compiler.BindingNames.Order(StringComparer.Ordinal).ToArray()
                }));
                throw;
            }
            finally
            {
                File.WriteAllLines(Path.Combine(directory, "compiler.trace.jsonl"), observations.Skip(traceStart).Select(record =>
                    JsonSerializer.Serialize(new { record.Name, record.Outcome, record.SpanId, record.ParentSpanId,
                        record.Attributes, record.Metrics, error = record.Error?.Message })));
            }
        }

        internal async Task RuntimeFailure(string source)
        {
            try
            {
                await Submit(source);
                throw new InvalidOperationException("An invalid initializer unexpectedly completed.");
            }
            catch (MsyncException error)
            {
                Require(error.Span is { Length: > 0 } span && span.Source == sourceName,
                    "Production initializer failure lost its actual source diagnostic context.");
            }
        }

        internal async Task Number(string source, double expected)
        {
            NativeValue result = await Submit(source);
            Require(result.AsNumber() == expected,
                $"{work} submission {Submissions}: expected {expected}, actual {result.AsNumber()}.");
            ExpectedNumbers++;
        }

        internal void BindingsPresent(params string[] names) => Require(names.All(compiler.BindingNames.Contains),
            "Production reconciliation dropped an initialized declaration or prior lexical binding.");
        internal void BindingsAbsent(params string[] names) => Require(names.All(name => !compiler.BindingNames.Contains(name)),
            "Production reconciliation published a failed or unreached initializer.");

        internal void Pending(string name)
        {
            NativeProgram program = currentProgram ?? throw new InvalidOperationException("No compiled production generation.");
            int slot = program.Functions[program.EntryPoint].Slots.Select((value, index) => (value, index))
                .Single(pair => pair.value.Role == NativeSlotRole.Local && pair.value.Name == name).index;
            Require(execution.IsLocalInitialized(slot) && execution.GetLocalValue(slot) is null,
                $"{work}: {name} must be an initialized, genuinely pending accepted root.");
        }

        internal void CheckPrimaryPath()
        {
            Require(observations.Any(record => record.Name == "compiler.llvm.lower")
                && observations.Any(record => record.Name == "compiler.llvm.target")
                && !observations.Any(record => record.Name == "optimizer.function"),
                "Actual public session compilation must traverse primary LLVM-derived IR, not the former Native optimizer.");
        }

        public ValueTask DisposeAsync() => execution.DisposeAsync();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
