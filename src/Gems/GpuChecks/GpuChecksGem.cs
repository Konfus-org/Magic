using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Debugging;
using Magic.Gems;
using Magic.Gpu;
using Magic.Pipelines;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;

namespace GpuChecks;

/// <summary>
/// Proves the GPU provider and the shader compiler work together, before any renderer draws: as the run starts, a
/// compute entry fills a texture with a gradient through the heap, the texture is read back and its corners checked,
/// and each is an error under --fail-on-error when wrong. It is public because nothing in its assembly makes it:
/// Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class GpuChecksGem(IGraphicsApi gpu, IShaderCompiler shaders, AssetDatabase assets, Threads threads)
    : IGem,
        IPipeline<Startup>,
        IDisposable
{
    private const int Size = 256;
    private const string ShaderPath = "Shaders/Checks/Gradient.slang";

    private readonly GpuCommandRecorder _recorder = new();
    private GpuPipeline _pipeline;
    private GpuTexture _gradient;
    private GpuBuffer _readback;
    private uint _groupsX;
    private uint _groupsY;

    public void Dispose()
    {
        if (!_pipeline.IsNone)
            gpu.Destroy(_pipeline);

        if (!_gradient.IsNone)
            gpu.Destroy(_gradient);

        if (!_readback.IsNone)
            gpu.Destroy(_readback);
    }

    /// <summary>
    /// Compiles the entry, makes the texture, computes the gradient once and checks what came back. It runs to the end
    /// even once the run was told to exit, as the validation types exit the moment the run starts: a check that stops
    /// then checks nothing.
    /// </summary>
    public async ValueTask<Startup> RunAsync(Startup state, CancellationToken cancel)
    {
        Handle<Shader> shader = assets.Find<Shader>(ShaderPath);
        if (shader == Handle<Shader>.None)
        {
            Log.Error($"GPU check: there is no {ShaderPath}.");

            return state;
        }

        Result<CompiledShader> compiled = await shaders
            .CompileAsync(
                new ShaderEntry
                {
                    Module = shader,
                    Name = "FillGradient",
                    Stage = ShaderStage.Compute,
                },
                null,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        await threads.Enter(EngineThread.Main);
        if (!compiled.Ok)
        {
            Log.Error($"GPU check: the gradient shader did not compile: {compiled.Error}");

            return state;
        }

        if (compiled.Value.GroupSizeX == 0 || compiled.Value.GroupSizeY == 0)
        {
            Log.Error(
                $"GPU check: the compiler reported a group size of {compiled.Value.GroupSizeX} by {compiled.Value.GroupSizeY}."
            );

            return state;
        }

        _groupsX = (Size + compiled.Value.GroupSizeX - 1) / compiled.Value.GroupSizeX;
        _groupsY = (Size + compiled.Value.GroupSizeY - 1) / compiled.Value.GroupSizeY;
        if (!TryCreateResources(compiled.Value))
            return state;

        RecordGradient();
        _recorder.BeginTransferPass("Read gradient back");
        _recorder.Transition(_gradient, GpuAccess.ShaderWrite, GpuAccess.CopySource);
        _recorder.Transition(_readback, GpuAccess.None, GpuAccess.CopyDestination);
        _recorder.CopyTextureToBuffer(_gradient, 0, 0, _readback, 0);
        _recorder.Transition(_readback, GpuAccess.CopyDestination, GpuAccess.HostRead);
        _recorder.EndPass();
        gpu.Submit(_recorder.Finish());
        Result<ReadOnlyMemory<byte>> pixels = await gpu.ReadBackAsync(
                _readback,
                0,
                Size * Size * 4,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        await threads.Enter(EngineThread.Main);
        if (!pixels.Ok)
        {
            Log.Error($"GPU check: the gradient could not be read back: {pixels.Error}");

            return state;
        }

        Check(pixels.Value.Span);

        return state;
    }

    /// <summary>
    /// The gradient is red along x, green along y, a quarter blue and opaque; each corner and the centre must read so.
    /// </summary>
    private static void Check(ReadOnlySpan<byte> pixels)
    {
        ReadOnlySpan<(int X, int Y)> probes =
        [
            (0, 0),
            (Size - 1, 0),
            (0, Size - 1),
            (Size - 1, Size - 1),
            (Size / 2, Size / 2),
        ];
        int wrong = 0;
        foreach ((int x, int y) in probes)
        {
            int at = ((y * Size) + x) * 4;
            byte red = pixels[at];
            byte green = pixels[at + 1];
            byte blue = pixels[at + 2];
            byte alpha = pixels[at + 3];
            int expectedRed = x * 255 / (Size - 1);
            int expectedGreen = y * 255 / (Size - 1);
            bool isRight =
                Math.Abs(red - expectedRed) <= 1
                && Math.Abs(green - expectedGreen) <= 1
                && Math.Abs(blue - 64) <= 1
                && alpha == 255;
            if (isRight)
                continue;

            wrong++;
            Log.Error(
                $"GPU check: the gradient at ({x}, {y}) is ({red}, {green}, {blue}, {alpha}), not ({expectedRed}, {expectedGreen}, 64, 255)."
            );
        }

        if (wrong == 0)
            Log.Info($"GPU check passed: a {Size} by {Size} gradient computed through the heap and read back.");
    }

    private bool TryCreateResources(CompiledShader compiled)
    {
        Result<GpuPipeline> pipeline = gpu.CreatePipeline(
            new ComputePipelineDescription
            {
                Spirv = compiled.Spirv,
                EntryPoint = compiled.EntryPoint,
                Name = "FillGradient",
            }
        );
        if (!pipeline.Ok)
        {
            Log.Error($"GPU check: the gradient pipeline was not built: {pipeline.Error}");

            return false;
        }

        _pipeline = pipeline.Value;
        Result<GpuTexture> gradient = gpu.CreateTexture(
            new TextureDescription
            {
                Width = Size,
                Height = Size,
                Format = GpuFormat.R8G8B8A8Unorm,
                Usage = TextureUsages.Storage | TextureUsages.CopySource,
                Name = "Gradient",
            }
        );
        if (!gradient.Ok)
        {
            Log.Error($"GPU check: the gradient texture was not made: {gradient.Error}");

            return false;
        }

        _gradient = gradient.Value;
        Result<GpuBuffer> readback = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = Size * Size * 4,
                Usage = BufferUsages.Readback,
                Name = "Gradient readback",
            }
        );
        if (!readback.Ok)
        {
            Log.Error($"GPU check: the readback buffer was not made: {readback.Error}");

            return false;
        }

        _readback = readback.Value;

        return true;
    }

    /// <summary>
    /// Records the gradient's dispatch into the gradient texture.
    /// </summary>
    private void RecordGradient()
    {
        _recorder.BeginComputePass("Fill gradient");
        _recorder.Transition(_gradient, GpuAccess.None, GpuAccess.ShaderWrite);
        _recorder.BindPipeline(_pipeline);
        _recorder.PushConstants(new GpuConstants(_gradient.Id, Size, Size));
        _recorder.Dispatch(_groupsX, _groupsY);
        _recorder.EndPass();
    }
}
