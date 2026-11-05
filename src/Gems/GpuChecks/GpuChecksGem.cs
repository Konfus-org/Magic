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
/// Proves the GPU provider and the shader compiler work together, before any renderer draws: from the first frame, a
/// compute entry is compiled, fills a texture with a gradient through the heap, the texture is read back and its
/// corners checked, and each is an error under --fail-on-error when wrong. Every GPU call is made in the frame, from
/// <see cref="Render"/>; the compile and the readback wait on workers. It is public because nothing in its assembly
/// makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class GpuChecksGem(IGraphicsApi gpu, IShaderCompiler shaders, Assets assets) : IGem, IDisposable
{
    private const int Size = 256;
    private const string ShaderPath = "Shaders/Gradient.slang";

    private readonly GpuCommandRecorder _recorder = new();
    private GpuPipeline _pipeline;
    private GpuTexture _gradient;
    private GpuBuffer _readback;
    private uint _groupsX;
    private uint _groupsY;
    private bool _isStarted;
    private bool _isRecorded;
    private bool _isChecked;

    /// <summary>
    /// What the entry compiled into, written whole by the compile on its worker and taken by <see cref="Render"/>.
    /// </summary>
    private CompiledEntry? _compiled;

    /// <summary>
    /// A run that ends before the check has finished checked nothing, which is an error: a run that validates waits
    /// for it.
    /// </summary>
    public void Dispose()
    {
        if (_isStarted && !_isChecked)
            Log.Error("GPU check: the run ended before the check finished, so nothing was checked.");

        if (!_pipeline.IsNone)
            gpu.Destroy(_pipeline);

        if (!_gradient.IsNone)
            gpu.Destroy(_gradient);

        if (!_readback.IsNone)
            gpu.Destroy(_readback);
    }

    /// <summary>
    /// Starts compiling the entry on the first frame, once.
    /// </summary>
    public void Update(in Frame frame)
    {
        if (_isStarted)
            return;

        _isStarted = true;
        Handle<Shader> shader = assets.Find<Shader>(ShaderPath);
        if (shader == Handle<Shader>.None)
        {
            Log.Error($"GPU check: there is no {ShaderPath}.");
            _isChecked = true;

            return;
        }

        CompileAsync(shader).FireAndForget();
    }

    /// <summary>
    /// Once the entry has compiled: makes the texture, computes the gradient and submits it, then reads it back and
    /// checks it on a worker.
    /// </summary>
    public void Render(in Frame frame)
    {
        if (_isRecorded || Interlocked.Exchange(ref _compiled, null) is not { } compiled)
            return;

        _isRecorded = true;
        if (!compiled.Result.Ok)
        {
            Log.Error($"GPU check: the gradient shader did not compile: {compiled.Result.Error}");
            _isChecked = true;

            return;
        }

        CompiledShader entry = compiled.Result.Value;
        if (entry.GroupSizeX == 0 || entry.GroupSizeY == 0)
        {
            Log.Error($"GPU check: the compiler reported a group size of {entry.GroupSizeX} by {entry.GroupSizeY}.");
            _isChecked = true;

            return;
        }

        _groupsX = (Size + entry.GroupSizeX - 1) / entry.GroupSizeX;
        _groupsY = (Size + entry.GroupSizeY - 1) / entry.GroupSizeY;
        if (!TryCreateResources(entry))
        {
            _isChecked = true;

            return;
        }

        RecordGradient();
        _recorder.BeginTransferPass("Read gradient back");
        _recorder.Transition(_gradient, GpuAccess.ShaderWrite, GpuAccess.CopySource);
        _recorder.Transition(_readback, GpuAccess.None, GpuAccess.CopyDestination);
        _recorder.CopyTextureToBuffer(_gradient, 0, 0, _readback, 0);
        _recorder.Transition(_readback, GpuAccess.CopyDestination, GpuAccess.HostRead);
        _recorder.EndPass();
        gpu.Submit(_recorder.Finish());
        ReadBackAsync().FireAndForget();
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

    private async Task CompileAsync(Handle<Shader> shader)
    {
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
        Interlocked.Exchange(ref _compiled, new CompiledEntry(compiled));
    }

    /// <summary>
    /// Reads the gradient back once the GPU has computed it, and checks it. Checked once it has reported, right or
    /// wrong.
    /// </summary>
    private async Task ReadBackAsync()
    {
        try
        {
            Result<ReadOnlyMemory<byte>> pixels = await gpu.ReadBackAsync(
                    _readback,
                    0,
                    Size * Size * 4,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            if (!pixels.Ok)
            {
                Log.Error($"GPU check: the gradient could not be read back: {pixels.Error}");

                return;
            }

            Check(pixels.Value.Span);
        }
        finally
        {
            _isChecked = true;
        }
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

    /// <summary>
    /// What the entry compiled into, boxed so it is written whole from the compile's worker.
    /// </summary>
    private sealed class CompiledEntry(Result<CompiledShader> result)
    {
        public Result<CompiledShader> Result { get; } = result;
    }
}
