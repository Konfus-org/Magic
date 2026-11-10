using System.ComponentModel;
using System.Drawing;
using Magic.Configuration;
using Magic.Files;
using Magic.Projects;
using Magic.Utils;
using Xunit;

namespace Magic.Tests.Configuration;

/// <summary>
/// A gem's settings set by key: the gem runs by them, and the gem made again, as after a rebuild, starts with them.
/// </summary>
public sealed class SettingsTests
{
    [Fact]
    public void SetByKeyHoldsAcrossARegistration()
    {
        using App app = new();
        Settings settings = new(app, new FileSystem());
        DrawGem first = new(new DrawSettings());
        Subscription registered = settings.Register(first);

        Assert.True(settings.Set("Rendering.Resolution", new Size(2560, 1440)).Ok);
        Assert.Equal(new Size(2560, 1440), first.Settings.Resolution);

        registered.Dispose();
        DrawGem second = new(settings.Get<DrawSettings>());
        settings.Register(second).Dispose();

        Assert.Equal(new Size(2560, 1440), second.Settings.Resolution);
    }

    [Fact]
    public async Task SavedSettingsOpenWithTheProject()
    {
        // A project of its own, whose folder under the user's local application data goes when the test ends.
        FileSystem files = new();
        string name = $"Magic.Tests.{Guid.NewGuid():N}";
        Project project = new()
        {
            Name = name,
            Gems = [],
            Path = $"{name}.magic",
        };
        using App app = new() { Project = project };
        try
        {
            Settings saving = new(app, files);
            using Subscription registered = saving.Register(new DrawGem(saving.Get<DrawSettings>()));
            Assert.True(saving.Set("Rendering.Resolution", new Size(1280, 720)).Ok);
            Assert.True((await saving.SaveAsync(TestContext.Current.CancellationToken)).Ok);

            Settings opening = new(app, files);

            Assert.Equal(new Size(1280, 720), opening.Get<DrawSettings>().Resolution);
        }
        finally
        {
            files.Delete(UserFolders.GetProjectFolder(files, project), recursive: true);
        }
    }

    [Category("Rendering")]
    private sealed record DrawSettings
    {
        public Size Resolution { get; init; } = new(1920, 1080);
    }

    private sealed class DrawGem(DrawSettings settings) : IGem, IHasSettings<DrawSettings>
    {
        public DrawSettings Settings { get; set; } = settings;
    }
}
