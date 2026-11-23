using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirFuture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MathNode<TResult> where TResult : unmanaged
    {
        internal Lifted<double, TResult> Operands;
        internal int Function;
        internal int Instruction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MathTriple
    {
        internal Cell<double> Completion;
        internal nint ThirdDependency;
        internal double First;
        internal double Second;
        internal double Third;
        internal int Function;
        internal int Instruction;
    }

    internal static void LiftMath(nint pointer, HpirMathOp operation, int arity, HpirWidth payload,
        HpirScalarInput<double> a, HpirScalarInput<double> b, HpirScalarInput<double> c, HpirWorkSite site)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.future.lift");
        if (trace is not null)
        {
            trace.SetAttribute("operation", HpirScalarImports.MathName(operation));
            trace.SetMetric("future.dependencies", (a.Pending == 0 ? 0 : 1) + (b.Pending == 0 ? 0 : 1) + (c.Pending == 0 ? 0 : 1));
        }
        Header header = new() { Left = a.Pending, Right = b.Pending, Job = (int)operation };
        if (arity == 3)
        {
            header.Operation = HpirFutureOperation.MathTriple;
            *(MathTriple*)pointer = new() { Completion = new() { Header = header }, ThirdDependency = c.Pending,
                First = a.Value, Second = b.Value, Third = c.Value, Function = site.Function, Instruction = site.Instruction };
        }
        else if (payload == HpirWidth.U8)
        {
            header.Operation = HpirFutureOperation.MathBool;
            *(MathNode<byte>*)pointer = Node<byte>(header, a.Value, b.Value, site);
        }
        else
        {
            header.Operation = HpirFutureOperation.MathNumber;
            *(MathNode<double>*)pointer = Node<double>(header, a.Value, b.Value, site);
        }
    }

    private static MathNode<T> Node<T>(Header header, double a, double b, HpirWorkSite site) where T : unmanaged => new()
    {
        Operands = new() { Completion = new() { Header = header }, Left = a, Right = b },
        Function = site.Function, Instruction = site.Instruction
    };

    private static void CompleteMath(MathNode<double>* node, HpirTextDemandContext context)
    {
        var operands = &node->Operands;
        var header = operands->Completion.Header;
        operands->Completion.Value = EvaluateMath(header.Job, Value(header.Left, operands->Left),
            Value(header.Right, operands->Right), 0, node->Function, node->Instruction, context);
    }

    private static void CompleteMath(MathTriple* node, HpirTextDemandContext context)
    {
        var header = node->Completion.Header;
        node->Completion.Value = EvaluateMath(header.Job, Value(header.Left, node->First),
            Value(header.Right, node->Second), Value(node->ThirdDependency, node->Third), node->Function, node->Instruction, context);
        node->ThirdDependency = 0;
    }

    private static void CompletePredicate(MathNode<byte>* node, HpirTextDemandContext context)
    {
        var operands = &node->Operands;
        var header = operands->Completion.Header;
        try
        {
            operands->Completion.Value = ScalarMath.Predicate((HpirMathOp)header.Job, Value(header.Left, operands->Left)) ? (byte)1 : (byte)0;
        }
        catch (MsyncException error) when (error.Span is null)
        { throw context.Attribute(error, node->Function, node->Instruction); }
    }

    private static double EvaluateMath(int operation, double a, double b, double c, int function, int pc,
        HpirTextDemandContext context)
    {
        try { return ScalarMath.Evaluate((HpirMathOp)operation, a, b, c); }
        catch (MsyncException error) when (error.Span is null) { throw context.Attribute(error, function, pc); }
    }
}
