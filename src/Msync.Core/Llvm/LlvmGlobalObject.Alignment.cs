// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/GlobalValue.h isStrongDefinitionForLinker; Globals.cpp canIncreaseAlignment.
namespace Msync.Core.Llvm;

public abstract partial class LlvmGlobalValue
{
    public bool IsThreadLocal => ThreadLocalMode != LlvmThreadLocalMode.NotThreadLocal;
    public bool IsWeakForLinker => Linkage is LlvmLinkage.WeakAnyLinkage or LlvmLinkage.WeakODRLinkage
        or LlvmLinkage.LinkOnceAnyLinkage or LlvmLinkage.LinkOnceODRLinkage or LlvmLinkage.CommonLinkage or LlvmLinkage.ExternalWeakLinkage;
    public bool IsDeclarationForLinker => Linkage == LlvmLinkage.AvailableExternallyLinkage || IsDeclaration;
    public bool IsStrongDefinitionForLinker => !IsDeclarationForLinker && !IsWeakForLinker;
}

public abstract partial class LlvmGlobalObject
{
    public string Section { get; set; } = "";
    public bool HasSection => Section.Length != 0;
    public bool CanIncreaseAlignment()
    {
        if (!IsStrongDefinitionForLinker || HasSection && Alignment.HasValue) return false;
        bool elf = Parent is null || Parent.TargetTriple.IsOSBinFormatELF();
        if (elf && !IsDSOLocal) return false;
        bool xcoff = Parent is null || Parent.TargetTriple.IsOSBinFormatXCOFF();
        return !xcoff || this is not LlvmGlobalVariable global || !global.HasAttribute("toc-data");
    }
}

public sealed partial class LlvmGlobalVariable
{
    private Dictionary<string, string>? stringAttributes;
    public bool HasAttribute(string name) => stringAttributes?.ContainsKey(name) == true;
    public string? GetAttribute(string name) => stringAttributes?.GetValueOrDefault(name);
    public void AddAttribute(string name, string value = "") => (stringAttributes ??= new(StringComparer.Ordinal))[name] = value;
    public void RemoveAttribute(string name) => stringAttributes?.Remove(name);
}

public sealed partial class LlvmModule
{
    // The scalar field is the source MaxTLSAlign module flag, in bits, not host state.
    public uint MaxTLSAlignment { get; set; }
    public uint GetMaxTLSAlignment() => MaxTLSAlignment;
}
