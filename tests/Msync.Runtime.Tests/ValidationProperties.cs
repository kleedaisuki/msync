using Msync.Core;

internal static class ValidationProperties
{
    private static readonly Value[] Constants = [new StringValue("text"), new PathValue("./file"), Value.Null, Value.True, Value.False, new NumberValue(1), new TypeValue(TypeRef.Path)];

    internal static int Run()
    {
        var random = new Random(8080);
        int accepted = 0, rejected = 0, ambiguous = 0;
        for (int i = 0; i < 2000; i++)
        {
            foreach (var code in new[] { Branch(random), Loop(random), Iterator(random) })
            {
                var module = new Module(Constants, [new("generated", 1, 8, code, TypeRef.Any, ParameterTypes: [TypeRef.Bool])]);
                string? previousError = Error(() => InstructionValidationOracle.Validate(module));
                string? currentError = Error(() => Artifact.Validate(module));
                if (previousError is null && currentError is not null)
                    throw new InvalidOperationException($"Block verifier rejected an admitted CFG program {i}: {currentError}\n{string.Join('\n', code)}");
                if (previousError is not null && currentError is null)
                {
                    if (!previousError.Contains("expected Bool, found", StringComparison.Ordinal))
                        throw new InvalidOperationException($"Block verifier lost a structural rejection: {previousError}\n{string.Join('\n', code)}");
                    ambiguous++;
                }
                if (currentError is null) accepted++; else rejected++;
            }
        }
        if (accepted == 0 || rejected == 0) throw new InvalidOperationException("Validation corpus must exercise accepted and rejected programs");
        Console.WriteLine($"Block verifier retained all admitted CFGs and structural rejections: {accepted} accepted, {rejected} rejected; {ambiguous} invalid mixed-type probes defer their precise Bool check to execution.");
        return accepted + rejected;
    }

    private static string? Error(Action validate)
    {
        try { validate(); return null; }
        catch (MsyncException error) { return error.Message; }
    }

    private static Instruction[] Branch(Random random)
    {
        List<Instruction> code = [];
        if (random.Next(2) == 0) Store(code, 1, random);
        Condition(code, random);
        int choose = code.Count;
        code.Add(new(random.Next(2) == 0 ? OpCode.JumpIfFalse : OpCode.JumpIfTrue));
        if (random.Next(3) != 0) Store(code, 1, random);
        int skip = code.Count;
        code.Add(new(OpCode.Jump));
        code[choose] = code[choose] with { A = code.Count };
        if (random.Next(3) != 0) Store(code, 1, random);
        code[skip] = code[skip] with { A = code.Count };
        Probe(code, random);
        return code.ToArray();
    }

    private static Instruction[] Loop(Random random)
    {
        List<Instruction> code = [];
        if (random.Next(3) != 0) Store(code, 1, random);
        int start = code.Count;
        Condition(code, random);
        int finish = code.Count;
        code.Add(new(OpCode.JumpIfFalse));
        if (random.Next(3) != 0) Store(code, 1, random);
        code.Add(new(OpCode.Jump, start));
        code[finish] = code[finish] with { A = code.Count };
        Probe(code, random);
        return code.ToArray();
    }

    private static Instruction[] Iterator(Random random)
    {
        List<Instruction> code = [new(OpCode.MakeRange), new(OpCode.GetIterator), new(OpCode.StoreLocal, 2), new(OpCode.IteratorNext, 2), new(OpCode.StoreLocal, 1)];
        if (random.Next(2) == 0) Store(code, 1, random);
        code.Add(new(OpCode.Jump, 3));
        code[3] = code[3] with { B = code.Count };
        if (random.Next(2) == 0) code.Add(new(OpCode.LoadLocal, 1)); else code.Add(new(OpCode.Null));
        code.Add(new(OpCode.Return));
        return code.ToArray();
    }

    private static void Store(List<Instruction> code, int slot, Random random)
    {
        code.Add(new(OpCode.Constant, random.Next(Constants.Length)));
        code.Add(new(OpCode.StoreLocal, slot));
        for (int i = random.Next(4); i > 0; i--) code.Add(new(OpCode.Nop));
    }

    private static void Condition(List<Instruction> code, Random random)
    {
        code.Add(random.Next(3) switch { 0 => new(OpCode.LoadLocal, 0), 1 => new(OpCode.True), _ => new(OpCode.False) });
        if (random.Next(2) == 0) code.Add(new(OpCode.Not));
    }

    private static void Probe(List<Instruction> code, Random random)
    {
        code.Add(new(OpCode.LoadLocal, 1));
        switch (random.Next(5))
        {
            case 0: code.Add(new(OpCode.Not)); break;
            case 1: code.Add(new(OpCode.RegexMatch, Text: ".+")); break;
            case 2: code.Add(new(OpCode.IsNull)); break;
            case 3: code.Add(new(OpCode.Constant, 6)); code.Add(new(OpCode.IsType)); break;
        }
        code.Add(new(OpCode.Return));
    }
}
