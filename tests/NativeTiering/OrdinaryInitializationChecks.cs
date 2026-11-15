using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class OrdinaryInitializationChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            ++checks;
        }
        var number = NativeType.Of(NativeKind.Number);
        var byteMachine = NativeMachineType.Integer(8);
        const ulong bits = 0x0123456789abcdef;
        double value = BitConverter.UInt64BitsToDouble(bits);
        foreach (int route in new[] { 0, 1, 2 })
        {
            var raw = new NativeAllocation(new NativeLayout(number)).Field(0);
            if (route == 0) NativeStorage.WriteNumber(raw, value);
            else if (route == 1) NativeStorage.StoreNumber(raw, value);
            else raw.Store(NativeValue.FromNumber(value));
            for (int index = 0; index < 8; ++index)
            {
                var interior = raw.OffsetBy(new(index, 0), byteMachine.PhysicalType);
                Require(interior.IsInitialized && NativeStorage.ReadMachineScalar(byteMachine, interior).AsMachineInteger().GetZExtValue()
                    == ((bits >> (8 * index)) & 255), "Public Number writes must retain every observable interior byte view.");
            }
            var copied = new NativeAllocation(new NativeLayout(number)).Field(0);
            NativeStorage.Copy(raw, copied);
            Require(copied.OffsetBy(new(7, 0), byteMachine.PhysicalType).IsInitialized,
                "Public copy must preserve raw interior readiness.");
            raw.Clear();
            Require(Enumerable.Range(0, 8).All(index => !raw.OffsetBy(new(index, 0)).IsInitialized),
                "Public clear must still clear the entire raw readiness extent.");
        }

        NativeMemory memory = new();
        var pointee = new NativeAllocation(new NativeLayout(number)).Field(0);
        (NativeValue Value, int Bytes)[] values =
        [
            (NativeValue.FromNumber(7), 8),
            (NativeValue.FromFunction(new(3, NativeName: "test-native")), 4),
            (NativeValue.FromAddress(pointee), 8),
            (NativeValue.FromPath(new("example.txt")), 2)
        ];
        foreach (var test in values)
        {
            NativeType type = NativeType.Of(test.Value.Kind);
            var source = new NativeAllocation(new NativeLayout(type)).Field(0);
            memory.Write(source, new(test.Value));
            Require(source.IsInitialized && memory.Read(source).Value.Equals(test.Value),
                "Compiled source writes must retain payload, roots and ordinary readiness.");
            Require(Enumerable.Range(1, test.Bytes - 1).All(index => !source.OffsetBy(new(index, 0)).IsInitialized),
                "Compiled ordinary writes must not maintain unconsumed interior byte flags.");
            var destination = new NativeAllocation(new NativeLayout(type)).Field(0);
            memory.CopyCompiled(source, destination);
            Require(memory.Read(destination).Value.Equals(test.Value)
                && Enumerable.Range(1, test.Bytes - 1).All(index => !destination.OffsetBy(new(index, 0)).IsInitialized),
                "Compiled copies must preserve values while marking only their declared source-carrier start.");
            var union = NativeType.Union(type, NativeType.Of(NativeKind.Null));
            var widened = new NativeAllocation(new NativeLayout(union)).Field(0);
            memory.Copy(source, widened);
            Require(memory.Read(widened).Value.Equals(test.Value) && widened.IsInitialized,
                "Kind-adapting compiled copies must preserve union tags and roots.");
            memory.Clear(source);
            Require(!source.IsInitialized, "Compiled clear must clear ordinary readiness.");
        }
        var pointerSlot = new NativeAllocation(new NativeLayout(NativeType.Of(NativeKind.Address))).Field(0);
        memory.WriteAddress(pointerSlot, pointee);
        Require(memory.Read(pointerSlot).Value.AsAddress() == pointee && !pointerSlot.OffsetBy(new(7, 0)).IsInitialized,
            "The exact-address compiled writer must keep identity without full-byte readiness maintenance.");

        var machine = NativeMachineType.Integer(64);
        var machineSource = new NativeAllocation(new NativeLayout(machine.PhysicalType)).Field(0);
        memory.Write(machineSource, new(NativeValue.FromMachineInteger(new LlvmAPInt(64, bits), machine)));
        var machineCopy = new NativeAllocation(new NativeLayout(machine.PhysicalType)).Field(0);
        memory.CopyCompiled(machineSource, machineCopy);
        Require(NativeStorage.ReadMachineScalar(byteMachine, machineCopy.OffsetBy(new(7, 0), byteMachine.PhysicalType))
            .AsMachineInteger().GetZExtValue() == 1, "Compiled machine writes and copies must retain byte-extent readiness.");
        memory.Clear(machineCopy);
        Require(Enumerable.Range(0, 8).All(index => !machineCopy.OffsetBy(new(index, 0)).IsInitialized),
            "Compiled machine clear must still clear interior byte readiness.");

        var pendingSlot = new NativeAllocation(new NativeLayout(number)).Field(0);
        memory.Write(pendingSlot, new(NativeValue.FromNumber(4)));
        TaskCompletionSource<NativeValue> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeFuture future = new(completion.Task, logicalType: TypeRef.Number, physicalType: number);
        memory.Write(pendingSlot, new(future));
        Require(!pendingSlot.IsInitialized && ReferenceEquals(memory.Read(pendingSlot).Future, future),
            "Pending publication must clear the ordinary ready bit and preserve the original future.");
        completion.SetResult(NativeValue.FromNumber(9));
        Require(memory.Read(pendingSlot).Value.AsNumber() == 9 && pendingSlot.IsInitialized
            && !pendingSlot.OffsetBy(new(7, 0)).IsInitialized,
            "A completed pending value must materialize through the compiled readiness policy.");

        RuntimeProfile profile = new();
        NativeMemory observed = new(profile.StorageCounters);
        var counted = new NativeAllocation(new NativeLayout(number)).Field(0);
        var countedCopy = new NativeAllocation(new NativeLayout(number)).Field(0);
        observed.Write(counted, new(NativeValue.FromNumber(1)));
        observed.CopyCompiled(counted, countedCopy);
        observed.Clear(counted);
        var sample = profile.Drain().Single(item => item.Name == "vm.storage.initialization");
        Require(sample.Metrics["writes"] == 1 && sample.Metrics["copies"] == 1 && sample.Metrics["clears"] == 1
            && sample.Metrics["flags_written"] == 3 && sample.Metrics["flags_avoided_vs_full_extent"] == 21,
            "Storage telemetry must count actual writes/copies/clears and the removed readiness-flag work.");
        Require(profile.Drain().All(item => item.Name != "vm.storage.initialization"),
            "Draining must not repeat the same storage operation counts.");
        return checks;
    }

    internal static async Task<int> RunSourceAsync()
    {
        const string source = """
            fn make(seed: Number) { fn(value: Number) { seed = seed + value; seed } };
            let add = make(1);
            let row = scope { total: 0 };
            for item in [1, 2, 3, 4] { row::total = row::total + add(item); };
            row::total
            """;
        int executions = 0;
        foreach (int optimization in new[] { 0, 2 })
        {
            NativeProgram program = Compiler.Compile(source, "ordinary-initialization.msync", optimization);
            using MemoryStream bytes = new(); NativeArtifact.Write(program, bytes); bytes.Position = 0;
            foreach (NativeProgram executable in new[] { program, NativeArtifact.Read(bytes) })
            {
                RuntimeProfile profile = new();
                await using NativeInterpreter interpreter = new(executable, new() { Profile = profile });
                if ((await interpreter.ExecuteAsync()).AsNumber() != 24)
                    throw new InvalidOperationException("Source capture mutation, Function calls and Scope stores must preserve the result 24.");
                if (!profile.Drain().Any(sample => sample.Name == "vm.storage.initialization"
                    && sample.Metrics["flags_avoided_vs_full_extent"] > 0))
                    throw new InvalidOperationException("The real source journey must exercise compiled start-bit storage.");
                ++executions;
            }
        }
        return executions;
    }
}
