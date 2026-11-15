using System.Text.Json;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class LicmPendingJourney
{
    internal static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        List<object> receipts = [];
        foreach (int level in new[] { 0, 2 })
        {
            string work = Path.Combine(output, "O" + level);
            Directory.CreateDirectory(work);
            string gate = Path.Combine(work, "release-number");
            if (File.Exists(gate)) File.Delete(gate);
            List<CompilationObservation> observations = [];
            CompilerSession compiler = new(Path.Combine(work, "pending-zero-trip.msync"), level,
                new CompilationTrace(observations.Add, includeOptimizerPasses: true));
            await using NativeExecutionSession execution = new(new ExecutionOptions(WorkingDirectory: work));
            NativeProgram? current = null;
            int submissions = 0;
            async Task<NativeValue> Submit(string source)
            {
                string path = Path.Combine(work, (++submissions).ToString("D2") + ".msync");
                File.WriteAllText(path, source);
                current = compiler.Compile(source);
                try
                {
                    NativeValue value = await execution.ExecuteAsync(current);
                    compiler.AcceptSubmission(execution.GetLocalValue);
                    return value;
                }
                catch
                {
                    compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
                    throw;
                }
            }
            void PendingNumber()
            {
                NativeProgram program = current ?? throw new InvalidOperationException("No production generation.");
                int slot = program.Functions[program.EntryPoint].Slots.Select((value, index) => (value, index))
                    .Single(pair => pair.value.Role == NativeSlotRole.Local && pair.value.Name == "pendingNumber").index;
                Require(execution.IsLocalInitialized(slot) && execution.GetLocalValue(slot) is null,
                    "A gate-held Number result must remain genuinely pending and undemanded.");
            }
            try
            {
                await Submit("""
                    let child = release /bin/sh("-c", "while [ ! -f ./release-number ]; do sleep 0.01; done; exit 0");
                    let pendingNumber = child::exit_code;
                    null
                    """);
                PendingNumber();
                NativeValue result = await Submit("""
                    fn zero(limit: Number, scale: Number, shift: Number) {
                        let index = 0; let total = 0;
                        loop {
                            if index >= limit { break; };
                            let invariant = scale * scale + shift;
                            total = total + invariant;
                            index = index + 1;
                        };
                        total
                    };
                    zero(0, pendingNumber, 1)
                    """);
                Require(result.AsNumber() == 0, "A zero-trip loop with a pending numeric operand changed its result.");
                PendingNumber();
                File.WriteAllText(gate, "release");
                NativeValue resolved = await Submit("pendingNumber");
                Require(resolved.AsNumber() == 0, "The retained Number future lost its eventual process exit result.");
                receipts.Add(new { optimization = level, submissions, zeroTripResult = result.AsNumber(),
                    pendingRootObservations = 2, resolvedNumber = resolved.AsNumber() });
            }
            finally
            {
                File.WriteAllText(gate, "release");
                File.WriteAllLines(Path.Combine(work, "compiler.trace.jsonl"), observations.Select(record => JsonSerializer.Serialize(new
                { record.Name, record.Outcome, record.Attributes, record.Metrics, error = record.Error?.Message })));
            }
        }
        string receipt = JsonSerializer.Serialize(new { status = "passed", path = "production-zero-trip-pending-number", receipts });
        File.WriteAllText(Path.Combine(output, "receipt.json"), receipt);
        Console.WriteLine(receipt);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
