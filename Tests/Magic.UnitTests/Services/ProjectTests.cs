using Magic.Services;
using Xunit;

namespace Magic.UnitTests.Services;

public sealed class ProjectTests
{
    [Fact]
    public void Assets_hang_off_the_root()
    {
        Project project = new() { Root = "Demo" };

        string assets = project.Assets;

        Assert.Equal(Path.Combine("Demo", "Assets"), assets);
    }
}
