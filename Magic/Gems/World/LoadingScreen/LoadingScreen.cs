using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Domain;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace LoadingScreenGem;

/// <summary>
/// The gem: while the world loads (<see cref="World.State"/>), its loading domain is open over it as an
/// <see cref="OpenMode.Overlay"/>, and closed once the world is there. The domain is the <c>LoadingScreen.Domain</c>
/// setting (<c>--loading</c>), else the project's <c>"loading"</c>, else the gem's own,
/// <c>Domains/Loading/Loading.domain</c>: the project's icon over a bar (the scripts LoadingIcon and LoadingBar,
/// compiled into this gem).
/// </summary>
internal sealed class LoadingScreen : IGem
{
    /// <summary>
    /// The gem's own loading domain, for a project that names none.
    /// </summary>
    private const string OwnDomain = "Domains/Loading/Loading.domain";

    private readonly World _world;
    private Handle<Domain> _screen; // none once it could not be opened

    public LoadingScreen(World world, Assets assets, Project project, LoadingScreenSettings settings)
    {
        _world = world;
        _screen = settings.Domain.Length > 0 ? assets.Find<Domain>(settings.Domain) : project.Read<Handle<Domain>>("loading");
        if (!_screen.IsValid)
            _screen = assets.Find<Domain>(OwnDomain);

        if (!_screen.IsValid)
            Log.Warn($"There is no loading domain ({(settings.Domain.Length > 0 ? settings.Domain : OwnDomain)}): the world shows nothing while it loads.");
    }

    public void Update(in Frame frame)
    {
        if (!_screen.IsValid)
            return;

        bool loading = _world.State == DomainState.Loading;
        bool shown = _world.Find(_screen) is not null;
        if (loading && !shown && _world.Open(_screen, OpenMode.Overlay) is { Failed: true } opened)
        {
            Log.Error($"{opened.Message} The world shows nothing while it loads.");
            _screen = Handle<Domain>.None;
        }
        else if (!loading && shown)
            _world.Close(_screen);
    }
}
