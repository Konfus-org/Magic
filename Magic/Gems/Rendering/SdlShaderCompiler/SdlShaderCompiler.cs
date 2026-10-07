using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using SDL3;
using System.Runtime.InteropServices;

namespace SdlShaderCompilerGem;

/// <summary>
/// The gem: HLSL to SPIR-V or DXIL (<see cref="IShaderCompiler"/>) with SDL_shadercross, which it starts for the run.
/// </summary>
internal sealed class SdlShaderCompiler : IGem, IShaderCompiler
{
    public SdlShaderCompiler(IFileSystem files, Project project)
    {
        // DXIL is D3D12's, so Windows only. dxcompiler.dll loads dxil.dll by bare name and, without it, produces
        // unsigned DXIL that D3D12 rejects; the gem folder is not on the search path, so it is loaded by full path first.
        if (OperatingSystem.IsWindows())
        {
            string dxil = files.Combine(project.EngineGems, "dxil.dll");
            if (files.FileExists(dxil))
                NativeLibrary.Load(dxil);
            else
                Log.Warn($"dxil.dll not found at {dxil}; DXIL shaders will be unsigned.");
        }

        if (!ShaderCross.Init())
            throw new InvalidOperationException($"SDL_shadercross failed to initialise: {SDL.GetError()}");
    }

    public void Dispose()
    {
        ShaderCross.Quit();
    }

    public Result<CompiledShader> Compile(string hlsl, string name, GpuStage stage, string format)
    {
        if (!Enum.TryParse(format, ignoreCase: true, out SDL.GPUShaderFormat target) || target is not (SDL.GPUShaderFormat.SPIRV or SDL.GPUShaderFormat.DXIL))
            return Result<CompiledShader>.Failure($"{name}: no compiler for the shader format {format}.");

        return Compiler.Compile(hlsl, name, stage, target);
    }
}
