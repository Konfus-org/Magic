using Magic.Contexts.Assets;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Services;
using System.Text.Json;
using Xunit;

namespace Magic.UnitTests.Contexts;

public sealed class SettingsTests
{
    [Fact]
    public void A_registered_object_takes_the_preset_value()
    {
        Settings settings = new();
        settings.Apply(Preset(("Sample.Speed", "3")));

        SampleSettings sample = Register(settings);

        Assert.Equal(3f, sample.Speed);
    }

    [Fact]
    public void Applying_a_preset_changes_a_registered_object()
    {
        Settings settings = new();
        SampleSettings sample = Register(settings);

        settings.Apply(Preset(("Sample.Speed", "5")));

        Assert.Equal(5f, sample.Speed);
    }

    [Fact]
    public void Applying_a_preset_puts_what_it_does_not_name_back_to_its_default()
    {
        Settings settings = new();
        SampleSettings sample = Register(settings);
        settings.Apply(Preset(("Sample.Speed", "5")));

        settings.Apply(Preset());

        Assert.Equal(new SampleSettings().Speed, sample.Speed);
    }

    [Fact]
    public void An_override_stays_over_a_preset_applied_later()
    {
        Settings settings = new(["Sample.Speed=7"]);
        SampleSettings sample = Register(settings);

        settings.Apply(Preset(("Sample.Speed", "5")));

        Assert.Equal(7f, sample.Speed);
    }

    [Fact]
    public void A_value_that_does_not_fit_leaves_the_default()
    {
        Settings settings = new(["Sample.Speed=\"fast\""]);

        SampleSettings sample = Register(settings);

        Assert.Equal(new SampleSettings().Speed, sample.Speed);
    }

    [Fact]
    public void Applying_a_preset_makes_new_values()
    {
        Settings settings = new();
        IReadOnlyDictionary<string, JsonElement> before = settings.Values;

        settings.Apply(Preset(("ShadowPlan.distance", "120")));

        Assert.NotSame(before, settings.Values);
    }

    [Fact]
    public void Set_changes_a_registered_object()
    {
        Settings settings = new();
        SampleSettings sample = Register(settings);

        settings.Set(["Sample.Speed=9"]);

        Assert.Equal(9f, sample.Speed);
    }

    [Fact]
    public void A_preset_applied_later_replaces_a_set_value()
    {
        Settings settings = new();
        SampleSettings sample = Register(settings);
        settings.Set(["Sample.Speed=9"]);

        settings.Apply(Preset(("Sample.Speed", "5")));

        Assert.Equal(5f, sample.Speed);
    }

    [Fact]
    public void A_set_value_is_over_a_start_up_override()
    {
        Settings settings = new(["Sample.Speed=7"]);
        SampleSettings sample = Register(settings);

        settings.Set(["Sample.Speed=9"]);

        Assert.Equal(9f, sample.Speed);
    }

    [Fact]
    public void A_start_up_override_comes_back_when_a_preset_drops_a_set_value()
    {
        Settings settings = new(["Sample.Speed=7"]);
        SampleSettings sample = Register(settings);
        settings.Set(["Sample.Speed=9"]);

        settings.Apply(Preset(("Sample.Speed", "5")));

        Assert.Equal(7f, sample.Speed);
    }

    [Fact]
    public void Set_keeps_the_applied_preset_under_it()
    {
        Settings settings = new();
        settings.Apply(Preset(("ShadowPlan.distance", "120")));

        settings.Set(["Sample.Speed=9"]);

        Assert.Equal(120, settings.Values["ShadowPlan.distance"].GetInt32());
    }

    [Fact]
    public void Set_makes_new_values()
    {
        Settings settings = new();
        IReadOnlyDictionary<string, JsonElement> before = settings.Values;

        settings.Set(["ShadowPlan.distance=500"]);

        Assert.NotSame(before, settings.Values);
    }

    [Fact]
    public void Set_returns_the_lines_that_are_not_overrides()
    {
        Settings settings = new();

        string[] unused = settings.Set(["Speed=9", "Sample.Speed=9"]);

        Assert.Equal(["Speed=9"], unused);
    }

    [Fact]
    public void An_unregistered_object_leaves_all()
    {
        Settings settings = new();
        Register(settings);

        ((IRegisterFromGem<SettingsAttribute>)settings).Unregister(typeof(SampleSettings));

        Assert.Empty(settings.All);
    }

    private static SampleSettings Register(Settings settings)
    {
        return (SampleSettings)(((IRegisterFromGem<SettingsAttribute>)settings).Register(typeof(SampleSettings))
            ?? throw new InvalidOperationException("Nothing was registered."));
    }

    private static Preset Preset(params (string Key, string Json)[] values)
    {
        return new Preset { Values = values.ToDictionary(value => value.Key, value => JsonDocument.Parse(value.Json).RootElement.Clone()) };
    }

    [Settings("Sample")]
    private sealed class SampleSettings
    {
        public float Speed { get; set; } = 1f;
    }
}
