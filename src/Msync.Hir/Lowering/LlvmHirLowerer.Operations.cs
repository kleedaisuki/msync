using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public enum LlvmRuntimeValueOperation : byte { SelectPath, Barrier, Acquire, ProcessCall, HasLayout, DemandBool }
public sealed record LlvmRuntimeValueBinding(LlvmRuntimeValueOperation Operation, int Mode = 0, string? Symbol = null);

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        internal readonly Dictionary<(NativeBinary Operation, LlvmFunctionType Signature), LlvmFunction> binaryBindings = [];
        internal readonly Dictionary<LlvmFunction, LlvmRuntimeValueBinding> valueOperations = [];
        private readonly Dictionary<(LlvmRuntimeValueBinding Binding, LlvmFunctionType Signature), LlvmFunction> operationBindings = [];
        internal LlvmFunction OperationBinding(LlvmRuntimeValueBinding binding, LlvmFunctionType signature)
        {
            if (operationBindings.TryGetValue((binding, signature), out var target))
            { trace?.Detail?.Metric("operations.binding.reused", 1); return target; }
            target = LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage,
                "msync.operation." + binding.Operation.ToString().ToLowerInvariant() + "." + operationBindings.Count, module);
            if (binding.Operation == LlvmRuntimeValueOperation.DemandBool)
                // Waiting touches runtime state, not source-addressable objects.
                target.MemoryEffectsAttribute = new(12);
            operationBindings.Add((binding, signature), target);
            valueOperations.Add(target, binding);
            trace?.Detail?.Metric("operations.binding.created", 1);
            return target;
        }
    }
    private sealed partial class Body
    {
        private LlvmValue RuntimeBinary(HirNode node, HirBinaryOperator operation, LlvmValue left, LlvmValue right)
        {
            var runtimeOperation = operation switch
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
            return RuntimeBinary(node, runtimeOperation, left, right);
        }

        private LlvmValue RuntimeBinary(HirNode node, NativeBinary runtimeOperation, LlvmValue left, LlvmValue right)
        {
            current = node;
            return RuntimeBinary(runtimeOperation, owner.Type(node.Type), owner.metadata.Type(node.Type), left, right);
        }

        private LlvmValue RuntimeBinary(NativeBinary runtimeOperation, LlvmType resultType, int resultLogical,
            LlvmValue left, LlvmValue right)
        {
            if (owner.trace?.Detail is not { } detail) return EmitRuntimeBinary(runtimeOperation, resultType, resultLogical, left, right);
            return detail.MeasureDetailed("compiler.llvm.lower.value-binary", (Body: this, Operation: runtimeOperation, Result: resultType, Logical: resultLogical, Left: left, Right: right),
                static state => state.Body.EmitRuntimeBinary(state.Operation, state.Result, state.Logical, state.Left, state.Right),
                attributes: OperationAttributes(runtimeOperation.ToString()));
        }
        private LlvmValue EmitRuntimeBinary(NativeBinary runtimeOperation, LlvmType resultType, int resultLogical,
            LlvmValue left, LlvmValue right)
        {
            var signature = LlvmFunctionType.Get(resultType, [left.Type, right.Type]);
            if (!owner.binaryBindings.TryGetValue((runtimeOperation, signature), out var target))
            {
                target = LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage,
                    "msync.value." + runtimeOperation, owner.module);
                owner.runtimeCalls.Add(target, runtimeOperation);
                owner.binaryBindings.Add((runtimeOperation, signature), target);
                owner.trace?.Detail?.Metric("operations.binary.binding_created", 1);
            }
            else owner.trace?.Detail?.Metric("operations.binary.binding_reused", 1);
            owner.trace?.Detail?.Metric("operations.binary." + runtimeOperation.ToString().ToLowerInvariant(), 1);
            return Add(new LlvmCallInstruction(signature, target, [left, right])
            { LogicalType = resultLogical });
        }

        // Evaluate source children before reversing
        // Qualify's operands. Preserve that order rather than swapping child visits.
        private LlvmValue ValuePair(HirNode node)
        {
            var left = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            var right = Child(node, 1);
            if (block.HasTerminator) return owner.Null;
            return node.Kind == HirKind.Qualify
                ? RuntimeBinary(node, NativeBinary.Qualify, right, left)
                : RuntimeBinary(node, NativeBinary.Qualify, left, right);
        }
        private LlvmValue Regex(HirNode node)
        {
            var subject = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            current = node;
            var pattern = OperationLiteral(new(HirLiteralKind.String, Text: node.Text));
            return RuntimeBinary(node, NativeBinary.RegexMatch, subject, pattern);
        }
        private LlvmValue SelectPath(HirNode node)
        {
            var value = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            current = node;
            return OperationCall(new(LlvmRuntimeValueOperation.SelectPath, Symbol: node.Text),
                owner.Type(node.Type), owner.metadata.Type(node.Type), [value]);
        }
        private LlvmValue ProcessCall(HirNode node, LlvmValue callee, IReadOnlyList<LlvmValue> arguments)
        {
            current = node;
            return OperationCall(new(LlvmRuntimeValueOperation.ProcessCall, Hir.Calls[node.Data].Scheduling), owner.Type(node.Type),
                owner.metadata.Type(node.Type), new[] { callee }.Concat(arguments).ToArray());
        }
        private LlvmCallInstruction OperationCall(LlvmRuntimeValueBinding binding, LlvmType result,
            int logicalType, IReadOnlyList<LlvmValue> arguments)
        {
            if (owner.trace?.Detail is not { } detail) return EmitOperationCall(binding, result, logicalType, arguments);
            var attributes = OperationAttributes(binding.Operation.ToString());
            attributes["operation.mode"] = binding.Mode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (binding.Symbol is not null) attributes["operation.symbol"] = binding.Symbol;
            return detail.MeasureDetailed("compiler.llvm.lower.value-operation", (Body: this, binding, result, logicalType, arguments),
                static state => state.Body.EmitOperationCall(state.binding, state.result, state.logicalType, state.arguments), attributes: attributes);
        }
        private LlvmCallInstruction EmitOperationCall(LlvmRuntimeValueBinding binding, LlvmType result,
            int logicalType, IReadOnlyList<LlvmValue> arguments)
        {
            var signature = LlvmFunctionType.Get(result, arguments.Select(value => value.Type).ToArray());
            var target = owner.OperationBinding(binding, signature);
            owner.trace?.Detail?.Metric("operations." + binding.Operation.ToString().ToLowerInvariant(), 1);
            return Add(new LlvmCallInstruction(signature, target, arguments) { LogicalType = logicalType });
        }
        private Dictionary<string, string> OperationAttributes(string operation)
        {
            var attributes = new Dictionary<string, string>
            {
                ["function.name"] = function.Name,
                ["operation.name"] = operation,
                ["source.offset"] = current.Source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["source.length"] = current.Source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (current.Source.Source.Value >= 0) attributes["source.file"] = Hir.Sources[current.Source.Source.Value].Name;
            return attributes;
        }
        private LlvmValue OperationLiteral(HirLiteral literal)
        {
            TypeRef runtime = literal.Kind switch
            {
                HirLiteralKind.Null => TypeRef.Null,
                HirLiteralKind.Bool => TypeRef.Bool,
                HirLiteralKind.Number => TypeRef.Number,
                HirLiteralKind.String => TypeRef.String,
                HirLiteralKind.Path => TypeRef.Path,
                HirLiteralKind.Connection => TypeRef.Connection,
                _ => TypeRef.Type
            };
            int logicalType = owner.metadata.RuntimeType(runtime);
            LlvmValue value = literal.Kind switch
            {
                HirLiteralKind.Null => owner.Null,
                HirLiteralKind.Bool => LlvmConstantInt.GetBool(owner.context, literal.Boolean),
                HirLiteralKind.Number => LlvmConstantFP.Get(LlvmType.GetDoubleTy(owner.context), literal.Number),
                _ => RelocatedValue(logicalType, owner.RepresentationType(runtime.Kind),
                    new(literal.Kind switch
                    {
                        HirLiteralKind.String => NativeKind.String,
                        HirLiteralKind.Path => NativeKind.Path,
                        HirLiteralKind.Connection => NativeKind.Connection,
                        _ => NativeKind.Type
                    }, Text: literal.Text, Type: literal.Type, ConnectionKind: literal.ConnectionKind)
                    { OriginId = current.Source.Source.Value, OriginOffset = current.Source.Start })
            };
            if (value is not LlvmConstant || value is LlvmGlobalValue) value.LogicalType = logicalType;
            return value;
        }
        private LlvmValue Match(HirNode node)
        {
            var subject = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            current = node;
            var done = new LlvmBasicBlock(owner.context, "match.end", function);
            var incoming = new List<(LlvmValue Value, LlvmBasicBlock Block)>();
            foreach (var arm in Hir.Matches[node.Data].Arms)
            {
                current = node;
                LlvmBasicBlock? next = null;
                if (arm.Pattern.Kind != HirPatternKind.Any)
                {
                    var accepted = MatchPattern(arm.Pattern, subject);
                    var selected = new LlvmBasicBlock(owner.context, "match.arm", function);
                    next = new LlvmBasicBlock(owner.context, "match.next", function);
                    BranchOn(accepted, selected, next);
                    block = selected;
                }
                if (arm.Pattern.Binding is { } binding)
                {
                    if (owner.captured.Contains(binding)) FreshLocation(binding);
                    var location = Hir.Locations[binding.Value];
                    var narrowed = Represent(subject, location.StorageType, location.AdmitsNull);
                    Add(new LlvmStoreInstruction(narrowed, Address(binding)));
                    owner.trace?.Detail?.Metric("operations.match.bindings", 1);
                }
                var value = Emit(arm.Value);
                if (!block.HasTerminator)
                {
                    current = node;
                    value = Represent(value, node.Type);
                    incoming.Add((value, block));
                    Add(new LlvmUncondBrInstruction(done));
                }
                owner.trace?.Detail?.Metric("operations.match.arms", 1);
                if (next is null) break;
                block = next;
            }
            current = node;
            if (!block.HasTerminator) Add(new LlvmUnreachableInstruction(owner.context));
            block = done;
            if (incoming.Count == 0)
            { Add(new LlvmUnreachableInstruction(owner.context)); return owner.Null; }
            if (incoming.Count == 1) return incoming[0].Value;
            var phi = Add(new LlvmPhiInstruction(owner.Type(node.Type), incoming.Count)
            { LogicalType = owner.metadata.Type(node.Type) });
            foreach (var entry in incoming) phi.AddIncoming(entry.Value, entry.Block);
            owner.trace?.Detail?.Metric("operations.match.phis", 1);
            return phi;
        }
        private LlvmValue MatchPattern(HirPattern pattern, LlvmValue subject)
        {
            owner.trace?.Detail?.Metric("operations.match.pattern." + pattern.Kind.ToString().ToLowerInvariant(), 1);
            var boolean = LlvmIntegerType.Get(owner.context, 1);
            int logical = owner.metadata.RuntimeType(TypeRef.Bool);
            return pattern.Kind switch
            {
                HirPatternKind.Null => ConversionCall(LlvmRuntimeConversionOperation.IsNull, boolean, logical, [subject]),
                HirPatternKind.Type => ConversionCall(LlvmRuntimeConversionOperation.IsType, boolean, logical, [subject, TypeValue(new(pattern.Data))]),
                HirPatternKind.Regex => RuntimeBinary(NativeBinary.RegexMatch, boolean, logical, subject,
                    OperationLiteral(new(HirLiteralKind.String, Text: pattern.Text))),
                HirPatternKind.Literal => RuntimeBinary(NativeBinary.Equal, boolean, logical, subject,
                    OperationLiteral(Hir.Literals[pattern.Data])),
                _ => LlvmConstantInt.GetTrue(owner.context)
            };
        }

        private readonly List<string> acquireRegions = [];
        private int nextAcquireRegion;
        private LlvmValue Barrier(HirNode node)
        {
            var barrier = Hir.Barriers[node.Data];
            if (barrier.Release) BarrierCall(0, null);
            if (node.Children.Count == 0)
            {
                if (barrier.Acquire) AcquireCall(null);
                return owner.Null;
            }
            string? region = barrier.Acquire ? (++nextAcquireRegion).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            if (region is not null)
            {
                BarrierCall(1, region);
                acquireRegions.Add(region);
            }
            var value = Child(node, 0);
            if (region is not null) acquireRegions.RemoveAt(acquireRegions.Count - 1);
            if (block.HasTerminator) return owner.Null;
            current = node;
            if (region is null) return value;
            if (Hir.Types[node.Type.Value].RuntimeType == TypeRef.Process) value = AcquireCall(value);
            BarrierCall(0, region);
            return value;
        }
        private void CloseAcquire(int depth)
        {
            for (int i = acquireRegions.Count - 1; i >= depth; --i)
            {
                BarrierCall(0, acquireRegions[i]);
                owner.trace?.Detail?.Metric("operations.barrier.abrupt_releases", 1);
            }
        }
        private void BarrierCall(int beginRegion, string? region) => OperationCall(
            new(LlvmRuntimeValueOperation.Barrier, beginRegion, region), owner.Null.Type,
            owner.metadata.RuntimeType(TypeRef.Null), []);
        private LlvmValue AcquireCall(LlvmValue? process) => OperationCall(
            new(LlvmRuntimeValueOperation.Acquire), process?.Type ?? owner.Null.Type,
            process?.LogicalType ?? owner.metadata.RuntimeType(TypeRef.Null), process is null ? [] : [process]);
    }
}
