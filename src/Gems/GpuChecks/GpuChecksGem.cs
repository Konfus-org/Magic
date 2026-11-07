using Magic.Assets;
using Magic.Debugging;
using Magic.Projects;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GpuChecks;

/// <summary>
/// Proves the GPU provider and the shader compiler work together, before any renderer draws: as the run starts, a
/// compute entry the provider builds fills a texture with a gradient through the heap, the texture is read back and its
/// corners checked, and each is an error under --fail-on-error when wrong. The readback waits on a worker; every GPU
/// call is made on the render thread, where <see cref="Render"/> asks for the texture and the readback buffer every
/// frame while the check runs and records the dispatch once the entry is built. It is public because nothing in its
/// assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class GpuChecksGem : IGem, IDisposable
{
    private const int Size = 256;
    private const int Bytes = Size * Size * 4;
    private const string ShaderPath = "Shaders/Gradient.slang";

    private static readonly TextureDescription _gradient = new()
    {
        Width = Size,
        Height = Size,
        Format = GpuFormat.R8G8B8A8Unorm,
        Usage = TextureUsages.Storage | TextureUsages.CopySource,
        Name = "Gradient",
    };

    private static readonly BufferDescription _readback = new()
    {
        Bytes = Bytes,
        Usage = BufferUsages.Readback,
        Name = "Gradient readback",
    };

    private readonly IGraphicsApi _gpu;
    private readonly Task _check;

    /// <summary>
    /// Set once <see cref="Render"/> has recorded the dispatch, to the buffer the check then reads back, or found the
    /// entry failed to build or what it writes not made, when there is nothing to read.
    /// </summary>
    private readonly TaskCompletionSource<Result<GpuBuffer>> _recorded = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    /// <summary>
    /// The entry to dispatch, from the check until <see cref="Render"/> records it. Render thread.
    /// </summary>
    private ShaderEntry? _fill;

    public GpuChecksGem(IGraphicsApi gpu, AssetManager assets, Threads threads)
    {
        _gpu = gpu;
        _check = CheckAsync(assets, threads);
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
    }

    /// <summary>
    /// The gradient and its readback buffer asked for while the check runs, so they last until it has read back, and
    /// the gradient's dispatch and its copy into the buffer once the check asks and the GPU provider has built the entry.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph)
    {
        if (_check.IsCompleted)
            return;

        Result<GpuTexture> gradient = graph.Texture(_gradient);
        Result<GpuBuffer> readback = graph.Buffer(_readback);
        if (_fill is not { } fill)
            return;

        GpuState state = graph.GetState(fill);
        Result made =
            !gradient.Ok ? gradient
            : !readback.Ok ? readback
            : state == GpuState.Failed ? Result.Failure("the gradient shader was not built")
            : Result.Success();
        if (made.Ok && state == GpuState.Loading)
            return;

        _fill = null;
        if (!made.Ok)
        {
            _recorded.SetResult(Result.Failure(made.Error));

            return;
        }

        graph
            .Compute("Fill gradient", fill, new GpuConstants(gradient.Value.Id, Size, Size))
            .Writes(gradient.Value)
            .DispatchOver(Size, Size);
        graph.ReadBack(gradient.Value, readback.Value);
        _recorded.SetResult(readback);
    }

    private async Task CheckAsync(AssetManager assets, Threads threads)
    {
        Handle<Shader> shader = assets.Find<Shader>(ShaderPath);
        if (shader == Handle<Shader>.None)
        {
            Log.Error($"GPU check: there is no {ShaderPath}.");

            return;
        }

        // Have Render, on the render thread, whose the GPU is, record the dispatch and its copy into the buffer.
        await threads.Enter(EngineThread.Render);
        _fill = new ShaderEntry
        {
            Module = shader,
            Name = "FillGradient",
            Stage = ShaderStage.Compute,
        };
        Result<GpuBuffer> readback = await _recorded.Task.ConfigureAwait(false);
        if (!readback.Ok)
        {
            Log.Error($"GPU check: {readback.Error}, so nothing was checked.");

            return;
        }

        // Read the gradient back once the GPU has computed it. It is red along x, green along y, a quarter blue and
        // opaque; each corner and the centre must read so.
        Result<ReadOnlyMemory<byte>> pixels = await _gpu.ReadBackAsync(readback.Value, 0, Bytes, CancellationToken.None)
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
