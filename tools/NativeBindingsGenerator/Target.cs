using CppAst;

namespace Magic.Tools.NativeBindingsGenerator;

/// <summary>
/// A platform the headers are read for. Every target is 64-bit, where each OS has one calling convention, so the
/// generated code names none; 32-bit x86, where it would matter, is not a target.
/// </summary>
internal sealed record Target(string Name, CppTargetCpu Cpu, string System, string Vendor, string Abi)
{
    public static readonly Target[] All =
    [
        new("win-x64", CppTargetCpu.X86_64, "windows", "pc", "msvc"),
        new("linux-x64", CppTargetCpu.X86_64, "linux", "pc", "gnu"),
        new("osx-arm64", CppTargetCpu.ARM64, "darwin", "apple", ""),
    ];
}
