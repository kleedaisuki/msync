using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<LlvmValue, IReadOnlyList<LlvmValue>?> rangeInputs = [];
        private HashSet<LlvmFunction>? dynamicallyDispatched;
        private Dictionary<LlvmFunction, List<LlvmCallInstruction>>? closedInvocations;
        private int representedRangeEdges;
        private int returnedRangeArguments;

        internal TypeKind SnapshotKind(LlvmCallInstruction origin, int index, LlvmType element)
        {
            int type = origin.GetOperandUse(index).LogicalType;
            if (type < 0) type = origin.GetArgument(index).LogicalType;
            int item = type >= 0 ? graph.Types[type].Element : -1;
            return item >= 0 ? graph.Types[item].Kind : element.IsDoubleTy() ? TypeKind.Number : TypeKind.Never;
        }

        internal void ReportRangeInputs(CompilationTrace trace)
        {
            trace.Metric("range.forwarded.queries", rangeInputs.Count);
            trace.Metric("range.forwarded.edges", rangeInputs.Values.Sum(values => values?.Count ?? 0));
            trace.Metric("range.forwarded.arguments",
                rangeInputs.Count(pair => pair.Key is LlvmArgument && pair.Value is { Count: > 0 }));
            trace.Metric("range.forwarded.representations", representedRangeEdges);
            trace.Metric("range.forwarded.returned_arguments", returnedRangeArguments);
        }

        internal IReadOnlyList<LlvmValue>? RangeInputs(LlvmValue value)
        {
            if (rangeInputs.TryGetValue(value, out var inputs)) return inputs;
            inputs = value switch
            {
                LlvmPhiInstruction phi => Enumerable.Range(0, phi.IncomingCount).Select(phi.GetIncomingValue).ToArray(),
                LlvmSelectInstruction select => [select.TrueValue, select.FalseValue],
                LlvmArgument argument => DirectRangeArguments(argument),
                LlvmCallInstruction { ArgumentCount: 1 } representation when PreservesRangePayload(representation) =>
                    RepresentedRange(representation),
                LlvmCallInstruction { CalledFunction: { IsDeclaration: false } target } direct =>
                    ReturnedRanges(direct, target).ToArray(),
                LlvmCallInstruction { CalledFunction: { } target } indirect when PhysicalInvoke(target) is { } invoke =>
                    invoke.Targets.Values.Distinct().SelectMany(callee => ReturnedRanges(indirect, callee, invocation: true)).ToArray(),
                _ => null
            };
            if (inputs is { Count: > 1 } && (Union(value.Type) & TypeKind.Range) != 0)
                inputs = inputs.Where(CanSupplyRangeArm).ToArray();
            return rangeInputs[value] = inputs;
        }

        private bool CanSupplyRangeArm(LlvmValue input)
        {
            TypeKind kind;
            if (input is LlvmCallInstruction { ArgumentCount: 1 } pack
                && RepresentationOperation(pack) == LlvmRuntimeConversionOperation.Represent)
                kind = ReflectedType(pack, 0)?.Kind ?? TypeKind.Never;
            else if ((uint)input.LogicalType < (uint)graph.Types.Count)
                kind = graph.Types[input.LogicalType].Kind;
            else return true;
            return kind == TypeKind.Never || (kind & TypeKind.Range) != 0;
        }

        private bool PreservesRangePayload(LlvmCallInstruction call)
        {
            if (RepresentationOperation(call) is not (LlvmRuntimeConversionOperation.Represent or LlvmRuntimeConversionOperation.Narrow))
                return false;
            TypeKind result = Union(call.Type);
            if (result == TypeKind.Never && (uint)call.LogicalType < (uint)graph.Types.Count)
                result = graph.Types[call.LogicalType].Kind;
            if ((result & TypeKind.Range) == 0) return false;
            TypeKind input = Union(call.GetArgument(0).Type);
            if (input == TypeKind.Never) input = ReflectedType(call, 0)?.Kind ?? TypeKind.Never;
            return (input & TypeKind.Range) != 0;
        }

        private LlvmValue[] RepresentedRange(LlvmCallInstruction call)
        {
            ++representedRangeEdges;
            return [call.GetArgument(0)];
        }

        private IReadOnlyList<LlvmValue>? DirectRangeArguments(LlvmArgument argument)
        {
            if (argument.Parent is not { } function || IsEntry(function)) return null;
            if (IsPersistentSession) return SessionRangeArguments(argument, function);
            // An indirect entry can receive plans from paths outside its
            // direct call uses. Keep only genuinely closed argument facts.
            dynamicallyDispatched ??= graph.CallableCalls
                .Where(pair => pair.Value.Operation == LlvmCallableRuntimeOperation.Invoke && pair.Key.Uses.Any())
                .SelectMany(pair => pair.Value.Targets.Values).ToHashSet();
            if (dynamicallyDispatched.Contains(function)) return null;
            return function.Users.OfType<LlvmCallInstruction>()
                .Where(call => call.CalledFunction == function)
                .Select(call => call.GetArgument(checked((int)argument.ArgNo))).ToArray();
        }

        private IEnumerable<LlvmValue> ReturnedRanges(LlvmCallInstruction call, LlvmFunction callee, bool invocation = false)
        {
            int captures = invocation ? Captures(callee) : 0, prefix = invocation ? 1 : 0;
            foreach (var block in callee.BasicBlocks)
            {
                if (block.LastInstruction is not LlvmReturnInstruction { ReturnValue: { } result }) continue;
                var input = result;
                while (input is LlvmCallInstruction { ArgumentCount: 1 } representation && PreservesRangePayload(representation))
                    input = representation.GetArgument(0);
                if (input is LlvmArgument formal)
                {
                    int actual = checked((int)formal.ArgNo) - captures + prefix;
                    if (actual >= prefix && actual < call.ArgumentCount)
                    {
                        ++returnedRangeArguments;
                        yield return call.GetArgument(actual);
                        continue;
                    }
                }
                yield return result;
            }
        }

        private LlvmCallableRuntimeBinding? PhysicalInvoke(LlvmFunction target) =>
            IsPersistentSession && Callable(target) is { Operation: LlvmCallableRuntimeOperation.Invoke, Targets.Count: > 0 } call
                ? call : null;

        private IReadOnlyList<LlvmValue>? SessionRangeArguments(LlvmArgument argument, LlvmFunction function)
        {
            int index = checked((int)argument.ArgNo) - Captures(function);
            if (index < 0) return null;
            if (closedInvocations is null) IndexSessionInvocations();
            if (dynamicallyDispatched!.Contains(function)) return null;
            var inputs = function.Users.OfType<LlvmCallInstruction>()
                .Where(call => call.CalledFunction == function)
                .Select(call => call.GetArgument(checked((int)argument.ArgNo)));
            return closedInvocations!.TryGetValue(function, out var calls)
                ? inputs.Concat(calls.Select(call => call.GetArgument(index + 1))).ToArray() : inputs.ToArray();
        }

        private void IndexSessionInvocations()
        {
            closedInvocations = [];
            dynamicallyDispatched = [];
            foreach (var (target, binding) in graph.CallableCalls)
            {
                var users = target.Users.OfType<LlvmCallInstruction>().Where(call => call.CalledFunction == target).ToArray();
                if (users.Length == 0) continue;
                foreach (var destination in binding.Targets.Values.Distinct())
                {
                    if (binding.Operation != LlvmCallableRuntimeOperation.Invoke)
                        dynamicallyDispatched.Add(destination);
                    else
                    {
                        if (!closedInvocations.TryGetValue(destination, out var paths)) closedInvocations.Add(destination, paths = []);
                        paths.AddRange(users);
                    }
                }
            }
        }
    }
}
