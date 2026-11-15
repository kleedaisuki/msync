using System.Text;
using System.Text.Json;
using Msync.Compiler;
using Msync.Compiler.Semantics;
using Msync.Compiler.Session;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Core.Llvm;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class LlvmSessionJourney
{
    internal static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        List<object> receipts = [];
        foreach (bool optimize in new[] { false, true })
        {
            string work = Path.Combine(output, optimize ? "mem2reg-dce" : "unoptimized");
            Directory.CreateDirectory(work);
            await using JourneySession session = new(work, optimize);
            string gate = Path.Combine(work, "release-future");
            if (File.Exists(gate)) File.Delete(gate);
            try
            {
                await session.Submit("""
                    let state = 1;
                    fn read() { state };
                    fn set(value: Number) { state = value; };
                    fn make(value: Number) { fn { value + state } };
                    let escaped = make(7);
                    let object = scope { value: 2, read: fn { value } };
                    let shared = object;
                    let operation = fn(value: Number) { value + state };
                    let delayed = $std::ranges::transform([1, 2], fn(item) { operation(item) });
                    null
                    """);
                await session.Submit("state = 3; shared::value = 4; operation = fn(value: Number) { value * 10 + state }; null");
                await session.Number("read() + object::read()", 7);
                await session.Number("$std::ranges::front(delayed)", 13);
                await session.Number("escaped()", 10);
                await session.Submit("let state = 100; null");
                await session.Number("read() + state", 103);
                await session.Submit("set(5); null");
                await session.Number("escaped() + operation(2) + state", 137);
                await session.Submit("fn identity(value) { value }; let id = identity; null");
                await session.Number("if id(true) { identity(42) } else { 0 }", 42);
                await session.Number("shared::value = 9; object::read()", 9);

                await session.Submit("""
                    let child = release /bin/sh("-c", "while [ ! -f ./release-future ]; do sleep 0.01; done; printf pending-value");
                    let pending = child::stdout;
                    null
                    """);
                session.Pending("pending");
                await session.Submit("let savedPending = pending; fn pending_reader() { savedPending }; null");
                session.Pending("pending");
                session.Pending("savedPending");
                File.WriteAllText(gate, "release");
                await session.Submit("$std::io::write_text(./future.txt, pending_reader()); null");
                Require(File.ReadAllBytes(Path.Combine(work, "future.txt")).AsSpan()
                    .SequenceEqual(Encoding.UTF8.GetBytes("pending-value")),
                    "A retained pending future/captured alias lost its result across accepted submissions.");
                receipts.Add(new { mode = optimize ? "mem2reg-dce" : "unoptimized", session.Submissions,
                    session.ExpectedNumbers, pendingRootsObserved = 3, checkedFiles = 1 });
            }
            finally
            {
                // Release the test-owned child even if an earlier acceptance assertion fails.
                File.WriteAllText(gate, "release");
            }
        }
        string receipt = JsonSerializer.Serialize(new { status = "passed", path = "source-session-llvm-native", receipts });
        File.WriteAllText(Path.Combine(output, "receipt.json"), receipt);
        Console.WriteLine(receipt);
    }

    private sealed class JourneySession(string work, bool optimize) : IAsyncDisposable
    {
        private readonly HirBuilder workspace = new();
        private readonly SemanticInstanceCache instances = new();
        private readonly LlvmHirLoweringSession lowering = new();
        private readonly NativeExecutionSession execution = new(new ExecutionOptions(WorkingDirectory: work));
        private BoundProgram? accepted;
        private SemanticContext? facts;
        private SemanticSessionDispatch dispatches = new();
        private HirLoweringLayout layout = HirLoweringLayout.Empty;
        internal int Submissions { get; private set; }
        internal int ExpectedNumbers { get; private set; }

        internal async Task<NativeValue> Submit(string source)
        {
            string directory = Path.Combine(work, (++Submissions).ToString("D2"));
            Directory.CreateDirectory(directory);
            string sourceFile = Path.Combine(directory, "source.msync");
            File.WriteAllText(sourceFile, source);
            using var transaction = workspace.BeginTransaction();
            using var additions = instances.BeginAdditions();
            SourceBinder binder = accepted is null ? new(workspace, isSession: true) : new(accepted);
            BoundProgram bound = Compiler.Bind(binder, source, sourceFile, null, null);
            SemanticSessionDispatch nextDispatches = dispatches.Fork();
            SemanticAnalyzer analyzer = new(bound, codeCache: instances, sessionDispatch: nextDispatches);
            HirModule module = Compiler.Analyze(analyzer, facts, null);
            File.WriteAllText(Path.Combine(directory, "typed-hir.txt"), HirPrinter.Print(module));
            LlvmHirLoweringResult graph = lowering.Lower(module);
            File.WriteAllText(Path.Combine(directory, "before.ll"), LlvmIrWriter.Write(graph.Module));
            if (optimize)
            foreach (LlvmFunction function in graph.Module.Functions.Where(function => function.BasicBlocks.Count > 0))
            {
                LlvmPromoteMemoryToRegister.Run(function, new LlvmDominatorTree(function));
                LlvmDeadCodeElimination.Run(function);
            }
            File.WriteAllText(Path.Combine(directory, "after.ll"), LlvmIrWriter.Write(graph.Module));
            NativeHirLoweringResult result = lowering.Emit(graph);
            Require(result.Program.Backend is not null && result.Program.PreserveLocals,
                "The explicit new session target must directly provide executable backend/root storage.");
            additions.Commit();
            transaction.Commit();
            NativeValue actual = await execution.ExecuteAsync(result.Program);
            accepted = bound;
            layout = result.Layout;
            dispatches = nextDispatches;
            facts = analyzer.FinalState.QueryFork();
            new NativeSemanticSessionValues(bound, layout, facts, new SemanticSessionSites(bound, layout), dispatches)
                .Refresh(bound.Exports.Values, execution.GetLocalValue);
            facts.DetachFacts();
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                kind = actual.Kind.ToString(),
                number = actual.Kind == NativeKind.Number ? actual.AsNumber() : (double?)null,
                sessionRoots = result.Program.SessionRoots?.Count ?? 0,
                functions = result.Program.Functions.Count
            }));
            return actual;
        }

        internal async Task Number(string source, double expected)
        {
            NativeValue actual = await Submit(source);
            Require(actual.AsNumber() == expected,
                $"{work} submission {Submissions}: expected {expected}, actual {actual.AsNumber()}.");
            ExpectedNumbers++;
        }

        internal void Pending(string name)
        {
            BoundProgram current = accepted ?? throw new InvalidOperationException("No accepted source generation.");
            HirLocationId location = current.Exports[name];
            int slot = layout.Locations[new(current.Entry, location)];
            Require(execution.IsLocalInitialized(slot) && execution.GetLocalValue(slot) is null,
                $"{work}: {name} must remain an initialized, genuinely pending future without being demanded by acceptance.");
        }

        public ValueTask DisposeAsync() => execution.DisposeAsync();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
