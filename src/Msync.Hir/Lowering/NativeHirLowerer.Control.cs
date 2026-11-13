using System.Globalization;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class NativeHirLowerer
{
    private sealed partial class FunctionLowering
    {
        private int Conditional(HirNode node)
        {
            int condition = Emit(Child(node, 0));
            if (!reachable) return Register(node.Type);
            int targetType = owner.Type(node.Type);
            int result = Register(owner.Union([targetType, owner.Type(Hir.Node(Child(node, 1)).Type),
                owner.Type(Hir.Node(Child(node, 2)).Type)]));
            int branch = Add(new(NativeOp.Branch, A: condition));
            int yes = code.Count;
            reachable = true;
            int accepted = Emit(Child(node, 1));
            Copy(result, accepted, Source(node));
            int done = Add(new(NativeOp.Jump));
            int no = code.Count;
            reachable = true;
            int rejected = Emit(Child(node, 2));
            Copy(result, rejected, Source(node));
            bool noContinues = reachable;
            PatchBranch(branch, yes, no);
            if (done >= 0) PatchJump(done, code.Count);
            reachable = done >= 0 || noContinues;
            return reachable ? Narrow(result, targetType, Source(node)) : Register(node.Type);
        }

        private int Binary(HirNode node)
        {
            var operation = (HirBinaryOperator)node.Data;
            if (operation is HirBinaryOperator.And or HirBinaryOperator.Or)
                return ShortCircuit(node, operation == HirBinaryOperator.And);
            NativeBinary lowered = operation switch
            {
                HirBinaryOperator.Add => NativeBinary.Add,
                HirBinaryOperator.Subtract => NativeBinary.Subtract,
                HirBinaryOperator.Multiply => NativeBinary.Multiply,
                HirBinaryOperator.Divide => NativeBinary.Divide,
                HirBinaryOperator.Remainder => NativeBinary.Remainder,
                HirBinaryOperator.Power => NativeBinary.Power,
                HirBinaryOperator.Equal => NativeBinary.Equal,
                HirBinaryOperator.NotEqual => NativeBinary.NotEqual,
                HirBinaryOperator.Less => NativeBinary.Less,
                HirBinaryOperator.LessEqual => NativeBinary.LessEqual,
                HirBinaryOperator.Greater => NativeBinary.Greater,
                HirBinaryOperator.GreaterEqual => NativeBinary.GreaterEqual,
                _ => throw new InvalidOperationException($"Unknown binary operation {operation}.")
            };
            return Pair(node, lowered);
        }

        private int ShortCircuit(HirNode node, bool and)
        {
            int left = Emit(Child(node, 0));
            if (!reachable) return Register(node.Type);
            int result = Register(node.Type); Copy(result, left);
            int branch = Add(new(NativeOp.Branch, A: left));
            int evaluate = code.Count;
            reachable = true;
            int right = Emit(Child(node, 1)); Copy(result, right);
            int end = code.Count;
            PatchBranch(branch, and ? evaluate : end, and ? end : evaluate);
            reachable = true; return result;
        }

        private int For(HirNode node)
        {
            var iteration = Hir.Fors[node.Data];
            int producer = Emit(Child(node, 0)); int iterator = iteratorCount++;
            if (!reachable) return Register(node.Type);
            Add(new(NativeOp.OpenIterator, A: producer, B: iterator, Source: Source(node)));
            int begin = code.Count;
            int item = Register(owner.LocationType(iteration.Item));
            int next = Add(new(NativeOp.NextIterator, item, iterator, Source: Source(node)));
            var state = new LoopState(iteration.Loop, -1, iterators.Count, acquireRegions.Count);
            loops.Add(state); iterators.Add(iterator);
            if (owner.IsCaptured(iteration.Item)) FreshLocation(iteration.Item);
            Store(iteration.Item, item, true, Source(node));
            int body = Emit(Child(node, 1)); Add(new(NativeOp.Drop, A: body));
            Add(new(NativeOp.Jump, A: begin));
            code[next] = code[next] with { B = code.Count };
            reachable = true;
            Add(new(NativeOp.CloseIterator, A: iterator));
            FinishLoop(state, code.Count); return Null(node.Source);
        }

        private int Loop(HirNode node)
        {
            int result = Register(node.Type);
            var state = new LoopState(new(node.Data), result, iterators.Count, acquireRegions.Count);
            loops.Add(state); int begin = code.Count;
            int body = Emit(Child(node, 0)); Add(new(NativeOp.Drop, A: body));
            Add(new(NativeOp.Jump, A: begin)); FinishLoop(state, code.Count); return result;
        }

        private void FinishLoop(LoopState state, int end)
        {
            foreach (int branch in state.Breaks) PatchJump(branch, end);
            reachable |= state.Breaks.Count != 0;
            loops.RemoveAt(loops.Count - 1);
            iterators.RemoveRange(state.IteratorDepth, iterators.Count - state.IteratorDepth);
        }

        private int Break(HirNode node)
        {
            var state = loops.Last(loop => loop.Id.Value == node.Data);
            if (state.Result >= 0)
            {
                int value = node.Children.Count == 0 ? Null() : Emit(Child(node, 0));
                Copy(state.Result, value, Source(node));
            }
            CloseIterators(state.IteratorDepth); CloseAcquire(state.AcquireDepth);
            int branch = Add(new(NativeOp.Jump, Source: Source(node)));
            if (branch >= 0) state.Breaks.Add(branch);
            return Register(node.Type);
        }

        private int Return(HirNode node)
        {
            int value = node.Children.Count == 0 ? Null() : Emit(Child(node, 0));
            EmitReturn(value, Source(node)); return Register(node.Type);
        }

        private void EmitReturn(int value, SourceSpan? source)
        {
            if (slots[value].Type == owner.Type(new TypeRef(TypeKind.Never)))
            {
                Add(new(NativeOp.Unreachable, Source: source));
                return;
            }
            int contract = owner.Type(function.Result);
            int abi = owner.Union([slots[value].Type, contract]);
            if (abi != slots[value].Type)
            {
                int widened = Register(abi);
                Copy(widened, value, source);
                value = widened;
            }
            int result = Narrow(value, contract, source);
            CloseIterators(0); CloseAcquire(0);
            Add(new(NativeOp.Return, A: result, Source: source));
        }

        private void CloseIterators(int depth)
        {
            for (int i = iterators.Count - 1; i >= depth; i--)
                Add(new(NativeOp.CloseIterator, A: iterators[i]));
        }

        private void CloseAcquire(int depth)
        {
            for (int i = acquireRegions.Count - 1; i >= depth; i--)
                Add(new(NativeOp.Barrier, A: (int)BarrierDirection.Release, B: 0, Symbol: acquireRegions[i]));
        }

        private int Barrier(HirNode node)
        {
            var barrier = Hir.Barriers[node.Data];
            if (barrier.Release) Add(new(NativeOp.Barrier, A: (int)BarrierDirection.Release, B: 0, Source: Source(node)));
            if (node.Children.Count == 0)
            {
                if (barrier.Acquire) Add(new(NativeOp.Acquire, A: -1, Source: Source(node)));
                return Null();
            }
            string? region = barrier.Acquire ? (++nextRegion).ToString(CultureInfo.InvariantCulture) : null;
            if (region is not null)
            {
                Add(new(NativeOp.Barrier, A: (int)BarrierDirection.Release, B: 1, Symbol: region, Source: Source(node)));
                acquireRegions.Add(region);
            }
            int value = Emit(Child(node, 0));
            if (region is null) return value;
            acquireRegions.RemoveAt(acquireRegions.Count - 1);
            if (owner.Runtime(node.Type) == TypeRef.Process)
            {
                int result = Register(node.Type);
                Add(new(NativeOp.Acquire, result, value, Source: Source(node))); value = result;
            }
            Add(new(NativeOp.Barrier, A: (int)BarrierDirection.Release, B: 0, Symbol: region, Source: Source(node)));
            return value;
        }

        private int Match(HirNode node)
        {
            int subject = Emit(Child(node, 0)); int result = Register(node.Type);
            if (!reachable) return result;
            var arms = Hir.Matches[node.Data].Arms;
            slots[result] = slots[result] with
            {
                Type = owner.Union(
                arms.Select(arm => owner.Type(Hir.Node(arm.Value).Type)).Append(owner.Type(node.Type)))
            };
            List<int> exits = [];
            for (int i = 0; i < arms.Length; i++)
            {
                var arm = arms[i];
                int branch = -1;
                if (arm.Pattern.Kind != HirPatternKind.Any)
                {
                    int accepted = Pattern(arm.Pattern, subject, node.Source);
                    branch = Add(new(NativeOp.Branch, A: accepted, B: code.Count + 1));
                    reachable = true;
                }
                if (arm.Pattern.Binding is { } binding)
                {
                    if (owner.IsCaptured(binding)) FreshLocation(binding);
                    int narrowed = Narrow(subject, owner.LocationType(binding), Source(node));
                    Store(binding, narrowed, true, Source(node));
                }
                int value = Emit(arm.Value); Copy(result, value, Source(node));
                int exit = Add(new(NativeOp.Jump));
                if (exit >= 0) exits.Add(exit);
                if (branch < 0) break;
                code[branch] = code[branch] with { C = code.Count };
                reachable = true;
            }
            Add(new(NativeOp.Unreachable, Symbol: "An exhaustive match selected no arm.", Source: Source(node)));
            foreach (int exit in exits) PatchJump(exit, code.Count);
            reachable = exits.Count != 0;
            return reachable ? Narrow(result, owner.Type(node.Type), Source(node)) : Register(node.Type);
        }

        private int Pattern(HirPattern pattern, int subject, HirSourceRange source)
        {
            int result = Register(owner.Type(TypeRef.Bool));
            SourceSpan? span = owner.Source(source);
            if (pattern.Kind == HirPatternKind.Any)
            {
                int truth = Literal(new(HirLiteralKind.Bool, Boolean: true), span);
                Copy(result, truth); return result;
            }
            if (pattern.Kind == HirPatternKind.Null)
                Add(new(NativeOp.Unary, result, subject, Immediate: (int)NativeUnary.IsNull, Source: span));
            else if (pattern.Kind == HirPatternKind.Type)
                Add(new(NativeOp.Unary, result, subject, TypeValue(new(pattern.Data), span),
                    Immediate: (int)NativeUnary.IsType, Source: span));
            else if (pattern.Kind == HirPatternKind.Regex)
                Add(new(NativeOp.Binary, result, subject,
                    Literal(new(HirLiteralKind.String, Text: pattern.Text), span),
                    Immediate: (int)NativeBinary.RegexMatch, Source: span));
            else
            {
                var literal = Hir.Literals[pattern.Data];
                int expected = Literal(literal, span);
                Add(new(NativeOp.Binary, result, subject, expected, Immediate: (int)NativeBinary.Equal, Source: span));
            }
            return result;
        }
    }
}
