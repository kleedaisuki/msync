using Msync.Compiler.Syntax;

static Expr ParseExpression(string source) => ((ExpressionStatement)Parser.Parse(source).Statements[0]).Expression;
static void Require(bool condition, string context)
{
    if (!condition) throw new InvalidOperationException(context);
}

var assignment = (AssignmentExpr)ParseExpression("object::filename = ~/.codex;");
Require(assignment.Target is BinaryExpr { Operator: "::", Right: NameExpr { Name: "filename" } }, "field target");
Require(assignment.OperatorSpan is { Start: 17, Length: 1 }, "assignment operator source span");
Require(ParseExpression("first = second = true") is AssignmentExpr { Value: AssignmentExpr }, "right-associative assignment");
Require(ParseExpression("value = false or true") is AssignmentExpr { Value: BinaryExpr { Operator: "or" } }, "assignment precedence");
Require(ParseExpression("destination = source -> output") is AssignmentExpr { Value: BinaryExpr { Operator: "->" } }, "transfer precedence");
Require(ParseExpression(".codex = ~/.codex") is AssignmentExpr { Target: NameExpr { Name: ".codex" } }, "dot binding assignment");
Require(ParseExpression("value=true") is AssignmentExpr { Target: NameExpr { Name: "value" }, Value: BoolExpr { Value: true } }, "unspaced assignment");
Require(ParseExpression("./\"source=1\" -> ./\"output=2\"") is BinaryExpr { Operator: "->", Left: BinaryExpr { Operator: "/", Right: StringExpr { Value: "source=1" } } }, "quoted equals filenames");

const string declaration = "scope SomeType { remote: ssh://forge, new: fn { scope { filename?: null } }, }; let object = SomeType::new(); object::filename = ~/.codex;";
var document = Parser.Parse(declaration);
var scope = ((ScopeDeclaration)document.Statements[0]).Scope;
Require(!scope.Members[0].Nullable, "ordinary field remains nonnullable");
var constructor = (FunctionExpr)scope.Members[1].Value;
var result = (ScopeExpr)((ExpressionStatement)constructor.Body.Statements[0]).Expression;
Require(result.Members[0] is { Name: "filename", Nullable: true, Value: NullExpr }, "nullable anonymous returned scope");
Require(((ScopeExpr)ParseExpression("scope { \"two words\"?: null, .codex?: null }")).Members.All(member => member.Nullable), "nullable quoted and dot fields");
string formatted = Formatter.Format(declaration);
Require(Formatter.Format(formatted) == formatted, "nullable field and assignment formatting idempotence");
Require(formatted.Contains("filename?: null", StringComparison.Ordinal), "formatter preserves field admission");
var incomplete = Parser.ParseForEditor("scope SomeType { field?: null, new: fn { scope { filename?:");
Require(incomplete.Error is not null && incomplete.Document.Statements.Count == 1, "incomplete nullable scope recovery");
Console.WriteLine("Mutable assignment and nullable field syntax: 15 contracts passed.");

const string globalSource = "global .codex, copy-config, 值;";
var imported = (GlobalStatement)Parser.Parse(globalSource).Statements.Single();
Require(imported.Names.Select(name => name.Name).SequenceEqual([".codex", "copy-config", "值"]), "global import preserves legal atom names");
Require(imported.Names.All(name => globalSource[name.Span.Start..name.Span.End] == name.Name), "global names own exact source spans");
Require(imported.Span == new SourceSpan(0, globalSource.Length), "global statement owns its terminating semicolon");
var nestedGlobal = (FunctionDeclaration)Parser.Parse("fn run() { global root, operation; root }").Statements.Single();
Require(nestedGlobal.Function.Body.Statements[0] is GlobalStatement { Names.Count: 2 }, "global parses in function bodies");
string globalFormatting = Formatter.Format("fn run() { global root, operation; root }");
Require(globalFormatting.Contains("global root, operation;", StringComparison.Ordinal), "short global lists remain on one line");
Require(Formatter.Format(globalFormatting) == globalFormatting, "global formatting is idempotent");
string longGlobal = "fn run() { global " + string.Join(", ", Enumerable.Range(0, 12).Select(index => $"binding_{index:D2}")) + "; root }";
string wrappedGlobal = Formatter.Format(longGlobal);
Require(wrappedGlobal.Split('\n').All(line => line.Length <= 80), "long global import wraps at names rather than exceeding print width");
Require(Formatter.Format(wrappedGlobal) == wrappedGlobal, "wrapped global formatting is idempotent");
Require(Parser.Parse("scope Item { global: true, other: false }").Statements.Single() is ScopeDeclaration,
    "a Scope field named global is not an import statement");
foreach (string invalidGlobal in new[] { "global;", "global root,;", "global root other;", "global root" })
{
    try { Parser.Parse(invalidGlobal); throw new InvalidOperationException("Accepted malformed import: " + invalidGlobal); }
    catch (SyntaxException syntax) { Require(syntax.Diagnostic.Message.Contains("global", StringComparison.Ordinal), "global syntax error explains its grammatical owner"); }
}
var unfinishedGlobal = Parser.ParseForEditor("fn run() { global root,");
Require(unfinishedGlobal.Error is { IsIncomplete: true }
    && ((FunctionDeclaration)unfinishedGlobal.Document.Statements.Single()).Function.Body.Statements[0] is GlobalStatement { Names.Count: 1 },
    "unfinished import retains completed names and original incomplete diagnostic");
Console.WriteLine("Global import syntax: 14 contracts passed.");

Require(ParseExpression("release") is BarrierExpr { Release: true, Acquire: false, Value: null }, "standalone release");
Require(ParseExpression("acquire;") is BarrierExpr { Release: false, Acquire: true, Value: null }, "standalone acquire");
Require(ParseExpression("release acquire ./tool()") is BarrierExpr { Release: true, Acquire: true, Value: CallExpr }, "combined scheduling modifiers");
Require(ParseExpression("acqrel source -> target") is BarrierExpr { Release: true, Acquire: true, Value: BinaryExpr { Operator: "->" } }, "whole transfer scheduling");
Require(ParseExpression("acquire process") is BarrierExpr { Acquire: true, Value: NameExpr { Name: "process" } }, "selected process acquire");
Require(ParseExpression("release process = ./tool()") is BarrierExpr { Value: AssignmentExpr { Value: CallExpr } }, "released process assignment");
Require(ParseExpression("[release, acquire]") is RangeExpr { Items.Count: 2 }, "standalone barrier delimiter boundaries");
Require(ParseExpression("async") is NameExpr { Name: "async" }, "removed async keyword remains ordinary name");
foreach (string duplicate in new[] { "release release;", "acqrel acquire;", "acquire acqrel;" })
{
    try { Parser.Parse(duplicate); throw new InvalidOperationException("Accepted duplicate prefix: " + duplicate); }
    catch (SyntaxException syntax) { Require(syntax.Diagnostic.Message.Contains("Duplicate scheduling prefix", StringComparison.Ordinal), "duplicate prefix diagnostic"); }
}
string barriers = Formatter.Format("release; let p = release ./tool(); acquire p; acqrel source -> target;");
Require(Formatter.Format(barriers) == barriers, "barrier formatting idempotence");
Console.WriteLine("Barrier syntax: 12 contracts passed.");

static IReadOnlyList<Msync.Core.Instruction> Instructions(string source) =>
    Msync.Compiler.Compiler.Compile(source, optimization: 0).Functions[0].Instructions;
Require(Instructions("release;").Any(instruction => instruction.Op == Msync.Core.OpCode.Barrier && instruction.A == 1), "standalone Before lowering");
Require(Instructions("acquire;").Any(instruction => instruction.Op == Msync.Core.OpCode.Acquire && instruction.A == 0), "standalone join-all lowering");
Require(Instructions("release ./tool();").Single(instruction => instruction.Op == Msync.Core.OpCode.ProcessCall).B == 1, "released process lowering");
Require(Instructions("acqrel ./tool();").Single(instruction => instruction.Op == Msync.Core.OpCode.ProcessCall).B == 3, "acqrel process lowering");
Require(Instructions("./tool();").Single(instruction => instruction.Op == Msync.Core.OpCode.ProcessCall).B == 0, "ordinary process waits");
Require(Instructions("let process = release ./tool(); acquire process;").Any(instruction => instruction.Op == Msync.Core.OpCode.Acquire && instruction.A == 1), "selected process join lowering");
var generic = Instructions("acquire ./source -> ./destination;");
Require(generic.Any(instruction => instruction.Op == Msync.Core.OpCode.Barrier && instruction.A == 2)
    && generic.All(instruction => instruction.Op != Msync.Core.OpCode.Acquire), "after fence is not join-all");
var later = Instructions("release; ./tool();");
Require(later.Single(instruction => instruction.Op == Msync.Core.OpCode.ProcessCall).B == 0, "release is not a sticky process mode");
var acquireRegion = Instructions("acquire ./source -> ./destination;").ToArray();
int regionStart = Array.FindIndex(acquireRegion, instruction => instruction is { Op: Msync.Core.OpCode.Barrier, A: 2, B: 1 });
int transfer = Array.FindIndex(acquireRegion, instruction => instruction.Op == Msync.Core.OpCode.Transfer);
int regionEnd = Array.FindIndex(acquireRegion, instruction => instruction is { Op: Msync.Core.OpCode.Barrier, A: 2, B: 0 });
Require(regionStart >= 0 && regionStart < transfer && transfer < regionEnd, "acquire region brackets exactly its transfer");
var nestedRegions = Instructions("acquire { acquire ./first -> ./second; ./third -> ./fourth }")
    .Where(instruction => instruction is { Op: Msync.Core.OpCode.Barrier, A: 2 }).Select(instruction => instruction.B);
Require(nestedRegions.SequenceEqual([1, 1, 0, 0]), "nested acquire regions retain lexical ownership");
Require(Instructions("acquire;").All(instruction => instruction.Op != Msync.Core.OpCode.Barrier), "standalone acquire does not open a region");
Require(Instructions("release ./source -> ./destination;").All(instruction => instruction is not { Op: Msync.Core.OpCode.Barrier, A: 2 }), "release does not open an acquire region");
var nestedMarkerIds = Instructions("acquire { loop { acquire { break true; }; }; true }")
    .Where(instruction => instruction is { Op: Msync.Core.OpCode.Barrier, A: 2 }).ToArray();
Require(nestedMarkerIds.Length == 5 && nestedMarkerIds[0].Text is not null
    && nestedMarkerIds[0].Text == nestedMarkerIds[4].Text
    && nestedMarkerIds[1].Text is not null && nestedMarkerIds[1].Text == nestedMarkerIds[2].Text
    && nestedMarkerIds[1].Text == nestedMarkerIds[3].Text
    && nestedMarkerIds[0].Text != nestedMarkerIds[1].Text, "nested break acquire markers retain distinct paired identities");
var callRegions = Instructions("fn identity(value) { value }; identity(acquire true); identity(acquire false);")
    .Where(instruction => instruction is { Op: Msync.Core.OpCode.Barrier, A: 2, B: 1 }).Select(instruction => instruction.Text).ToArray();
Require(callRegions.Length == 2 && callRegions.Distinct(StringComparer.Ordinal).Count() == 2,
    "scratch call lowering preserves unique acquire region identities");
var breakCode = Instructions("acquire { loop { acquire { break true; }; }; true }").ToArray();
int breakJump = Array.FindIndex(breakCode, instruction => instruction.Op == Msync.Core.OpCode.Jump);
Require(breakJump > 0 && breakCode[breakJump - 1] is { Op: Msync.Core.OpCode.Barrier, A: 2, B: 0 }
    && breakCode[breakJump - 1].Text == nestedMarkerIds[1].Text, "break closes only its exited acquire region before jumping");
var outerLoop = Instructions("acquire loop { break true; }").ToArray();
int outerBreak = Array.FindIndex(outerLoop, instruction => instruction.Op == Msync.Core.OpCode.Jump);
Require(outerBreak >= 0 && outerLoop[outerBreak - 1].Op != Msync.Core.OpCode.Barrier,
    "break does not close acquire region surrounding whole loop");
var returnModule = Msync.Compiler.Compiler.Compile("fn finish { acquire { return true; } }; finish();", optimization: 0);
var returnCode = returnModule.Functions.Single(function => function.Name == "finish").Instructions.ToArray();
int explicitReturn = Array.FindIndex(returnCode, instruction => instruction.Op == Msync.Core.OpCode.Return);
Require(explicitReturn > 0 && returnCode[explicitReturn - 1] is { Op: Msync.Core.OpCode.Barrier, A: 2, B: 0 },
    "return closes active acquire regions before leaving frame");
Console.WriteLine("Barrier lowering: 17 contracts passed.");
