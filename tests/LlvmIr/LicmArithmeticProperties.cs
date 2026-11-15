using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class LicmArithmeticProperties
{
    internal static int Run()
    {
        Associative();
        Subtract();
        FloatingFlags();
        MinMax();
        Gep();
        return 5;
    }
    private static bool Hoist(Fixture fixture, LlvmInstruction instruction) =>
        LlvmLicmArithmetic.HoistArithmetics(instruction, fixture.Loop, fixture.Tree, static dead => dead.EraseFromParent());
    private static void Associative()
    {
        Fixture f = new();
        var inner = new LlvmBinaryOperator(LlvmOpcode.Add, f.Index, f.Function.Arguments[0]) { HasNoUnsignedWrap = true, HasNoSignedWrap = true };
        var outer = new LlvmBinaryOperator(LlvmOpcode.Add, inner, f.Function.Arguments[1]) { HasNoUnsignedWrap = true, HasNoSignedWrap = true };
        f.Header.Append(inner); f.Header.Append(outer); f.Seal(outer);
        Program.Require(Hoist(f, outer) && inner.Parent is null && outer.Parent is null,
            "Associative invariant operands must hoist together and replace the dead loop chain.");
        var invariant = f.Entry.Instructions.OfType<LlvmBinaryOperator>().Single();
        var result = f.Header.Instructions.OfType<LlvmBinaryOperator>().Single(operation => !ReferenceEquals(operation, f.Next));
        Program.Require(ReferenceEquals(invariant.Left, f.Function.Arguments[0]) && ReferenceEquals(invariant.Right, f.Function.Arguments[1]) &&
            ReferenceEquals(result.Left, f.Index) && ReferenceEquals(result.Right, invariant) && result.HasNoUnsignedWrap && result.HasNoSignedWrap,
            "Source associative rewrites must preserve the applicable merged integer flags.");
    }
    private static void Subtract()
    {
        Fixture f = new();
        var inner = new LlvmBinaryOperator(LlvmOpcode.Sub, f.Index, f.Function.Arguments[0]) { HasNoSignedWrap = true };
        var outer = new LlvmBinaryOperator(LlvmOpcode.Add, inner, f.Function.Arguments[1]) { HasNoSignedWrap = true };
        f.Header.Append(inner); f.Header.Append(outer); f.Seal(outer);
        Program.Require(Hoist(f, outer), "The source subtraction/addition reassociation must expose invariant subtraction.");
        var invariant = f.Entry.Instructions.OfType<LlvmBinaryOperator>().Single();
        var result = f.Header.Instructions.OfType<LlvmBinaryOperator>().Single(operation => !ReferenceEquals(operation, f.Next));
        Program.Require(invariant.Opcode == LlvmOpcode.Sub && ReferenceEquals(invariant.Left, f.Function.Arguments[1]) &&
            ReferenceEquals(invariant.Right, f.Function.Arguments[0]) && !result.HasNoSignedWrap && !result.HasNoUnsignedWrap,
            "Subtraction reassociation must order C2-C1 correctly and not invent no-wrap flags.");
    }
    private static void FloatingFlags()
    {
        Fixture f = new(true);
        var inner = new LlvmBinaryOperator(LlvmOpcode.FMul, f.Index, f.Function.Arguments[0]);
        var outer = new LlvmBinaryOperator(LlvmOpcode.FMul, inner, f.Function.Arguments[1]);
        f.Header.Append(inner); f.Header.Append(outer); f.Seal(outer);
        Program.Require(!Hoist(f, outer), "Ordinary language FP operations must not acquire reassociation permission.");
        inner.FastMathFlags = outer.FastMathFlags = LlvmFastMathFlags.AllowReassoc;
        Program.Require(Hoist(f, outer), "The source FMul binary-association path uses its actual allow-reassoc flag.");
        var invariant = f.Entry.Instructions.OfType<LlvmBinaryOperator>().Single();
        Program.Require(invariant.FastMathFlags == LlvmFastMathFlags.AllowReassoc,
            "New floating arithmetic must receive only the intersection of existing source flags.");
    }
    private static void MinMax()
    {
        Fixture f = new();
        var first = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSlt, f.Index, f.Function.Arguments[0]);
        var second = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSlt, f.Index, f.Function.Arguments[1]);
        var both = new LlvmSelectInstruction(first, second, LlvmConstantInt.Get(LlvmType.GetInt1Ty(f.Context), 0));
        f.Header.Append(first); f.Header.Append(second); f.Header.Append(both); f.Seal(both);
        Program.Require(Hoist(f, both) && first.Parent is null && second.Parent is null && both.Parent is null,
            "Logical relational tests must become one loop compare against the hoisted source min intrinsic.");
        var call = f.Entry.Instructions.OfType<LlvmCallInstruction>().Single();
        Program.Require(LlvmIntrinsic.GetDescriptor(call.CalledFunction!)?.Kind == LlvmIntrinsicKind.SMin &&
            f.Entry.Instructions.OfType<LlvmFreezeInstruction>().Count() == 1,
            "A poison-blocking select must preserve the source freeze on its newly guaranteed invariant operand.");
    }
    private static void Gep()
    {
        Fixture f = new();
        var allocation = new LlvmAllocaInstruction(f.Number, LlvmConstantInt.Get((LlvmIntegerType)f.Number, 1));
        f.Entry.InsertBefore(allocation, f.Entry.GetTerminator());
        var inner = new LlvmGetElementPtrInstruction(f.Number, allocation, [f.Index], LlvmGepNoWrapFlags.InBounds);
        var outer = new LlvmGetElementPtrInstruction(f.Number, inner, [f.Function.Arguments[0]], LlvmGepNoWrapFlags.InBounds);
        var load = new LlvmLoadInstruction(f.Number, outer);
        f.Header.Append(inner); f.Header.Append(outer); f.Header.Append(load); f.Seal(load);
        Program.Require(Hoist(f, outer) && inner.Parent is null && outer.Parent is null,
            "Nested variant-then-invariant GEP offsets must swap to expose the invariant preheader address.");
        var invariant = f.Entry.Instructions.OfType<LlvmGetElementPtrInstruction>().Single();
        Program.Require(ReferenceEquals(invariant.PointerOperand, allocation) && ReferenceEquals(invariant.GetIndex(0), f.Function.Arguments[0]) &&
            !invariant.NoWrapFlags.IsInBounds,
            "GEP reassociation must not retain inbounds when source non-negative offset knowledge is absent.");
    }

    private sealed class Fixture
    {
        internal readonly LlvmContext Context = new();
        internal readonly LlvmType Number;
        internal readonly LlvmFunction Function;
        internal readonly LlvmBasicBlock Entry, Header, Exit;
        internal readonly LlvmPhiInstruction Index;
        internal readonly LlvmBinaryOperator Next;
        internal LlvmDominatorTree Tree = null!;
        internal LlvmLoop Loop = null!;
        internal Fixture(bool floating = false)
        {
            LlvmModule module = new("licm-arithmetic", Context);
            Number = floating ? LlvmType.GetDoubleTy(Context) : LlvmIntegerType.Get(Context, 32);
            Function = LlvmFunction.Create(LlvmFunctionType.Get(Number, [Number, Number, LlvmType.GetInt1Ty(Context)]),
                LlvmLinkage.InternalLinkage, "arithmetic", module);
            Entry = new(Context, "preheader", Function); Header = new(Context, "loop", Function); Exit = new(Context, "exit", Function);
            Entry.Append(new LlvmUncondBrInstruction(Header));
            Index = new(Number);
            LlvmValue zero = floating ? LlvmConstantFP.Get(Number, LlvmAPFloat.FromBits(0)) : LlvmConstantInt.Get((LlvmIntegerType)Number, 0);
            LlvmValue one = floating ? LlvmConstantFP.Get(Number, LlvmAPFloat.FromBits(0x3ff0000000000000UL)) : LlvmConstantInt.Get((LlvmIntegerType)Number, 1);
            Next = new(floating ? LlvmOpcode.FAdd : LlvmOpcode.Add, Index, one);
            Index.AddIncoming(zero, Entry); Index.AddIncoming(Next, Header); Header.Append(Index);
        }
        internal void Seal(LlvmValue result)
        {
            Header.Append(Next); Header.Append(new LlvmCondBrInstruction(Function.Arguments[2], Header, Exit));
            var exitPhi = new LlvmPhiInstruction(result.Type); exitPhi.AddIncoming(result, Header); Exit.Append(exitPhi);
            LlvmValue returned = exitPhi;
            if (!ReferenceEquals(result.Type, Number))
            { var cast = new LlvmCastInstruction(LlvmOpcode.ZExt, exitPhi, Number); Exit.Append(cast); returned = cast; }
            Exit.Append(new LlvmReturnInstruction(Context, returned));
            Tree = new(Function); Loop = new LlvmLoopInfo(Function, Tree).GetLoopFor(Header)!;
        }
    }
}
