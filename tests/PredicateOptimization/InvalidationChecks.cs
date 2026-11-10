using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;
using Module = Msync.Core.Module;

internal static class InvalidationChecks
{
    internal static async Task<int> Run(string directory, Func<Module, Module> optimize)
    {
        int checks = 0;
        foreach (bool present in new[] { false, true })
        {
            string probe = Path.Combine(directory, "probe");
            if (present) File.WriteAllText(probe, "actual unknown Bool input");
            else File.Delete(probe);
            await Check("saved-predicate-after-call", SavedPredicate(),
                $"current=False,copy={present},saved={!present}");
            foreach (bool initialize in new[] { false, true })
            {
                await Check("returned-value-boundary-" + initialize, ReturnedValue(initialize, present),
                    $"current={present},copy={present},returned={!present}");
                foreach (bool capture in new[] { false, true })
                    await Check($"direct-raw-alias-{capture}-{initialize}", DirectAlias(capture, initialize, present),
                        $"current={(initialize ? present : !present)},copy={present},alias={!present}");
            }
            await Check("raw-location-CFG-join", JoinedAlias(),
                $"first={!present},second={present},copy=True");
        }
        for (int count = 1; count <= 4; count++)
        {
            foreach (bool initialize in new[] { false, true })
                await Check($"raw-location-loop-{count}-{initialize}", IteratedAlias(count, initialize),
                    $"current={initialize},copy=True");
            await Check("same-call-PC-loop-" + count, RepeatedCall(count),
                $"current={count % 2 == 0},copy=True,saved={count % 2 == 0}");
        }
        return checks;

        async Task Check(string name, Module original, string expected)
        {
            Artifact.Validate(original);
            await Require(original, "original");
            Module changed = optimize(original);
            foreach (Module candidate in new[] { changed, RoundTrip(changed), RoundTrip(Binding.Bind(changed, new(directory, directory))) })
                await Require(candidate, candidate.Stage.ToString());

            async Task Require(Module candidate, string stage)
            {
                Value result = await Interpreter.ExecuteAsync(candidate, new(WorkingDirectory: directory));
                string actual = result is ScopeValue scope
                    ? string.Join(",", scope.Members.Select(pair => pair.Key + "=" +
                        (pair.Value is BoolValue boolean ? boolean.Data.ToString() : $"<{pair.Value.Type}:{pair.Value}>")))
                    : result.ToString()!;
                checks++;
                if (actual != expected)
                    throw new InvalidOperationException($"{name} {stage}: expected {expected}, found {actual}");
            }
        }
    }

    private static Module SavedPredicate()
    {
        List<Instruction> code = Unknown();
        code.AddRange([new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 1),
            new(OpCode.LoadLocal, 0), new(OpCode.Not), new(OpCode.InitializeLocal, 2),
            new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.Call, 0, 2), new(OpCode.Pop),
            new(OpCode.LoadLocal, 2)]);
        int branch = code.Count;
        code.Add(new(OpCode.JumpIfFalse));
        code.AddRange([new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 3)]);
        int join = code.Count;
        code.Add(new(OpCode.Jump));
        code[branch] = code[branch] with { A = code.Count };
        code.AddRange([new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 3)]);
        code[join] = code[join] with { A = code.Count };
        Finish(code, "current\0copy\0saved", 3, 1, 2);
        return Module(code, 4, new("change", 0, 1,
            [new(OpCode.False), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)],
            TypeRef.Null, CaptureCount: 1));
    }

    private static Module ReturnedValue(bool initialize, bool present)
    {
        List<Instruction> code = Unknown();
        code.AddRange([new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 2),
            new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.Call, 0, 2),
            new(OpCode.InitializeLocal, 1), new(present ? OpCode.False : OpCode.True),
            new(initialize ? OpCode.InitializeLocal : OpCode.StoreLocal, 1)]);
        Finish(code, "current\0copy\0returned", 0, 2, 1);
        return Module(code, 3, new("raw-return", 0, 1,
            [new(OpCode.LoadStorage, 0), new(OpCode.Return)], TypeRef.Bool, CaptureCount: 1));
    }

    private static Module JoinedAlias()
    {
        List<Instruction> code = [new(OpCode.True), new(OpCode.InitializeLocal, 0),
            new(OpCode.True), new(OpCode.InitializeLocal, 1), new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 3),
            new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: "fs.exists")];
        int branch = code.Count;
        code.Add(new(OpCode.JumpIfFalse));
        code.Add(new(OpCode.LoadCell, 0));
        int join = code.Count;
        code.Add(new(OpCode.Jump));
        code[branch] = code[branch] with { A = code.Count };
        code.Add(new(OpCode.LoadCell, 1));
        code[join] = code[join] with { A = code.Count };
        code.AddRange([new(OpCode.InitializeLocal, 2), new(OpCode.False), new(OpCode.StoreLocal, 2)]);
        Finish(code, "first\0second\0copy", 0, 1, 3);
        return Module(code, 4);
    }

    private static Module DirectAlias(bool capture, bool initialize, bool present)
    {
        List<Instruction> code = Unknown();
        code.AddRange([new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 2), new(OpCode.LoadCell, 0)]);
        if (capture) code.AddRange([new(OpCode.MakeClosure, 1, 1), new(OpCode.GetCapture, 0)]);
        else code.AddRange([new(OpCode.Pop), new(OpCode.LoadStorage, 0)]);
        code.AddRange([new(OpCode.InitializeLocal, 1), new(present ? OpCode.False : OpCode.True),
            new(initialize ? OpCode.InitializeLocal : OpCode.StoreLocal, 1)]);
        Finish(code, "current\0copy\0alias", 0, 2, 1);
        return Module(code, 3, capture ? new("capture-holder", 0, 1,
            [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, CaptureCount: 1) : null);
    }

    private static Module IteratedAlias(int count, bool initialize)
    {
        List<Instruction> code = [new(OpCode.True), new(OpCode.InitializeLocal, 0),
            new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 3)];
        for (int index = 0; index < count; index++) code.Add(new(OpCode.LoadCell, 0));
        code.AddRange([new(OpCode.MakeRange, count), new(OpCode.GetIterator), new(OpCode.InitializeLocal, 1)]);
        int header = code.Count;
        code.Add(new(OpCode.IteratorNext, 1));
        code.AddRange([new(OpCode.InitializeLocal, 2), new(OpCode.False),
            new(initialize ? OpCode.InitializeLocal : OpCode.StoreLocal, 2), new(OpCode.Jump, header)]);
        code[header] = code[header] with { B = code.Count };
        Finish(code, "current\0copy", 0, 3);
        return Module(code, 4);
    }

    private static Module RepeatedCall(int count)
    {
        List<Instruction> code = [new(OpCode.True), new(OpCode.InitializeLocal, 0),
            new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 4),
            new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.InitializeLocal, 1),
            new(OpCode.False), new(OpCode.InitializeLocal, 5), new(OpCode.False), new(OpCode.InitializeLocal, 6)];
        for (int index = 0; index < count; index++) code.Add(new(OpCode.True));
        code.AddRange([new(OpCode.MakeRange, count), new(OpCode.GetIterator), new(OpCode.InitializeLocal, 2)]);
        int header = code.Count;
        code.Add(new(OpCode.IteratorNext, 2));
        code.AddRange([new(OpCode.InitializeLocal, 3), new(OpCode.LoadLocal, 0), new(OpCode.Not),
            new(OpCode.InitializeLocal, 5), new(OpCode.LoadLocal, 1), new(OpCode.Call, 0, 2),
            new(OpCode.Pop), new(OpCode.LoadLocal, 5)]);
        int branch = code.Count;
        code.Add(new(OpCode.JumpIfFalse));
        code.AddRange([new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 6)]);
        int join = code.Count;
        code.Add(new(OpCode.Jump));
        code[branch] = code[branch] with { A = code.Count };
        code.AddRange([new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 6)]);
        code[join] = code[join] with { A = code.Count };
        code.Add(new(OpCode.Jump, header));
        code[header] = code[header] with { B = code.Count };
        Finish(code, "current\0copy\0saved", 6, 4, 5);
        return Module(code, 7, new("flip", 0, 1,
            [new(OpCode.LoadLocal, 0), new(OpCode.Not), new(OpCode.StoreLocal, 0),
                new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, CaptureCount: 1));
    }

    private static List<Instruction> Unknown() =>
        [new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.InitializeLocal, 0)];

    private static void Finish(List<Instruction> code, string fields, params int[] slots)
    {
        foreach (int slot in slots) code.Add(new(OpCode.LoadLocal, slot));
        code.Add(new(OpCode.MakeScope, slots.Length, Text: fields));
        code.Add(new(OpCode.Return));
    }

    private static Module Module(List<Instruction> code, int locals, FunctionCode? callee = null) =>
        new([new PathValue("./probe")], callee is null
            ? [new("entry", 0, locals, code, TypeRef.Scope)]
            : [new("entry", 0, locals, code, TypeRef.Scope), callee], OptimizationLevel: 0);

    private static Module RoundTrip(Module module)
    {
        Artifact.Validate(module);
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }
}
