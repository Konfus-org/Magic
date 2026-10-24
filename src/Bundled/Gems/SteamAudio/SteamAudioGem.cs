using System.Diagnostics.Metrics;
using System.Numerics;
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
using Magic.Services;
using static Magic.SteamAudio.IPLerror;

namespace Magic.SteamAudio;

/// <summary>
/// Plays every <see cref="Sound"/> with Steam Audio, heard from the player's camera. Each frame, in LateUpdate, every
/// entity's sound is compared with the copy kept from the last, as the physics gem compares bodies: what a script
/// changed is carried over, and how far in each sound is, and whether one has ended, is written back. The nearest
/// sounds, as many as the settings' voices, are heard; the rest go on in silence, their time running, until they are
/// near again. Once the audio device is open, the gem hands each frame to its hearing (<see cref="Hearing"/>), which
/// plays the heard ones and works out what reaches the listener.
/// <para>
/// With no window, or no audio device, there is no hearing and nothing is played: every sound goes on in silence, as
/// one out of reach does, so a script sees the same either way.
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
    /// How often the solids near the player are gathered again: what stays put needs no look every frame, and walking a
    /// world of tens of thousands of entities is most of what the gem costs the main thread.
    /// </summary>
    private const float GatherSec = 0.1f;

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
    /// What hears the sounds; none headless, or when the audio device did not open.
    /// </summary>
    private readonly Hearing? _hearing;

    /// <summary>
    /// Where the listener was last found, which it stays at while there is none.
    /// </summary>
    private Transform _listener = new();

    /// <summary>
    /// When the solids were last gathered, on the real clock; never, until the first frame or once the world empties,
    /// so they are gathered at once.
    /// </summary>
    private double _gatheredAtSec = double.NegativeInfinity;

    public SteamAudioGem(
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

        Result<Hearing> opened = Hearing.TryOpen(settings, threads, MarkUnplayable);
        if (!opened.Ok)
        {
            Debugging.Warn($"Sound is not played, as {opened.Error}. Every sound goes on in silence.");

            return;
        }

        _hearing = opened.Value;
    }

    /// <summary>
    /// The gem's meter, which its parts record on (Core's README, Telemetry).
    /// </summary>
    internal static Meter Meter { get; } = new("Magic.Gems.SteamAudio");

    /// <summary>
    /// Buckets from a tenth of a millisecond to a second, as the audio thread's work is measured against a frame of
    /// sound and the simulation's against a frame.
    /// </summary>
    internal static InstrumentAdvice<double> Buckets { get; } = new()
    {
        HistogramBucketBoundaries =
        [0.0001, 0.00025, 0.0005, 0.001, 0.002, 0.004, 0.008, 0.016, 0.032, 0.064, 0.125, 0.25, 0.5, 1]
    };

    /// <summary>
    /// Releases what the hearing made of Steam Audio and SDL once Stop has closed the device and ended the simulation
    /// thread, on the render thread, which made it.
    /// </summary>
    public void Dispose() => _hearing?.Dispose();

    public void LateUpdate(in Frame frame)
    {
        WorldStream stream = _world.Stream;
        TakeArrived();
        _surroundings.TakeArrived();
        if (stream.IsEmptied) Forget(stream.Prefabs);

        FindListener();

        // The slots the audio thread let go are taken back before the sounds are followed, so one that ended of itself
        // is written back as stopped, and its slot given out, this frame.
        _hearing?.TakeReleased();
        FollowSounds(frame.Index, stream.IsHeld ? 0 : frame.RealDeltaSec, frame.IsPaused);

        if (_hearing is not { } hearing) return;

        // A few times a second, the solids near the player are gathered into the frame the hearing hands over.
        bool hasSolids = frame.RealSec - _gatheredAtSec >= GatherSec;
        if (hasSolids)
        {
            _gatheredAtSec = frame.RealSec;
            _surroundings.Gather(_world, CollectionsMarshal.AsSpan(_players), _settings.Range, hearing.Solids);
        }

        hearing.HandOver(_listener, _wanted, frame.Index, stream.IsHeld, hasSolids);
    }

    /// <summary>
    /// Pauses the device while the app is paused, so nothing is heard until the next frame resumes it, as sound holds
    /// with the frames.
    /// </summary>
    public void Idle(EngineThread thread)
    {
        if (thread == EngineThread.Main) _hearing?.Pause();
    }

    /// <summary>
    /// Closes the device and ends the simulation thread, once the frames are over (<see cref="Hearing.Stop"/>).
    /// </summary>
    public void Stop() => _hearing?.Stop();

    /// <summary>
    /// How many of the audio files the sounds about to spawn play, and the models their solids are shaped like, are
    /// in, asking for those not on their way, so each sound is heard, muffled and echoed, from its first frame. A file
    /// that did not load counts as in, as does a model when there is no scene to fit it for.
    /// </summary>
    public float GetProgress(in WorldStream stream)
    {
        HashSet<Handle<Audio>> audios = [];
        HashSet<Handle<Model>> models = [];
        AddAudiosAndModels(stream.TakingIn, audios, models);

        int done = 0;
        foreach (Handle<Audio> audio in audios)
        {
            if (_failed.Contains(audio) || GetAudio(audio) is not null) done++;
        }

        foreach (Handle<Model> model in models)
        {
            if (_hearing is null || _surroundings.IsFitted(model)) done++;
        }

        int count = audios.Count + models.Count;

        return count == 0 ? 1 : (float)done / count;
    }

    /// <summary>
    /// Ready once the scene holds every solid of the new world's first gathering, made the frame the world emptied for
    /// it and placed with no limit on time while it is held; at once with nothing to play sound on, as there is no
    /// scene.
    /// </summary>
    public bool IsReady(in WorldStream stream) => _hearing is null || _hearing.IsReady;

    /// <summary>
    /// Throws when Steam Audio could not make <paramref name="what"/>, as the gem cannot play sound without it.
    /// </summary>
    internal static void Check(IPLerror error, string what)
    {
        if (error == IPL_STATUS_SUCCESS) return;

        throw new InvalidOperationException($"Steam Audio could not make {what}: {error}.");
    }

    /// <summary>
    /// The audio files <paramref name="prefabs"/>' sounds play, into <paramref name="audios"/>, and the models their
    /// solids are shaped like, into <paramref name="models"/>.
    /// </summary>
    private static void AddAudiosAndModels(
        IReadOnlyList<Prefab> prefabs,
        HashSet<Handle<Audio>> audios,
        HashSet<Handle<Model>> models)
    {
        foreach (Prefab prefab in prefabs)
        {
            AddAudios(prefab.Parts, audios);
            Surroundings.AddModels(prefab.Parts, models);
        }
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
    /// Takes an audio file the device cannot play as one that did not load, so the sounds that play it are silent.
    /// </summary>
    private void MarkUnplayable(Handle<Audio> audio)
    {
        _audios[audio] = null;
        _failed.Add(audio);
    }

    /// <summary>
    /// The world emptied for a new one: what the old world alone played and was solid as is let go at once, every audio
    /// file that no sound left plays and nothing <paramref name="prefabs"/> spawns does, and the triangles of every
    /// model no body left and none of those is shaped like; and the solids are gathered now, the new world's first
    /// gathering, once the scene has taken which it lets go of the sub-scenes no solid is shaped like
    /// (<see cref="Hearing.Forget"/>).
    /// </summary>
    private void Forget(IReadOnlyList<Prefab> prefabs)
    {
        HashSet<Handle<Audio>> audios = [];
        HashSet<Handle<Model>> models = [];
        AddAudiosAndModels(prefabs, audios, models);
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
        _hearing?.Forget();
        _gatheredAtSec = double.NegativeInfinity;
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

        if (listener is { } found) _listener = found;
    }

    /// <summary>
    /// Brings every voice up to date with its entity's sound in frame <paramref name="frame"/>, and writes back how far
    /// in it is: a script's writes are carried over, a sound that is not heard has its time run on as if it were, and
    /// the voices that want to be heard are listed for the hearing. While the world is paused
    /// (<paramref name="isPaused"/>), only the sounds of entities tagged <see cref="Tag.Unpausable"/> are; every other
    /// holds where it is.
    /// </summary>
    private void FollowSounds(ulong frame, float deltaSec, bool isPaused)
    {
        _wanted.Clear();
        foreach (EcsChunk<Transform, Sound> chunk in _world.Query<Transform, Sound>())
        {
            (ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<Sound> sounds) = chunk;
            for (int i = 0; i < entities.Length; i++)
            {
                bool isHeld = isPaused && !IsUnpausable(entities[i]);
                Follow(entities[i], transforms[i], ref sounds[i], frame, isHeld ? 0 : deltaSec, isHeld);
            }
        }

        // A voice this frame did not see is of an entity gone or without its sound, and its slot goes; listed first, as
        // the voices cannot change while they are walked.
        foreach ((Entity entity, Voice voice) in _voices)
        {
            if (voice.Seen != frame) _gone.Add(entity);
        }

        foreach (Entity entity in _gone)
        {
            if (_voices.Remove(entity, out Voice? voice)) _hearing?.Free(voice);
        }

        _gone.Clear();
    }

    /// <summary>
    /// <see cref="FollowSounds"/> for one entity's sound, seen in frame <paramref name="frame"/>, its time run on by
    /// <paramref name="deltaSec"/> when it is not heard; held where it is, when <paramref name="isHeld"/>, it wants no
    /// voice, so it is not heard.
    /// </summary>
    private void Follow(
        Entity entity,
        in Transform transform,
        ref Sound sound,
        ulong frame,
        float deltaSec,
        bool isHeld)
    {
        if (!_voices.TryGetValue(entity, out Voice? voice))
        {
            voice = new Voice { Entity = entity, Sound = sound, TimeSec = sound.TimeSec };
            _voices.Add(entity, voice);
        }

        // What a script wrote since the last frame, told from the sound as the gem wrote it back then: another audio or
        // space needs another slot, another audio from its start; another time is sought; and a pause or stop lets the
        // slot go, a stop back to the start.
        voice.Seen = frame;
        if (sound.Audio != voice.Sound.Audio || sound.Space != voice.Sound.Space)
        {
            _hearing?.Free(voice);
            if (sound.Audio != voice.Sound.Audio) voice.TimeSec = 0;
        }

        if (sound.TimeSec != voice.Sound.TimeSec)
        {
            voice.TimeSec = Math.Max(0, sound.TimeSec);
            voice.IsSeeking = true;
        }

        if (sound.Playback != voice.Sound.Playback && sound.Playback != Playback.Playing)
        {
            _hearing?.Free(voice);
            if (sound.Playback == Playback.Stopped) voice.TimeSec = 0;
        }

        // How far in it is now: one that ended is stopped, one heard is where the audio thread has played to unless a
        // seek is on its way, and one playing but not heard runs on in silence.
        voice.Position = transform.Position;
        voice.Rotation = transform.Rotation;
        voice.Distance = Vector3.Distance(transform.Position, _listener.Position);
        Audio? audio = GetAudio(sound.Audio);
        voice.Audio = audio;
        if (voice.HasEnded)
        {
            voice.HasEnded = false;
            sound.Playback = Playback.Stopped;
            voice.TimeSec = 0;
        }
        else if (voice.Slot >= 0 && !voice.IsSeeking && _hearing is not null)
        {
            voice.TimeSec = _hearing.GetPlayedSec(voice);
        }
        else if (voice.Slot < 0 && sound.Playback == Playback.Playing && audio is not null)
        {
            RunSilently(voice, ref sound, audio, deltaSec);
        }

        bool isAudible = !isHeld && sound.Playback == Playback.Playing && audio?.Samples.Length > 0
            && (sound.Space == SoundSpace.Everywhere || voice.Distance < sound.Range);
        if (isAudible) _wanted.Add(voice);

        // Written back for scripts to read, and kept to tell what a script writes next from it.
        sound.TimeSec = (float)voice.TimeSec;
        voice.Sound = sound;
    }

    /// <summary>
    /// Whether <paramref name="entity"/> is tagged <see cref="Tag.Unpausable"/>, so its sound plays while the world is
    /// paused.
    /// </summary>
    private bool IsUnpausable(Entity entity)
        => _world.Has<Tags>(entity) && _world.Get<Tags>(entity).Has(Tag.Unpausable);
}
