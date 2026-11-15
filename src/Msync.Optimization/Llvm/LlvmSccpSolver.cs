// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Utils/SCCPSolver.cpp SCCPInstVisitor, function-local state/worklists.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmSccpSolver
{
    private readonly LlvmFunction function;
    private readonly LlvmDataLayout layout;
    private readonly CompilationTrace? trace;
    private readonly Dictionary<string, ulong>? metrics;
    private readonly Dictionary<LlvmValue, LlvmValueLatticeElement> values = [];
    private readonly Dictionary<(LlvmValue Value, int Field), LlvmValueLatticeElement> fields = [];
    private readonly HashSet<LlvmBasicBlock> executable = [];
    private readonly HashSet<LlvmBasicBlock> visited = [];
    private readonly HashSet<(LlvmBasicBlock Source, LlvmBasicBlock Destination)> feasibleEdges = [];
    private readonly List<LlvmInstruction> instructions = [];
    private readonly HashSet<LlvmInstruction> instructionMembership = [];
    private readonly Stack<LlvmBasicBlock> blocks = [];
    private LlvmInstruction? current;
    public LlvmSccpSolver(LlvmFunction function, CompilationTrace? trace = null)
    {
        this.function = function; layout = function.Parent?.DataLayout ?? new(); this.trace = trace?.Detail;
        metrics = this.trace is null ? null : [];
    }
    public IReadOnlyDictionary<string, ulong>? Metrics => metrics;
    public bool IsBlockExecutable(LlvmBasicBlock block) => executable.Contains(block);
    public bool IsEdgeFeasible(LlvmBasicBlock from, LlvmBasicBlock to) => feasibleEdges.Contains((from, to));
    public static bool IsConstant(LlvmValueLatticeElement value) => value.IsConstant ||
        value.IsConstantRange() && value.GetConstantRange().IsSingleElement;
    public static bool IsOverdefined(LlvmValueLatticeElement value) => !value.IsUnknownOrUndef && !IsConstant(value);
    public LlvmValueLatticeElement GetLatticeValueFor(LlvmValue value) => State(value);
    private LlvmValueLatticeElement State(LlvmValue value)
    {
        Debug.Assert(!value.Type.IsStructTy());
        if (values.TryGetValue(value, out var state)) return state;
        state = new(); values.Add(value, state); Count("lattice.values_created");
        if (value is LlvmConstant constant) state.MarkConstant(constant);
        return state;
    }
    private LlvmValueLatticeElement FieldState(LlvmValue value, int field)
    {
        Debug.Assert(value.Type is LlvmStructType);
        var key = (value, field);
        if (fields.TryGetValue(key, out var state)) return state;
        state = new(); fields.Add(key, state); Count("lattice.struct_fields_created");
        if (value is LlvmConstant constant)
        {
            if (constant.GetAggregateElement((uint)field) is { } element) state.MarkConstant(element);
            else state.MarkOverdefined();
        }
        return state;
    }
    private LlvmValueLatticeElement ElementState(LlvmValue value, int field) => field < 0 ? State(value) : FieldState(value, field);
    private IEnumerable<int> ElementIndices(LlvmValue value)
    {
        if (value.Type is not LlvmStructType structure) { yield return -1; yield break; }
        for (int i = 0; i < structure.NumElements; ++i) yield return i;
    }
    private static LlvmConstant? Constant(LlvmValueLatticeElement state, LlvmType type)
    {
        if (state.IsConstant) { Debug.Assert(ReferenceEquals(state.GetConstant().Type, type)); return state.GetConstant(); }
        if (state.IsConstantRange() && state.GetConstantRange().GetSingleElement() is { } value)
        {
            if (type.IsVectorTy())
                throw new NotSupportedException("SCCP vector singleton materialization requires the unported ConstantVector::getSplat representation.");
            return LlvmConstantInt.Get(type.Context, value);
        }
        return null;
    }
    public LlvmConstant? GetConstantOrNull(LlvmValue value)
    {
        if (value.Type is LlvmStructType structure)
        {
            var elements = new LlvmConstant[structure.NumElements];
            for (int i = 0; i < elements.Length; ++i)
            {
                var state = FieldState(value, i);
                if (IsOverdefined(state)) return null;
                elements[i] = IsConstant(state) ? Constant(state, structure.GetElementType(i))! : LlvmUndefValue.Get(structure.GetElementType(i));
            }
            return LlvmConstantStruct.Get(structure, elements);
        }
        var scalar = State(value);
        return IsOverdefined(scalar) ? null : IsConstant(scalar) ? Constant(scalar, value.Type) : LlvmUndefValue.Get(value.Type);
    }
    public bool MarkBlockExecutable(LlvmBasicBlock block)
    {
        if (!executable.Add(block)) return false;
        blocks.Push(block); Count("reachability.blocks_executable"); return true;
    }
    private bool MarkEdgeExecutable(LlvmBasicBlock source, LlvmBasicBlock destination)
    {
        if (trace is null) return MarkEdgeCore(source, destination);
        var attributes = InstructionAttributes(source.GetTerminator());
        attributes["edge.destination"] = destination.Name;
        return trace.MeasureDetailed("optimizer.llvm-sccp.executable-edge", (Self: this, Source: source, Destination: destination),
            static state => state.Self.MarkEdgeCore(state.Source, state.Destination), attributes: attributes);
    }
    private bool MarkEdgeCore(LlvmBasicBlock source, LlvmBasicBlock destination)
    {
        if (!feasibleEdges.Add((source, destination))) return false;
        Count("reachability.edges_feasible");
        if (!MarkBlockExecutable(destination))
            for (var instruction = destination.FirstInstruction; instruction is LlvmPhiInstruction; instruction = instruction.Next)
                Push(instruction);
        return true;
    }
    private void Push(LlvmInstruction instruction)
    {
        if (current is not null && ReferenceEquals(instruction.Parent, current.Parent) && current.ComesBefore(instruction, trace))
        { Count("worklist.skipped_later_instruction"); return; }
        if (!visited.Contains(instruction.Parent!)) { Count("worklist.skipped_unvisited_block"); return; }
        if (!instructionMembership.Add(instruction)) { Count("worklist.duplicates"); return; }
        instructions.Add(instruction); Count("worklist.instructions_pushed");
    }
    private void PushUsers(LlvmValue value)
    {
        foreach (var user in value.Users)
        { Count("worklist.users_visited"); if (user is LlvmInstruction instruction) Push(instruction); }
    }
    private bool Merge(LlvmValueLatticeElement state, LlvmValue value, LlvmValueLatticeElement rhs,
        LlvmLatticeMergeOptions? options = null)
    {
        Count("lattice.merge_queries");
        if (!state.MergeIn(rhs, options)) return false;
        Count("lattice.merge_changes"); PushUsers(value); return true;
    }
    private void MarkConstant(LlvmValue value, LlvmConstant constant)
    { if (State(value).MarkConstant(constant)) { Count("lattice.constant_transitions"); PushUsers(value); } }
    private void MarkOverdefined(LlvmValue value)
    {
        foreach (int index in ElementIndices(value))
            if (ElementState(value, index).MarkOverdefined()) { Count("lattice.overdefined_transitions"); PushUsers(value); }
    }
    private bool FullyOverdefined(LlvmInstruction instruction) => ElementIndices(instruction).All(i => ElementState(instruction, i).IsOverdefined);
    public void Solve()
    {
        if (trace is null) SolveWorklists();
        else trace.Run("optimizer.llvm-sccp.solve", this, static solver => solver.SolveWorklists());
    }
    private void SolveWorklists()
    {
        while (blocks.Count != 0 || instructions.Count != 0)
        {
            while (instructions.Count != 0)
            {
                var instruction = instructions[^1]; instructions.RemoveAt(instructions.Count - 1);
                instructionMembership.Remove(instruction); Count("worklist.instructions_popped"); Visit(instruction);
            }
            while (blocks.TryPop(out var block))
            {
                visited.Add(block); Count("worklist.blocks_popped");
                foreach (var instruction in block.Instructions) { current = instruction; Visit(instruction); }
                current = null;
            }
        }
    }
    public bool ResolvedUndefsIn() => trace is null ? ResolveUndefsCore() :
        trace.MeasureDetailed("optimizer.llvm-sccp.resolve-undefs", this, static solver => solver.ResolveUndefsCore(),
            attributes: new Dictionary<string, string> { ["function.name"] = function.Name });
    private bool ResolveUndefsCore()
    {
        bool changed = false;
        foreach (var block in function.BasicBlocks)
        {
            if (!executable.Contains(block)) continue;
            foreach (var instruction in block.Instructions)
            {
                Count("undef.instructions_visited");
                if (instruction.Type.IsVoidTy()) continue;
                if (instruction.Type is LlvmStructType structure)
                {
                    if (instruction is LlvmExtractValueInstruction or LlvmInsertValueInstruction) continue;
                    for (int i = 0; i < structure.NumElements; ++i)
                    {
                        var state = FieldState(instruction, i);
                        if (!state.IsUnknown) continue;
                        state.MarkOverdefined(); PushUsers(instruction); changed = true; Count("undef.struct_fields_resolved"); break;
                    }
                }
                else if (State(instruction).IsUnknown && instruction is not LlvmLoadInstruction)
                { MarkOverdefined(instruction); changed = true; Count("undef.scalars_resolved"); }
            }
        }
        return changed;
    }
    private string StateSummary(LlvmValue value)
    {
        if (value.Type is not LlvmStructType structure)
            return values.TryGetValue(value, out var state) ? state.Describe() : "Unknown";
        return string.Join(",", Enumerable.Range(0, structure.NumElements)
            .Select(field => fields.TryGetValue((value, field), out var state) ? state.Describe() : "Unknown"));
    }
    private Dictionary<string, string> InstructionAttributes(LlvmInstruction instruction)
    {
        var attributes = new Dictionary<string, string>
        {
            ["function.name"] = function.Name,
            ["block.name"] = instruction.Parent!.Name,
            ["instruction.name"] = instruction.Name,
            ["instruction.opcode"] = instruction.Opcode.ToString()
        };
        if (instruction.Source is { } source)
        {
            attributes["source.file"] = source.Source;
            attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return attributes;
    }
    private void Count(string key, ulong amount = 1)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + amount; }
}
