using System.Collections;
using System.Reflection;
using Msync.Core;
using Msync.Optimization;

internal static class SparseDefUseChecks
{
    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run()
    {
        int checks = 0;
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var contextType = typeof(Optimizer).Assembly.GetType("Msync.Optimization.FunctionAnalysisContext", true)!;
        var module = new Msync.Core.Module([new NumberValue(0), new NumberValue(1)], []);
        object context = Activator.CreateInstance(contextType, Internal | BindingFlags.Public,
            binder: null, args: [module], culture: null)!;
        object Analyze(FunctionCode function) => contextType.GetMethod("DefUse", Internal)!.Invoke(context, [function])!;
        object Invoke(object graph, string method, params object[] arguments) =>
            graph.GetType().GetMethod(method, Internal)!.Invoke(graph, arguments)!;
        int[] Before(object graph, int pc, int slot) =>
            ((IReadOnlyCollection<int>)Invoke(graph, "DefinitionsBefore", pc, slot)).ToArray();
        int[][] Inputs(object graph, int pc)
        {
            object memory = Invoke(graph, "InputMemory", pc);
            var operands = (Array)memory.GetType().GetMethod("ToArray")!.Invoke(memory, [])!;
            return operands.Cast<IReadOnlyCollection<int>>().Select(values => values.ToArray()).ToArray();
        }

        var diamond = Body(new(OpCode.True), new(OpCode.JumpIfTrue, 5),
            new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.Jump, 7),
            new(OpCode.Constant, 1), new(OpCode.InitializeLocal, 0),
            new(OpCode.LoadLocal, 0), new(OpCode.Return));
        object joined = Analyze(diamond);
        Require(Before(joined, 7, 0).SequenceEqual([6, 12]), "Selected local definitions were not joined");
        Require(Inputs(joined, 7).Single().SequenceEqual([6, 12]), "Load did not use both reaching writes");
        Require(Inputs(joined, 8).Single().SequenceEqual([14]), "Return did not use the load's value");
        var uses = (IEnumerable)Invoke(joined, "Uses", 6);
        object loadUse = uses.Cast<object>().Single();
        Require((int)loadUse.GetType().GetProperty("Instruction")!.GetValue(loadUse)! == 7
            && (int)loadUse.GetType().GetProperty("Operand")!.GetValue(loadUse)! == 0,
            "The inverse use list lost its operand identity");
        Require(ReferenceEquals(joined, Analyze(diamond with { Name = "metadata only" })),
            "Metadata-only body reuse discarded the graph");

        var stackJoin = Body(new(OpCode.True), new(OpCode.JumpIfTrue, 4), new(OpCode.Constant, 0),
            new(OpCode.Jump, 5), new(OpCode.Constant, 1), new(OpCode.Dup), new(OpCode.Add), new(OpCode.Return));
        object stack = Analyze(stackJoin);
        Require(Inputs(stack, 5).Single().SequenceEqual([4, 8]), "Stack join lost one incoming producer");
        var add = Inputs(stack, 6);
        Require(add[0].SequenceEqual([10]) && add[1].SequenceEqual([11]), "Dup outputs or operand order were conflated");
        Require(!ReferenceEquals(joined, Analyze(diamond)), "A different instruction stream did not evict cached def-use");

        var loop = Body(new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0),
            new(OpCode.LoadLocal, 0), new(OpCode.True), new(OpCode.JumpIfTrue, 9), new(OpCode.Pop),
            new(OpCode.Constant, 1), new(OpCode.InitializeLocal, 0), new(OpCode.Jump, 2), new(OpCode.Return));
        object cyclic = Analyze(loop);
        Require(Before(cyclic, 2, 0).SequenceEqual([2, 14]), "Loop header lost its seed or latch writer");
        Require(Inputs(cyclic, 9).Single().SequenceEqual([4]), "Loop exit lost the live stack column");

        var iterator = Body(new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.GetIterator),
            new(OpCode.InitializeLocal, 0), new(OpCode.IteratorNext, 0, 8),
            new(OpCode.Pop), new(OpCode.Jump, 4), new(OpCode.Nop), new(OpCode.Return));
        object iteration = Analyze(iterator);
        Require(Inputs(iteration, 4).Single().SequenceEqual([6]), "Iterator slot did not reach the loop");
        Require(Inputs(iteration, 5).Single().SequenceEqual([8]), "Yield edge lost its item");
        Require(Inputs(iteration, 8).Single().SequenceEqual([0]), "Exhausted edge invented a stack item");
        Require(!(bool)Invoke(iteration, "IsReachable", 7), "Unreachable fallthrough became a predecessor");

        var partial = Body(new(OpCode.True), new(OpCode.JumpIfTrue, 4),
            new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.Return));
        Require(Before(Analyze(partial), 4, 0).SequenceEqual([-1, 6]),
            "The untouched entry path silently disappeared from a slot definition");
        return checks;
    }

    private static FunctionCode Body(params Instruction[] code) => new("facts", 0, 1, code, TypeRef.Any);
}
