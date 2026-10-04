using Magic.Contexts.Settings;
using Xunit;

namespace Magic.UnitTests.Settings;

public sealed class ShadowSettingsTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(9, 4)]
    public void The_cascade_count_is_held_between_one_and_four(int asked, int kept)
    {
        ShadowSettings settings = new() { Cascades = asked };

        Assert.Equal(kept, settings.Cascades);
    }

    [Theory]
    [InlineData(16, 256)]
    [InlineData(1024, 1024)]
    [InlineData(100000, 4096)]
    public void The_cascade_resolution_is_held_in_range(int asked, int kept)
    {
        ShadowSettings settings = new() { CascadeResolution = asked };

        Assert.Equal(kept, settings.CascadeResolution);
    }

    [Fact]
    public void A_negative_filter_radius_is_held_at_zero()
    {
        ShadowSettings settings = new() { FilterRadius = -1f };

        Assert.Equal(0f, settings.FilterRadius);
    }

    [Fact]
    public void The_local_light_count_can_be_zero()
    {
        ShadowSettings settings = new() { MaxLocalLights = -3 };

        Assert.Equal(0, settings.MaxLocalLights);
    }
}
