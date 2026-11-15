using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class RegisterDemandChecks
{
    internal static async Task<int> RunAsync()
    {
        const string source = "fn kernel(a:Number,b:Number) { (a+b)*b }; kernel(2,3)";
        int checks = 0;
        foreach (int optimization in new[] { 0, 2 })
        {
            NativeProgram program = NativePreparation.Prepare(Compiler.Compile(source, "register-demand.msync", optimization));
            int index = Enumerable.Range(0, program.Functions.Count).Single(i => program.Functions[i] is { ParameterCount: 2, IsTemplate: false });
            NativeImage image = new(program, new());
            using NativeExecutionFunction function = new(image, index, new(NativeJitPolicy.Force));
            using NativeCompiledFunction compiled = NativeCompiledFunction.Compile(image, function, NativeJitPolicy.Force,
                observeRegisterPlanning: true)!;
            var skipped = compiled.Statistics.RegisterPlanning!;
            if (skipped.EligibilityInstructions == 0 || skipped.SkippedNoEligible != 1 || skipped.ControlFlowBuilds != 0 ||
                skipped.LivenessVisits != 0 || skipped.PendingVisits != 0 || compiled.Layout.Regions.Length != 0)
                throw new InvalidOperationException("A standard backend without writable register results performed register dataflow.");
            checks++;

            RuntimeProfile profile = new();
            await using (NativeInterpreter interpreter = new(program,
                new() { Profile = profile, Tiers = new(NativeJitPolicy.Force, 1, 1, 1) }))
            {
                if ((await interpreter.ExecuteAsync()).AsNumber() != 15)
                    throw new InvalidOperationException("Demand-driven register planning changed the source result.");
            }
            var samples = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
            if (!samples.Any(sample => sample.Metrics.GetValueOrDefault("jit_register_plan_skipped_no_eligible") > 0) ||
                samples.Any(sample => sample.Metrics.GetValueOrDefault("jit_register_plan_cfg_builds") != 0 ||
                    sample.Metrics.GetValueOrDefault("jit_register_plan_liveness_visits") != 0 ||
                    sample.Metrics.GetValueOrDefault("jit_register_plan_pending_visits") != 0))
                throw new InvalidOperationException("Function telemetry lost the no-eligible decision or recorded skipped work.");
            checks++;

            var body = program.Backend!.Functions[index];
            bool[] writable = new bool[program.Functions[index].Slots.Count];
            foreach (var instruction in body.Instructions)
                if (instruction.Result >= 0 && program.Types[program.Functions[index].Slots[instruction.Result].Type].Kind is TypeKind.Number or TypeKind.Bool)
                    writable[instruction.Result] = true;
            var eligibleProgram = program with { Backend = program.Backend with
            {
                Functions = program.Backend.Functions.Select((value, i) => i == index ? value with { RawWritable = writable } : value).ToArray()
            } };
            NativeImage eligibleImage = new(eligibleProgram, new());
            using NativeExecutionFunction eligibleFunction = new(eligibleImage, index, new(NativeJitPolicy.Force));
            var metrics = new NativeRegisterPlanMetrics();
            var regions = NativeRegisterPlan.Create(eligibleFunction, compiled.Operations, metrics);
            if (regions.Length == 0 || metrics.SkippedNoEligible != 0 || metrics.ControlFlowBuilds != 1 ||
                metrics.LivenessVisits == 0 || metrics.PendingVisits == 0)
                throw new InvalidOperationException("Existing eligible arithmetic no longer reaches the unchanged register planner.");
            checks++;
        }
        return checks;
    }
}
