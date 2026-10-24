using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.SdlBindings;
using Magic.Services;
using static Magic.SdlBindings.SDL_AudioFormat;
using static Magic.SdlBindings.SDL3;
using static Magic.SteamAudio.IPL;
using static Magic.SteamAudio.IPLAirAbsorptionModelType;
using static Magic.SteamAudio.IPLDirectEffectFlags;
using static Magic.SteamAudio.IPLDirectSimulationFlags;
using static Magic.SteamAudio.IPLDistanceAttenuationModelType;
using static Magic.SteamAudio.IPLerror;
using static Magic.SteamAudio.IPLHRTFNormType;
using static Magic.SteamAudio.IPLHRTFType;
using static Magic.SteamAudio.IPLLogLevel;
using static Magic.SteamAudio.IPLOcclusionType;
using static Magic.SteamAudio.IPLReflectionEffectType;
using static Magic.SteamAudio.IPLSceneType;
using static Magic.SteamAudio.IPLSIMDLevel;
using static Magic.SteamAudio.IPLSimulationFlags;

namespace Magic.SteamAudio;

/// <summary>
/// What hears the sounds, made only once the audio device opens (<see cref="TryOpen"/>): Steam Audio's context, the
/// head the listener hears with, the scene and the simulator; the mixer SDL's audio thread plays
/// (<see cref="Mixer"/>), whose slots the nearest voices are heard in and whose reflectors echo the nearest few; and
/// the simulation thread. Each frame, once the gem has followed the sounds, it hands the hearing where the listener is,
/// the voices that want to be heard and the solids it gathered (<see cref="HandOver"/>).
/// <para>
/// Steam Audio's scene and simulator are the simulation thread's own: the main thread gathers, each frame, where the
/// listener and each heard sound are and what is solid near the player (<see cref="Surroundings"/>), and hands it over
/// whole; the simulation thread brings the acoustic scene up to date with it (<see cref="SoundWorld"/>), works out what
/// reaches the listener straight from each sound, and, a few times a second, traces the echoes of the nearest few. So
/// nothing of Steam Audio's runs on the main thread, and neither thread waits on the other.
/// </para>
/// </summary>
internal sealed unsafe class Hearing : IDisposable
{
    /// <summary>
    /// The share of a sound's range over which it fades out, at its far end, as Steam Audio's own fall-off never
    /// reaches silence.
    /// </summary>
    private const float FadeShare = 0.2f;

    /// <summary>
    /// How far a sound at the listener's head is taken to be: one closer has no side to be heard from.
    /// </summary>
    private const float ClosestDistance = 0.001f;

    /// <summary>
    /// Occlusion rays cast from the listener toward the sphere around each sound, the most a sound is traced with.
    /// </summary>
    private const int OcclusionSamples = 16;

    /// <summary>
    /// How long one update of the acoustic scene may spend placing new solids, before the direct sound is worked out:
    /// the rest come in on the updates after, nearest first.
    /// </summary>
    private const double PlacingSec = 0.008;

    private static readonly Comparison<Voice> _byNearness = CompareNearness;
    private static readonly Gauge<int> _heard = SteamAudioGem.Meter.CreateGauge<int>(
        "magic.gems.steam_audio.voices",
        "{sound}",
        "Sounds heard this frame, of those playing.");
    private static readonly Gauge<int> _sceneSolids = SteamAudioGem.Meter.CreateGauge<int>(
        "magic.gems.steam_audio.scene.solids",
        "{solid}",
        "Solids in the acoustic scene, which sound is muffled and echoed by.");
    private static readonly Histogram<double> _simulateDuration = SteamAudioGem.Meter.CreateTimer(
        "magic.gems.steam_audio.simulate.duration",
        "How long the simulation thread took to bring the scene up to date and work out the direct sound.",
        SteamAudioGem.Buckets);
    private static readonly Histogram<double> _traceDuration = SteamAudioGem.Meter.CreateTimer(
        "magic.gems.steam_audio.trace.duration",
        "How long one trace of the echoes took, on the simulation thread.",
        SteamAudioGem.Buckets);

    private readonly SteamAudioSettings _settings;

    /// <summary>
    /// What the gem does with an audio file the device cannot play: as with one that did not load, the sounds that
    /// play it are silent.
    /// </summary>
    private readonly Action<Handle<Audio>> _markUnplayable;

    /// <summary>
    /// Set by the main thread when it has handed over a frame, or as the run ends, waking the simulation thread.
    /// </summary>
    private readonly ManualResetEventSlim _wake = new();

    /// <summary>
    /// Set by the simulation thread as it returns, which Stop waits for.
    /// </summary>
    private readonly ManualResetEventSlim _simulationEnded = new();
    private readonly _IPLContext_t* _context;
    private readonly _IPLHRTF_t* _hrtf;
    private readonly _IPLEmbreeDevice_t* _embree;
    private readonly _IPLScene_t* _scene;
    private readonly _IPLSimulator_t* _simulator;
    private readonly Mixer _mixer;
    private readonly SoundWorld _soundWorld;

    /// <summary>
    /// The device the mixer plays into; null when it did not open, and the hearing is let go at once
    /// (<see cref="TryOpen"/>).
    /// </summary>
    private readonly SDL_AudioStream* _device;

    /// <summary>
    /// What the main thread holds of each slot: the voice in it, which start it is, and the stream and pinned samples
    /// it handed the audio thread, kept until the audio thread lets the start go.
    /// </summary>
    private readonly Hold[] _holds;
    private readonly Stack<int> _free = [];

    /// <summary>
    /// The slot each reflector echoes, or -1, which each slot is told (<see cref="WriteReflectors"/>).
    /// </summary>
    private readonly int[] _reflected;

    /// <summary>
    /// What the simulation thread works from, a triple buffer: the main thread fills one, the simulation thread reads
    /// another, and the third is the newest filled, swapped whole between them.
    /// </summary>
    private readonly SimulationFrame[] _frames;

    /// <summary>
    /// The settings the simulator was created with as its most, which a trace keeps to on the simulation thread
    /// however the held settings change before a restart.
    /// </summary>
    private readonly int _rays;
    private readonly float _reverbSec;
    private readonly int _order;
    private GCHandle _self;
    private int _filling;
    private int _newest = 1;
    private int _reading = 2;
    private int _isNewest;

    /// <summary>
    /// The run is ending: the simulation thread returns when next woken.
    /// </summary>
    private bool _isEnding;

    /// <summary>
    /// The device is paused, as the app is (<see cref="Pause"/>), until the next frame is handed over. Main thread.
    /// </summary>
    private bool _isDevicePaused;

    /// <summary>
    /// How many times the gem has gathered the solids, each frame handed over stamped with it. Main thread.
    /// </summary>
    private int _gathered;

    /// <summary>
    /// The gathering last taken whose solids the scene held every one of after an update, written by the simulation
    /// thread for the main thread.
    /// </summary>
    private int _placed;

    /// <summary>
    /// The first gathering since the world last emptied for a new one (<see cref="Forget"/>). Main thread.
    /// </summary>
    private int _firstGathering;

    /// <summary>
    /// Creates Steam Audio's context, head, scene and simulator, the mixer, and all the device and the simulation
    /// thread read, then opens the device, paused, which <see cref="TryOpen"/> resumes, or lets all of it go when it
    /// did not open.
    /// </summary>
    private Hearing(SteamAudioSettings settings, Action<Handle<Audio>> markUnplayable)
    {
        _settings = settings;
        _markUnplayable = markUnplayable;
        _rays = settings.Rays;
        _reverbSec = settings.ReverbSec;
        _order = settings.Order;
        IPLContextSettings contextSettings = new()
        {
            version = STEAMAUDIO_VERSION,
            logCallback = &OnLog,
            simdLevel = IPL_SIMDLEVEL_AVX2
        };
        _IPLContext_t* context;
        SteamAudioGem.Check(iplContextCreate(&contextSettings, &context), "its context");
        _context = context;

        IPLAudioSettings audio = new() { samplingRate = settings.SampleRate, frameSize = settings.FrameSize };
        IPLHRTFSettings hrtfSettings = new()
        {
            type = IPL_HRTFTYPE_DEFAULT,
            volume = 1,
            normType = IPL_HRTFNORMTYPE_NONE
        };
        _IPLHRTF_t* hrtf;
        SteamAudioGem.Check(iplHRTFCreate(context, &audio, &hrtfSettings, &hrtf), "the head the listener hears with");
        _hrtf = hrtf;

        // Embree traces several times faster than Steam Audio's own tracer, which stands in where it cannot start.
        _IPLEmbreeDevice_t* embree;
        IPLSceneSettings sceneSettings = iplEmbreeDeviceCreate(context, null, &embree) == IPL_STATUS_SUCCESS
            ? new IPLSceneSettings { type = IPL_SCENETYPE_EMBREE, embreeDevice = embree }
            : new IPLSceneSettings { type = IPL_SCENETYPE_DEFAULT };
        _embree = sceneSettings.embreeDevice;
        _IPLScene_t* scene;
        SteamAudioGem.Check(iplSceneCreate(context, &sceneSettings, &scene), "the scene");
        _scene = scene;

        // The simulator works out the direct sound of every slot, and the echoes when any sound may echo; the mixer
        // adds each slot's and reflector's source to it before it commits them, with the scene, empty until solids come.
        IPLSimulationSettings simulation = new()
        {
            flags = settings.ReflectionSources > 0
                ? IPL_SIMULATIONFLAGS_DIRECT | IPL_SIMULATIONFLAGS_REFLECTIONS
                : IPL_SIMULATIONFLAGS_DIRECT,
            sceneType = sceneSettings.type,
            reflectionType = IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = OcclusionSamples,
            maxNumRays = _rays,
            numDiffuseSamples = 32,
            maxDuration = _reverbSec,
            maxOrder = _order,
            maxNumSources = Math.Max(1, settings.ReflectionSources),
            numThreads = Math.Max(1, Environment.ProcessorCount / 4),
            samplingRate = settings.SampleRate,
            frameSize = settings.FrameSize,
        };
        _IPLSimulator_t* simulator;
        SteamAudioGem.Check(iplSimulatorCreate(context, &simulation, &simulator), "the simulator");
        _simulator = simulator;
        iplSceneCommit(scene);
        iplSimulatorSetScene(simulator, scene);
        _mixer = new Mixer(context, hrtf, simulator, settings);
        iplSimulatorCommit(simulator);
        _soundWorld = new SoundWorld(context, scene, sceneSettings);

        // Every slot starts free, pushed last first so the first is taken first, and no reflector echoes anything yet.
        _holds = new Hold[settings.Voices];
        for (int i = _holds.Length - 1; i >= 0; i--)
        {
            _holds[i] = new Hold();
            _free.Push(i);
        }

        _reflected = new int[settings.ReflectionSources];
        Array.Fill(_reflected, -1);
        _frames = [new SimulationFrame(settings), new SimulationFrame(settings), new SimulationFrame(settings)];

        // The device opens paused, once all it reads is made.
        _self = GCHandle.Alloc(_mixer);
        SDL_AudioSpec device = new() { format = SDL_AUDIO_F32, channels = 2, freq = settings.SampleRate };
        _device = SDL_OpenAudioDeviceStream(
            SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK,
            &device,
            &Mixer.OnAudio,
            (void*)GCHandle.ToIntPtr(_self));
    }

    /// <summary>
    /// The solids of the frame being filled, which the gem gathers into, when they are due, before it hands the frame
    /// over (<see cref="HandOver"/>).
    /// </summary>
    internal List<Solid> Solids => _frames[_filling].Solids;

    /// <summary>
    /// Whether the scene holds every solid of the first gathering since the world last emptied (<see cref="Forget"/>).
    /// </summary>
    internal bool IsReady => Volatile.Read(ref _placed) >= _firstGathering;

    /// <summary>
    /// Releases what the hearing made of Steam Audio and SDL once Stop has closed the device and ended the simulation
    /// thread, or the device did not open, so nothing plays or simulates: every stream and pinned sound, the scene, the
    /// mixer, Steam Audio itself, then SDL's audio, on the render thread, which started it.
    /// </summary>
    public void Dispose()
    {
        _self.Free();

        foreach (Hold hold in _holds)
        {
            hold.Release();
        }

        _soundWorld.Dispose();
        _mixer.Dispose();
        _wake.Dispose();
        _simulationEnded.Dispose();
        _IPLSimulator_t* simulator = _simulator;
        iplSimulatorRelease(&simulator);
        _IPLScene_t* scene = _scene;
        iplSceneRelease(&scene);
        if (_embree is not null)
        {
            _IPLEmbreeDevice_t* embree = _embree;
            iplEmbreeDeviceRelease(&embree);
        }

        _IPLHRTF_t* hrtf = _hrtf;
        iplHRTFRelease(&hrtf);
        _IPLContext_t* context = _context;
        iplContextRelease(&context);
        SDL_QuitSubSystem(SDL_INIT_AUDIO);
    }

    /// <summary>
    /// Starts SDL's audio and Steam Audio, opens this machine's audio device and starts the simulation thread; or, when
    /// SDL cannot start its audio or open the device, lets go of all it made and says why. On the render thread, which
    /// <see cref="Dispose"/> runs on too.
    /// </summary>
    internal static Result<Hearing> TryOpen(
        SteamAudioSettings settings,
        Threads threads,
        Action<Handle<Audio>> markUnplayable)
    {
        if (!SDL_InitSubSystem(SDL_INIT_AUDIO))
            return Result.Failure<Hearing>($"SDL could not start this machine's audio: {CString.Read(SDL_GetError())}");

        Hearing hearing = new(settings, markUnplayable);
        if (hearing._device is null)
        {
            string error = CString.Read(SDL_GetError());
            hearing.Dispose();

            return Result.Failure<Hearing>($"SDL could not open this machine's audio device: {error}");
        }

        SDL_ResumeAudioStreamDevice(hearing._device);
        threads.RunAsync("Audio simulation", hearing.RunSimulation).FireAndForget();
        string tracer = hearing._embree is not null ? "Embree" : "its own tracer";
        Debugging.Info($"Steam Audio {STEAMAUDIO_VERSION_MAJOR}.{STEAMAUDIO_VERSION_MINOR}.{STEAMAUDIO_VERSION_PATCH} "
            + $"is playing up to {settings.Voices} sounds at {settings.SampleRate} Hz, "
            + $"echoing the nearest {settings.ReflectionSources}, traced with {tracer}.");

        return Result.Success(hearing);
    }

    /// <summary>
    /// Pauses the device while the app is paused, so nothing is heard until the next frame is handed over, as sound
    /// holds with the frames. Main thread.
    /// </summary>
    internal void Pause()
    {
        if (_isDevicePaused) return;

        _isDevicePaused = true;
        SDL_PauseAudioStreamDevice(_device);
    }

    /// <summary>
    /// Closes the device, which waits out the audio thread, and ends the simulation thread, waiting for it to finish
    /// what it is working out: from here nothing plays or simulates, and Dispose releases the rest. The one wait the
    /// gem makes, once the frames are over and nothing waits on the main thread.
    /// </summary>
    internal void Stop()
    {
        SDL_DestroyAudioStream(_device);
        Volatile.Write(ref _isEnding, true);
        _wake.Set();
        _simulationEnded.Wait(CancellationToken.None);
    }

    /// <summary>
    /// Takes back every slot the audio thread let go since the last frame: its stream is destroyed and its samples
    /// unpinned, and a sound that ended of itself is stopped. Also tells again a free the audio thread had no room for.
    /// Before the gem follows the sounds, so one that ended is written back as stopped, and its slot given out, this
    /// frame.
    /// </summary>
    internal void TakeReleased()
    {
        for (int i = 0; i < _holds.Length; i++)
        {
            Hold hold = _holds[i];

            if (hold.Stream == 0) continue;

            // Not let go yet: a free the audio thread had no room for is told again. A slot whose voice was taken from
            // it is being freed.
            Mixer.Slot slot = _mixer.Slots[i];
            if (slot.Released != hold.Generation)
            {
                if (hold.Voice is null && !hold.IsFreeTold)
                {
                    hold.IsFreeTold = _mixer.TryTell(
                        new SlotCommand { Action = SlotAction.Free, Slot = i, Generation = hold.Generation });
                }

                continue;
            }

            if (slot.HasEnded && hold.Voice is { } voice && voice.Slot == i)
            {
                voice.Slot = -1;
                voice.HasEnded = true;
                Debugging.Verbose($"Entity {voice.Entity.Id}'s sound ended.");
            }

            // No reflector echoes a slot let go.
            for (int k = 0; k < _reflected.Length; k++)
            {
                if (_reflected[k] == i) _reflected[k] = -1;
            }

            hold.Release();
            _free.Push(i);
        }

        // Before a slot let go is started again, so it starts with no echo.
        WriteReflectors();
    }

    /// <summary>
    /// Seconds into <paramref name="voice"/>'s sound, heard in a slot, that the audio thread has played it to; where
    /// the gem has it until the audio thread has taken in its start.
    /// </summary>
    internal double GetPlayedSec(Voice voice)
    {
        Mixer.Slot slot = _mixer.Slots[voice.Slot];

        return slot.Started == voice.Generation ? slot.TimeSec : voice.TimeSec;
    }

    /// <summary>
    /// Takes the voice's slot from it, if it has one: the audio thread is told to let it go, and the slot comes back
    /// once it has. The voice goes on in silence from where it was.
    /// </summary>
    internal void Free(Voice voice)
    {
        if (voice.Slot < 0) return;

        Hold hold = _holds[voice.Slot];
        hold.Voice = null;
        hold.IsFreeTold = _mixer.TryTell(
            new SlotCommand { Action = SlotAction.Free, Slot = voice.Slot, Generation = hold.Generation });
        voice.Slot = -1;
        Debugging.Verbose($"Entity {voice.Entity.Id}'s sound goes on in silence.");
    }

    /// <summary>
    /// The world emptied for a new one: the next gathering is its first. Once the scene has taken it, it lets go of
    /// what only the old world's solids needed (<see cref="SoundWorld.Forget"/>), and once it holds all of it, the
    /// hearing is ready (<see cref="IsReady"/>).
    /// </summary>
    internal void Forget() => _firstGathering = _gathered + 1;

    /// <summary>
    /// Hands the simulation thread this frame, once the gem has followed the sounds: the device plays again if the app
    /// paused it; the nearest of <paramref name="wanted"/> are given slots in frame <paramref name="frame"/>, unless
    /// the world's stream holds it (<paramref name="isHeld"/>); and where <paramref name="listener"/> is, the slots,
    /// the echoes and, when the gem gathered them into <see cref="Solids"/> (<paramref name="hasSolids"/>), the solids
    /// go to the simulation thread.
    /// </summary>
    internal void HandOver(in Transform listener, List<Voice> wanted, ulong frame, bool isHeld, bool hasSolids)
    {
        if (_isDevicePaused)
        {
            _isDevicePaused = false;
            SDL_ResumeAudioStreamDevice(_device);
        }

        IPLCoordinateSpace3 space = ToSpace(listener.Position, listener.Rotation);
        _mixer.Volume = _settings.Volume;
        _mixer.IsReflecting = _settings.HasReflections && _reflected.Length > 0;
        _mixer.Listener = space;
        if (!isHeld) Assign(wanted, frame);

        // This frame's listener, slots, echoes and the solids when gathered, written into the frame being filled,
        // which is then swapped in as the newest and the simulation thread woken to take it.
        SimulationFrame filling = _frames[_filling];
        filling.Listener = space;
        filling.ListenerPosition = listener.Position;
        filling.Bounces = _settings.Bounces;
        filling.IsReflecting = _mixer.IsReflecting;
        filling.ReflectionPeriodSec = 1 / Math.Clamp(_settings.ReflectionRate, 1, 60);
        filling.PlacingSec = isHeld ? double.PositiveInfinity : PlacingSec;
        Steer(space, filling);
        Reflect(wanted, filling);
        filling.HasSolids = hasSolids;
        if (hasSolids) _gathered++;
        filling.Gathered = _gathered;
        filling.ForgetAt = _firstGathering;
        _filling = Interlocked.Exchange(ref _newest, _filling);
        Volatile.Write(ref _isNewest, 1);
        _wake.Set();
        _heard.Record(_holds.Length - _free.Count);
    }

    /// <summary>
    /// Sounds everywhere first, then the nearest: the order voices are given slots in.
    /// </summary>
    private static int CompareNearness(Voice a, Voice b)
    {
        bool isAEverywhere = a.Sound.Space == SoundSpace.Everywhere;
        bool isBEverywhere = b.Sound.Space == SoundSpace.Everywhere;

        if (isAEverywhere != isBEverywhere) return isAEverywhere ? -1 : 1;

        return a.Distance.CompareTo(b.Distance);
    }

    /// <summary>
    /// Where something at <paramref name="position"/> turned by <paramref name="rotation"/> is and faces, in Steam
    /// Audio's space: the world mirrored along Z, its axes mirrored with it.
    /// </summary>
    private static IPLCoordinateSpace3 ToSpace(Vector3 position, Quaternion rotation)
    {
        return new IPLCoordinateSpace3
        {
            right = SoundWorld.ToVector(Vector3.Transform(Vector3.UnitX, rotation)),
            up = SoundWorld.ToVector(Vector3.Transform(Vector3.UnitY, rotation)),
            ahead = SoundWorld.ToVector(Vector3.Transform(Vector3.UnitZ, rotation)),
            origin = SoundWorld.ToVector(position),
        };
    }

    /// <summary>
    /// What Steam Audio logs: a warning or an error as one, and the rest only when asked for.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(IPLLogLevel level, byte* message)
    {
        string text = $"Steam Audio: {CString.Read(message).TrimEnd()}";
        switch (level)
        {
            case IPL_LOGLEVEL_ERROR:
                Debugging.Error(text);
                break;
            case IPL_LOGLEVEL_WARNING:
                Debugging.Warn(text);
                break;
            case IPL_LOGLEVEL_INFO:
            case IPL_LOGLEVEL_DEBUG:
            default:
                Debugging.Verbose(text);
                break;
        }
    }

    /// <summary>
    /// Gives slots to the nearest of <paramref name="wanted"/> in frame <paramref name="frame"/>, sounds everywhere
    /// first, as many as there are, taking slots from the voices that no longer want one or are no longer among the
    /// nearest, which go on in silence.
    /// </summary>
    private void Assign(List<Voice> wanted, ulong frame)
    {
        wanted.Sort(_byNearness);
        int heard = Math.Min(wanted.Count, _holds.Length);
        for (int i = 0; i < heard; i++)
        {
            wanted[i].Chosen = frame;
        }

        foreach (Hold hold in _holds)
        {
            if (hold.Voice is { } voice && voice.Chosen != frame) Free(voice);
        }

        for (int i = 0; i < heard && _free.Count > 0; i++)
        {
            if (wanted[i].Slot < 0) Start(wanted[i]);
        }
    }

    /// <summary>
    /// Starts <paramref name="voice"/> in a free slot: a stream from its audio's format to the device's, its samples
    /// pinned for the audio thread to read, from where it is. When the audio thread has no room for the start, nothing
    /// is kept and it is tried again next frame.
    /// </summary>
    private void Start(Voice voice)
    {
        if (voice.Audio is not { } audio) return;

        int index = _free.Peek();
        // From the file's rate and channels to the device's rate, mono for a sound in the world, which Steam Audio
        // places around the head, and stereo for one everywhere, mixed in as it is.
        Hold hold = _holds[index];
        SDL_AudioSpec from = new() { format = SDL_AUDIO_F32, channels = audio.Channels, freq = audio.SampleRate };
        SDL_AudioSpec to = new()
        {
            format = SDL_AUDIO_F32,
            channels = voice.Sound.Space == SoundSpace.Everywhere ? 2 : 1,
            freq = _settings.SampleRate
        };
        SDL_AudioStream* stream = SDL_CreateAudioStream(&from, &to);
        if (stream is null)
        {
            Debugging.Error($"Sounds that play an audio of {audio.Channels} channel(s) at {audio.SampleRate} Hz are "
                + $"silent: SDL cannot convert it to the device: {CString.Read(SDL_GetError())}");
            _markUnplayable(voice.Sound.Audio);

            return;
        }

        MemoryHandle pin = audio.Samples.Pin();
        SlotCommand command = new()
        {
            Action = SlotAction.Start,
            Slot = index,
            Generation = hold.Generation + 1,
            TimeSec = voice.TimeSec,
            Stream = (nint)stream,
            Samples = (nint)pin.Pointer,
            Count = audio.Samples.Length,
            Channels = audio.Channels,
            Rate = audio.SampleRate,
            DurationSec = audio.DurationSec,
            IsEverywhere = voice.Sound.Space == SoundSpace.Everywhere,
        };
        if (!_mixer.TryTell(command))
        {
            pin.Dispose();
            SDL_DestroyAudioStream(stream);

            return;
        }

        _free.Pop();
        hold.Take(voice, command.Generation, stream, pin);
        voice.Slot = index;
        voice.Generation = command.Generation;
        voice.IsSeeking = false;
        Debugging.Verbose(
            $"Entity {voice.Entity.Id}'s sound is heard, {voice.Distance:0.#} m away, from {voice.TimeSec:0.##} s in.");
    }

    /// <summary>
    /// Writes each heard voice's volume, pitch and loop to its slot, and, for a sound in the world, which way it is
    /// from <paramref name="listener"/> and what the simulation thread is to work out for it: its place, its focus, how
    /// it falls off, and what is in the way unless it is unoccluded. A slot no sound is in is simulated for nothing.
    /// </summary>
    private void Steer(in IPLCoordinateSpace3 listener, SimulationFrame filling)
    {
        for (int i = 0; i < _holds.Length; i++)
        {
            filling.Slots[i] = default;

            if (_holds[i].Voice is not { } voice) continue;

            // A sound in the world fades to silence over the far end of its range, as Steam Audio's fall-off never
            // does; a seek the audio thread had no room for is told again next frame.
            Mixer.Slot slot = _mixer.Slots[i];
            Sound sound = voice.Sound;
            bool isWorld = sound.Space == SoundSpace.World;
            float fade = isWorld ? Math.Clamp((sound.Range - voice.Distance) / (sound.Range * FadeShare), 0, 1) : 1;
            slot.Gain = sound.Volume * fade;
            slot.Pitch = sound.Pitch;
            slot.IsLooping = sound.IsLooping;
            if (voice.IsSeeking)
            {
                voice.IsSeeking = !_mixer.TryTell(new SlotCommand
                {
                    Action = SlotAction.Seek,
                    Slot = i,
                    Generation = voice.Generation,
                    TimeSec = voice.TimeSec
                });
            }

            if (!isWorld) continue;

            // Which way it is from the listener, straight ahead when at the head; and what reaches the listener
            // straight from it, the same worked out as applied: fallen off with distance, dulled by the air, quieter
            // off its front, and muffled by what is in the way unless it is unoccluded.
            IPLVector3 position = SoundWorld.ToVector(voice.Position);
            slot.Direction = voice.Distance < ClosestDistance
                ? new IPLVector3 { z = -1 }
                : iplCalculateRelativeDirection(_context, position, listener.origin, listener.ahead, listener.up);
            IPLDirectSimulationFlags simulated = IPL_DIRECTSIMULATIONFLAGS_DISTANCEATTENUATION
                | IPL_DIRECTSIMULATIONFLAGS_AIRABSORPTION
                | IPL_DIRECTSIMULATIONFLAGS_DIRECTIVITY;
            IPLDirectEffectFlags applied = IPL_DIRECTEFFECTFLAGS_APPLYDISTANCEATTENUATION
                | IPL_DIRECTEFFECTFLAGS_APPLYAIRABSORPTION
                | IPL_DIRECTEFFECTFLAGS_APPLYDIRECTIVITY;
            if (!sound.IsUnoccluded)
            {
                simulated |= IPL_DIRECTSIMULATIONFLAGS_OCCLUSION | IPL_DIRECTSIMULATIONFLAGS_TRANSMISSION;
                applied |= IPL_DIRECTEFFECTFLAGS_APPLYOCCLUSION | IPL_DIRECTEFFECTFLAGS_APPLYTRANSMISSION;
            }

            slot.Flags = applied;
            filling.Slots[i] = new IPLSimulationInputs
            {
                flags = IPL_SIMULATIONFLAGS_DIRECT,
                directFlags = simulated,
                source = ToSpace(voice.Position, voice.Rotation),
                distanceAttenuationModel = new IPLDistanceAttenuationModel
                {
                    type = IPL_DISTANCEATTENUATIONTYPE_INVERSEDISTANCE,
                    minDistance = sound.Near
                },
                airAbsorptionModel = new IPLAirAbsorptionModel { type = IPL_AIRABSORPTIONTYPE_DEFAULT },
                directivity = new IPLDirectivity { dipoleWeight = Math.Clamp(sound.Focus, 0, 1), dipolePower = 1 },
                occlusionType = IPL_OCCLUSIONTYPE_VOLUMETRIC,
                occlusionRadius = 0.5f,
                numOcclusionSamples = OcclusionSamples,
                numTransmissionRays = 3,
            };
        }
    }

    /// <summary>
    /// Gives the reflectors to the nearest heard sounds in the world that echo, of <paramref name="wanted"/>, keeping
    /// each with the sound it has while that is still among them, and writes where each reflector's sound is for the
    /// simulation thread.
    /// </summary>
    private void Reflect(List<Voice> wanted, SimulationFrame filling)
    {
        if (_reflected.Length == 0) return;

        // The slots of the nearest heard sounds in the world that are not dry, one for each reflector, from the voices
        // wanting a slot, which are sorted nearest first.
        Span<int> nearest = stackalloc int[_reflected.Length];
        int count = 0;
        if (_mixer.IsReflecting)
        {
            foreach (Voice voice in wanted)
            {
                if (count == nearest.Length) break;
                if (voice.Slot >= 0 && voice.Sound.Space == SoundSpace.World
                    && !voice.Sound.IsDry)
                {
                    nearest[count++] = voice.Slot;
                }
            }
        }

        // A reflector whose sound is no longer among them lets it go.
        nearest = nearest[..count];
        for (int k = 0; k < _reflected.Length; k++)
        {
            if (!nearest.Contains(_reflected[k])) _reflected[k] = -1;
        }

        // A sound newly among them takes a free reflector; one already echoed keeps its own.
        foreach (int slot in nearest)
        {
            if (Array.IndexOf(_reflected, slot) < 0) _reflected[Array.IndexOf(_reflected, -1)] = slot;
        }

        WriteReflectors();
        for (int k = 0; k < _reflected.Length; k++)
        {
            Voice? echoed = _reflected[k] >= 0 ? _holds[_reflected[k]].Voice : null;
            filling.IsEchoing[k] = echoed is not null;
            if (echoed is not null) filling.Echoes[k] = ToSpace(echoed.Position, echoed.Rotation);
        }
    }

    /// <summary>
    /// Tells each slot which reflector it is echoed through, as <see cref="_reflected"/> says, or none: the audio thread
    /// reads it as it mixes.
    /// </summary>
    private void WriteReflectors()
    {
        for (int i = 0; i < _holds.Length; i++)
        {
            _mixer.Slots[i].Reflector = Array.IndexOf(_reflected, i);
        }
    }

    /// <summary>
    /// The simulation thread: each time the main thread hands over a frame, brings the scene up to date with it, works
    /// out the direct sound of every slot, and traces the echoes when they are due, until the run ends.
    /// </summary>
    private void RunSimulation(CancellationToken stopping)
    {
        try
        {
            long tracedTimestamp = 0;
            int taken = 0;
            int forgotten = 0;
            while (!stopping.IsCancellationRequested)
            {
                // Woken by a frame handed over or by the run ending; a wake with no new frame waits again.
                _wake.Wait(CancellationToken.None);
                _wake.Reset();

                if (Volatile.Read(ref _isEnding)) return;
                if (Interlocked.Exchange(ref _isNewest, 0) == 0) continue;

                // The newest frame is taken in exchange for the one last read, which the main thread may fill next.
                _reading = Interlocked.Exchange(ref _newest, _reading);
                SimulationFrame frame = _frames[_reading];
                if (frame.HasSolids) taken = frame.Gathered;

                // Once the scene holds every solid of the gathering last taken, the main thread is told, for readying;
                // once it has taken the first gathering since the world last emptied, the old world's solids are out of
                // it, and it lets go of what none left needs, once.
                Simulate(frame);
                if (_soundWorld.IsWhole) Volatile.Write(ref _placed, taken);

                if (taken >= frame.ForgetAt && forgotten < frame.ForgetAt)
                {
                    _soundWorld.Forget();
                    forgotten = frame.ForgetAt;
                }

                if (frame.IsReflecting && Array.IndexOf(frame.IsEchoing, true) >= 0
                    && Stopwatch.GetElapsedTime(tracedTimestamp).TotalSeconds >= frame.ReflectionPeriodSec)
                {
                    tracedTimestamp = Stopwatch.GetTimestamp();
                    Trace(frame);
                }
            }
        }
        finally
        {
            _simulationEnded.Set();
        }
    }

    /// <summary>
    /// The scene brought up to date, with the frame's solids when it gathered them, and committed when it changed, then
    /// the direct sound of every slot worked out from where the frame says the listener and each sound are. On the
    /// simulation thread.
    /// </summary>
    private void Simulate(SimulationFrame frame)
    {
        long startTimestamp = _simulateDuration.Start();
        if (frame.HasSolids) _soundWorld.Take(frame.Solids);

        if (_soundWorld.Update(frame.ListenerPosition, frame.PlacingSec))
        {
            iplSceneCommit(_scene);
            iplSimulatorCommit(_simulator);
        }

        IPLSimulationSharedInputs shared = new() { listener = frame.Listener };
        iplSimulatorSetSharedInputs(_simulator, IPL_SIMULATIONFLAGS_DIRECT, &shared);
        fixed (IPLSimulationInputs* inputs = frame.Slots)
        {
            for (int i = 0; i < frame.Slots.Length; i++)
            {
                iplSourceSetInputs(_mixer.Slots[i].Source, IPL_SIMULATIONFLAGS_DIRECT, &inputs[i]);
            }
        }

        iplSimulatorRunDirect(_simulator);
        _simulateDuration.RecordSince(startTimestamp);
        _sceneSolids.Record(_soundWorld.Count);
    }

    /// <summary>
    /// One trace: each reflector put where its sound is, or set to trace nothing, and the echoes worked out. On the
    /// simulation thread.
    /// </summary>
    private void Trace(SimulationFrame frame)
    {
        long startTimestamp = _traceDuration.Start();
        IPLSimulationSharedInputs shared = new()
        {
            listener = frame.Listener,
            numRays = _rays,
            numBounces = frame.Bounces,
            duration = _reverbSec,
            order = _order,
            irradianceMinDistance = 1,
        };
        iplSimulatorSetSharedInputs(_simulator, IPL_SIMULATIONFLAGS_REFLECTIONS, &shared);
        IReadOnlyList<Mixer.Reflector> reflectors = _mixer.Reflectors;
        for (int k = 0; k < reflectors.Count; k++)
        {
            IPLSimulationInputs source = new()
            {
                flags = frame.IsEchoing[k] ? IPL_SIMULATIONFLAGS_REFLECTIONS : default,
                source = frame.Echoes[k],
                distanceAttenuationModel = new IPLDistanceAttenuationModel
                {
                    type = IPL_DISTANCEATTENUATIONTYPE_DEFAULT
                },
                airAbsorptionModel = new IPLAirAbsorptionModel { type = IPL_AIRABSORPTIONTYPE_DEFAULT },
            };
            source.reverbScale[0] = 1;
            source.reverbScale[1] = 1;
            source.reverbScale[2] = 1;
            iplSourceSetInputs(reflectors[k].Source, IPL_SIMULATIONFLAGS_REFLECTIONS, &source);
        }

        iplSimulatorRunReflections(_simulator);
        _traceDuration.RecordSince(startTimestamp);
    }

    /// <summary>
    /// What the main thread holds of one slot: the voice heard in it, which start it is, and what it handed the audio
    /// thread for that start, a stream and pinned samples, kept until the audio thread lets the start go. One with a
    /// stream and no voice is being freed.
    /// </summary>
    private sealed class Hold
    {
        private MemoryHandle _pin;

        internal Voice? Voice { get; set; }

        internal int Generation { get; private set; }

        internal nint Stream { get; private set; }

        /// <summary>
        /// The audio thread has been told to let the start go.
        /// </summary>
        internal bool IsFreeTold { get; set; }

        internal void Take(Voice voice, int generation, SDL_AudioStream* stream, MemoryHandle pin)
        {
            Voice = voice;
            Generation = generation;
            Stream = (nint)stream;
            _pin = pin;
            IsFreeTold = false;
        }

        /// <summary>
        /// Destroys the stream and unpins the samples, once the audio thread no longer reads them.
        /// </summary>
        internal void Release()
        {
            if (Stream != 0) SDL_DestroyAudioStream((SDL_AudioStream*)Stream);

            _pin.Dispose();
            _pin = default;
            Stream = 0;
            Voice = null;
            IsFreeTold = false;
        }
    }

    /// <summary>
    /// One frame of what the simulation thread works from: where the listener is, what each slot's sound is to be
    /// simulated as, where each reflector's sound is and whether it has one, the solids near the player, and the
    /// settings it follows.
    /// </summary>
    private sealed class SimulationFrame(SteamAudioSettings settings)
    {
        internal IPLCoordinateSpace3 Listener { get; set; }

        internal Vector3 ListenerPosition { get; set; }

        internal IPLSimulationInputs[] Slots { get; } = new IPLSimulationInputs[settings.Voices];

        internal IPLCoordinateSpace3[] Echoes { get; } = new IPLCoordinateSpace3[settings.ReflectionSources];

        internal bool[] IsEchoing { get; } = new bool[settings.ReflectionSources];

        internal List<Solid> Solids { get; } = [];

        /// <summary>
        /// The solids were gathered this frame; otherwise the scene goes on from those it took last.
        /// </summary>
        internal bool HasSolids { get; set; }

        /// <summary>
        /// Which gathering of the solids is the latest as of this frame (<see cref="HasSolids"/>).
        /// </summary>
        internal int Gathered { get; set; }

        /// <summary>
        /// The first gathering since the world last emptied for a new one: once the scene has taken it, or a later one,
        /// it lets go of what no solid left needs (<see cref="SoundWorld.Forget"/>).
        /// </summary>
        internal int ForgetAt { get; set; }

        /// <summary>
        /// How long the scene's update may spend placing new solids (<see cref="SoundWorld.Update"/>).
        /// </summary>
        internal double PlacingSec { get; set; }

        internal int Bounces { get; set; }

        internal bool IsReflecting { get; set; }

        internal float ReflectionPeriodSec { get; set; }
    }
}
