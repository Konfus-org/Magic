using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.SdlBindings;
using Magic.Services;

namespace Magic.SteamAudio;

/// <summary>
/// What the device plays: a fixed table of slots, each a sound being heard, and the callback SDL's audio thread runs to
/// fill the device a frame of samples at a time. Each slot pulls its sound through an SDL stream, which converts it to
/// the device's rate and to mono for a sound in the world or stereo for one everywhere, and plays it faster or slower
/// for its pitch; a sound in the world then has Steam Audio's direct effect, the distance, the air, its focus and what
/// is in the way, and is placed around the listener's head, and the nearest few are echoed through a reflection
/// effect each into one mixer, heard around the head once a frame. Everything is made as the gem starts and released
/// as it ends, so nothing is allocated while the device plays.
/// <para>
/// The main thread starts, seeks and frees a slot with commands, taken in at the start of each frame; what changes
/// continuously, the volume, the pitch, where it is heard from, it writes to the slot whole, where the audio thread
/// reads it each frame. The audio thread says back how far in each slot is, and when it let a slot go, with the
/// generation the main thread gave it, so a slot is never reused while the audio thread still reads it.
/// </para>
/// </summary>
internal sealed unsafe class Voices : IDisposable
{
    /// <summary>
    /// Sample frames of a sound put into its stream at a time, ahead of what is played.
    /// </summary>
    private const int Feed = 4096;

    private static readonly Histogram<double> _renderDuration = SteamAudioGem.Meter.CreateHistogram("magic.gems.steam_audio.render.duration", "s", "How long the audio thread took to fill the device once, every sound and echo mixed.", tags: null, advice: SteamAudioGem.Buckets);
    private static readonly Counter<int> _late = SteamAudioGem.Meter.CreateCounter<int>("magic.gems.steam_audio.render.late", "{fill}", "Fills of the device that took longer than the sound they made lasts, which the device hears as a gap.");

    private readonly IPL.Context* _context;
    private readonly IPL.HRTF* _hrtf;
    private readonly Slot[] _slots;
    private readonly Reflector[] _reflectors;
    private readonly IPL.ReflectionMixer* _mixer;
    private readonly IPL.AmbisonicsDecodeEffect* _decoder;
    private readonly Channel<VoiceCommand> _commands;
    private readonly int _sampleRate;
    private readonly int _frameSize;
    private readonly int _order;
    private readonly int _ambisonicChannels;
    private readonly int _irSize;

    /// <summary>
    /// The frame every sound is mixed into, interleaved left and right, as the device takes it.
    /// </summary>
    private readonly float[] _mixed;

    /// <summary>
    /// A sound everywhere's frame as its stream gives it, interleaved, before it is mixed in.
    /// </summary>
    private readonly float[] _stereo;
    private IPL.AudioBuffer _ambisonics;
    private IPL.AudioBuffer _decoded;

    /// <summary>
    /// The audio thread's own failure, logged once.
    /// </summary>
    private int _hasFailed;

    internal Voices(IPL.Context* context, IPL.HRTF* hrtf, IPL.Simulator* simulator, SteamAudioSettings settings)
    {
        _context = context;
        _hrtf = hrtf;
        _sampleRate = settings.SampleRate;
        _frameSize = settings.FrameSize;
        _order = settings.Order;
        _ambisonicChannels = (_order + 1) * (_order + 1);
        _irSize = (int)MathF.Ceiling(settings.ReverbSeconds * _sampleRate);
        _mixed = new float[_frameSize * 2];
        _stereo = new float[_frameSize * 2];
        _commands = Channel.CreateBounded<VoiceCommand>(new BoundedChannelOptions(settings.Voices * 4) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

        IPL.AudioSettings audio = new() { SamplingRate = _sampleRate, FrameSize = _frameSize };
        _slots = new Slot[settings.Voices];
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new Slot(context, hrtf, simulator, &audio);
        }

        IPL.ReflectionEffectSettings reflection = new() { Type = IPL.ReflectionEffectType.Convolution, IrSize = _irSize, NumChannels = _ambisonicChannels };
        _reflectors = new Reflector[settings.ReflectionSources];
        for (int i = 0; i < _reflectors.Length; i++)
        {
            _reflectors[i] = new Reflector(context, simulator, &audio, &reflection);
        }

        if (_reflectors.Length > 0)
        {
            IPL.ReflectionMixer* mixer;
            Check(IPL.ReflectionMixerCreate(context, &audio, &reflection, &mixer), "the reflection mixer");
            _mixer = mixer;
            IPL.AmbisonicsDecodeEffectSettings decode = new() { SpeakerLayout = new IPL.SpeakerLayout { Type = IPL.SpeakerLayoutType.Stereo }, Hrtf = hrtf, MaxOrder = _order };
            IPL.AmbisonicsDecodeEffect* decoder;
            Check(IPL.AmbisonicsDecodeEffectCreate(context, &audio, &decode, &decoder), "the echoes' decoder");
            _decoder = decoder;
            Check(Allocate(_ambisonicChannels, out _ambisonics), "the echoes' buffer");
            Check(Allocate(2, out _decoded), "the echoes' buffer");
        }
    }

    /// <summary>
    /// Releases every slot's and every reflector's effects and sources, then the mixer and the decoder. Once the device
    /// is closed and nothing simulates.
    /// </summary>
    public void Dispose()
    {
        foreach (Slot slot in _slots)
        {
            slot.Release(_context);
        }

        foreach (Reflector reflector in _reflectors)
        {
            reflector.Release();
        }

        if (_mixer is not null)
        {
            IPL.ReflectionMixer* mixer = _mixer;
            IPL.ReflectionMixerRelease(&mixer);
            IPL.AmbisonicsDecodeEffect* decoder = _decoder;
            IPL.AmbisonicsDecodeEffectRelease(&decoder);
            fixed (IPL.AudioBuffer* ambisonics = &_ambisonics)
            fixed (IPL.AudioBuffer* decoded = &_decoded)
            {
                IPL.AudioBufferFree(_context, ambisonics);
                IPL.AudioBufferFree(_context, decoded);
            }
        }
    }

    /// <summary>
    /// How loud everything is, as the settings say; written by the main thread each frame.
    /// </summary>
    internal float Volume
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>
    /// Whether echoes are heard; written by the main thread each frame.
    /// </summary>
    internal bool IsReflecting
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>
    /// Where the listener is and which way it faces, in Steam Audio's space, which the echoes are heard around; written
    /// by the main thread each frame, whole enough for sound that a frame read halfway hears no difference.
    /// </summary>
    internal IPL.CoordinateSpace3 Listener { get; set; }

    internal IReadOnlyList<Slot> Slots => _slots;

    internal IReadOnlyList<Reflector> Reflectors => _reflectors;

    /// <summary>
    /// Hands the audio thread <paramref name="command"/>, for the start of its next frame; false when it has not taken
    /// in enough of those before, and the main thread tries again next frame.
    /// </summary>
    internal bool TryTell(in VoiceCommand command)
    {
        return _commands.Writer.TryWrite(command);
    }

    /// <summary>
    /// SDL's audio thread asking for more: frames are made until it has what it asked for. Whatever goes wrong is
    /// logged once and the device hears silence, as an exception must not cross into SDL.
    /// </summary>
    [UnmanagedCallersOnly]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Nothing may be thrown back into SDL's audio thread; a failure is logged once and the device hears silence.")]
    internal static void OnAudio(void* self, SDL.AudioStream* device, int additional, int _)
    {
        if (GCHandle.FromIntPtr((nint)self).Target is not Voices voices) return;

        try
        {
            voices.Render(device, additional);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref voices._hasFailed, 1) == 0) Debugging.Error($"Sound stopped: the audio thread failed: {ex}");
        }
    }

    private static IPL.Error Allocate(IPL.Context* context, int channels, int frameSize, out IPL.AudioBuffer buffer)
    {
        IPL.AudioBuffer made;
        IPL.Error error = IPL.AudioBufferAllocate(context, channels, frameSize, &made);
        buffer = made;
        return error;
    }

    private static void Check(IPL.Error error, string what)
    {
        if (error != IPL.Error.Success) throw new InvalidOperationException($"Steam Audio could not make {what}: {error}.");
    }

    private IPL.Error Allocate(int channels, out IPL.AudioBuffer buffer)
    {
        return Allocate(_context, channels, _frameSize, out buffer);
    }

    /// <summary>
    /// Fills the device with frames until it has <paramref name="additional"/> bytes more, and measures how long that
    /// took against how long it plays.
    /// </summary>
    private void Render(SDL.AudioStream* device, int additional)
    {
        if (_hasFailed != 0 || additional <= 0) return;

        long start = Stopwatch.GetTimestamp();
        int bytes = _mixed.Length * sizeof(float);
        int frames = 0;
        while (additional > 0)
        {
            RenderFrame();
            fixed (float* mixed = _mixed)
            {
                SDL.PutAudioStreamData(device, mixed, bytes);
            }

            additional -= bytes;
            frames++;
        }

        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        _renderDuration.Record(seconds);
        if (seconds > (double)frames * _frameSize / _sampleRate) _late.Add(1);
    }

    /// <summary>
    /// One frame of every sound playing, and of their echoes, mixed and scaled by the volume.
    /// </summary>
    private void RenderFrame()
    {
        TakeCommands();
        Array.Clear(_mixed);
        bool isReflecting = IsReflecting && _mixer is not null;
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (!slot.IsActive) continue;

            if (slot.IsEverywhere)
            {
                MixEverywhere(slot);
            }
            else
            {
                MixWorld(slot, i, isReflecting);
            }

            if (slot.IsDone) slot.Let(hasEnded: true);
        }

        if (isReflecting) MixEchoes();

        float volume = Volume;
        Span<float> mixed = _mixed;
        for (int i = 0; i < mixed.Length; i++)
        {
            mixed[i] = Math.Clamp(mixed[i] * volume, -1, 1);
        }
    }

    private void TakeCommands()
    {
        while (_commands.Reader.TryRead(out VoiceCommand command))
        {
            Slot slot = _slots[command.Slot];
            switch (command.Action)
            {
                case VoiceAction.Start:
                    slot.Begin(command);
                    break;
                case VoiceAction.Seek:
                    if (slot.IsActive && slot.Generation == command.Generation) slot.SeekTo(command.Seconds);
                    break;
                case VoiceAction.Free:
                    if (slot.IsActive && slot.Generation == command.Generation) slot.Let(hasEnded: false);
                    break;
                default:
                    break;
            }
        }
    }

    private void MixEverywhere(Slot slot)
    {
        fixed (float* stereo = _stereo)
        {
            slot.Pull(stereo, _frameSize, 2, _sampleRate);
        }

        float gain = slot.Gain;
        Span<float> mixed = _mixed;
        for (int i = 0; i < mixed.Length; i++)
        {
            mixed[i] += _stereo[i] * gain;
        }
    }

    /// <summary>
    /// A sound in the world: scaled by its volume and fade, through the direct effect Steam Audio worked out for it,
    /// placed around the head, and into the mixer through its reflector's echo when it has one.
    /// </summary>
    private void MixWorld(Slot slot, int index, bool isReflecting)
    {
        float* input = slot.Input.Data[0];
        slot.Pull(input, _frameSize, 1, _sampleRate);
        float gain = slot.Gain;
        for (int i = 0; i < _frameSize; i++)
        {
            input[i] *= gain;
        }

        IPL.SimulationOutputs outputs;
        IPL.SourceGetOutputs(slot.Source, IPL.SimulationFlags.Direct, &outputs);
        IPL.DirectEffectParams direct = outputs.Direct;
        direct.Flags = slot.Flags;
        direct.TransmissionType = IPL.TransmissionType.Freqdependent;
        IPL.BinauralEffectParams binaural = new() { Direction = slot.Direction, Interpolation = IPL.HRTFInterpolation.Bilinear, SpatialBlend = 1, Hrtf = _hrtf };
        fixed (IPL.AudioBuffer* buffers = slot.Buffers)
        {
            IPL.DirectEffectApply(slot.Direct, &direct, &buffers[0], &buffers[1]);
            IPL.BinauralEffectApply(slot.Binaural, &binaural, &buffers[1], &buffers[2]);
            float* left = buffers[2].Data[0];
            float* right = buffers[2].Data[1];
            for (int i = 0; i < _frameSize; i++)
            {
                _mixed[i * 2] += left[i];
                _mixed[(i * 2) + 1] += right[i];
            }

            int at = slot.Reflector;
            if (!isReflecting || at < 0 || at >= _reflectors.Length) return;

            Reflector reflector = _reflectors[at];
            if (reflector.Slot != index)
            {
                IPL.ReflectionEffectReset(reflector.Effect);
                reflector.Slot = index;
            }

            IPL.SourceGetOutputs(reflector.Source, IPL.SimulationFlags.Reflections, &outputs);
            IPL.ReflectionEffectParams echo = outputs.Reflections;
            if (echo.Ir is null) return;

            echo.Type = IPL.ReflectionEffectType.Convolution;
            echo.NumChannels = _ambisonicChannels;
            echo.IrSize = _irSize;
            fixed (IPL.AudioBuffer* ambisonics = &_ambisonics)
            {
                IPL.ReflectionEffectApply(reflector.Effect, &echo, &buffers[0], ambisonics, _mixer);
            }
        }
    }

    /// <summary>
    /// Every echo of the frame, mixed by the reflection effects, heard around the listener's head.
    /// </summary>
    private void MixEchoes()
    {
        IPL.ReflectionEffectParams mix = new() { Type = IPL.ReflectionEffectType.Convolution, NumChannels = _ambisonicChannels, IrSize = _irSize };
        IPL.AmbisonicsDecodeEffectParams decode = new() { Order = _order, Hrtf = _hrtf, Orientation = Listener, Binaural = IPL.Bool.True };
        fixed (IPL.AudioBuffer* ambisonics = &_ambisonics)
        fixed (IPL.AudioBuffer* decoded = &_decoded)
        {
            IPL.ReflectionMixerApply(_mixer, &mix, ambisonics);
            IPL.AmbisonicsDecodeEffectApply(_decoder, &decode, ambisonics, decoded);
            float* left = decoded->Data[0];
            float* right = decoded->Data[1];
            for (int i = 0; i < _frameSize; i++)
            {
                _mixed[i * 2] += left[i];
                _mixed[(i * 2) + 1] += right[i];
            }
        }
    }

    /// <summary>
    /// One sound being heard: its stream and where it is in its sound, which the audio thread alone touches between a
    /// start and a let-go; what the main thread writes each frame; what the audio thread says back; and its Steam Audio
    /// source and effects, made once for the run.
    /// </summary>
    internal sealed class Slot
    {
        private SDL.AudioStream* _stream;
        private float* _samples;
        private int _count;
        private int _channels;
        private int _rate;
        private int _fed;
        private bool _isFlushed;
        private double _duration;
        private float _appliedPitch;
        private int _flags;

        internal Slot(IPL.Context* context, IPL.HRTF* hrtf, IPL.Simulator* simulator, IPL.AudioSettings* audio)
        {
            IPL.SourceSettings source = new() { Flags = IPL.SimulationFlags.Direct };
            IPL.Source* made;
            Check(IPL.SourceCreate(simulator, &source, &made), "a sound's source");
            Source = made;
            IPL.SourceAdd(made, simulator);
            IPL.DirectEffectSettings direct = new() { NumChannels = 1 };
            IPL.DirectEffect* directEffect;
            Check(IPL.DirectEffectCreate(context, audio, &direct, &directEffect), "a sound's direct effect");
            Direct = directEffect;
            IPL.BinauralEffectSettings binaural = new() { Hrtf = hrtf };
            IPL.BinauralEffect* binauralEffect;
            Check(IPL.BinauralEffectCreate(context, audio, &binaural, &binauralEffect), "a sound's binaural effect");
            Binaural = binauralEffect;
            Buffers = new IPL.AudioBuffer[3];
            Check(Allocate(context, 1, audio->FrameSize, out Buffers[0]), "a sound's buffer");
            Check(Allocate(context, 1, audio->FrameSize, out Buffers[1]), "a sound's buffer");
            Check(Allocate(context, 2, audio->FrameSize, out Buffers[2]), "a sound's buffer");
        }

        internal IPL.Source* Source { get; }

        internal IPL.DirectEffect* Direct { get; }

        internal IPL.BinauralEffect* Binaural { get; }

        /// <summary>
        /// The frame's sound as it comes, mono; after the direct effect, mono; and placed around the head, stereo.
        /// </summary>
        internal IPL.AudioBuffer[] Buffers { get; }

        internal IPL.AudioBuffer Input => Buffers[0];

        /// <summary>
        /// Which start this is: the main thread's count for the slot, which the audio thread's word back names.
        /// </summary>
        internal int Generation { get; private set; }

        /// <summary>
        /// Playing: between a start and a let-go. Audio thread.
        /// </summary>
        internal bool IsActive { get; private set; }

        internal bool IsEverywhere { get; private set; }

        /// <summary>
        /// Its sound has run out, and every sample it had was played. Audio thread.
        /// </summary>
        internal bool IsDone { get; private set; }

        /// <summary>
        /// Its volume times its fade at the edge of its range; written by the main thread.
        /// </summary>
        internal float Gain
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        /// <summary>
        /// How fast it plays; written by the main thread.
        /// </summary>
        internal float Pitch
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        /// <summary>
        /// It plays again from the start when it ends; written by the main thread.
        /// </summary>
        internal bool Loop
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        /// <summary>
        /// What of the direct effect it hears; written by the main thread.
        /// </summary>
        internal IPL.DirectEffectFlags Flags
        {
            get => (IPL.DirectEffectFlags)Volatile.Read(ref _flags);
            set => Volatile.Write(ref _flags, (int)value);
        }

        /// <summary>
        /// Which way it is from the listener's head, in the head's own space; written by the main thread.
        /// </summary>
        internal IPL.Vector3 Direction { get; set; } = new() { Z = -1 };

        /// <summary>
        /// The reflector whose echo it is heard through, or -1; written by the main thread.
        /// </summary>
        internal int Reflector
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        } = -1;

        /// <summary>
        /// Seconds into its sound as last played; written by the audio thread.
        /// </summary>
        internal double Seconds
        {
            get => Volatile.Read(ref field);
            private set => Volatile.Write(ref field, value);
        }

        /// <summary>
        /// The generation the audio thread last started; written by it once it has.
        /// </summary>
        internal int Started
        {
            get => Volatile.Read(ref field);
            private set => Volatile.Write(ref field, value);
        }

        /// <summary>
        /// The generation the audio thread last let go of, after which it touches nothing of it; written by it last.
        /// </summary>
        internal int Released
        {
            get => Volatile.Read(ref field);
            private set => Volatile.Write(ref field, value);
        }

        /// <summary>
        /// The generation let go of ended of itself, rather than being freed.
        /// </summary>
        internal bool HasEnded
        {
            get => Volatile.Read(ref field);
            private set => Volatile.Write(ref field, value);
        }

        /// <summary>
        /// Takes the started sound's stream and samples, from <paramref name="command"/>'s place in it. Audio thread.
        /// </summary>
        internal void Begin(in VoiceCommand command)
        {
            _stream = (SDL.AudioStream*)command.Stream;
            _samples = (float*)command.Samples;
            _count = command.Count;
            _channels = command.Channels;
            _rate = command.Rate;
            _duration = command.Duration;
            _appliedPitch = 0;
            Generation = command.Generation;
            IsEverywhere = command.IsEverywhere;
            IsActive = true;
            IsDone = false;
            IPL.DirectEffectReset(Direct);
            IPL.BinauralEffectReset(Binaural);
            SeekTo(command.Seconds);
            Started = command.Generation;
        }

        /// <summary>
        /// Plays on from <paramref name="seconds"/> in, dropping what the stream held. Audio thread.
        /// </summary>
        internal void SeekTo(double seconds)
        {
            SDL.ClearAudioStream(_stream);
            long frame = (long)(Math.Clamp(seconds, 0, _duration) * _rate);
            _fed = (int)Math.Min(frame * _channels, _count);
            _isFlushed = false;
            IsDone = false;
            Seconds = seconds;
        }

        /// <summary>
        /// One frame of the sound into <paramref name="into"/>, <paramref name="channels"/> interleaved, at the device's
        /// rate and the slot's pitch, the rest silence once it has run out. Audio thread.
        /// </summary>
        internal void Pull(float* into, int frameSize, int channels, int sampleRate)
        {
            float pitch = Math.Clamp(Pitch, 0.5f, 2);
            if (pitch != _appliedPitch)
            {
                SDL.SetAudioStreamFrequencyRatio(_stream, pitch);
                _appliedPitch = pitch;
            }

            int want = frameSize * channels * sizeof(float);
            while (!_isFlushed && SDL.GetAudioStreamAvailable(_stream) < want)
            {
                FeedMore();
            }

            int got = Math.Max(0, SDL.GetAudioStreamData(_stream, into, want));
            new Span<float>(into + (got / sizeof(float)), (want - got) / sizeof(float)).Clear();
            double seconds = Seconds + ((double)got / (channels * sizeof(float)) * pitch / sampleRate);
            Seconds = Loop && _duration > 0 ? seconds % _duration : Math.Min(seconds, _duration);
            IsDone = _isFlushed && got < want;
        }

        /// <summary>
        /// Stops playing and says so, with whether it ended of itself: from here the audio thread touches nothing of
        /// this start, and the main thread may free its stream. Audio thread.
        /// </summary>
        internal void Let(bool hasEnded)
        {
            IsActive = false;
            _stream = null;
            _samples = null;
            HasEnded = hasEnded;
            Released = Generation;
        }

        internal void Release(IPL.Context* context)
        {
            IPL.Source* source = Source;
            IPL.SourceRelease(&source);
            IPL.DirectEffect* direct = Direct;
            IPL.DirectEffectRelease(&direct);
            IPL.BinauralEffect* binaural = Binaural;
            IPL.BinauralEffectRelease(&binaural);
            fixed (IPL.AudioBuffer* buffers = Buffers)
            {
                for (int i = 0; i < Buffers.Length; i++)
                {
                    IPL.AudioBufferFree(context, &buffers[i]);
                }
            }
        }

        /// <summary>
        /// Puts the next stretch of the sound into its stream; at its end, wraps to its start when it loops, or flushes
        /// the stream, so the last of it is played, when it does not.
        /// </summary>
        private void FeedMore()
        {
            int chunk = Math.Min(Feed * _channels, _count - _fed);
            if (chunk > 0)
            {
                SDL.PutAudioStreamData(_stream, _samples + _fed, chunk * sizeof(float));
                _fed += chunk;
            }

            if (_fed < _count) return;

            if (Loop && _count > 0)
            {
                _fed = 0;
                return;
            }

            SDL.FlushAudioStream(_stream);
            _isFlushed = true;
        }
    }

    /// <summary>
    /// One of the few sounds in the world that echo: a Steam Audio source the trace works out the echo at, put where
    /// the sound it is given is, and the effect that plays the echo. Made once for the run, and given to the nearest
    /// sounds as they change.
    /// </summary>
    internal sealed class Reflector
    {
        internal Reflector(IPL.Context* context, IPL.Simulator* simulator, IPL.AudioSettings* audio, IPL.ReflectionEffectSettings* reflection)
        {
            IPL.SourceSettings source = new() { Flags = IPL.SimulationFlags.Reflections };
            IPL.Source* made;
            Check(IPL.SourceCreate(simulator, &source, &made), "an echo's source");
            Source = made;
            IPL.SourceAdd(made, simulator);
            IPL.ReflectionEffect* effect;
            Check(IPL.ReflectionEffectCreate(context, audio, reflection, &effect), "an echo's effect");
            Effect = effect;
        }

        internal IPL.Source* Source { get; }

        internal IPL.ReflectionEffect* Effect { get; }

        /// <summary>
        /// The slot whose sound the effect last echoed, so an effect given to another sound starts clean. Audio thread.
        /// </summary>
        internal int Slot { get; set; } = -1;

        internal void Release()
        {
            IPL.Source* source = Source;
            IPL.SourceRelease(&source);
            IPL.ReflectionEffect* effect = Effect;
            IPL.ReflectionEffectRelease(&effect);
        }
    }
}

/// <summary>
/// What the main thread tells a slot.
/// </summary>
internal enum VoiceAction : byte
{
    /// <summary>
    /// Play a sound, from a place in it.
    /// </summary>
    Start,

    /// <summary>
    /// Play on from another place.
    /// </summary>
    Seek,

    /// <summary>
    /// Stop, and let the slot go.
    /// </summary>
    Free,
}

/// <summary>
/// One thing the main thread tells a slot, for the audio thread to take in at the start of its next frame: which slot,
/// which start of it, and for a start the stream and the pinned samples it plays, which the main thread keeps until the
/// audio thread lets the start go.
/// </summary>
internal readonly struct VoiceCommand
{
    internal VoiceAction Action { get; init; }

    internal int Slot { get; init; }

    internal int Generation { get; init; }

    /// <summary>
    /// Seconds into the sound to play from.
    /// </summary>
    internal double Seconds { get; init; }

    internal nint Stream { get; init; }

    internal nint Samples { get; init; }

    /// <summary>
    /// How many samples there are, every channel's.
    /// </summary>
    internal int Count { get; init; }

    internal int Channels { get; init; }

    internal int Rate { get; init; }

    internal double Duration { get; init; }

    internal bool IsEverywhere { get; init; }
}
