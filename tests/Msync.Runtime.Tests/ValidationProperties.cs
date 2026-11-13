using Msync.Core;
using Msync.Core.Native;
using static RuntimeFixture;

internal static class ValidationProperties
{
    internal static void Run()
    {
        Random random = new(8080); int accepted = 0, rejected = 0;
        for (int sample = 0; sample < 2000; sample++)
            foreach (NativeProgram program in new[] { Branch(random), Loop(random), Iterator(random) })
            {
                bool expected = InstructionValidationOracle.Accepts(program), actual;
                try { NativeArtifact.Validate(program); actual = true; } catch (MsyncException) { actual = false; }
                Check(actual == expected, "Native verifier disagrees with exhaustive path-state oracle:\n" + NativeArtifact.Disassemble(program));
                if (actual) accepted++; else rejected++;
            }
        Check(accepted > 0 && rejected > 0, "Generated corpus did not cover both admission outcomes");
        foreach (TypeKind destination in new[] { TypeKind.Number, TypeKind.Bool, TypeKind.String, TypeKind.Number | TypeKind.Bool })
            foreach (var (constant, kind) in new (NativeConstant, TypeKind)[]
            { (new(NativeKind.Number, Number: 7), TypeKind.Number), (new(NativeKind.Bool, Boolean: true), TypeKind.Bool),
              (new(NativeKind.String, "typed"), TypeKind.String), (new(NativeKind.Null), TypeKind.Null) })
            {
                NativeProgram abi = new([constant], [new("generated-abi", [new(0)],
                    [new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)], new([], 0, []))], [new(destination)], []);
                bool admitted;
                try { NativeArtifact.Validate(abi); admitted = true; } catch (MsyncException) { admitted = false; }
                Check(admitted == ((destination & kind) == kind), "Closed builtin register ABI disagrees with independent finite-set membership");
            }
        NativeProgram incompatiblePointer = new([new(NativeKind.Number, Number: 1)], [new("wrong-pointee", [new(0), new(2)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Return, A: 0)], new([], 0, []))],
            [new(TypeKind.Number), new(TypeKind.Bool), new(TypeKind.Never, Pointee: 1)], []);
        Reject(incompatiblePointer, "storage contract");
        NativeProgram valid = Branch(new(1));
        Reject(valid with { Functions = [valid.Functions[0] with { Instructions = [new(NativeOp.Return, A: 99)] }] }, "register");
        Reject(valid with { Functions = [valid.Functions[0] with { Instructions = [new(NativeOp.Jump, A: 999)] }] }, "target");
        Reject(valid with { Types = [new(TypeKind.Storage), new(TypeKind.Number)] }, "unknown builtin");
        Console.WriteLine($"PASS native typed CFG/iterator properties: {accepted} admitted, {rejected} rejected; independent exhaustive oracle");
    }
    internal static async Task RunRegions()
    {
        Random random = new(090); SourceSpan origin = new("region-property.msync", 7, 12);
        for (int sample = 0; sample < 128; sample++)
        {
            int count = random.Next(17), index = random.Next(count + 2);
            NativeProgram program = new([new(NativeKind.Number, Number: count), new(NativeKind.Number, Number: index)],
                [new("indexed-region", [new(0), new(0), new(1), new(1)],
                    [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1), new(NativeOp.Allocate, 2, 0, C: 0),
                     new(NativeOp.IndexAddress, 3, 2, 1, 0, Source: origin), new(NativeOp.Return, A: 3)], new([], 1, []))],
                [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0])], [new([new("value", 0)])]);
            NativeArtifact.Validate(program);
            if (index < count)
                Check((await RuntimeFixture.Run(Reload(program))).AsAddress().Offset.Bytes == index * sizeof(double), "Typed region index changed its coordinate geometry");
            else
            {
                try { await RuntimeFixture.Run(Reload(program)); throw new InvalidOperationException("Out-of-range native region admitted"); }
                catch (MsyncException error) { Check(error.Span == origin && error.InnerException is ArgumentOutOfRangeException, "Region bounds failure lost origin or concrete cause"); }
            }
        }
        Console.WriteLine("PASS real native IR region geometry and bounds properties");
    }

    private static NativeProgram Make(List<NativeInstruction> code, bool range = false) => new([new(NativeKind.Number, Number: 1)],
        [new("generated", [new(0, NativeSlotRole.Parameter), new(1)], code, new([0], 1, []), ParameterCount: 1, IteratorCount: range ? 1 : 0)],
        [new(range ? TypeKind.Range : TypeKind.Bool, Element: range ? 1 : -1), new(TypeKind.Number)], []);
    private static void MaybeStore(List<NativeInstruction> code, Random random)
    {
        if (random.Next(2) == 0) code.Add(new(NativeOp.Constant, 1, 0));
        for (int count = random.Next(3); count > 0; count--) code.Add(new(NativeOp.Nop));
    }
    private static NativeProgram Branch(Random random)
    {
        List<NativeInstruction> code = []; MaybeStore(code, random); int branch = code.Count; code.Add(new(NativeOp.Branch, A: 0));
        int left = code.Count; MaybeStore(code, random); int jump = code.Count; code.Add(new(NativeOp.Jump));
        int right = code.Count; MaybeStore(code, random); int join = code.Count; code.Add(new(NativeOp.Return, A: 1));
        code[branch] = code[branch] with { B = left, C = right }; code[jump] = code[jump] with { A = join }; return Make(code);
    }
    private static NativeProgram Loop(Random random)
    {
        List<NativeInstruction> code = []; MaybeStore(code, random); int branch = code.Count; code.Add(new(NativeOp.Branch, A: 0));
        int body = code.Count; MaybeStore(code, random); code.Add(new(NativeOp.Jump, A: branch));
        int end = code.Count; code.Add(new(NativeOp.Return, A: 1)); code[branch] = code[branch] with { B = body, C = end }; return Make(code);
    }
    private static NativeProgram Iterator(Random random)
    {
        List<NativeInstruction> code = []; MaybeStore(code, random);
        code.Add(new(NativeOp.OpenIterator, A: 0, B: 0)); int next = code.Count; code.Add(new(NativeOp.NextIterator, 1, 0));
        if (random.Next(4) == 0) code.Add(new(NativeOp.OpenIterator, A: 0, B: 0));
        code.Add(new(NativeOp.Jump, A: next)); int end = code.Count;
        code.Add(new(NativeOp.CloseIterator, A: 0)); code.Add(new(NativeOp.Return, A: 1)); code[next] = code[next] with { B = end }; return Make(code, true);
    }
}
