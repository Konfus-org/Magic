using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Projects;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GpuChecks;

/// <summary>
/// Proves the GPU provider and the shader compiler work together, before any renderer draws: as the run starts, a
/// compute entry is compiled, fills a texture with a gradient through the heap, the texture is read back and its
/// corners checked, and each is an error under --fail-on-error when wrong. The compile and the readback wait on
/// workers; every GPU call is made on the main thread. It is public because nothing in its assembly makes it: Magic
/// does, by reflection, when a project lists the gem.
/// </summary>
public sealed class GpuChecksGem : IGem, IDisposable
{
    private const int Size = 256;
    private const int Bytes = Size * Size * 4;
    private const string ShaderPath = "Shaders/Gradient.slang";

    private readonly IGraphicsApi _gpu;
    private readonly Task _check;
    private GpuPipeline _pipeline;
    private GpuTexture _gradient;
    private GpuBuffer _readback;

    public GpuChecksGem(IGraphicsApi gpu, IShaderCompiler shaders, Assets assets, Threads threads)
    {
        _gpu = gpu;
        _check = CheckAsync(shaders, assets, threads);
        _check.FireAndForget();
    }

    /// <summary>
    /// A run that ends before the check has finished checked nothing, which is an error: a run that validates waits
    /// for it.
    /// </summary>
    public void Dispose()
    {
        if (!_check.IsCompleted)
            Log.Error("GPU check: the run ended before the check finished, so nothing was checked.");

        _gpu.Destroy(_pipeline);
        _gpu.Destroy(_gradient);
        _gpu.Destroy(_readback);
    }

    private async Task CheckAsync(IShaderCompiler shaders, Assets assets, Threads threads)
    {
        // Compile the entry on a worker.
        Handle<Shader> shader = assets.Find<Shader>(ShaderPath);
        if (shader == Handle<Shader>.None)
        {
            Log.Error($"GPU check: there is no {ShaderPath}.");

            return;
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
        if (!compiled.Ok)
        {
            Log.Error($"GPU check: the gradient shader did not compile: {compiled.Error}");

            return;
        }

        // Make the pipeline, the texture and the readback buffer, on the main thread.
        await threads.Enter(EngineThread.Main);
        CompiledShader entry = compiled.Value;
        Result<GpuPipeline> pipeline = _gpu.CreatePipeline(
            new ComputePipelineDescription
            {
                Spirv = entry.Spirv,
                EntryPoint = entry.EntryPoint,
                Name = "FillGradient",
            }
        );
        if (!pipeline.Ok)
        {
            Log.Error($"GPU check: the gradient pipeline was not built: {pipeline.Error}");

            return;
        }

        _pipeline = pipeline.Value;
        Result<GpuTexture> gradient = _gpu.CreateTexture(
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

            return;
        }

        _gradient = gradient.Value;
        Result<GpuBuffer> readback = _gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = Bytes,
                Usage = BufferUsages.Readback,
                Name = "Gradient readback",
            }
        );
        if (!readback.Ok)
        {
            Log.Error($"GPU check: the readback buffer was not made: {readback.Error}");

            return;
        }

        _readback = readback.Value;

        // Record the gradient's dispatch and its copy into the buffer, and submit them.
        GpuCommandRecorder recorder = new();
        recorder.BeginComputePass("Fill gradient");
        recorder.Transition(_gradient, GpuAccess.None, GpuAccess.ShaderWrite);
        recorder.BindPipeline(_pipeline);
        recorder.PushConstants(new GpuConstants(_gradient.Id, Size, Size));
        recorder.Dispatch(
            (Size + entry.GroupSizeX - 1) / entry.GroupSizeX,
            (Size + entry.GroupSizeY - 1) / entry.GroupSizeY
        );
        recorder.EndPass();
        recorder.BeginTransferPass("Read gradient back");
        recorder.Transition(_gradient, GpuAccess.ShaderWrite, GpuAccess.CopySource);
        recorder.Transition(_readback, GpuAccess.None, GpuAccess.CopyDestination);
        recorder.CopyTextureToBuffer(_gradient, 0, 0, _readback, 0);
        recorder.Transition(_readback, GpuAccess.CopyDestination, GpuAccess.HostRead);
        recorder.EndPass();
        _gpu.Submit(recorder.Finish());

        // Read the gradient back once the GPU has computed it. It is red along x, green along y, a quarter blue and
        // opaque; each corner and the centre must read so.
        Result<ReadOnlyMemory<byte>> pixels = await _gpu.ReadBackAsync(_readback, 0, Bytes, CancellationToken.None)
            .ConfigureAwait(false);
        if (!pixels.Ok)
        {
            Log.Error($"GPU check: the gradient could not be read back: {pixels.Error}");

            return;
        }

        ReadOnlySpan<(int X, int Y)> probes =
        [
            (0, 0),
            (Size - 1, 0),
            (0, Size - 1),
            (Size - 1, Size - 1),
            (Size / 2, Size / 2),
        ];
        ReadOnlySpan<byte> bytes = pixels.Value.Span;
        int wrong = 0;
        foreach ((int x, int y) in probes)
        {
            int at = ((y * Size) + x) * 4;
            byte red = bytes[at];
            byte green = bytes[at + 1];
            byte blue = bytes[at + 2];
            byte alpha = bytes[at + 3];
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
}
