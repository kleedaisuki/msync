using System.Globalization;

namespace Msync.Core.Hpir;

public readonly record struct HpirListingSite(int Line, int Column, int Length, int Function, int Instruction,
    SourceSpan? Source, int CalledFunction = -1, int CallColumn = -1, int CallLength = 0);

public static class HpirDisassembly
{
    public static void Write(HpirProgram program, TextWriter writer, Action<HpirListingSite>? sites = null,
        CancellationToken cancellationToken = default)
    {
        var output = new Listing(writer, cancellationToken);
        output.Line($"HPIR {program.Stage.ToString().ToLowerInvariant()} entry @{program.EntryPoint}");
        for (int i = 0; i < program.Layouts.Count; ++i)
        {
            var layout = program.Layouts[i];
            output.Line($"layout @{i} size={layout.Size} alignment={layout.Alignment} roots=[{string.Join(',', layout.PointerOffsets)}] borrows=[{string.Join(',', layout.BorrowOffsets)}]");
        }
        for (int i = 0; i < program.Globals.Count; ++i)
            output.Line($"global @{i} declaration={program.Globals[i].Declaration} layout=@{program.Globals[i].Layout}");
        for (int i = 0; i < program.TypeLiterals.Count; ++i)
            output.Line($"type @{i} {program.TypeLiterals[i].Kinds} element={program.TypeLiterals[i].Element}");
        for (int i = 0; i < program.TypeCells.Count; ++i)
            output.Line($"type-cell @{i} -> type @{program.TypeCells[i]}");
        for (int i = 0; i < program.Imports.Count; ++i)
        {
            var import = program.Imports[i];
            string shape = import.Service == HpirService.ProcessStart ? $" argv=[{string.Join(',', import.ProcessArguments)}]" : "";
            output.Line($"import %{i} {import.Service}:{import.Binding} ({string.Join(',', import.Arguments)}) -> {import.Result}{shape}");
        }
        WriteEffects(program, output);
        for (int i = 0; i < program.Functions.Count; ++i)
            WriteFunction(program, program.Functions[i], i, output, sites);
    }

    private static void WriteEffects(HpirProgram program, Listing output)
    {
        for (int id = 0; id < program.IteratorSelections.Count; ++id)
        {
            var selection = program.IteratorSelections[id];
            output.Line($"iterator-selection {id} {Quote(selection.Directory.Raw)} {Quote(selection.Pattern)}"
                + (selection.Members is { } members ? $" frozen members={members.Count}" : " residual"));
        }
        for (int id = 0; id < program.ContextChecks.Count; ++id)
        {
            var check = program.ContextChecks[id];
            output.Line($"context-check {id} {check.Operation} {Quote(check.Path.Raw)}"
                + (check.Value is { } value ? value ? " = true" : " = false" : ""));
        }
        for (int id = 0; id < program.TransferInputs.Count; ++id)
        {
            var input = program.TransferInputs[id];
            output.Line($"effect {id} {input.Kind}: {Quote(input.Path?.Raw ?? input.Text!)} -> {Quote(input.Destination.Raw)}");
            if (input.Pattern is { } pattern) output.Line($"  select {Quote(pattern)}");
        }
        for (int id = 0; id < program.PreparedTransfers.Count; ++id)
        {
            var plan = program.PreparedTransfers[id];
            output.Line($"effect {id} {plan.Kind} -> {Quote(plan.Destination)}");
            if (plan.Predecessors.Count != 0) output.Line($"  after {string.Join(',', plan.Predecessors)}");
            if (plan.Pattern is { } pattern) output.Line($"  residual select {Quote(pattern)}");
            if (plan.Text is { } text) output.Line($"  literal {Quote(text)}");
            foreach (string path in plan.Paths) output.Line($"  source {Quote(path)}");
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void WriteFunction(HpirProgram program, HpirFunction function, int id, Listing output,
        Action<HpirListingSite>? sites)
    {
        string parameters = string.Join(", ", function.Parameters.Select(parameter => $"{Address(parameter.Offset)}:{parameter.Width}"));
        string identity = "@" + id.ToString(CultureInfo.InvariantCulture);
        sites?.Invoke(new(output.Row, "function ".Length, identity.Length, id, -1, null));
        output.Line($"function {identity} {Quote(function.Name)} ({parameters}) -> {function.Result}, frame={function.FrameBytes}");
        int source = 0, safepoint = 0;
        SourceSpan? currentSource = null;
        for (int pc = 0; pc < function.Instructions.Length; ++pc)
        {
            HpirInstruction instruction = function.Instructions[pc];
            string position = pc.ToString("D4", CultureInfo.InvariantCulture);
            string operation = Instruction(program, function, instruction);
            string line = $"  {position} {operation}";
            if (safepoint < function.Safepoints.Count && function.Safepoints[safepoint].Instruction == pc)
            {
                var map = function.Safepoints[safepoint++];
                line += $" roots=[{string.Join(',', map.RootOffsets)}] borrows=[{string.Join(',', map.BorrowOffsets)}]";
            }
            if (source < function.Sources.Count && function.Sources[source].Instruction == pc)
            {
                var span = function.Sources[source++].Source;
                currentSource = span;
                line += $" ; {Quote(span.Source)}:{span.Start}+{span.Length}";
            }
            int target = instruction.Op == HpirOp.Call ? function.Calls[instruction.A].Target : -1;
            sites?.Invoke(new(output.Row, 2, position.Length, id, pc, currentSource, target,
                target < 0 ? -1 : position.Length + 3 + "Call ".Length,
                target < 0 ? 0 : target.ToString(CultureInfo.InvariantCulture).Length + 1));
            output.Line(line);
        }
    }

    private sealed class Listing(TextWriter writer, CancellationToken cancellationToken)
    {
        internal int Row { get; private set; }

        internal void Line(string text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteLine(text);
            ++Row;
        }
    }

    private static string Instruction(HpirProgram program, HpirFunction function, HpirInstruction code)
    {
        switch (code.Op)
        {
            case HpirOp.Jump: return $"Jump {code.A}";
            case HpirOp.Branch: return $"Branch {Address(code.A)}, {code.B}, {code.D}";
            case HpirOp.Return:
                if (code.Immediate >= (long)HpirReturnRoot.AggregateLayout)
                    return $"Return publish {Address(code.A)} layout @{code.Immediate - (long)HpirReturnRoot.AggregateLayout}";
                return function.Result == HpirWidth.Void ? "Return" : $"Return {Address(code.A)}";
            case HpirOp.Call:
                HpirCall call = function.Calls[code.A];
                string args = string.Join(", ", call.Arguments.Select(Address));
                if (call.Target >= 0)
                    return Call("@" + call.Target, args, code.D, program.Functions[call.Target].Result);
                return Call("%" + ~call.Target, args, code.D, program.Imports[~call.Target].Result);
            case HpirOp.AddressOf: return $"AddressOf {Address(code.D)}, {Address(code.A)}";
            case HpirOp.AddressOfStatic or HpirOp.AddressOfStaticCell: return $"{code.Op} {Address(code.D)}, text @{code.Immediate}";
            case HpirOp.AddressOfConnection:
                string endpoint = Quote(program.TextLiterals[HpirConnectionAbi.Text(code.Immediate)]);
                return $"AddressOfConnection {Address(code.D)}, connection({HpirConnectionAbi.Kind(code.Immediate)}, {endpoint})";
            case HpirOp.AddressOfTypeCell: return $"AddressOfTypeCell {Address(code.D)}, type-cell @{code.Immediate}";
            case HpirOp.AddressOfGlobal:
                return $"AddressOfGlobal {Address(code.D)}, @{code.Immediate >> 1}{((code.Immediate & 1) == 0 ? "" : ".ready")}";
            case HpirOp.OffsetPointer: return $"OffsetPointer {Address(code.D)}, {Address(code.A)}, {code.Immediate}";
            case HpirOp.ConstU8 or HpirOp.ConstU16 or HpirOp.ConstU32 or HpirOp.ConstU64:
                return $"{code.Op} {Address(code.D)}, 0x{unchecked((ulong)code.Immediate).ToString("x", CultureInfo.InvariantCulture)}";
        }
        var shape = HpirInstructionShape.Of(code.Op);
        return shape.B == HpirWidth.Void
            ? $"{code.Op} {Address(code.D)}, {Address(code.A)}"
            : $"{code.Op} {Address(code.D)}, {Address(code.A)}, {Address(code.B)}";
    }

    private static string Call(string target, string args, int destination, HpirWidth result) => result == HpirWidth.Void
        ? $"Call {target}({args})" : $"Call {target}({args}) -> {Address(destination)}";

    private static string Address(int operand) => operand >= 0 ? $"[fp+{operand}]" : $"[[fp+{~operand}]]";
}
