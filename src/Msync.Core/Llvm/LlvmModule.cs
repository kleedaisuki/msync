// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Module.h/.cpp, SymbolTableListTraits.h.
namespace Msync.Core.Llvm;

public sealed partial class LlvmModule
{
    private readonly List<LlvmFunction> functions = [];
    private readonly List<LlvmGlobalVariable> globals = [];
    internal LlvmThreadModel? ExplicitThreadModel { get; private set; }
    public LlvmContext Context { get; }
    public string ModuleIdentifier { get; set; }
    public string SourceFileName { get; set; }
    public LlvmTriple TargetTriple { get; set; } = new();
    public bool SemanticInterposition { get; set; }
    public LlvmDataLayout DataLayout { get; set; } = new();
    public LlvmValueSymbolTable ValueSymbolTable { get; } = new();
    public IReadOnlyList<LlvmFunction> Functions { get; }
    public IReadOnlyList<LlvmGlobalVariable> GlobalVariables { get; }
    // Module.cpp getThreadModel/setThreadModel: the optional field represents
    // the explicit thread-model module flag, not the host's threading state.
    public LlvmThreadModel GetThreadModel() => ExplicitThreadModel ?? TargetTriple.GetDefaultThreadModel();
    public void SetThreadModel(LlvmThreadModel model) => ExplicitThreadModel = model;
    public LlvmModule(string moduleId, LlvmContext context)
    {
        Context = context;
        ModuleIdentifier = moduleId;
        SourceFileName = moduleId;
        Functions = functions.AsReadOnly();
        GlobalVariables = globals.AsReadOnly();
    }
    public LlvmGlobalValue? GetNamedValue(ReadOnlySpan<byte> name) => (LlvmGlobalValue?)ValueSymbolTable.Lookup(name);
    public LlvmFunction? GetFunction(ReadOnlySpan<byte> name) => ValueSymbolTable.Lookup(name) as LlvmFunction;
    public void InsertFunction(LlvmFunction function, LlvmFunction? before = null)
    {
        System.Diagnostics.Debug.Assert(function.Parent is null);
        int index = before is null ? functions.Count : functions.IndexOf(before);
        function.Parent = this;
        functions.Insert(index, function);
        if (function.HasName) ValueSymbolTable.ReinsertValue(function);
    }
    public void RemoveFunction(LlvmFunction function)
    {
        if (function.HasName) ValueSymbolTable.RemoveValueName(function.GetValueName()!);
        functions.Remove(function);
        function.Parent = null;
    }
    public void InsertGlobalVariable(LlvmGlobalVariable global, LlvmGlobalVariable? before = null)
    {
        System.Diagnostics.Debug.Assert(global.Parent is null);
        int index = before is null ? globals.Count : globals.IndexOf(before);
        global.Parent = this;
        globals.Insert(index, global);
        if (global.HasName) ValueSymbolTable.ReinsertValue(global);
    }
    public void RemoveGlobalVariable(LlvmGlobalVariable global)
    {
        if (global.HasName) ValueSymbolTable.RemoveValueName(global.GetValueName()!);
        globals.Remove(global);
        global.Parent = null;
    }
}
