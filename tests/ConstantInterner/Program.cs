using System.Reflection;
using Msync.Core;
using Module = Msync.Core.Module;

int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
Type type = typeof(Module).Assembly.GetType("Msync.Core.ConstantInterner", throwOnError: true)!;
ConstructorInfo constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
MethodInfo intern = type.GetMethod("Intern", BindingFlags.Instance | BindingFlags.NonPublic)!;
FieldInfo indices = type.GetField("indices", BindingFlags.Instance | BindingFlags.NonPublic)!;
int Intern(object pool, Value value) => (int)intern.Invoke(pool, [value])!;

List<Value> values = Enumerable.Range(0, 2048).Select(index => (Value)new StringValue("value-" + index)).ToList();
object pool = constructor.Invoke([values]);
Require(indices.GetValue(pool) is null, "Index is not lazy");
for (int index = 0; index < values.Count; index++)
    Require(Intern(pool, new StringValue("value-" + index)) == index, "Existing index changed");
Require(values.Count == 2048, "Existing values were appended again");
values.Add(new StringValue("external append"));
Require(Intern(pool, new StringValue("external append")) == 2048, "Append synchronization failed");
Require(Intern(pool, new StringValue("new value")) == 2049 && values.Count == 2050, "New index was not retained");
int positiveZero = Intern(pool, new NumberValue(0));
int negativeZero = Intern(pool, new NumberValue(BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000))));
Require(positiveZero != negativeZero, "Signed zero representations merged");
int firstNan = Intern(pool, new NumberValue(BitConverter.Int64BitsToDouble(0x7ff8000000000001)));
int secondNan = Intern(pool, new NumberValue(BitConverter.Int64BitsToDouble(0x7ff8000000000002)));
Require(firstNan != secondNan, "NaN payload representations merged");
Require(Intern(pool, values[firstNan]) == firstNan, "NaN representation lookup is unstable");
var scope = new ScopeValue(new Dictionary<string, Value> { ["item"] = new NumberValue(1) });
Require(Intern(pool, scope) != Intern(pool, scope), "Mutable allocations were interned");
var closure = new FunctionValue(1, [scope]);
Require(Intern(pool, closure) != Intern(pool, closure), "Mutable captures were interned");

var constants = new List<Value>();
var code = new List<Instruction>();
for (int index = 0; index < 1024; index++)
{
    code.Add(new(OpCode.Constant, constants.Count)); constants.Add(new StringValue("bytes-" + index));
    code.Add(new(OpCode.Constant, constants.Count)); constants.Add(new PathValue("./file-" + index));
    code.Add(new(OpCode.Transfer)); code.Add(new(OpCode.Pop));
}
code.Add(new(OpCode.Null)); code.Add(new(OpCode.Return));
var function = new FunctionCode("main", 0, 0, code, TypeRef.Null);
var module = new Module(constants, [function], OptimizationLevel: 0);
pool = constructor.Invoke([constants]);
Type graph = typeof(Module).Assembly.GetType("Msync.Core.GraphOptimization", throwOnError: true)!;
MethodInfo optimize = graph.GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic)!;
var result = (FunctionCode)optimize.Invoke(null, [module, function, constants, null, pool])!;
Require(indices.GetValue(pool) is null, "Original literal transfer script unnecessarily built an interning index");
Require(result.Instructions.Where(instruction => instruction.Op == OpCode.Constant).Select(instruction => instruction.A)
    .SequenceEqual(Enumerable.Range(0, 2048)), "Original literal operands were re-interned");
Require(result.Instructions.Count(instruction => instruction.Op == OpCode.Transfer) == 1024, "Transfer effects changed");
Console.WriteLine($"Constant interning: {assertions} contracts passed.");
