using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
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
/// Plays every <see cref="Sound"/> with Steam Audio, heard from the player's camera. Each frame, in LateUpdate, every
/// entity's sound is compared with the copy kept from the last, as the physics gem compares bodies: what a script
/// changed is carried over, and how far in each sound is, and whether one has ended, is written back. The nearest
/// sounds, as many as the settings' voices, are heard; the rest go on in silence, their time running, until they are
/// near again. SDL's audio thread plays the heard ones (<see cref="Voices"/>).
/// <para>
/// Steam Audio's scene and simulator are a thread of the gem's own, the simulation thread: the main thread gathers,
/// each frame, where the listener and each heard sound are and what is solid near the player
/// (<see cref="Surroundings"/>), and hands it over whole; the simulation thread brings the acoustic scene up to date
/// with it (<see cref="SoundWorld"/>), works out what reaches the listener straight from each sound, and, a few times a
/// second, traces the echoes of the nearest few. So nothing of Steam Audio's runs on the main thread, and neither
/// thread waits on the other.
/// </para>
/// <para>
/// With no window, or no audio device, nothing is played: every sound goes on in silence, as one out of reach does, so
/// a script sees the same either way.
/// </para>
/// <para>
/// A domain's sounds and solids are loaded before it spawns (<see cref="GetProgress"/>). While the world's stream holds
/// it (<see cref="WorldStream.IsHeld"/>), from the frame what replaces the world spawns out of sight until the frame
/// after it is first shown, no sound plays nor has its time run, and the solids near the listener are placed with no
/// limit on how long an update of the scene takes, as no sound waits on it; it is ready once the first gathering of the
/// new world is all placed (<see cref="IsReady"/>), so the first sound heard is muffled and echoed as it should be.
/// </para>
/// <para>
/// An audio file, a model's triangles and its sub-scene in the acoustic scene are kept while the world's domains name
/// them, or a sound plays the file or a body is shaped like the model, and let go as the world empties for a new one
/// without them (<see cref="WorldStream.IsEmptied"/>).
/// </para>
/// <para>
/// A sound plays at its own speed, its time running in real time, whatever the world's time scale. While the world is
/// paused, every sound holds where it is but those of entities tagged <see cref="Tag.Unpausable"/>, such as a pause
/// menu's music; while the app is paused, the device is, and nothing is heard.
/// </para>
/// </summary>
internal sealed class SteamAudioGem : IGem, IPreloader, IDisposable
{
    /// <summary>
    /// The share of a sound's range over which it fades out, at its far end, as Steam Audio's own fall-off never
    /// reaches silence.
    /// </summary>
    private const float Fade = 0.2f;

    /// <summary>
    /// How far a sound at the listener's head is taken to be: one closer has no side to be heard from.
    /// </summary>
    private const float Closest = 0.001f;

    /// <summary>
    /// Occlusion rays cast from the listener toward the sphere around each sound, the most a sound is traced with.
    /// </summary>
    private const int OcclusionSamples = 16;

    /// <summary>
    /// How long one update of the acoustic scene may spend placing new solids, before the direct sound is worked out:
    /// the rest come in on the updates after, nearest first.
    /// </summary>
    private const double PlacingSec = 0.008;

    /// <summary>
    /// How often the solids near the player are gathered again: what stays put needs no look every frame, and walking a
    /// world of tens of thousands of entities is most of what the gem costs the main thread.
    /// </summary>
    private const float GatherSec = 0.1f;

    [SuppressMessage(
        "Style",
        "IDE0032:Use auto property",
        Justification = "The instruments below are made from it as the type starts, so it is a field, set first: an "
            + "auto property goes after the fields and would be read before it is set.")]
    private static readonly Meter _meter = new("Magic.Gems.SteamAudio");

    [SuppressMessage(
        "Style",
        "IDE0032:Use auto property",
        Justification = "The instruments below are made from it as the type starts, so it is a field, set first: an "
            + "auto property goes after the fields and would be read before it is set.")]
    private static readonly InstrumentAdvice<double> _buckets = new()
    {
        HistogramBucketBoundaries =
        [0.0001, 0.00025, 0.0005, 0.001, 0.002, 0.004, 0.008, 0.016, 0.032, 0.064, 0.125, 0.25, 0.5, 1]
    };

    private static readonly Comparison<Voice> _byNearness = CompareNearness;
    private static readonly Gauge<int> _heard = _meter.CreateGauge<int>(
        "magic.gems.steam_audio.voices",
        "{sound}",
        "Sounds heard this frame, of those playing.");
    private static readonly Gauge<int> _solids = _meter.CreateGauge<int>(
        "magic.gems.steam_audio.scene.solids",
        "{solid}",
        "Solids in the acoustic scene, which sound is muffled and echoed by.");
    private static readonly Histogram<double> _simulateDuration = _meter.CreateTimer(
        "magic.gems.steam_audio.simulate.duration",
        "How long the simulation thread took to bring the scene up to date and work out the direct sound.",
        _buckets);
    private static readonly Histogram<double> _traceDuration = _meter.CreateTimer(
        "magic.gems.steam_audio.trace.duration",
        "How long one trace of the echoes took, on the simulation thread.",
        _buckets);

    private readonly World _world;
    private readonly Assets _assets;
    private readonly SteamAudioSettings _settings;
    private readonly Dictionary<Entity, Voice> _voices = [];
    private readonly List<Voice> _wanted = [];
    private readonly List<Entity> _gone = [];
    private readonly List<Vector3> _players = [];
    private readonly Surroundings _surroundings;

    /// <summary>
    /// Each audio file once it has loaded; null while it loads, or when it did not.
    /// </summary>
    private readonly Dictionary<Handle<Audio>, Audio?> _audios = [];
    private readonly HashSet<Handle<Audio>> _failed = [];

    /// <summary>
    /// Audio files loaded on workers, or why not, for the main thread to take in at the start of its hook.
    /// </summary>
    private readonly Channel<(Handle<Audio> Handle, Result<Audio> Loaded)> _arrived =
        Channel.CreateUnbounded<(Handle<Audio> Handle, Result<Audio> Loaded)>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Set by the main thread when it has handed over a frame, or as the run ends, waking the simulation thread.
    /// </summary>
    private readonly ManualResetEventSlim _wake = new();

    /// <summary>
    /// Set by the simulation thread as it returns, which Stop waits for.
    /// </summary>
    private readonly ManualResetEventSlim _simulationEnded = new();
    private readonly unsafe _IPLContext_t* _context;
    private readonly unsafe _IPLHRTF_t* _hrtf;
    private readonly unsafe _IPLEmbreeDevice_t* _embree;
    private readonly unsafe _IPLScene_t* _scene;
    private readonly unsafe _IPLSimulator_t* _simulator;
    private readonly Voices? _sounds;
    private readonly SoundWorld? _soundWorld;
    private readonly bool _isAudioStarted;
    private readonly bool _isSimulating;

    /// <summary>
    /// What the main thread holds of each slot: the voice in it, which start it is, and the stream and pinned samples
    /// it handed the audio thread, kept until the audio thread lets the start go.
    /// </summary>
    private readonly Hold[] _holds = [];
    private readonly Stack<int> _free = [];

    /// <summary>
    /// The slot each reflector echoes, or -1; the main thread's copy of what it told the slots.
    /// </summary>
    private readonly int[] _reflected = [];

    /// <summary>
    /// What the simulation thread works from: three, so the main thread fills one, the simulation thread reads another,
    /// and the third is the newest filled, swapped whole between them.
    /// </summary>
    private readonly SimulationFrame[] _frames = [];
    private unsafe SDL_AudioStream* _device;
    private GCHandle _self;
    private IPLCoordinateSpace3 _listener = new()
    {
        right = new IPLVector3 { x = 1 },
        up = new IPLVector3 { y = 1 },
        ahead = new IPLVector3 { z = -1 }
    };
    private Vector3 _listenerPosition;
    private int _filling;
    private int _newest = 1;
    private int _reading = 2;
    private int _isNewest;
    private float _sinceGatheredSec = GatherSec;

    /// <summary>
    /// The run is ending: the simulation thread returns when next woken.
    /// </summary>
    private bool _isEnding;

    /// <summary>
    /// Which frame this is, stamped on each voice seen in it: one not stamped is of an entity gone.
    /// </summary>
    private uint _frame;

    /// <summary>
    /// The device is paused, as the app is (<see cref="Idle"/>), until the next frame. Main thread.
    /// </summary>
    private bool _isDevicePaused;

    /// <summary>
    /// How many times the solids have been gathered, each frame handed over stamped with it. Main thread.
    /// </summary>
    private int _gathered;

    /// <summary>
    /// The gathering last taken whose solids the scene held every one of after an update, written by the simulation
    /// thread for the main thread.
    /// </summary>
    private int _placed;

    /// <summary>
    /// The first gathering since the world last emptied for a new one: once the scene has taken it, it lets go of what
    /// only the old world's solids needed, and once it holds all of it, the new world is ready (<see cref="IsReady"/>).
    /// Main thread.
    /// </summary>
    private int _firstGathering;

    public unsafe SteamAudioGem(
        World world,
        Assets assets,
        Threads threads,
        Args run,
        SteamAudioSettings settings)
    {
        _world = world;
        _assets = assets;
        _settings = settings;
        _surroundings = new Surroundings(assets);
        if (run.IsHeadless)
        {
            Debugging.Info("Sound is not played headless: every sound goes on in silence.");

            return;
        }

        if (!SDL_InitSubSystem(SDL_INIT_AUDIO))
        {
            Debugging.Warn("Sound is not played, as SDL could not start this machine's audio: "
                + $"{CString.Read(SDL_GetError())}. Every sound goes on in silence.");

            return;
        }

        _isAudioStarted = true;
        IPLContextSettings contextSettings = new()
        {
            version = STEAMAUDIO_VERSION,
            logCallback = &OnLog,
            simdLevel = IPL_SIMDLEVEL_AVX2
        };
        _IPLContext_t* context;
        Check(iplContextCreate(&contextSettings, &context), "its context");
        _context = context;

        IPLAudioSettings audio = new() { samplingRate = settings.SampleRate, frameSize = settings.FrameSize };
        IPLHRTFSettings hrtfSettings = new()
        {
            type = IPL_HRTFTYPE_DEFAULT,
            volume = 1,
            normType = IPL_HRTFNORMTYPE_NONE
        };
        _IPLHRTF_t* hrtf;
        Check(iplHRTFCreate(context, &audio, &hrtfSettings, &hrtf), "the head the listener hears with");
        _hrtf = hrtf;

        // Embree traces several times faster than Steam Audio's own tracer, which stands in where it cannot start.
        _IPLEmbreeDevice_t* embree;
        IPLSceneSettings sceneSettings = iplEmbreeDeviceCreate(context, null, &embree) == IPL_STATUS_SUCCESS
            ? new IPLSceneSettings { type = IPL_SCENETYPE_EMBREE, embreeDevice = embree }
            : new IPLSceneSettings { type = IPL_SCENETYPE_DEFAULT };
        _embree = sceneSettings.embreeDevice;
        _IPLScene_t* scene;
        Check(iplSceneCreate(context, &sceneSettings, &scene), "the scene");
        _scene = scene;

        // The simulator works out the direct sound of every slot, and the echoes when any sound may echo; the voices
        // add each slot's and reflector's source to it before it commits them, with the scene, empty until solids come.
        IPLSimulationSettings simulation = new()
        {
            flags = settings.ReflectionSources > 0
                ? IPL_SIMULATIONFLAGS_DIRECT | IPL_SIMULATIONFLAGS_REFLECTIONS
                : IPL_SIMULATIONFLAGS_DIRECT,
            sceneType = sceneSettings.type,
            reflectionType = IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = OcclusionSamples,
            maxNumRays = settings.Rays,
            numDiffuseSamples = 32,
            maxDuration = settings.ReverbSec,
            maxOrder = settings.Order,
            maxNumSources = Math.Max(1, settings.ReflectionSources),
            numThreads = Math.Max(1, Environment.ProcessorCount / 4),
            samplingRate = settings.SampleRate,
            frameSize = settings.FrameSize,
        };
        _IPLSimulator_t* simulator;
        Check(iplSimulatorCreate(context, &simulation, &simulator), "the simulator");
        _simulator = simulator;
        iplSceneCommit(scene);
        iplSimulatorSetScene(simulator, scene);
        Voices sounds = new(context, hrtf, simulator, settings);
        iplSimulatorCommit(simulator);

        _self = GCHandle.Alloc(sounds);
        SDL_AudioSpec device = new() { format = SDL_AUDIO_F32, channels = 2, freq = settings.SampleRate };
        _device = SDL_OpenAudioDeviceStream(
            SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK,
            &device,
            &Voices.OnAudio,
            (void*)GCHandle.ToIntPtr(_self));
        if (_device is null)
        {
            // With nothing to play them, the voices go, and every sound runs on in silence, as one out of reach does.
            Debugging.Warn("Sound is not played, as SDL could not open this machine's audio device: "
                + $"{CString.Read(SDL_GetError())}. Every sound goes on in silence.");
            _self.Free();
            sounds.Dispose();

            return;
        }

        // Every slot starts free, pushed last first so the first is taken first.
        _sounds = sounds;
        _soundWorld = new SoundWorld(context, scene, sceneSettings);
        _holds = new Hold[settings.Voices];
        for (int i = _holds.Length - 1; i >= 0; i--)
        {
            _holds[i] = new Hold();
            _free.Push(i);
        }

        // No reflector echoes anything yet; the device plays and the simulation thread runs only once all they read is
        // made.
        _reflected = new int[settings.ReflectionSources];
        Array.Fill(_reflected, -1);
        _frames = [new SimulationFrame(settings), new SimulationFrame(settings), new SimulationFrame(settings)];
        SDL_ResumeAudioStreamDevice(_device);
        _isSimulating = true;
        threads.RunAsync("Audio simulation", RunSimulation).FireAndForget();
        string tracer = sceneSettings.type == IPL_SCENETYPE_EMBREE ? "Embree" : "its own tracer";
        Debugging.Info($"Steam Audio {STEAMAUDIO_VERSION_MAJOR}.{STEAMAUDIO_VERSION_MINOR}.{STEAMAUDIO_VERSION_PATCH} "
            + $"is playing up to {settings.Voices} sounds at {settings.SampleRate} Hz, "
            + $"echoing the nearest {settings.ReflectionSources}, traced with {tracer}.");
    }

    /// <summary>
    /// The gem's meter, which its parts record on (Core's README, Telemetry).
    /// </summary>
    internal static Meter Meter => _meter;

    /// <summary>
    /// Buckets from a tenth of a millisecond to a second, as the audio thread's work is measured against a frame of
    /// sound and the simulation's against a frame.
    /// </summary>
    internal static InstrumentAdvice<double> Buckets => _buckets;

    /// <summary>
    /// Releases what the gem made of Steam Audio and SDL once Stop has closed the device and ended the simulation
    /// thread, so nothing plays or simulates: every stream and pinned sound, the scene, the voices, Steam Audio itself,
    /// then SDL's audio, on the render thread, which started it.
    /// </summary>
    public unsafe void Dispose()
    {
        if (_self.IsAllocated) _self.Free();

        foreach (Hold hold in _holds)
        {
            hold.Let();
        }

        _soundWorld?.Dispose();
        _sounds?.Dispose();
        _wake.Dispose();
        _simulationEnded.Dispose();
        if (_context is not null)
        {
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
        }

        if (_isAudioStarted) SDL_QuitSubSystem(SDL_INIT_AUDIO);
    }

    public unsafe void LateUpdate(in Frame frame)
    {
        if (_isDevicePaused)
        {
            _isDevicePaused = false;
            SDL_ResumeAudioStreamDevice(_device);
        }

        WorldStream stream = _world.Stream;
        _frame++;
        TakeArrived();
        _surroundings.TakeArrived();
        if (stream.IsEmptied) Forget(stream.Prefabs);

        FindListener();
        if (_sounds is not null)
        {
            _sounds.Volume = _settings.Volume;
            _sounds.IsReflecting = _settings.HasReflections && _reflected.Length > 0;
            TakeReleased(_sounds);
        }

        FollowSounds(stream.IsHeld ? 0 : frame.RealDeltaSec, frame.IsPaused);

        if (_sounds is null) return;

        if (!stream.IsHeld) Assign();

        // This frame's listener, slots, echoes and, a few times a second, the solids near the player, written into the
        // frame being filled, which is then swapped in as the newest and the simulation thread woken to take it.
        SimulationFrame filling = _frames[_filling];
        filling.Listener = _listener;
        filling.ListenerPosition = _listenerPosition;
        filling.Bounces = _settings.Bounces;
        filling.IsReflecting = _sounds.IsReflecting;
        filling.ReflectionPeriodSec = 1 / Math.Clamp(_settings.ReflectionRate, 1, 60);
        filling.PlacingSec = stream.IsHeld ? double.PositiveInfinity : PlacingSec;
        Steer(_sounds, filling);
        Reflect(_sounds, filling);
        _sinceGatheredSec += frame.RealDeltaSec;
        filling.HasSolids = _sinceGatheredSec >= GatherSec;
        if (filling.HasSolids)
        {
            _sinceGatheredSec = 0;
            _gathered++;
            _surroundings.Gather(_world, CollectionsMarshal.AsSpan(_players), _settings.Range, filling.Solids);
        }

        filling.Gathered = _gathered;
        filling.ForgetAt = _firstGathering;
        _filling = Interlocked.Exchange(ref _newest, _filling);
        Volatile.Write(ref _isNewest, 1);
        _wake.Set();
        _heard.Record(_holds.Length - _free.Count);
    }

    /// <summary>
    /// Pauses the device while the app is paused, so nothing is heard until the next frame resumes it, as sound holds
    /// with the frames.
    /// </summary>
    public unsafe void Idle(EngineThread thread)
    {
        if (thread != EngineThread.Main || _isDevicePaused || _device is null) return;

        _isDevicePaused = true;
        SDL_PauseAudioStreamDevice(_device);
    }

    /// <summary>
    /// Closes the device, which waits out the audio thread, and ends the simulation thread, waiting for it to finish
    /// what it is working out: from here nothing plays or simulates, and Dispose releases the rest. The one wait the
    /// gem makes, once the frames are over and nothing waits on the main thread.
    /// </summary>
    public unsafe void Stop()
    {
        if (_device is not null)
        {
            SDL_DestroyAudioStream(_device);
            _device = null;
        }

        Volatile.Write(ref _isEnding, true);
        _wake.Set();
        if (_isSimulating) _simulationEnded.Wait(CancellationToken.None);
    }

    /// <summary>
    /// How many of the audio files the sounds about to spawn play, and the models their solids are shaped like, are
    /// in, asking for those not on their way, so each sound is heard, muffled and echoed, from its first frame. A file
    /// that did not load counts as in, as does a model when there is no scene to fit it for.
    /// </summary>
    public float GetProgress(in WorldStream stream)
    {
        HashSet<Handle<Audio>> audios = [];
        HashSet<Handle<Model>> models = [];
        foreach (Prefab prefab in stream.TakingIn)
        {
            AddAudios(prefab.Parts, audios);
            Surroundings.AddModels(prefab.Parts, models);
        }

        int done = 0;
        foreach (Handle<Audio> audio in audios)
        {
            if (_failed.Contains(audio) || GetAudio(audio) is not null) done++;
        }

        foreach (Handle<Model> model in models)
        {
            if (_sounds is null || _surroundings.IsFitted(model)) done++;
        }

        int count = audios.Count + models.Count;

        return count == 0 ? 1 : (float)done / count;
    }

    /// <summary>
    /// Ready once the scene holds every solid of the new world's first gathering, made the frame the world emptied for
    /// it and placed with no limit on time while it is held; at once with nothing to play sound on, as there is no
    /// scene.
    /// </summary>
    public bool IsReady(in WorldStream stream) => _sounds is null || Volatile.Read(ref _placed) >= _firstGathering;

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

    private static void Check(IPLerror error, string what)
    {
        if (error == IPL_STATUS_SUCCESS) return;

        throw new InvalidOperationException($"Steam Audio could not make {what}: {error}.");
    }

    private static void AddAudios(IReadOnlyList<PrefabPart> parts, HashSet<Handle<Audio>> audios)
    {
        foreach (PrefabPart part in parts)
        {
            foreach (IComponent component in part.Components)
            {
                if (component is Sound sound && sound.Audio != Handle<Audio>.None) audios.Add(sound.Audio);
            }

            AddAudios(part.Children, audios);
        }
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
    /// Runs a sound's time on as if it were heard, at its pitch: one that loops wraps, and one that does not stops at
    /// its end.
    /// </summary>
    private static void RunSilently(Voice voice, ref Sound sound, Audio audio, float deltaSec)
    {
        voice.TimeSec += deltaSec * Math.Clamp(sound.Pitch, 0.5f, 2);

        if (voice.TimeSec < audio.DurationSec) return;

        if (sound.IsLooping && audio.DurationSec > 0)
        {
            voice.TimeSec %= audio.DurationSec;

            return;
        }

        voice.TimeSec = 0;
        sound.Playback = Playback.Stopped;
    }

    /// <summary>
    /// What Steam Audio logs: a warning or an error as one, and the rest only when asked for.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnLog(IPLLogLevel level, byte* message)
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
    /// The audio file, asking for it if it has not been asked for; null while it loads, or when it did not.
    /// </summary>
    private Audio? GetAudio(Handle<Audio> handle)
    {
        if (handle == Handle<Audio>.None) return null;

        if (!_audios.TryGetValue(handle, out Audio? audio))
        {
            _audios.Add(handle, null);
            LoadAsync(handle).FireAndForget();
        }

        return audio;
    }

    private async Task LoadAsync(Handle<Audio> handle)
    {
        Result<Audio> loaded = await _assets.LoadAsync(handle, CancellationToken.None).ConfigureAwait(false);
        _arrived.Writer.TryWrite((handle, loaded));
    }

    /// <summary>
    /// Takes in the audio files workers loaded since the last frame; one that did not load is an error, and the sounds
    /// that play it are silent.
    /// </summary>
    private void TakeArrived()
    {
        while (_arrived.Reader.TryRead(out (Handle<Audio> Handle, Result<Audio> Loaded) arrived))
        {
            if (arrived.Loaded.Ok)
            {
                _audios[arrived.Handle] = arrived.Loaded.Value;

                continue;
            }

            _failed.Add(arrived.Handle);
            Debugging.Error(
                $"A sound cannot play its audio, which does not load, so it is silent: {arrived.Loaded.Error}");
        }
    }

    /// <summary>
    /// The world emptied for a new one: what the old world alone played and was solid as is let go at once, every audio
    /// file that no sound left plays and nothing <paramref name="prefabs"/> spawns does, and the triangles of every
    /// model no body left and none of those is shaped like; and the solids are gathered now, the new world's first
    /// gathering, once the scene has taken which it lets go of the sub-scenes no solid is shaped like.
    /// </summary>
    private void Forget(IReadOnlyList<Prefab> prefabs)
    {
        HashSet<Handle<Audio>> audios = [];
        HashSet<Handle<Model>> models = [];
        foreach (Prefab prefab in prefabs)
        {
            AddAudios(prefab.Parts, audios);
            Surroundings.AddModels(prefab.Parts, models);
        }

        foreach ((_, Span<Sound> sounds) in _world.Query<Sound>())
        {
            foreach (Sound sound in sounds)
            {
                audios.Add(sound.Audio);
            }
        }

        foreach ((ReadOnlySpan<Entity> entities, _) in _world.Query<Physics>())
        {
            foreach (Entity entity in entities)
            {
                if (_world.Has<Renderer>(entity)) models.Add(_world.Get<Renderer>(entity).Model);
            }
        }

        ForgetAudios(audios);
        _surroundings.Forget(models);
        _sinceGatheredSec = GatherSec;
        _firstGathering = _gathered + 1;
    }

    /// <summary>
    /// Lets go of every audio file not in <paramref name="kept"/>, loaded or failed, so a sound that plays one again
    /// loads it again; one still loading stays, to be let go at a later forget once it is in.
    /// </summary>
    private void ForgetAudios(HashSet<Handle<Audio>> kept)
    {
        // Listed first, as the files cannot change while they are walked.
        List<Handle<Audio>> gone = [];
        foreach ((Handle<Audio> handle, Audio? audio) in _audios)
        {
            if (!kept.Contains(handle) && (audio is not null || _failed.Contains(handle))) gone.Add(handle);
        }

        foreach (Handle<Audio> handle in gone)
        {
            _audios.Remove(handle);
            _failed.Remove(handle);
        }

        if (gone.Count > 0) Debugging.Verbose($"{gone.Count} audio files let go, which only the world before played.");
    }

    /// <summary>
    /// The listener is the camera tagged player, else the camera drawing into the window, else the first; with none, it
    /// stays where it was. The player's cameras are also where the acoustic scene is kept near.
    /// </summary>
    private void FindListener()
    {
        _players.Clear();
        Transform? listener = null;
        foreach ((_, _, Span<Transform> transforms, Span<Tags> tags) in _world.Query<Camera, Transform, Tags>())
        {
            for (int i = 0; i < tags.Length; i++)
            {
                if (!tags[i].Has(Tag.Player)) continue;

                _players.Add(transforms[i].Position);
                listener ??= transforms[i];
            }
        }

        // With no player, the camera drawing into the window, else the first camera.
        if (listener is null)
        {
            foreach ((_, Span<Transform> transforms, Span<Camera> cameras) in _world.Query<Transform, Camera>())
            {
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (listener is null || cameras[i].Target.IsWindow) listener = transforms[i];

                    if (cameras[i].Target.IsWindow) break;
                }
            }
        }

        if (listener is not { } found) return;

        _listenerPosition = found.Position;
        _listener = ToSpace(found.Position, found.Rotation);
        _sounds?.Listener = _listener;
    }

    /// <summary>
    /// Takes back every slot the audio thread let go since the last frame: its stream is destroyed and its samples
    /// unpinned, and a sound that ended of itself is stopped. Also tells again a free the audio thread had no room for.
    /// </summary>
    private void TakeReleased(Voices sounds)
    {
        for (int i = 0; i < _holds.Length; i++)
        {
            Hold hold = _holds[i];

            if (hold.Stream == 0) continue;

            // Not let go yet: a free the audio thread had no room for is told again.
            Voices.Slot slot = sounds.Slots[i];
            if (slot.Released != hold.Generation)
            {
                if (hold.IsFreeing && !hold.IsFreeTold)
                {
                    hold.IsFreeTold = sounds.TryTell(
                        new VoiceCommand { Action = VoiceAction.Free, Slot = i, Generation = hold.Generation });
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

            hold.Let();
            _free.Push(i);
        }
    }

    /// <summary>
    /// Brings every voice up to date with its entity's sound, and writes back how far in it is: a script's writes are
    /// carried over, a sound that is not heard has its time run on as if it were, and the voices that want to be heard
    /// are listed for <see cref="Assign"/>. While the world is paused (<paramref name="isPaused"/>), only the sounds of
    /// entities tagged <see cref="Tag.Unpausable"/> are; every other holds where it is.
    /// </summary>
    private void FollowSounds(float deltaSec, bool isPaused)
    {
        _wanted.Clear();
        foreach (EcsChunk<Transform, Sound> chunk in _world.Query<Transform, Sound>())
        {
            (ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<Sound> sounds) = chunk;
            for (int i = 0; i < entities.Length; i++)
            {
                bool isHeld = isPaused && !IsUnpausable(entities[i]);
                Follow(entities[i], transforms[i], ref sounds[i], isHeld ? 0 : deltaSec, isHeld);
            }
        }

        // A voice this frame did not see is of an entity gone or without its sound, and its slot goes; listed first, as
        // the voices cannot change while they are walked.
        foreach ((Entity entity, Voice voice) in _voices)
        {
            if (voice.Seen != _frame) _gone.Add(entity);
        }

        foreach (Entity entity in _gone)
        {
            if (_voices.Remove(entity, out Voice? voice)) Free(voice);
        }

        _gone.Clear();
    }

    /// <summary>
    /// <see cref="FollowSounds"/> for one entity's sound, its time run on by <paramref name="deltaSec"/> when it is not
    /// heard; held where it is, when <paramref name="isHeld"/>, it wants no voice, so it is not heard.
    /// </summary>
    private void Follow(Entity entity, in Transform transform, ref Sound sound, float deltaSec, bool isHeld)
    {
        if (!_voices.TryGetValue(entity, out Voice? voice))
        {
            voice = new Voice
            {
                Entity = entity,
                Sound = sound,
                TimeSec = sound.TimeSec,
                WrittenTimeSec = sound.TimeSec,
                WrittenPlayback = sound.Playback
            };
            _voices.Add(entity, voice);
        }

        // What a script wrote since the last frame, told from what the gem wrote back then: another audio or space
        // needs another slot, another audio from its start; another time is sought; and a pause or stop lets the slot
        // go, a stop back to the start.
        voice.Seen = _frame;
        if (sound.Audio != voice.Sound.Audio || sound.Space != voice.Sound.Space)
        {
            Free(voice);
            if (sound.Audio != voice.Sound.Audio) voice.TimeSec = 0;
        }

        if (sound.TimeSec != voice.WrittenTimeSec)
        {
            voice.TimeSec = Math.Max(0, sound.TimeSec);
            voice.IsSeeking = true;
        }

        if (sound.Playback != voice.WrittenPlayback && sound.Playback != Playback.Playing)
        {
            Free(voice);
            if (sound.Playback == Playback.Stopped) voice.TimeSec = 0;
        }

        // How far in it is now: one that ended is stopped, one heard is where the audio thread has played to unless a
        // seek is on its way, and one playing but not heard runs on in silence.
        voice.Sound = sound;
        voice.Position = transform.Position;
        voice.Rotation = transform.Rotation;
        voice.Distance = Vector3.Distance(transform.Position, _listenerPosition);
        Audio? audio = GetAudio(sound.Audio);
        voice.Audio = audio;
        if (voice.HasEnded)
        {
            voice.HasEnded = false;
            sound.Playback = Playback.Stopped;
            voice.TimeSec = 0;
        }
        else if (voice.Slot >= 0 && _sounds is not null && !voice.IsSeeking)
        {
            Voices.Slot slot = _sounds.Slots[voice.Slot];
            if (slot.Started == voice.Generation) voice.TimeSec = slot.TimeSec;
        }
        else if (voice.Slot < 0 && sound.Playback == Playback.Playing && audio is not null)
        {
            RunSilently(voice, ref sound, audio, deltaSec);
        }

        bool isAudible = !isHeld && sound.Playback == Playback.Playing && audio?.Samples.Length > 0
            && (sound.Space == SoundSpace.Everywhere || voice.Distance < sound.Range);
        if (isAudible && _sounds is not null) _wanted.Add(voice);

        // Written back for scripts to read, and kept to tell what a script writes next from it.
        sound.TimeSec = (float)voice.TimeSec;
        voice.WrittenTimeSec = sound.TimeSec;
        voice.WrittenPlayback = sound.Playback;
        voice.Sound = sound;
    }

    /// <summary>
    /// Whether <paramref name="entity"/> is tagged <see cref="Tag.Unpausable"/>, so its sound plays while the world is
    /// paused.
    /// </summary>
    private bool IsUnpausable(Entity entity)
        => _world.Has<Tags>(entity) && _world.Get<Tags>(entity).Has(Tag.Unpausable);

    /// <summary>
    /// Gives slots to the nearest voices that want one, sounds everywhere first, as many as there are, taking slots
    /// from the voices that no longer want one or are no longer among the nearest, which go on in silence.
    /// </summary>
    private void Assign()
    {
        _wanted.Sort(_byNearness);
        int heard = Math.Min(_wanted.Count, _holds.Length);
        for (int i = 0; i < heard; i++)
        {
            _wanted[i].Chosen = _frame;
        }

        foreach (Hold hold in _holds)
        {
            if (hold.Voice is { } voice && voice.Chosen != _frame) Free(voice);
        }

        for (int i = 0; i < heard && _free.Count > 0; i++)
        {
            if (_wanted[i].Slot < 0) Start(_wanted[i]);
        }
    }

    /// <summary>
    /// Starts <paramref name="voice"/> in a free slot: a stream from its audio's format to the device's, its samples
    /// pinned for the audio thread to read, from where it is. When the audio thread has no room for the start, nothing
    /// is kept and it is tried again next frame.
    /// </summary>
    private unsafe void Start(Voice voice)
    {
        if (_sounds is null || voice.Audio is not { } audio) return;

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
            _audios[voice.Sound.Audio] = null;
            _failed.Add(voice.Sound.Audio);
            voice.Audio = null;

            return;
        }

        MemoryHandle pin = audio.Samples.Pin();
        VoiceCommand command = new()
        {
            Action = VoiceAction.Start,
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
        if (!_sounds.TryTell(command))
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
    /// Takes the voice's slot from it, if it has one: the audio thread is told to let it go, and the slot comes back
    /// once it has. The voice goes on in silence from where it was.
    /// </summary>
    private void Free(Voice voice)
    {
        if (voice.Slot < 0 || _sounds is null) return;

        Hold hold = _holds[voice.Slot];
        hold.Voice = null;
        hold.IsFreeing = true;
        hold.IsFreeTold = _sounds.TryTell(
            new VoiceCommand { Action = VoiceAction.Free, Slot = voice.Slot, Generation = hold.Generation });
        _sounds.Slots[voice.Slot].Reflector = -1;
        voice.Slot = -1;
        Debugging.Verbose($"Entity {voice.Entity.Id}'s sound goes on in silence.");
    }

    /// <summary>
    /// Writes each heard voice's volume, pitch and loop to its slot, and, for a sound in the world, which way it is
    /// from the listener and what the simulation thread is to work out for it: its place, its focus, how it falls off,
    /// and what is in the way unless it is unoccluded. A slot no sound is in is simulated for nothing.
    /// </summary>
    private unsafe void Steer(Voices sounds, SimulationFrame filling)
    {
        IPLVector3 listener = _listener.origin;
        for (int i = 0; i < _holds.Length; i++)
        {
            filling.Slots[i] = default;

            if (_holds[i].Voice is not { } voice) continue;

            // A sound in the world fades to silence over the far end of its range, as Steam Audio's fall-off never
            // does; a seek the audio thread had no room for is told again next frame.
            Voices.Slot slot = sounds.Slots[i];
            Sound sound = voice.Sound;
            bool isWorld = sound.Space == SoundSpace.World;
            float fade = isWorld ? Math.Clamp((sound.Range - voice.Distance) / (sound.Range * Fade), 0, 1) : 1;
            slot.Gain = sound.Volume * fade;
            slot.Pitch = sound.Pitch;
            slot.IsLooping = sound.IsLooping;
            if (voice.IsSeeking)
            {
                voice.IsSeeking = !sounds.TryTell(new VoiceCommand
                {
                    Action = VoiceAction.Seek,
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
            slot.Direction = voice.Distance < Closest
                ? new IPLVector3 { z = -1 }
                : iplCalculateRelativeDirection(_context, position, listener, _listener.ahead, _listener.up);
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
    /// Gives the reflectors to the nearest heard sounds in the world that echo, keeping each with the sound it has
    /// while that is still among them, and writes where each reflector's sound is for the simulation thread.
    /// </summary>
    private void Reflect(Voices sounds, SimulationFrame filling)
    {
        if (_reflected.Length == 0) return;

        // The slots of the nearest heard sounds in the world that are not dry, one for each reflector, from the voices
        // wanting a slot, which are sorted nearest first.
        Span<int> nearest = stackalloc int[_reflected.Length];
        int count = 0;
        if (sounds.IsReflecting)
        {
            foreach (Voice voice in _wanted)
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
            if (_reflected[k] < 0 || nearest.Contains(_reflected[k])) continue;

            sounds.Slots[_reflected[k]].Reflector = -1;
            _reflected[k] = -1;
        }

        // A sound newly among them takes a free reflector; one already echoed keeps its own.
        foreach (int slot in nearest)
        {
            if (Array.IndexOf(_reflected, slot) >= 0) continue;

            int free = Array.IndexOf(_reflected, -1);
            _reflected[free] = slot;
            sounds.Slots[slot].Reflector = free;
        }

        for (int k = 0; k < _reflected.Length; k++)
        {
            int slot = _reflected[k];
            filling.IsEchoing[k] = slot >= 0 && _holds[slot].Voice is not null;
            if (filling.IsEchoing[k] && _holds[slot].Voice is { } voice)
            {
                filling.Echoes[k] =
                ToSpace(voice.Position, voice.Rotation);
            }
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
            if (_sounds is null || _soundWorld is null) return;

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
                Simulate(_sounds, _soundWorld, frame);
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
                    Trace(_sounds, frame);
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
    private unsafe void Simulate(Voices sounds, SoundWorld soundWorld, SimulationFrame frame)
    {
        long startTimestamp = _simulateDuration.Start();
        if (frame.HasSolids) soundWorld.Take(frame.Solids);

        if (soundWorld.Update(frame.ListenerPosition, frame.PlacingSec))
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
                iplSourceSetInputs(sounds.Slots[i].Source, IPL_SIMULATIONFLAGS_DIRECT, &inputs[i]);
            }
        }

        iplSimulatorRunDirect(_simulator);
        _simulateDuration.RecordSince(startTimestamp);
        _solids.Record(soundWorld.Count);
    }

    /// <summary>
    /// One trace: each reflector put where its sound is, or set to trace nothing, and the echoes worked out. On the
    /// simulation thread.
    /// </summary>
    private unsafe void Trace(Voices sounds, SimulationFrame frame)
    {
        long startTimestamp = _traceDuration.Start();
        IPLSimulationSharedInputs shared = new()
        {
            listener = frame.Listener,
            numRays = _settings.Rays,
            numBounces = frame.Bounces,
            duration = _settings.ReverbSec,
            order = _settings.Order,
            irradianceMinDistance = 1,
        };
        iplSimulatorSetSharedInputs(_simulator, IPL_SIMULATIONFLAGS_REFLECTIONS, &shared);
        IReadOnlyList<Voices.Reflector> reflectors = sounds.Reflectors;
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
    /// A sound as the gem follows it: the component as last seen, what the gem last wrote back, how far in it is, and
    /// the slot it is heard in, or -1.
    /// </summary>
    private sealed class Voice
    {
        internal Entity Entity { get; init; }

        internal Sound Sound { get; set; }

        internal Audio? Audio { get; set; }

        internal int Slot { get; set; } = -1;

        /// <summary>
        /// Which start of its slot it is, which the audio thread's word back names.
        /// </summary>
        internal int Generation { get; set; }

        /// <summary>
        /// Seconds into its sound, as the gem knows it.
        /// </summary>
        internal double TimeSec { get; set; }

        internal float WrittenTimeSec { get; set; }

        internal Playback WrittenPlayback { get; set; }

        /// <summary>
        /// Its time was set other than by playing, so the audio thread is to be told to play from there.
        /// </summary>
        internal bool IsSeeking { get; set; }

        /// <summary>
        /// The audio thread said it ended of itself, which the next frame writes back as stopped.
        /// </summary>
        internal bool HasEnded { get; set; }

        internal Vector3 Position { get; set; }

        internal Quaternion Rotation { get; set; }

        internal float Distance { get; set; }

        internal uint Seen { get; set; }

        /// <summary>
        /// The frame it was last among the nearest that are heard.
        /// </summary>
        internal uint Chosen { get; set; }
    }

    /// <summary>
    /// What the main thread holds of one slot: the voice heard in it, which start it is, and what it handed the audio
    /// thread for that start, a stream and pinned samples, kept until the audio thread lets the start go.
    /// </summary>
    private sealed class Hold
    {
        private MemoryHandle _pin;

        internal Voice? Voice { get; set; }

        internal int Generation { get; private set; }

        internal nint Stream { get; private set; }

        /// <summary>
        /// The audio thread is to let the start go; and has been told so.
        /// </summary>
        internal bool IsFreeing { get; set; }

        internal bool IsFreeTold { get; set; }

        internal unsafe void Take(Voice voice, int generation, SDL_AudioStream* stream, MemoryHandle pin)
        {
            Voice = voice;
            Generation = generation;
            Stream = (nint)stream;
            _pin = pin;
            IsFreeing = false;
            IsFreeTold = false;
        }

        /// <summary>
        /// Destroys the stream and unpins the samples, once the audio thread no longer reads them.
        /// </summary>
        internal unsafe void Let()
        {
            if (Stream != 0) SDL_DestroyAudioStream((SDL_AudioStream*)Stream);

            _pin.Dispose();
            _pin = default;
            Stream = 0;
            Voice = null;
            IsFreeing = false;
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
