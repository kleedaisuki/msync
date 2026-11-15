using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Body
    {
        private LlvmValue Literal(HirNode node)
        {
            var literal = Hir.Literals[node.Data];
            return literal.Kind switch
            {
                HirLiteralKind.Null => owner.Null,
                HirLiteralKind.Bool => LlvmConstantInt.GetBool(owner.context, literal.Boolean),
                HirLiteralKind.Number => LlvmConstantFP.Get(owner.Type(node.Type), literal.Number),
                HirLiteralKind.String or HirLiteralKind.Path or HirLiteralKind.Connection or HirLiteralKind.Type => Relocated(node, literal),
                _ => throw new LlvmHirLoweringException(node.Source, "literal " + literal.Kind)
            };
        }

        private LlvmValue Relocated(HirNode node, HirLiteral literal)
        {
            NativeKind kind = literal.Kind switch
            {
                HirLiteralKind.Path => NativeKind.Path,
                HirLiteralKind.Connection => NativeKind.Connection,
                HirLiteralKind.Type => NativeKind.Type,
                _ => NativeKind.String
            };
            return RelocatedValue(owner.metadata.Type(node.Type), owner.Type(node.Type),
                new(kind, Text: literal.Text, Type: literal.Type, ConnectionKind: literal.ConnectionKind)
                { OriginId = node.Source.Source.Value, OriginOffset = node.Source.Start });
        }

        private LlvmValue Unary(HirNode node)
        {
            var value = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            current = node;
            return (HirUnaryOperator)node.Data == HirUnaryOperator.Not
                ? Add(new LlvmBinaryOperator(LlvmOpcode.Xor, value, LlvmConstantInt.GetTrue(owner.context)))
                : Add(new LlvmUnaryOperator(value));
        }

        private LlvmValue Binary(HirNode node)
        {
            var operation = (HirBinaryOperator)node.Data;
            var left = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            if (operation is HirBinaryOperator.And or HirBinaryOperator.Or)
                return Conditional(node, left,
                    operation == HirBinaryOperator.And ? () => Child(node, 1) : () => left,
                    operation == HirBinaryOperator.Or ? () => Child(node, 1) : () => left);
            var right = Child(node, 1);
            if (block.HasTerminator) return owner.Null;
            current = node;
            if (left.Type.IsDoubleTy() && right.Type.IsDoubleTy())
            {
                LlvmOpcode? opcode = operation switch
                {
                    HirBinaryOperator.Add => LlvmOpcode.FAdd,
                    HirBinaryOperator.Subtract => LlvmOpcode.FSub,
                    HirBinaryOperator.Multiply => LlvmOpcode.FMul,
                    HirBinaryOperator.Divide => LlvmOpcode.FDiv,
                    HirBinaryOperator.Remainder => LlvmOpcode.FRem,
                    _ => null
                };
                if (opcode is { } arithmetic) return Add(new LlvmBinaryOperator(arithmetic, left, right));
                var predicate = operation switch
                {
                    HirBinaryOperator.Equal => LlvmCmpPredicate.FCmpOeq,
                    HirBinaryOperator.NotEqual => LlvmCmpPredicate.FCmpUne,
                    HirBinaryOperator.Less => LlvmCmpPredicate.FCmpOlt,
                    HirBinaryOperator.LessEqual => LlvmCmpPredicate.FCmpOle,
                    HirBinaryOperator.Greater => LlvmCmpPredicate.FCmpOgt,
                    HirBinaryOperator.GreaterEqual => LlvmCmpPredicate.FCmpOge,
                    _ => (LlvmCmpPredicate?)null
                };
                if (predicate is { } comparison) return Add(new LlvmFCmpInstruction(comparison, left, right));
            }
            if (left.Type.IsIntegerTy(1) && right.Type.IsIntegerTy(1) && operation is HirBinaryOperator.Equal or HirBinaryOperator.NotEqual)
                return Add(new LlvmICmpInstruction(operation == HirBinaryOperator.Equal ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe, left, right));
            return RuntimeBinary(node, operation, left, right);
        }

        private LlvmValue If(HirNode node)
        {
            var condition = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            return Conditional(node, condition, () => Child(node, 1),
                () => node.Children.Count > 2 ? Child(node, 2) : owner.Null);
        }

        private LlvmValue Conditional(HirNode node, LlvmValue condition, Func<LlvmValue> whenTrue, Func<LlvmValue> whenFalse)
        {
            var yes = new LlvmBasicBlock(owner.context, "if.then", function);
            var no = new LlvmBasicBlock(owner.context, "if.else", function);
            var done = new LlvmBasicBlock(owner.context, "if.end", function);
            current = node;
            BranchOn(condition, yes, no);
            var incoming = new List<(LlvmValue Value, LlvmBasicBlock Block)>();
            BranchValue(yes, whenTrue);
            BranchValue(no, whenFalse);
            block = done;
            current = node;
            if (incoming.Count == 0)
            {
                Add(new LlvmUnreachableInstruction(owner.context));
                return owner.Null;
            }
            if (incoming.Count == 1) return incoming[0].Value;
            var phi = Add(new LlvmPhiInstruction(owner.Type(node.Type), 2));
            foreach (var item in incoming) phi.AddIncoming(item.Value, item.Block);
            return phi;

            void BranchValue(LlvmBasicBlock target, Func<LlvmValue> produce)
            {
                block = target;
                var value = produce();
                if (block.HasTerminator) return;
                value = Represent(value, node.Type);
                incoming.Add((value, block));
                current = node;
                Add(new LlvmUncondBrInstruction(done));
            }
        }

        private void BranchOn(LlvmValue condition, LlvmBasicBlock whenTrue, LlvmBasicBlock whenFalse)
        {
            if (condition is not LlvmConstantInt)
                OperationCall(new(LlvmRuntimeValueOperation.DemandBool), LlvmType.GetVoidTy(owner.context),
                    owner.metadata.RuntimeType(Msync.Core.TypeRef.Null), [condition]);
            Add(new LlvmCondBrInstruction(condition, whenTrue, whenFalse));
        }
    }
}
