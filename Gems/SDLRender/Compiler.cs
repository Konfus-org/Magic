using Magic.Contexts.Rendering;
using Magic.Utils;
using SDL3;
using System.Runtime.InteropServices;

namespace SDLRenderGem;

/// <summary>
/// HLSL to what the device eats with SDL_shadercross: SPIR-V through DXC, reflected for its binding counts (reflection
/// gives counts, no names, which is all SDL needs), translated on to DXIL for D3D12. Pure and safe from any thread; the
/// caller caches.
/// </summary>
internal static class Compiler
{
    public static unsafe Result<CompiledShader> Compile(string source, string name, GpuStage stage, string includeDirectory, SDL.GPUShaderFormat target)
    {
        (ShaderCross.ShaderStage crossStage, string stageDefine) = stage switch
        {
            GpuStage.Vertex => (ShaderCross.ShaderStage.Vertex, "STAGE_VERTEX"),
            GpuStage.Fragment => (ShaderCross.ShaderStage.Fragment, "STAGE_FRAGMENT"),
            _ => (ShaderCross.ShaderStage.Compute, "STAGE_COMPUTE"),
        };

        // The stage goes in front of the source: Include/Bindings.hlsli picks SDL's register spaces by it.
        source = $"#define {stageDefine} 1\n#line 1 \"{name}\"\n{source}";
        nint sourcePtr = Marshal.StringToCoTaskMemUTF8(source);
        nint entryPtr = Marshal.StringToCoTaskMemUTF8("main");
        nint includePtr = Marshal.StringToCoTaskMemUTF8(includeDirectory);
        uint props = SDL.CreateProperties();
        nint spirv = 0, reflection = 0, dxil = 0;

        try
        {
            SDL.SetStringProperty(props, ShaderCross.Props.ShaderDebugNameString, name);

            ShaderCross.HLSLInfo hlsl = new()
            {
                Source = sourcePtr,
                Entrypoint = entryPtr,
                IncludeDir = includePtr,
                Defines = 0,
                ShaderStage = crossStage,
                Props = props,
            };
            spirv = ShaderCross.CompileSPIRVFromHLSL(ref hlsl, out nuint spirvSize);
            if (spirv == 0)
                return Result<CompiledShader>.Failure($"{name}: {SDL.GetError()}");

            CompiledShader compiled = new() { Stage = stage };
            if (stage == GpuStage.Compute)
            {
                reflection = ShaderCross.ReflectComputeSPIRV(spirv, spirvSize, 0);
                if (reflection == 0)
                    return Result<CompiledShader>.Failure($"{name}: reflection failed: {SDL.GetError()}");

                ShaderCross.ComputePipelineMetadata meta = *(ShaderCross.ComputePipelineMetadata*)reflection;
                compiled.Samplers = meta.NumSamplers;
                compiled.StorageTextures = meta.NumReadOnlyStorageTextures;
                compiled.StorageBuffers = meta.NumReadOnlyStorageBuffers;
                compiled.ReadWriteStorageTextures = meta.NumReadWriteStorageTextures;
                compiled.ReadWriteStorageBuffers = meta.NumReadWriteStorageBuffers;
                compiled.UniformBuffers = meta.NumUniformBuffers;
                compiled.ThreadCountX = meta.ThreadCountX;
                compiled.ThreadCountY = meta.ThreadCountY;
                compiled.ThreadCountZ = meta.ThreadCountZ;
            }
            else
            {
                reflection = ShaderCross.ReflectGraphicsSPIRV(spirv, spirvSize, 0);
                if (reflection == 0)
                    return Result<CompiledShader>.Failure($"{name}: reflection failed: {SDL.GetError()}");

                ShaderCross.GraphicsShaderResourceInfo info = ((ShaderCross.GraphicsShaderMetadata*)reflection)->ResourceInfo;
                compiled.Samplers = info.NumSamplers;
                compiled.StorageTextures = info.NumStorageTextures;
                compiled.StorageBuffers = info.NumStorageBuffers;
                compiled.UniformBuffers = info.NumUniformBuffers;
            }

            if (target == SDL.GPUShaderFormat.SPIRV)
            {
                compiled.Code = new byte[(int)spirvSize];
                Marshal.Copy(spirv, compiled.Code, 0, compiled.Code.Length);
            }
            else
            {
                ShaderCross.SPIRVInfo spirvInfo = new()
                {
                    ByteCode = spirv,
                    ByteCodeSize = spirvSize,
                    Entrypoint = entryPtr,
                    ShaderStage = crossStage,
                    Props = props,
                };
                dxil = ShaderCross.CompileDXILFromSPIRV(in spirvInfo, out nuint dxilSize);
                if (dxil == 0)
                    return Result<CompiledShader>.Failure($"{name}: SPIR-V to DXIL failed: {SDL.GetError()}");

                compiled.Code = new byte[(int)dxilSize];
                Marshal.Copy(dxil, compiled.Code, 0, compiled.Code.Length);
            }

            return Result<CompiledShader>.Success(compiled);
        }
        finally
        {
            if (spirv != 0) SDL.Free(spirv);
            if (reflection != 0) SDL.Free(reflection);
            if (dxil != 0) SDL.Free(dxil);
            SDL.DestroyProperties(props);
            Marshal.FreeCoTaskMem(sourcePtr);
            Marshal.FreeCoTaskMem(entryPtr);
            Marshal.FreeCoTaskMem(includePtr);
        }
    }
}
