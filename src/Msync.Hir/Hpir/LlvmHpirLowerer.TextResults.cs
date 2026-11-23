using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private HashSet<LlvmValue>? forwardedText;
        private HashSet<LlvmValue>? varyingText;

        internal bool TextResult(LlvmValue value) => forwardedText?.Contains(value) == true;
        internal bool MixedTextResult(LlvmValue value) => varyingText?.Contains(value) == true;
        internal bool HasTextResults => forwardedText is not null;
        internal bool TextParameter(LlvmArgument argument) => TextResult(argument);

        internal bool TextCallArgument(LlvmCallInstruction call, LlvmValue operand)
        {
            if (call.CalledFunction is not { IsDeclaration: false } target) return false;
            bool matched = false;
            for (int index = 0; index < call.ArgumentCount; ++index)
            {
                if (call.GetArgument(index) != operand) continue;
                if (!TextParameter(target.Arguments[index])) return false;
                matched = true;
            }
            return matched;
        }

        internal void PrepareTextResults(CompilationTrace? trace)
        {
            if (!HasFileText && !HasProcessSources && !HasLineInput) return;
            Dictionary<LlvmFunction, List<LlvmCallInstruction>> callers = [];
            Dictionary<LlvmValue, List<LlvmValue>> inputs = [];
            Dictionary<LlvmValue, List<LlvmValue>> consumers = [];
            Queue<LlvmValue> pending = new();
            Queue<LlvmValue> mixed = new();
            HashSet<LlvmFunction>? indirectTargets = null;

            if (HasFileText || HasTextFields)
            {
                foreach (var function in entries.Keys)
                foreach (var block in function.BasicBlocks)
                foreach (var node in block.Instructions)
                    if (node is LlvmFileInInst { Format: LlvmFileInFormat.Text } && IsGuestPointer(node.Type)
                        || node is LlvmLoadInstruction load && TextFieldStorage(load.GetOperandUse(0))) Pending(node);
            }
            foreach (var (target, name) in graph.NativeCalls)
            {
                if (name is not ("io.read_line" or "io.read_stdin" or "process.stdout" or "process.stderr")) continue;
                foreach (var call in Invocations(target))
                    if (TextResultCarrier(call.Type) && (name is "io.read_line" or "io.read_stdin" || !ReadyProcessText(call)))
                        Pending(call);
            }
            if (pending.Count == 0) return;

            foreach (var (entry, binding) in graph.CallableCalls)
            {
                if (binding.Operation != LlvmCallableRuntimeOperation.Invoke) continue;
                foreach (var call in Invocations(entry))
                    if (IsGuestPointer(call.Type))
                        foreach (var arm in binding.Targets.Values) ResultOf(arm, call);
            }

            while (pending.TryDequeue(out var source))
            {
                if (source is LlvmFunction target)
                {
                    foreach (var call in Invocations(target))
                    {
                        Edge(target, call);
                        Pending(call);
                    }
                    if (callers.TryGetValue(target, out var invocations))
                        foreach (var call in invocations) Pending(call);
                    continue;
                }
                foreach (var user in source.Users)
                    switch (user)
                    {
                        case LlvmCallInstruction concat when concat.CalledFunction is { } operation
                            && graph.NativeCalls.GetValueOrDefault(operation) == "string.concat"
                            && concat.Arguments.Select((argument, index) => (argument, index))
                                .Any(use => use.argument == source && !ObservedTextArgument(concat, use.index)):
                            (liftedTextConcats ??= []).Add(concat);
                            Pending(concat);
                            break;
                        case LlvmCallInstruction { CalledFunction: { IsDeclaration: false } direct } invocation:
                            PassTextArguments(direct, invocation, source);
                            break;
                        case LlvmReturnInstruction ret when ret.ReturnValue == source
                            && ret.Parent?.Parent is { } owner && TextLanguageResult(owner) && !IsEntry(owner):
                            if (!IsGuestPointer(owner.ReturnType)
                                && owner.BasicBlocks.Any(block => block.LastInstruction is LlvmReturnInstruction { ReturnValue: { } returned }
                                    && !TextResult(returned))) break;
                            if (!inputs.ContainsKey(owner))
                                foreach (var block in owner.BasicBlocks)
                                    if (block.LastInstruction is LlvmReturnInstruction { ReturnValue: { } returned })
                                        Edge(returned, owner);
                            Pending(owner);
                            break;
                        case LlvmInstruction instruction when IsGuestPointer(instruction.Type)
                            && TextForwardInputs(instruction) is { } incoming:
                            if (!inputs.ContainsKey(instruction))
                                foreach (var operand in incoming) Edge(operand, instruction);
                            Pending(instruction);
                            break;
                    }
            }
            if (forwardedText is null) return;
            foreach (var value in forwardedText)
                if (inputs.TryGetValue(value, out var choices) && choices.Any(input => !TextResult(input)))
                    Mixed(value);
            while (mixed.TryDequeue(out var candidate))
                if (consumers.TryGetValue(candidate, out var users))
                    foreach (var next in users)
                        if (TextResult(next)) Mixed(next);

            trace?.Metric("text.result_functions", forwardedText.Count(value => value is LlvmFunction));
            trace?.Metric("text.argument_parameters", forwardedText.Count(value => value is LlvmArgument));
            trace?.Metric("text.argument_mixed_parameters", varyingText?.Count(value => value is LlvmArgument) ?? 0);
            trace?.Metric("text.mixed_result_functions", varyingText?.Count(value => value is LlvmFunction) ?? 0);
            trace?.Metric("text.result_calls", forwardedText.Count(value => value is LlvmCallInstruction call
                && inputs.ContainsKey(call) && !NarrowsText(call)));

            void PassTextArguments(LlvmFunction callee, LlvmCallInstruction caller, LlvmValue operand)
            {
                // Only direct incoming edges specify the complete physical
                // argument contract; keep unknown/indirect calls unchanged.
                if (!graph.SourceFunctions.TryGetValue(callee, out var source) || IsEntry(callee)) return;
                indirectTargets ??= graph.CallableCalls.Where(pair => pair.Value.Operation == LlvmCallableRuntimeOperation.Invoke
                    && pair.Key.Uses.Any()).SelectMany(pair => pair.Value.Targets.Values).ToHashSet();
                if (indirectTargets.Contains(callee)) return;
                for (int index = source.CaptureCount; index < caller.ArgumentCount; ++index)
                {
                    LlvmArgument parameter = callee.Arguments[index];
                    if (caller.GetArgument(index) != operand || !StringValue(parameter)) continue;
                    if (!inputs.ContainsKey(parameter))
                        foreach (var site in Invocations(callee)) Edge(site.GetArgument(index), parameter);
                    Pending(parameter);
                }
            }

            void ResultOf(LlvmFunction callee, LlvmCallInstruction caller)
            {
                if (!TextLanguageResult(callee)) return;
                if (!callers.TryGetValue(callee, out var sites)) callers.Add(callee, sites = []);
                sites.Add(caller);
                Edge(callee, caller);
            }

            void Edge(LlvmValue from, LlvmValue to)
            {
                if (!inputs.TryGetValue(to, out var options)) inputs.Add(to, options = []);
                options.Add(from);
                if (!consumers.TryGetValue(from, out var uses)) consumers.Add(from, uses = []);
                uses.Add(to);
            }

            void Pending(LlvmValue value)
            {
                if ((forwardedText ??= []).Add(value)) pending.Enqueue(value);
            }

            void Mixed(LlvmValue value)
            {
                if ((varyingText ??= []).Add(value)) mixed.Enqueue(value);
            }
        }

        private static IEnumerable<LlvmCallInstruction> Invocations(LlvmFunction function) =>
            function.Users.OfType<LlvmCallInstruction>().Where(call => call.CalledFunction == function);

        private bool TextResultCarrier(LlvmType type) => IsGuestPointer(type) || Union(type) == (TypeKind.String | TypeKind.Null);

        private bool TextLanguageResult(LlvmFunction function) => TextResultCarrier(function.ReturnType)
            && graph.SourceFunctions.TryGetValue(function, out var source)
            && (graph.Types[source.ResultType].Kind & ~TypeKind.Null) == TypeKind.String;

        internal IEnumerable<LlvmValue>? TextForwardInputs(LlvmInstruction instruction) => instruction switch
        {
            LlvmPhiInstruction phi => Enumerable.Range(0, phi.IncomingCount).Select(phi.GetIncomingValue),
            LlvmSelectInstruction select => [select.TrueValue, select.FalseValue],
            LlvmFreezeInstruction freeze => [freeze.Operand],
            LlvmCastInstruction cast when cast.Opcode is LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast => [cast.Operand],
            LlvmCallInstruction narrow when NarrowsText(narrow) => [narrow.GetArgument(0)],
            _ => null
        };
    }

    private sealed partial class Body
    {
        private Dictionary<LlvmValue, int>? textTags;
        private int textResultPointer = -1;
        private int readyTextTag = -1, pendingTextTag = -1;
        private int textReturns, mixedTextCalls;

        private int TextTag(LlvmValue value)
        {
            if (!geometry.MixedTextResult(value)) return TextCell(value) ? pendingTextTag : readyTextTag;
            var tags = textTags ??= [];
            if (!tags.TryGetValue(value, out int tag))
            {
                tag = Reserve(1, 1);
                tags.Add(value, tag);
            }
            return tag;
        }

        private void PrepareTextTags()
        {
            readyTextTag = Reserve(1, 1);
            pendingTextTag = Reserve(1, 1);
            Add(new(HpirOp.ConstU8, readyTextTag));
            Add(new(HpirOp.ConstU8, pendingTextTag, Immediate: 1));
        }

        private void ForwardTextTag(LlvmInstruction value, LlvmValue operand)
        {
            if (!geometry.MixedTextResult(value)) return;
            Copy(HpirWidth.U8, TextTag(value), TextTag(operand), value.Source);
        }

        private void SelectTextCarrier(LlvmInstruction site, LlvmValue value, Action<bool> emit)
        {
            int branch = code.Count;
            Add(new(HpirOp.Branch, A: TextTag(value), B: branch + 1), site.Source);
            emit(true);
            int join = code.Count;
            Add(new(HpirOp.Jump), site.Source);
            code[branch] = code[branch] with { D = code.Count };
            emit(false);
            code[join] = code[join] with { A = code.Count };
        }

        private void ReportTextResults(CompilationTrace? trace)
        {
            trace?.Metric("text.completion_returns", textReturns);
            trace?.Metric("text.mixed_calls", mixedTextCalls);
        }
    }
}
