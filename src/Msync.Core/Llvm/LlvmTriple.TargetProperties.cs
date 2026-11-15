// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Triple.cpp parseArch/parseOS/parseFormat/getDefaultFormat; Triple.h target queries.
namespace Msync.Core.Llvm;

public enum LlvmObjectFormat { Unknown, COFF, ELF, GOFF, MachO, Wasm, XCOFF, DXContainer, SPIRV }

public sealed partial class LlvmTriple
{
    private enum TargetOS { Unknown, Darwin, DragonFly, FreeBSD, Fuchsia, IOS, KFreeBSD, Linux, Lv2, MacOSX, Managarm, NetBSD, OpenBSD, Solaris, UEFI, Win32, ZOS, Haiku, RTEMS, AIX, CUDA, NVCL, AMDHSA, PS4, PS5, ELFIAMCU, TvOS, WatchOS, BridgeOS, DriverKit, XROS, Mesa3D, AMDPAL, HermitCore, Hurd, WASIp1, WASIp2, WASIp3, WASI, Emscripten, ShaderModel, LiteOS, Serenity, Vulkan, CheriotRTOS, OpenCL, ChipStar, Firmware, QURT, H2 }
    private enum FormatArch { Mixed, ELF, MipsEL, PowerPC, SystemZ, Wasm, SPIRV, DXContainer }
    private readonly string archName, vendorName;
    private readonly TargetOS os;
    private readonly LlvmObjectFormat objectFormat;
    public LlvmObjectFormat GetObjectFormat() => objectFormat;
    public bool IsOSBinFormatELF() => objectFormat == LlvmObjectFormat.ELF;
    public bool IsOSBinFormatXCOFF() => objectFormat == LlvmObjectFormat.XCOFF;
    public bool IsiOS() => os is TargetOS.IOS or TargetOS.TvOS;
    public bool IsOSDarwin() => os is TargetOS.Darwin or TargetOS.MacOSX or TargetOS.IOS or TargetOS.TvOS
        or TargetOS.WatchOS or TargetOS.DriverKit or TargetOS.XROS or TargetOS.BridgeOS
        || os == TargetOS.Firmware && vendorName == "apple";

    private static TargetOS ParseOS(string name)
    {
        if (name.StartsWith("darwin", StringComparison.Ordinal)) return TargetOS.Darwin;
        if (name.StartsWith("dragonfly", StringComparison.Ordinal)) return TargetOS.DragonFly;
        if (name.StartsWith("freebsd", StringComparison.Ordinal)) return TargetOS.FreeBSD;
        if (name.StartsWith("fuchsia", StringComparison.Ordinal)) return TargetOS.Fuchsia;
        if (name.StartsWith("ios", StringComparison.Ordinal)) return TargetOS.IOS;
        if (name.StartsWith("kfreebsd", StringComparison.Ordinal)) return TargetOS.KFreeBSD;
        if (name.StartsWith("linux", StringComparison.Ordinal)) return TargetOS.Linux;
        if (name.StartsWith("lv2", StringComparison.Ordinal)) return TargetOS.Lv2;
        if (name.StartsWith("macosx", StringComparison.Ordinal)) return TargetOS.MacOSX;
        if (name.StartsWith("macos", StringComparison.Ordinal)) return TargetOS.MacOSX;
        if (name.StartsWith("managarm", StringComparison.Ordinal)) return TargetOS.Managarm;
        if (name.StartsWith("netbsd", StringComparison.Ordinal)) return TargetOS.NetBSD;
        if (name.StartsWith("openbsd", StringComparison.Ordinal)) return TargetOS.OpenBSD;
        if (name.StartsWith("solaris", StringComparison.Ordinal)) return TargetOS.Solaris;
        if (name.StartsWith("uefi", StringComparison.Ordinal)) return TargetOS.UEFI;
        if (name.StartsWith("windows", StringComparison.Ordinal)) return TargetOS.Win32;
        if (name.StartsWith("win32", StringComparison.Ordinal)) return TargetOS.Win32;
        if (name.StartsWith("zos", StringComparison.Ordinal)) return TargetOS.ZOS;
        if (name.StartsWith("haiku", StringComparison.Ordinal)) return TargetOS.Haiku;
        if (name.StartsWith("rtems", StringComparison.Ordinal)) return TargetOS.RTEMS;
        if (name.StartsWith("aix", StringComparison.Ordinal)) return TargetOS.AIX;
        if (name.StartsWith("cuda", StringComparison.Ordinal)) return TargetOS.CUDA;
        if (name.StartsWith("nvcl", StringComparison.Ordinal)) return TargetOS.NVCL;
        if (name.StartsWith("amdhsa", StringComparison.Ordinal)) return TargetOS.AMDHSA;
        if (name.StartsWith("ps4", StringComparison.Ordinal)) return TargetOS.PS4;
        if (name.StartsWith("ps5", StringComparison.Ordinal)) return TargetOS.PS5;
        if (name.StartsWith("elfiamcu", StringComparison.Ordinal)) return TargetOS.ELFIAMCU;
        if (name.StartsWith("tvos", StringComparison.Ordinal)) return TargetOS.TvOS;
        if (name.StartsWith("watchos", StringComparison.Ordinal)) return TargetOS.WatchOS;
        if (name.StartsWith("bridgeos", StringComparison.Ordinal)) return TargetOS.BridgeOS;
        if (name.StartsWith("driverkit", StringComparison.Ordinal)) return TargetOS.DriverKit;
        if (name.StartsWith("xros", StringComparison.Ordinal)) return TargetOS.XROS;
        if (name.StartsWith("visionos", StringComparison.Ordinal)) return TargetOS.XROS;
        if (name.StartsWith("mesa3d", StringComparison.Ordinal)) return TargetOS.Mesa3D;
        if (name.StartsWith("amdpal", StringComparison.Ordinal)) return TargetOS.AMDPAL;
        if (name.StartsWith("hermit", StringComparison.Ordinal)) return TargetOS.HermitCore;
        if (name.StartsWith("hurd", StringComparison.Ordinal)) return TargetOS.Hurd;
        if (name.StartsWith("wasip1", StringComparison.Ordinal)) return TargetOS.WASIp1;
        if (name.StartsWith("wasip2", StringComparison.Ordinal)) return TargetOS.WASIp2;
        if (name.StartsWith("wasip3", StringComparison.Ordinal)) return TargetOS.WASIp3;
        if (name.StartsWith("wasi", StringComparison.Ordinal)) return TargetOS.WASI;
        if (name.StartsWith("emscripten", StringComparison.Ordinal)) return TargetOS.Emscripten;
        if (name.StartsWith("shadermodel", StringComparison.Ordinal)) return TargetOS.ShaderModel;
        if (name.StartsWith("liteos", StringComparison.Ordinal)) return TargetOS.LiteOS;
        if (name.StartsWith("serenity", StringComparison.Ordinal)) return TargetOS.Serenity;
        if (name.StartsWith("vulkan", StringComparison.Ordinal)) return TargetOS.Vulkan;
        if (name.StartsWith("cheriotrtos", StringComparison.Ordinal)) return TargetOS.CheriotRTOS;
        if (name.StartsWith("opencl", StringComparison.Ordinal)) return TargetOS.OpenCL;
        if (name.StartsWith("chipstar", StringComparison.Ordinal)) return TargetOS.ChipStar;
        if (name.StartsWith("firmware", StringComparison.Ordinal)) return TargetOS.Firmware;
        if (name.StartsWith("qurt", StringComparison.Ordinal)) return TargetOS.QURT;
        if (name.StartsWith("h2", StringComparison.Ordinal)) return TargetOS.H2;
        return TargetOS.Unknown;
    }

    private static LlvmObjectFormat ParseObjectFormat(string name)
    {
        if (name.EndsWith("xcoff", StringComparison.Ordinal)) return LlvmObjectFormat.XCOFF;
        if (name.EndsWith("coff", StringComparison.Ordinal)) return LlvmObjectFormat.COFF;
        if (name.EndsWith("elf", StringComparison.Ordinal)) return LlvmObjectFormat.ELF;
        if (name.EndsWith("goff", StringComparison.Ordinal)) return LlvmObjectFormat.GOFF;
        if (name.EndsWith("macho", StringComparison.Ordinal)) return LlvmObjectFormat.MachO;
        if (name.EndsWith("wasm", StringComparison.Ordinal)) return LlvmObjectFormat.Wasm;
        if (name.EndsWith("spirv", StringComparison.Ordinal)) return LlvmObjectFormat.SPIRV;
        return LlvmObjectFormat.Unknown;
    }

    private LlvmObjectFormat GetDefaultObjectFormat() => ParseFormatArchitecture(archName) switch
    {
        FormatArch.Mixed => os is TargetOS.Win32 or TargetOS.UEFI ? LlvmObjectFormat.COFF
            : IsOSDarwin() ? LlvmObjectFormat.MachO : LlvmObjectFormat.ELF,
        FormatArch.PowerPC => os == TargetOS.AIX ? LlvmObjectFormat.XCOFF
            : IsOSDarwin() ? LlvmObjectFormat.MachO : LlvmObjectFormat.ELF,
        FormatArch.MipsEL => os == TargetOS.Win32 ? LlvmObjectFormat.COFF : LlvmObjectFormat.ELF,
        FormatArch.SystemZ => os == TargetOS.ZOS ? LlvmObjectFormat.GOFF : LlvmObjectFormat.ELF,
        FormatArch.Wasm => LlvmObjectFormat.Wasm,
        FormatArch.SPIRV => LlvmObjectFormat.SPIRV,
        FormatArch.DXContainer => LlvmObjectFormat.DXContainer,
        _ => LlvmObjectFormat.ELF
    };

    private static FormatArch ParseFormatArchitecture(string name)
    {
        if (name.StartsWith("amdgpu", StringComparison.Ordinal) || name.StartsWith("kalimba", StringComparison.Ordinal)) return FormatArch.ELF;
        return name switch
        {
            "i386" or "i486" or "i586" or "i686" => FormatArch.Mixed,
            "i786" or "i886" or "i986" => FormatArch.Mixed,
            "amd64" or "x86_64" or "x86_64h" or "x86_64_lfi" => FormatArch.Mixed,
            "powerpc" or "powerpcspe" or "ppc" or "ppc32" => FormatArch.PowerPC,
            "powerpcle" or "ppcle" or "ppc32le" => FormatArch.ELF,
            "powerpc64" or "ppu" or "ppc64" => FormatArch.PowerPC,
            "powerpc64le" or "ppc64le" => FormatArch.ELF,
            "xscale" => FormatArch.Mixed,
            "xscaleeb" => FormatArch.ELF,
            "aarch64" => FormatArch.Mixed,
            "aarch64_be" => FormatArch.ELF,
            "aarch64_32" => FormatArch.Mixed,
            "aarch64_lfi" => FormatArch.Mixed,
            "arc" => FormatArch.ELF,
            "arm64" => FormatArch.Mixed,
            "arm64_32" => FormatArch.Mixed,
            "arm64e" => FormatArch.Mixed,
            "arm64e.x1" => FormatArch.Mixed,
            "arm64ec" => FormatArch.Mixed,
            "arm" => FormatArch.Mixed,
            "armeb" => FormatArch.ELF,
            "thumb" => FormatArch.Mixed,
            "thumbeb" => FormatArch.ELF,
            "avr" => FormatArch.ELF,
            "m68k" => FormatArch.ELF,
            "msp430" => FormatArch.ELF,
            "mips" or "mipseb" or "mipsallegrex" or "mipsisa32r6" or "mipsr6" => FormatArch.ELF,
            "mipsel" or "mipsallegrexel" or "mipsisa32r6el" or "mipsr6el" => FormatArch.MipsEL,
            "mips64" or "mips64eb" or "mipsn32" or "mipsisa64r6" or "mips64r6" or "mipsn32r6" => FormatArch.ELF,
            "mips64el" or "mipsn32el" or "mipsisa64r6el" or "mips64r6el" or "mipsn32r6el" => FormatArch.ELF,
            "amdgcn" => FormatArch.ELF,
            "r600" => FormatArch.ELF,
            "riscv32" => FormatArch.ELF,
            "riscv64" => FormatArch.ELF,
            "riscv32be" => FormatArch.ELF,
            "riscv64be" => FormatArch.ELF,
            "hexagon" => FormatArch.ELF,
            "s390x" or "systemz" => FormatArch.SystemZ,
            "sparc" => FormatArch.ELF,
            "sparcel" => FormatArch.ELF,
            "sparcv9" or "sparc64" => FormatArch.ELF,
            "tce" => FormatArch.ELF,
            "tcele" => FormatArch.ELF,
            "tcele64" => FormatArch.ELF,
            "xcore" => FormatArch.ELF,
            "nvptx" => FormatArch.ELF,
            "nvptx64" => FormatArch.ELF,
            "amdil" => FormatArch.ELF,
            "amdil64" => FormatArch.ELF,
            "hsail" => FormatArch.ELF,
            "hsail64" => FormatArch.ELF,
            "spir" => FormatArch.ELF,
            "spir64" => FormatArch.ELF,
            "spirv" or "spirv1.5" or "spirv1.6" => FormatArch.SPIRV,
            "spirv32" or "spirv32v1.0" or "spirv32v1.1" or "spirv32v1.2" or "spirv32v1.3" or "spirv32v1.4" or "spirv32v1.5" or "spirv32v1.6" => FormatArch.SPIRV,
            "spirv64" or "spirv64v1.0" or "spirv64v1.1" or "spirv64v1.2" or "spirv64v1.3" or "spirv64v1.4" or "spirv64v1.5" or "spirv64v1.6" => FormatArch.SPIRV,
            "lanai" => FormatArch.ELF,
            "renderscript32" => FormatArch.ELF,
            "renderscript64" => FormatArch.ELF,
            "shave" => FormatArch.ELF,
            "ve" => FormatArch.ELF,
            "wasm32" => FormatArch.Wasm,
            "wasm64" => FormatArch.Wasm,
            "csky" => FormatArch.ELF,
            "loongarch32" => FormatArch.ELF,
            "loongarch64" => FormatArch.ELF,
            "dxil" or "dxilv1.0" or "dxilv1.1" or "dxilv1.2" or "dxilv1.3" or "dxilv1.4" or "dxilv1.5" or "dxilv1.6" or "dxilv1.7" or "dxilv1.8" or "dxilv1.9" or "dxilv1.10" => FormatArch.DXContainer,
            "xtensa" => FormatArch.ELF,
            _ => ParseOtherFormatArchitecture(name)
        };
    }

    private static FormatArch ParseOtherFormatArchitecture(string name)
    {
        if (name is "bpf" or "bpf_be" or "bpfeb" or "bpf_le" or "bpfel") return FormatArch.ELF;
        if (!name.StartsWith("arm", StringComparison.Ordinal) && !name.StartsWith("thumb", StringComparison.Ordinal)
            && !name.StartsWith("aarch64", StringComparison.Ordinal)) return FormatArch.Mixed;
        string canonical = GetCanonicalArmArchName(name);
        if (canonical.Length == 0) return FormatArch.Mixed;
        if (name.StartsWith("thumb", StringComparison.Ordinal)
            && (canonical.StartsWith("v2", StringComparison.Ordinal) || canonical.StartsWith("v3", StringComparison.Ordinal)))
            return FormatArch.Mixed;
        bool bigEndian = name.StartsWith("armeb", StringComparison.Ordinal) || name.StartsWith("thumbeb", StringComparison.Ordinal)
            || name.StartsWith("aarch64_be", StringComparison.Ordinal)
            || (name.StartsWith("arm", StringComparison.Ordinal) || name.StartsWith("thumb", StringComparison.Ordinal))
                && name.EndsWith("eb", StringComparison.Ordinal);
        return bigEndian ? FormatArch.ELF : FormatArch.Mixed;
    }

    private static string GetCanonicalArmArchName(string name)
    {
        int offset = name.StartsWith("arm64_32", StringComparison.Ordinal) ? 8
            : name.StartsWith("arm64e", StringComparison.Ordinal) ? 6
            : name.StartsWith("arm64", StringComparison.Ordinal) ? 5
            : name.StartsWith("aarch64_32", StringComparison.Ordinal) ? 10
            : name.StartsWith("arm", StringComparison.Ordinal) ? 3
            : name.StartsWith("thumb", StringComparison.Ordinal) ? 5 : 7;
        if (offset == 7)
        {
            if (name.Contains("eb", StringComparison.Ordinal)) return "";
            if (name.AsSpan(offset).StartsWith("_be")) offset += 3;
        }
        if (name.AsSpan(offset).StartsWith("eb")) offset += 2;
        else if (name.EndsWith("eb", StringComparison.Ordinal)) name = name[..^2];
        string canonical = name[offset..];
        if (canonical.Length == 0) return name;
        if (canonical.Length >= 2 && (canonical[0] != 'v' || !char.IsAsciiDigit(canonical[1]))) return "";
        return canonical.Contains("eb", StringComparison.Ordinal) ? "" : canonical;
    }
}
