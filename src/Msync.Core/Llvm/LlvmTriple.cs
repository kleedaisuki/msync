// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: TargetParser/Triple.h/.cpp.
// Stored target description; target queries do not introduce a target backend.
namespace Msync.Core.Llvm;

// Support/CodeGen.h ThreadModel; module-flag storage is adapted by LlvmModule.
public enum LlvmThreadModel : byte { Posix, Single }

public sealed partial class LlvmTriple
{
    public LlvmTriple(string data = "")
    {
        Data = data;
        string[] components = data.Split('-', 4);
        archName = components[0];
        vendorName = components.Length > 1 ? components[1] : "";
        os = ParseOS(components.Length > 2 ? components[2] : "");
        objectFormat = ParseObjectFormat(components.Length > 3 ? components[3] : "");
        if (objectFormat == LlvmObjectFormat.Unknown) objectFormat = GetDefaultObjectFormat();
    }
    public string Data { get; }
    public string GetTriple() => Data;
    public string GetArchName()
    {
        int separator = Data.IndexOf('-');
        return separator < 0 ? Data : Data[..separator];
    }
    public bool IsNVPTX() => GetArchName() is "nvptx" or "nvptx64";
    public bool isNVPTX() => IsNVPTX();
    public LlvmThreadModel GetDefaultThreadModel() => LlvmThreadModel.Posix;
    public override string ToString() => Data;
}
