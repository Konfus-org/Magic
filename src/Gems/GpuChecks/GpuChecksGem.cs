using Magic.Assets;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Projects;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GpuChecks;

/// <summary>
/// Proves the GPU provider and the shader compiler work together, before any renderer draws: as the run starts, a
/// compute entry the provider builds fills a texture with a gradient through the heap, the texture is read back and its
/// corners checked, and each is an error under --fail-on-error when wrong. The readback waits on a worker; every GPU
/// call is made on the render thread, and the dispatch is recorded in <see cref="Render"/> once the entry is built. It is public because nothing in its assembly makes it: Magic
/// does, by reflection, when a project lists the gem.
/// </summary>
public sealed class GpuChecksGem : IGem, IDisposable
{
    private const int Size = 256;
    private const int Bytes = Size * Size * 4;
    private const string ShaderPath = "Shaders/Gradient.slang";

    private readonly IGraphicsApi _gpu;
    private readonly Task _check;

    /// <summary>
    /// Set once <see cref="Render"/> has recorded the dispatch, which the check then reads back, or found the entry
    /// failed to build, when there is nothing to read.
    /// </summary>
    private readonly TaskCompletionSource<bool> _recorded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GpuTexture _gradient;
    private GpuBuffer _readback;

    /// <summary>
    /// The entry to dispatch, from the check once what it writes is made until <see cref="Render"/> records it. Render
    /// thread.
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

        _gpu.Destroy(_gradient);
        _gpu.Destroy(_readback);
    }

    /// <summary>
    /// The gradient's dispatch and its copy into the readback buffer, once the check has made what they use and the
    /// GPU provider has built the entry.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph)
    {
        if (_fill is not { } fill)
            return;

        GpuState state = graph.GetState(fill);
        if (state == GpuState.Loading)
            return;

        _fill = null;
        if (state == GpuState.Failed)
        {
            _recorded.SetResult(false);

            return;
        }

        graph
            .Compute("Fill gradient", fill, new GpuConstants(_gradient.Id, Size, Size))
            .Writes(_gradient)
            .DispatchOver(Size, Size);
        graph.BeginTransferPass("Read gradient back");
        graph.Use(_gradient, GpuAccess.CopySource);
        graph.Use(_readback, GpuAccess.CopyDestination);
        graph.CopyTextureToBuffer(_gradient, 0, 0, _readback, 0);
        graph.EndPass();
        graph.BeginTransferPass("Gradient to the host");
        graph.Use(_readback, GpuAccess.HostRead);
        graph.EndPass();
        _recorded.SetResult(true);
    }

    private async Task CheckAsync(AssetManager assets, Threads threads)
    {
        Handle<Shader> shader = assets.Find<Shader>(ShaderPath);
        if (shader == Handle<Shader>.None)
        {
            Log.Error($"GPU check: there is no {ShaderPath}.");

            return;
        }

        // Make the texture and the readback buffer on the render thread, whose the GPU is.
        await threads.Enter(EngineThread.Render);
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

        // Have Render record the dispatch and its copy into the buffer.
        _fill = new ShaderEntry
        {
            Module = shader,
            Name = "FillGradient",
            Stage = ShaderStage.Compute,
        };
        if (!await _recorded.Task.ConfigureAwait(false))
        {
            Log.Error("GPU check: the gradient shader was not built, so nothing was checked.");

            return;
        }

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
