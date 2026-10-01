using Magic.Contexts.Events;
using Magic.Contexts.Input;
using Magic.Services;
using SDL3;
using SDLGem;
using SDLInputGem;
using Xunit;

namespace Magic.IntegrationTests.Input;

/// <summary>
/// The input gem over a real SDL, fed with pushed events: SDL runs the gem's watch as each is queued, and a
/// <see cref="Step"/> is one frame's Update (the SDL gem's pump, then the input gem's).
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class SdlInputTests : IDisposable
{
    private readonly Events _events = new();
    private readonly Sdl _sdl = new(new Project { Name = "Tests" });
    private readonly SdlInput _input;

    public SdlInputTests()
    {
        _input = new SdlInput(_events);
    }

    public void Dispose()
    {
        _input.Dispose();
        _sdl.Dispose();
    }

    [Fact]
    public void A_key_pushed_down_reads_as_pressed_that_frame()
    {
        PushKey(SDL.Keycode.A, down: true);

        Step();

        Assert.True(_input.WasPressed(Key.A));
    }

    [Fact]
    public void A_held_key_is_pressed_for_one_frame_only()
    {
        PushKey(SDL.Keycode.A, down: true);
        Step();

        Step();

        Assert.False(_input.WasPressed(Key.A));
    }

    [Fact]
    public void A_held_key_stays_down()
    {
        PushKey(SDL.Keycode.A, down: true);
        Step();

        Step();

        Assert.True(_input.IsDown(Key.A));
    }

    [Fact]
    public void A_key_let_go_reads_as_released_that_frame()
    {
        PushKey(SDL.Keycode.A, down: true);
        Step();
        PushKey(SDL.Keycode.A, down: false);

        Step();

        Assert.True(_input.WasReleased(Key.A));
    }

    [Fact]
    public void A_key_let_go_is_no_longer_down()
    {
        PushKey(SDL.Keycode.A, down: true);
        Step();
        PushKey(SDL.Keycode.A, down: false);

        Step();

        Assert.False(_input.IsDown(Key.A));
    }

    [Fact]
    public void A_tap_inside_one_frame_still_reads_as_pressed()
    {
        PushKey(SDL.Keycode.Space, down: true);
        PushKey(SDL.Keycode.Space, down: false);

        Step();

        Assert.True(_input.WasPressed(Key.Space));
    }

    [Fact]
    public void A_key_is_also_published_as_an_event()
    {
        PushKey(SDL.Keycode.F3, down: true);

        Event[] frame = _events.NextFrame();

        Assert.Equal([new Event(EventType.KeyDown, 0, Key: Key.F3)], frame);
    }

    [Fact]
    public void Mouse_motion_adds_up_over_a_frame()
    {
        PushMotion(10, 20, 3, 4);
        PushMotion(12, 25, 2, 5);

        Step();

        Assert.Equal(new(5, 9), _input.MouseDelta);
    }

    [Fact]
    public void The_mouse_delta_resets_on_the_next_frame()
    {
        PushMotion(10, 20, 3, 4);
        Step();

        Step();

        Assert.Equal(default, _input.MouseDelta);
    }

    [Fact]
    public void The_mouse_position_is_where_the_last_motion_left_it()
    {
        PushMotion(10, 20, 3, 4);
        PushMotion(12, 25, 2, 5);

        Step();

        Assert.Equal(new(12, 25), _input.MousePosition);
    }

    [Theory]
    [InlineData(7)]  // nothing plugged in there
    [InlineData(-1)] // not a slot at all
    public void A_gamepad_that_is_not_there_is_not_connected(int gamepad)
    {
        Step();

        Assert.False(_input.IsConnected(gamepad));
    }

    /// <summary>One frame's Update, in load order.</summary>
    private void Step()
    {
        _sdl.Update(default);
        _input.Update(default);
    }

    private static void PushKey(SDL.Keycode key, bool down)
    {
        SDL.Event sdlEvent = default;
        sdlEvent.Key.Type = down ? SDL.EventType.KeyDown : SDL.EventType.KeyUp;
        sdlEvent.Key.Key = key;
        sdlEvent.Key.Down = down;
        Assert.True(SDL.PushEvent(ref sdlEvent), SDL.GetError());
    }

    private static void PushMotion(float x, float y, float dx, float dy)
    {
        SDL.Event sdlEvent = default;
        sdlEvent.Motion.Type = SDL.EventType.MouseMotion;
        sdlEvent.Motion.X = x;
        sdlEvent.Motion.Y = y;
        sdlEvent.Motion.XRel = dx;
        sdlEvent.Motion.YRel = dy;
        Assert.True(SDL.PushEvent(ref sdlEvent), SDL.GetError());
    }
}
