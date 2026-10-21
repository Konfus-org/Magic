using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.SdlBindings;
using Magic.Services;

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
/// A domain that replaces the world is readied before it is shown (<see cref="ReadyAsync"/>): its sounds are held,
/// none played and none's time running, until the loading is told over, the frame after the world is first shown, and
/// the solids near the listener are all placed meanwhile, with no limit on how long an update of the scene takes, as no
/// sound waits on it, so the first sound heard is muffled and echoed as it should be.
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
    private const double PlacingSeconds = 0.008;

    /// <summary>
    /// How often the solids near the player are gathered again: what stays put needs no look every frame, and walking a
    /// world of tens of thousands of entities is most of what the gem costs the main thread.
    /// </summary>
    private const float GatherSeconds = 0.1f;

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
    private static readonly Histogram<double> _simulateDuration = _meter.CreateHistogram(
        "magic.gems.steam_audio.simulate.duration",
        "s",
        "How long the simulation thread took to bring the scene up to date and work out the direct sound.",
        tags: null,
        advice: _buckets);
    private static readonly Histogram<double> _traceDuration = _meter.CreateHistogram(
        "magic.gems.steam_audio.trace.duration",
        "s",
        "How long one trace of the echoes took, on the simulation thread.",
        tags: null,
        advice: _buckets);

    private readonly World _world;
    private readonly Assets _assets;
    private readonly Threads _threads;
    private readonly SteamAudioSettings _settings;
    private readonly Dictionary<Entity, Voice> _voices = [];
    private readonly List<Voice> _wanted = [];
    private readonly List<Entity> _gone = [];
    private readonly List<Vector3> _players = [];
    private readonly Surroundings _surroundings;
    private readonly Subscription _watching;

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
    /// The domains whose sounds and solids are being loaded before they spawn, checked every frame until all are.
    /// </summary>
    private readonly List<Preload> _preloads = [];

    /// <summary>
    /// Set by the main thread when it has handed over a frame, or as the run ends, waking the simulation thread.
    /// </summary>
    private readonly ManualResetEventSlim _wake = new();

    /// <summary>
    /// Set by the simulation thread as it returns, which Stop waits for.
    /// </summary>
    private readonly ManualResetEventSlim _simulationEnded = new();
    private readonly unsafe IPL.Context* _context;
    private readonly unsafe IPL.HRTF* _hrtf;
    private readonly unsafe IPL.EmbreeDevice* _embree;
    private readonly unsafe IPL.Scene* _scene;
    private readonly unsafe IPL.Simulator* _simulator;
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
    private unsafe SDL.AudioStream* _device;
    private GCHandle _self;
    private IPL.CoordinateSpace3 _listener = new()
    {
        Right = new IPL.Vector3 { X = 1 },
        Up = new IPL.Vector3 { Y = 1 },
        Ahead = new IPL.Vector3 { Z = -1 }
    };
    private Vector3 _listenerPosition;
    private int _filling;
    private int _newest = 1;
    private int _reading = 2;
    private int _isNewest;
    private float _sinceGathered = GatherSeconds;

    /// <summary>
    /// The run is ending: the simulation thread returns when next woken.
    /// </summary>
    private bool _isEnding;

    /// <summary>
    /// Which frame this is, stamped on each voice seen in it: one not stamped is of an entity gone.
    /// </summary>
    private uint _frame;

    /// <summary>
    /// A domain replacing the world is being readied, and no sound plays nor has its time run until the loading is
    /// told over (<see cref="ReadyAsync"/>). Main thread.
    /// </summary>
    private bool _isHeld;

    /// <summary>
    /// How many times the solids have been gathered, each frame handed over stamped with it. Main thread.
    /// </summary>
    private int _gathered;

    /// <summary>
    /// The gathering whose solids, all placed, end the readying, and what ends it; none when nothing is being readied.
    /// Main thread.
    /// </summary>
    private int _readyAt;
    private TaskCompletionSource? _readied;

    /// <summary>
    /// The gathering last taken whose solids the scene held every one of after an update, written by the simulation
    /// thread for the main thread.
    /// </summary>
    private int _placed;

    public unsafe SteamAudioGem(
        World world,
        Assets assets,
        Threads threads,
        Events events,
        Args run,
        SteamAudioSettings settings)
    {
        _world = world;
        _assets = assets;
        _threads = threads;
        _settings = settings;
        _surroundings = new Surroundings(assets);
        _watching = events.Watch<WorldLoading>(loading => _isHeld &= loading.IsLoading);
        if (run.Headless)
        {
            Debugging.Info("Sound is not played headless: every sound goes on in silence.");
            return;
        }

        if (!SDL.InitSubSystem(SDL.InitAudio))
        {
            Debugging.Warn("Sound is not played, as SDL could not start this machine's audio: "
                + $"{CString.Read(SDL.GetError())}. Every sound goes on in silence.");
            return;
        }

        _isAudioStarted = true;
        IPL.ContextSettings contextSettings = new()
        {
            Version = IPL.VERSION,
            LogCallback = &OnLog,
            SimdLevel = IPL.SIMDLevel.Avx2
        };
        IPL.Context* context;
        Check(IPL.ContextCreate(&contextSettings, &context), "its context");
        _context = context;

        IPL.AudioSettings audio = new() { SamplingRate = settings.SampleRate, FrameSize = settings.FrameSize };
        IPL.HRTFSettings hrtfSettings = new()
        {
            Type = IPL.HRTFType.Default,
            Volume = 1,
            NormType = IPL.HRTFNormType.None
        };
        IPL.HRTF* hrtf;
        Check(IPL.HRTFCreate(context, &audio, &hrtfSettings, &hrtf), "the head the listener hears with");
        _hrtf = hrtf;

        // Embree traces several times faster than Steam Audio's own tracer, which stands in where it cannot start.
        IPL.EmbreeDevice* embree;
        IPL.SceneSettings sceneSettings = IPL.EmbreeDeviceCreate(context, null, &embree) == IPL.Error.Success
            ? new IPL.SceneSettings { Type = IPL.SceneType.Embree, EmbreeDevice = embree }
            : new IPL.SceneSettings { Type = IPL.SceneType.Default };
        _embree = sceneSettings.EmbreeDevice;
        IPL.Scene* scene;
        Check(IPL.SceneCreate(context, &sceneSettings, &scene), "the scene");
        _scene = scene;

        IPL.SimulationSettings simulation = new()
        {
            Flags = settings.ReflectionSources > 0
                ? IPL.SimulationFlags.Direct | IPL.SimulationFlags.Reflections
                : IPL.SimulationFlags.Direct,
            SceneType = sceneSettings.Type,
            ReflectionType = IPL.ReflectionEffectType.Convolution,
            MaxNumOcclusionSamples = OcclusionSamples,
            MaxNumRays = settings.Rays,
            NumDiffuseSamples = 32,
            MaxDuration = settings.ReverbSeconds,
            MaxOrder = settings.Order,
            MaxNumSources = Math.Max(1, settings.ReflectionSources),
            NumThreads = Math.Max(1, Environment.ProcessorCount / 4),
            SamplingRate = settings.SampleRate,
            FrameSize = settings.FrameSize,
        };
        IPL.Simulator* simulator;
        Check(IPL.SimulatorCreate(context, &simulation, &simulator), "the simulator");
        _simulator = simulator;
        IPL.SceneCommit(scene);
        IPL.SimulatorSetScene(simulator, scene);
        Voices sounds = new(context, hrtf, simulator, settings);
        IPL.SimulatorCommit(simulator);

        _self = GCHandle.Alloc(sounds);
        SDL.AudioSpec device = new() { Format = SDL.AudioFormat.F32, Channels = 2, Freq = settings.SampleRate };
        _device = SDL.OpenAudioDeviceStream(
            SDL.AudioDeviceDefaultPlayback,
            &device,
            &Voices.OnAudio,
            (void*)GCHandle.ToIntPtr(_self));
        if (_device is null)
        {
            // With nothing to play them, the voices go, and every sound runs on in silence, as one out of reach does.
            Debugging.Warn("Sound is not played, as SDL could not open this machine's audio device: "
                + $"{CString.Read(SDL.GetError())}. Every sound goes on in silence.");
            _self.Free();
            sounds.Dispose();
            return;
        }

        _sounds = sounds;
        _soundWorld = new SoundWorld(context, scene, sceneSettings);
        _holds = new Hold[settings.Voices];
        for (int i = _holds.Length - 1; i >= 0; i--)
        {
            _holds[i] = new Hold();
            _free.Push(i);
        }

        _reflected = new int[settings.ReflectionSources];
        Array.Fill(_reflected, -1);
        _frames = [new SimulationFrame(settings), new SimulationFrame(settings), new SimulationFrame(settings)];
        SDL.ResumeAudioStreamDevice(_device);
        _isSimulating = true;
        threads.RunAsync("Audio simulation", RunSimulation).FireAndForget();
        string tracer = sceneSettings.Type == IPL.SceneType.Embree ? "Embree" : "its own tracer";
        Debugging.Info($"Steam Audio {IPL.VersionMajor}.{IPL.VersionMinor}.{IPL.VersionPatch} is playing up to "
            + $"{settings.Voices} sounds at {settings.SampleRate} Hz, "
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
        _watching.Dispose();
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
            IPL.Simulator* simulator = _simulator;
            IPL.SimulatorRelease(&simulator);
            IPL.Scene* scene = _scene;
            IPL.SceneRelease(&scene);
            if (_embree is not null)
            {
                IPL.EmbreeDevice* embree = _embree;
                IPL.EmbreeDeviceRelease(&embree);
            }

            IPL.HRTF* hrtf = _hrtf;
            IPL.HRTFRelease(&hrtf);
            IPL.Context* context = _context;
            IPL.ContextRelease(&context);
        }

        if (_isAudioStarted) SDL.QuitSubSystem(SDL.InitAudio);
    }

    public void LateUpdate(in Frame frame)
    {
        _frame++;
        TakeArrived();
        _surroundings.TakeArrived();
        CheckPreloads();
        FindListener();
        if (_sounds is not null)
        {
            _sounds.Volume = _settings.Volume;
            _sounds.IsReflecting = _settings.Reflections && _reflected.Length > 0;
            TakeReleased(_sounds);
        }

        FollowSounds(_isHeld ? 0 : frame.DeltaSeconds);
        if (_sounds is null) return;

        if (!_isHeld) Assign();

        SimulationFrame filling = _frames[_filling];
        filling.Listener = _listener;
        filling.ListenerPosition = _listenerPosition;
        filling.Bounces = _settings.Bounces;
        filling.IsReflecting = _sounds.IsReflecting;
        filling.ReflectionPeriod = 1 / Math.Clamp(_settings.ReflectionRate, 1, 60);
        filling.PlacingSeconds = _isHeld ? double.PositiveInfinity : PlacingSeconds;
        Steer(_sounds, filling);
        Reflect(_sounds, filling);
        _sinceGathered += frame.DeltaSeconds;
        filling.HasSolids = _sinceGathered >= GatherSeconds;
        if (filling.HasSolids)
        {
            _sinceGathered = 0;
            _gathered++;
            _surroundings.Gather(_world, CollectionsMarshal.AsSpan(_players), _settings.Range, filling.Solids);
        }

        filling.Gathered = _gathered;
        _filling = Interlocked.Exchange(ref _newest, _filling);
        Volatile.Write(ref _isNewest, 1);
        _wake.Set();
        _heard.Record(_holds.Length - _free.Count);
        if (_readied is null || Volatile.Read(ref _placed) < _readyAt) return;

        _readied.TrySetResult();
        _readied = null;
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
            SDL.DestroyAudioStream(_device);
            _device = null;
        }

        Volatile.Write(ref _isEnding, true);
        _wake.Set();
        if (_isSimulating) _simulationEnded.Wait(CancellationToken.None);
    }

    /// <summary>
    /// Loads every audio file a sound among <paramref name="prefabs"/> plays, and fits every model a solid among them
    /// is shaped like, before they spawn, so each sound is heard, muffled and echoed, from its first frame. Done once
    /// all are in; they are kept for the run, so what it hands back holds nothing.
    /// </summary>
    public async Task<Subscription> PreloadAsync(
        IReadOnlyList<Prefab> prefabs,
        IProgress<float> progress,
        CancellationToken cancel)
    {
        HashSet<Handle<Audio>> audios = [];
        HashSet<Handle<Model>> models = [];
        foreach (Prefab prefab in prefabs)
        {
            AddAudios(prefab.Parts, audios);
            Surroundings.AddModels(prefab.Parts, models);
        }

        Preload preload = new(audios, models, progress, cancel);
        await _threads.InvokeAsync(EngineThread.Main, () =>
        {
            foreach (Handle<Audio> audio in audios)
            {
                GetAudio(audio);
            }

            _preloads.Add(preload);
        }, cancel).ConfigureAwait(false);
        await preload.Done.Task.ConfigureAwait(false);

        return Subscription.None;
    }

    /// <summary>
    /// Holds every sound from now until the loading is told over (<see cref="WorldLoading"/>), the frame after the world
    /// is first shown: none is played and none's time runs. The solids around the listener are gathered at once and
    /// placed with no limit on time, and the task ends once the scene holds every one of them; at once with nothing to
    /// play sound on, as there is no scene.
    /// </summary>
    public Task ReadyAsync(CancellationToken cancel)
    {
        _isHeld = true;
        if (_sounds is null) return Task.CompletedTask;

        _sinceGathered = GatherSeconds;
        _readyAt = _gathered + 1;
        TaskCompletionSource readied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _readied?.TrySetCanceled(CancellationToken.None);
        _readied = readied;
        return readied.Task.WaitAsync(cancel);
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

    private static void Check(IPL.Error error, string what)
    {
        if (error == IPL.Error.Success) return;

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
    private static IPL.CoordinateSpace3 ToSpace(Vector3 position, Quaternion rotation)
    {
        return new IPL.CoordinateSpace3
        {
            Right = SoundWorld.ToVector(Vector3.Transform(Vector3.UnitX, rotation)),
            Up = SoundWorld.ToVector(Vector3.Transform(Vector3.UnitY, rotation)),
            Ahead = SoundWorld.ToVector(Vector3.Transform(Vector3.UnitZ, rotation)),
            Origin = SoundWorld.ToVector(position),
        };
    }

    /// <summary>
    /// Runs a sound's time on as if it were heard, at its pitch: one that loops wraps, and one that does not stops at
    /// its end.
    /// </summary>
    private static void RunSilently(Voice voice, ref Sound sound, Audio audio, float seconds)
    {
        voice.Time += seconds * Math.Clamp(sound.Pitch, 0.5f, 2);
        if (voice.Time < audio.Seconds) return;

        if (sound.Loop && audio.Seconds > 0)
        {
            voice.Time %= audio.Seconds;
            return;
        }

        voice.Time = 0;
        sound.Playback = Playback.Stopped;
    }

    /// <summary>
    /// What Steam Audio logs: a warning or an error as one, and the rest only when asked for.
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe void OnLog(IPL.LogLevel level, byte* message)
    {
        string text = $"Steam Audio: {CString.Read(message).TrimEnd()}";
        switch (level)
        {
            case IPL.LogLevel.Error:
                Debugging.Error(text);
                break;
            case IPL.LogLevel.Warning:
                Debugging.Warn(text);
                break;
            case IPL.LogLevel.Info:
            case IPL.LogLevel.Debug:
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
    /// Checks each domain whose sounds and solids are being loaded: one whose open was cancelled is let go; else how
    /// many are in tells how far it is, and once all are, whoever waits for it goes on and spawns it.
    /// </summary>
    private void CheckPreloads()
    {
        for (int i = _preloads.Count - 1; i >= 0; i--)
        {
            Preload preload = _preloads[i];
            if (preload.Cancel.IsCancellationRequested)
            {
                _preloads.RemoveAt(i);
                preload.Done.TrySetCanceled(preload.Cancel);
                continue;
            }

            int done = 0;
            foreach (Handle<Audio> audio in preload.Audios)
            {
                if (_failed.Contains(audio) || GetAudio(audio) is not null) done++;
            }

            foreach (Handle<Model> model in preload.Models)
            {
                if (_sounds is null || _surroundings.IsFitted(model)) done++;
            }

            int count = preload.Audios.Count + preload.Models.Count;
            float share = count == 0 ? 1 : (float)done / count;
            if (share != preload.Reported)
            {
                preload.Reported = share;
                preload.Progress.Report(share);
            }

            if (done == count)
            {
                _preloads.RemoveAt(i);
                preload.Done.TrySetResult();
            }
        }
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
    /// are listed for <see cref="Assign"/>.
    /// </summary>
    private void FollowSounds(float seconds)
    {
        _wanted.Clear();
        foreach (EcsChunk<Transform, Sound> chunk in _world.Query<Transform, Sound>())
        {
            (ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<Sound> sounds) = chunk;
            for (int i = 0; i < entities.Length; i++)
            {
                Follow(entities[i], transforms[i], ref sounds[i], seconds);
            }
        }

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

    private void Follow(Entity entity, in Transform transform, ref Sound sound, float seconds)
    {
        if (!_voices.TryGetValue(entity, out Voice? voice))
        {
            voice = new Voice
            {
                Entity = entity,
                Sound = sound,
                Time = sound.Time,
                WrittenTime = sound.Time,
                WrittenPlayback = sound.Playback
            };
            _voices.Add(entity, voice);
        }

        voice.Seen = _frame;
        if (sound.Audio != voice.Sound.Audio || sound.Space != voice.Sound.Space)
        {
            Free(voice);
            if (sound.Audio != voice.Sound.Audio) voice.Time = 0;
        }

        if (sound.Time != voice.WrittenTime)
        {
            voice.Time = Math.Max(0, sound.Time);
            voice.IsSeeking = true;
        }

        if (sound.Playback != voice.WrittenPlayback && sound.Playback != Playback.Playing)
        {
            Free(voice);
            if (sound.Playback == Playback.Stopped) voice.Time = 0;
        }

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
            voice.Time = 0;
        }
        else if (voice.Slot >= 0 && _sounds is not null && !voice.IsSeeking)
        {
            Voices.Slot slot = _sounds.Slots[voice.Slot];
            if (slot.Started == voice.Generation) voice.Time = slot.Seconds;
        }
        else if (voice.Slot < 0 && sound.Playback == Playback.Playing && audio is not null)
        {
            RunSilently(voice, ref sound, audio, seconds);
        }

        bool isAudible = sound.Playback == Playback.Playing && audio?.Samples.Length > 0
            && (sound.Space == SoundSpace.Everywhere || voice.Distance < sound.Range);
        if (isAudible && _sounds is not null) _wanted.Add(voice);

        sound.Time = (float)voice.Time;
        voice.WrittenTime = sound.Time;
        voice.WrittenPlayback = sound.Playback;
        voice.Sound = sound;
    }

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
        Hold hold = _holds[index];
        SDL.AudioSpec from = new() { Format = SDL.AudioFormat.F32, Channels = audio.Channels, Freq = audio.SampleRate };
        SDL.AudioSpec to = new()
        {
            Format = SDL.AudioFormat.F32,
            Channels = voice.Sound.Space == SoundSpace.Everywhere ? 2 : 1,
            Freq = _settings.SampleRate
        };
        SDL.AudioStream* stream = SDL.CreateAudioStream(&from, &to);
        if (stream is null)
        {
            Debugging.Error($"Sounds that play an audio of {audio.Channels} channel(s) at {audio.SampleRate} Hz are "
                + $"silent: SDL cannot convert it to the device: {CString.Read(SDL.GetError())}");
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
            Seconds = voice.Time,
            Stream = (nint)stream,
            Samples = (nint)pin.Pointer,
            Count = audio.Samples.Length,
            Channels = audio.Channels,
            Rate = audio.SampleRate,
            Duration = audio.Seconds,
            IsEverywhere = voice.Sound.Space == SoundSpace.Everywhere,
        };
        if (!_sounds.TryTell(command))
        {
            pin.Dispose();
            SDL.DestroyAudioStream(stream);
            return;
        }

        _free.Pop();
        hold.Take(voice, command.Generation, stream, pin);
        voice.Slot = index;
        voice.Generation = command.Generation;
        voice.IsSeeking = false;
        Debugging.Verbose(
            $"Entity {voice.Entity.Id}'s sound is heard, {voice.Distance:0.#} m away, from {voice.Time:0.##} s in.");
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
        IPL.Vector3 listener = _listener.Origin;
        for (int i = 0; i < _holds.Length; i++)
        {
            filling.Slots[i] = default;
            if (_holds[i].Voice is not { } voice) continue;

            Voices.Slot slot = sounds.Slots[i];
            Sound sound = voice.Sound;
            bool isWorld = sound.Space == SoundSpace.World;
            float fade = isWorld ? Math.Clamp((sound.Range - voice.Distance) / (sound.Range * Fade), 0, 1) : 1;
            slot.Gain = sound.Volume * fade;
            slot.Pitch = sound.Pitch;
            slot.Loop = sound.Loop;
            if (voice.IsSeeking)
            {
                voice.IsSeeking = !sounds.TryTell(new VoiceCommand
                {
                    Action = VoiceAction.Seek,
                    Slot = i,
                    Generation = voice.Generation,
                    Seconds = voice.Time
                });
            }

            if (!isWorld) continue;

            IPL.Vector3 position = SoundWorld.ToVector(voice.Position);
            slot.Direction = voice.Distance < Closest
                ? new IPL.Vector3 { Z = -1 }
                : IPL.CalculateRelativeDirection(_context, position, listener, _listener.Ahead, _listener.Up);
            IPL.DirectSimulationFlags simulated = IPL.DirectSimulationFlags.Distanceattenuation
                | IPL.DirectSimulationFlags.Airabsorption
                | IPL.DirectSimulationFlags.Directivity;
            IPL.DirectEffectFlags applied = IPL.DirectEffectFlags.Applydistanceattenuation
                | IPL.DirectEffectFlags.Applyairabsorption
                | IPL.DirectEffectFlags.Applydirectivity;
            if (!sound.Unoccluded)
            {
                simulated |= IPL.DirectSimulationFlags.Occlusion | IPL.DirectSimulationFlags.Transmission;
                applied |= IPL.DirectEffectFlags.Applyocclusion | IPL.DirectEffectFlags.Applytransmission;
            }

            slot.Flags = applied;
            filling.Slots[i] = new IPL.SimulationInputs
            {
                Flags = IPL.SimulationFlags.Direct,
                DirectFlags = simulated,
                Source = ToSpace(voice.Position, voice.Rotation),
                DistanceAttenuationModel = new IPL.DistanceAttenuationModel
                {
                    Type = IPL.DistanceAttenuationModelType.Inversedistance,
                    MinDistance = sound.Near
                },
                AirAbsorptionModel = new IPL.AirAbsorptionModel { Type = IPL.AirAbsorptionModelType.Default },
                Directivity = new IPL.Directivity { DipoleWeight = Math.Clamp(sound.Focus, 0, 1), DipolePower = 1 },
                OcclusionType = IPL.OcclusionType.Volumetric,
                OcclusionRadius = 0.5f,
                NumOcclusionSamples = OcclusionSamples,
                NumTransmissionRays = 3,
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

        Span<int> nearest = stackalloc int[_reflected.Length];
        int count = 0;
        if (sounds.IsReflecting)
        {
            foreach (Voice voice in _wanted)
            {
                if (count == nearest.Length) break;
                if (voice.Slot >= 0 && voice.Sound.Space == SoundSpace.World
                    && !voice.Sound.Dry)
                {
                    nearest[count++] = voice.Slot;
                }
            }
        }

        nearest = nearest[..count];
        for (int k = 0; k < _reflected.Length; k++)
        {
            if (_reflected[k] < 0 || nearest.Contains(_reflected[k])) continue;

            sounds.Slots[_reflected[k]].Reflector = -1;
            _reflected[k] = -1;
        }

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

            long traced = 0;
            int taken = 0;
            while (!stopping.IsCancellationRequested)
            {
                _wake.Wait(CancellationToken.None);
                _wake.Reset();
                if (Volatile.Read(ref _isEnding)) return;
                if (Interlocked.Exchange(ref _isNewest, 0) == 0) continue;

                _reading = Interlocked.Exchange(ref _newest, _reading);
                SimulationFrame frame = _frames[_reading];
                if (frame.HasSolids) taken = frame.Gathered;

                Simulate(_sounds, _soundWorld, frame);
                if (_soundWorld.IsWhole) Volatile.Write(ref _placed, taken);

                if (frame.IsReflecting && Array.IndexOf(frame.IsEchoing, true) >= 0
                    && Stopwatch.GetElapsedTime(traced).TotalSeconds >= frame.ReflectionPeriod)
                {
                    traced = Stopwatch.GetTimestamp();
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
        long start = Stopwatch.GetTimestamp();
        if (frame.HasSolids) soundWorld.Take(frame.Solids);

        if (soundWorld.Update(frame.ListenerPosition, frame.PlacingSeconds))
        {
            IPL.SceneCommit(_scene);
            IPL.SimulatorCommit(_simulator);
        }

        IPL.SimulationSharedInputs shared = new() { Listener = frame.Listener };
        IPL.SimulatorSetSharedInputs(_simulator, IPL.SimulationFlags.Direct, &shared);
        fixed (IPL.SimulationInputs* inputs = frame.Slots)
        {
            for (int i = 0; i < frame.Slots.Length; i++)
            {
                IPL.SourceSetInputs(sounds.Slots[i].Source, IPL.SimulationFlags.Direct, &inputs[i]);
            }
        }

        IPL.SimulatorRunDirect(_simulator);
        _simulateDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
        _solids.Record(soundWorld.Count);
    }

    /// <summary>
    /// One trace: each reflector put where its sound is, or set to trace nothing, and the echoes worked out. On the
    /// simulation thread.
    /// </summary>
    private unsafe void Trace(Voices sounds, SimulationFrame frame)
    {
        long start = Stopwatch.GetTimestamp();
        IPL.SimulationSharedInputs shared = new()
        {
            Listener = frame.Listener,
            NumRays = _settings.Rays,
            NumBounces = frame.Bounces,
            Duration = _settings.ReverbSeconds,
            Order = _settings.Order,
            IrradianceMinDistance = 1,
        };
        IPL.SimulatorSetSharedInputs(_simulator, IPL.SimulationFlags.Reflections, &shared);
        IReadOnlyList<Voices.Reflector> reflectors = sounds.Reflectors;
        for (int k = 0; k < reflectors.Count; k++)
        {
            IPL.SimulationInputs source = new()
            {
                Flags = frame.IsEchoing[k] ? IPL.SimulationFlags.Reflections : default,
                Source = frame.Echoes[k],
                DistanceAttenuationModel = new IPL.DistanceAttenuationModel
                {
                    Type = IPL.DistanceAttenuationModelType.Default
                },
                AirAbsorptionModel = new IPL.AirAbsorptionModel { Type = IPL.AirAbsorptionModelType.Default },
            };
            source.ReverbScale[0] = 1;
            source.ReverbScale[1] = 1;
            source.ReverbScale[2] = 1;
            IPL.SourceSetInputs(reflectors[k].Source, IPL.SimulationFlags.Reflections, &source);
        }

        IPL.SimulatorRunReflections(_simulator);
        _traceDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
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
        internal double Time { get; set; }

        internal float WrittenTime { get; set; }

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

        internal unsafe void Take(Voice voice, int generation, SDL.AudioStream* stream, MemoryHandle pin)
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
            if (Stream != 0) SDL.DestroyAudioStream((SDL.AudioStream*)Stream);

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
        internal IPL.CoordinateSpace3 Listener { get; set; }

        internal Vector3 ListenerPosition { get; set; }

        internal IPL.SimulationInputs[] Slots { get; } = new IPL.SimulationInputs[settings.Voices];

        internal IPL.CoordinateSpace3[] Echoes { get; } = new IPL.CoordinateSpace3[settings.ReflectionSources];

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
        /// How long the scene's update may spend placing new solids (<see cref="SoundWorld.Update"/>).
        /// </summary>
        internal double PlacingSeconds { get; set; }

        internal int Bounces { get; set; }

        internal bool IsReflecting { get; set; }

        internal float ReflectionPeriod { get; set; }
    }

    /// <summary>
    /// A domain whose sounds and solids are being loaded before it spawns: which, how far that is as last told, the
    /// task whoever opens it awaits, and the token that cancels the open, checked each frame.
    /// </summary>
    private sealed class Preload(
        HashSet<Handle<Audio>> audios,
        HashSet<Handle<Model>> models,
        IProgress<float> progress,
        CancellationToken cancel)
    {
        internal HashSet<Handle<Audio>> Audios { get; } = audios;

        internal HashSet<Handle<Model>> Models { get; } = models;

        internal IProgress<float> Progress { get; } = progress;

        internal CancellationToken Cancel { get; } = cancel;

        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal float Reported { get; set; } = -1;
    }
}
