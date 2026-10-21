using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.SdlBindings;

namespace Magic.SdlAudioLoader;

/// <summary>
/// Reads audio files into <see cref="Audio"/>s with SDL_mixer's decoders: every sample of the file as a float, at the
/// file's own rate and channels. A decoder may be used on any thread, so files decode in parallel; SDL_mixer itself
/// starts and stops alone, so a file still decoding as the gem ends is finished first.
/// </summary>
internal sealed unsafe class SdlAudioLoaderGem : IGem, IAssetLoader<Audio>, IDisposable
{
    /// <summary>
    /// Bytes decoded at a time, a fraction of a second of most files.
    /// </summary>
    private const int Chunk = 64 * 1024;

    /// <summary>
    /// Read while a file decodes, by as many workers as there are; written while SDL_mixer stops.
    /// </summary>
    private readonly ReaderWriterLockSlim _decoding = new();

    /// <summary>
    /// SDL_mixer has stopped: nothing more decodes.
    /// </summary>
    private bool _isStopped;

    public SdlAudioLoaderGem()
    {
        // SDL itself first, so SDL_mixer binds to the SDL every gem shares.
        _ = SDL.GetVersion();
        if (MIX
            .Init())
        {
            return;
        }

        throw new InvalidOperationException($"SDL_mixer could not start: {CString.Read(SDL.GetError())}");
    }

    /// <summary>
    /// Stops SDL_mixer once the files decoding on workers are done; none decodes after.
    /// </summary>
    public void Dispose()
    {
        _decoding.EnterWriteLock();
        try
        {
            _isStopped = true;
            MIX.Quit();
        }
        finally
        {
            _decoding.ExitWriteLock();
        }

        _decoding.Dispose();
    }

    public ValueTask<Result<Audio>> LoadAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        JsonElement settings,
        CancellationToken cancel)
    {
        _decoding.EnterReadLock();
        try
        {
            return new(
                _isStopped ? Result.Failure<Audio>("the audio loader has stopped.") : Decode(bytes.Span, cancel));
        }
        finally
        {
            _decoding.ExitReadLock();
        }
    }

    /// <summary>
    /// Every sample of the file in <paramref name="file"/>, as floats at its own rate and channels. Under a read of
    /// <see cref="_decoding"/>.
    /// </summary>
    private static Result<Audio> Decode(ReadOnlySpan<byte> file, CancellationToken cancel)
    {
        // The decoder reads the file for as long as it is open, so the file stays pinned until then.
        fixed (byte* bytes = file)
        {
            SDL.IOStream* io = SDL.IOFromConstMem(bytes, (ulong)file.Length);
            MIX.AudioDecoder* decoder = io == null ? null : MIX.CreateAudioDecoderIo(io, closeio: true, props: 0);
            if (decoder == null)
            {
                return Result.Failure<Audio>(
                $"SDL_mixer could not read it: {CString.Read(SDL.GetError())}");
            }

            try
            {
                SDL.AudioSpec spec;
                if (!MIX.GetAudioDecoderFormat(decoder, &spec))
                {
                    return Result.Failure<Audio>(
                    $"SDL_mixer could not tell its format: {CString.Read(SDL.GetError())}");
                }

                if (spec.Channels <= 0 || spec.Freq <= 0)
                {
                    return Result.Failure<Audio>(
                    $"it says it has {spec.Channels} channel(s) at {spec.Freq} Hz.");
                }

                spec.Format = SDL.AudioFormat.F32;
                float[] samples = new float[Chunk / sizeof(float)];
                int count = 0;
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (samples.Length - count < Chunk / sizeof(float)) Array.Resize(ref samples, samples.Length * 2);

                    int read;
                    fixed (float* into = &samples[count])
                    {
                        read = MIX.DecodeAudio(decoder, into, Chunk, &spec);
                    }

                    if (read < 0)
                    {
                        return Result.Failure<Audio>(
                        $"SDL_mixer could not decode it: {CString.Read(SDL.GetError())}");
                    }

                    if (read == 0) break;

                    count += read / sizeof(float);
                }

                // A frame left half written by a file cut short is dropped, so every frame has every channel.
                count -= count % spec.Channels;
                return Result.Success(new Audio
                {
                    SampleRate = spec.Freq,
                    Channels = spec.Channels,
                    Samples = samples.AsMemory(0, count),
                });
            }
            finally
            {
                MIX.DestroyAudioDecoder(decoder);
            }
        }
    }
}
