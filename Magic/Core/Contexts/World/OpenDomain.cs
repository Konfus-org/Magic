using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Domain;

namespace Magic.Services;

/// <summary>
/// One domain open in the <see cref="World"/>: how it was opened and how far it is. Whatever fills the world (the
/// streaming system) sets <see cref="State"/> and <see cref="Progress"/> as it goes; the progress goes on to whoever
/// opened the domain, and the domain being <see cref="DomainState.Loaded"/> is published as <see cref="DomainLoaded"/>.
/// Main thread.
/// </summary>
public sealed class OpenDomain
{
    private readonly Events _events;
    private readonly IProgress<float>? _progress;

    internal OpenDomain(Handle<Domain> handle, Domain domain, OpenMode mode, IProgress<float>? progress, Events events)
    {
        Handle = handle;
        Domain = domain;
        Mode = mode;
        _progress = progress;
        _events = events;
    }

    public Handle<Domain> Handle { get; }

    /// <summary>
    /// The domain's own file, as it was when it was opened.
    /// </summary>
    public Domain Domain { get; }

    public OpenMode Mode { get; }

    /// <summary>
    /// <see cref="DomainState.Loading"/> until its globals and the chunks its cameras want are there, then
    /// <see cref="DomainState.Loaded"/>; <see cref="DomainState.Closed"/> once it is closed. Set to Loaded, it is
    /// published as <see cref="DomainLoaded"/> and its progress is 1; set back to Loading (its file changed), it fills again.
    /// </summary>
    public DomainState State
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            if (value != DomainState.Loaded)
                return;

            Progress = 1f;
            _events.Publish(new DomainLoaded(Handle.Id));
        }
    } = DomainState.Loading;

    /// <summary>
    /// How much of it is there while it is <see cref="DomainState.Loading"/>, 0 to 1; told on to whoever opened it.
    /// </summary>
    public float Progress
    {
        get;
        set
        {
            field = value;
            _progress?.Report(value);
        }
    }
}
