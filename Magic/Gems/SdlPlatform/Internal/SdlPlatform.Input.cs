using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Utils;
using SDL3;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SdlPlatformGem;

/// <summary>
/// Keyboard, mouse and gamepads as <see cref="IInput"/>, and keyboard, text and mouse as published events for whoever
/// needs every one in order (text fields, the debug UI). The pump updates the live state as it takes each event;
/// <see cref="LatchInput"/>, at the end of the pump, copies that into the frame state every reader sees. Gamepads are
/// opened as they connect and kept in the first free slot.
/// </summary>
internal sealed partial class SdlPlatform : IInput
{
    private const int MaxGamepads = 8;

    private static int KeyCount { get; } = Enum.GetValues<Key>().Length;
    private static int MouseButtonCount { get; } = Enum.GetValues<MouseButton>().Length;
    private static int GamepadButtonCount { get; } = Enum.GetValues<GamepadButton>().Length;
    private static int GamepadAxisCount { get; } = Enum.GetValues<GamepadAxis>().Length;

    private readonly Buttons _keys = new(KeyCount);
    private readonly Buttons _mouseButtons = new(MouseButtonCount);
    private readonly Gamepad?[] _gamepads = new Gamepad?[MaxGamepads];

    private Vector2 _liveMouseDelta, _liveMouseWheel;

    public Vector2 MousePosition { get; private set; }

    public Vector2 MouseDelta { get; private set; }

    public Vector2 MouseWheel { get; private set; }

    public bool IsDown(Key key) => _keys.IsDown((int)key);

    public bool WasPressed(Key key) => _keys.WasPressed((int)key);

    public bool WasReleased(Key key) => _keys.WasReleased((int)key);

    public bool IsDown(MouseButton button) => _mouseButtons.IsDown((int)button);

    public bool WasPressed(MouseButton button) => _mouseButtons.WasPressed((int)button);

    public bool WasReleased(MouseButton button) => _mouseButtons.WasReleased((int)button);

    public bool IsConnected(int gamepad) => Slot(gamepad) is not null;

    public bool IsDown(int gamepad, GamepadButton button) => Slot(gamepad)?.Buttons.IsDown((int)button) ?? false;

    public bool WasPressed(int gamepad, GamepadButton button) => Slot(gamepad)?.Buttons.WasPressed((int)button) ?? false;

    public bool WasReleased(int gamepad, GamepadButton button) => Slot(gamepad)?.Buttons.WasReleased((int)button) ?? false;

    public float Axis(int gamepad, GamepadAxis axis) => Slot(gamepad)?.Axes[(int)axis] ?? 0f;

    /// <summary>
    /// Makes what arrived since the last latch the state every reader sees until the next.
    /// </summary>
    private void LatchInput()
    {
        _keys.Latch();
        _mouseButtons.Latch();
        MouseDelta = _liveMouseDelta;
        MouseWheel = _liveMouseWheel;
        _liveMouseDelta = _liveMouseWheel = Vector2.Zero;

        foreach (Gamepad? gamepad in _gamepads)
        {
            gamepad?.Buttons.Latch();
            gamepad?.LiveAxes.CopyTo(gamepad.Axes, 0);
        }
    }

    private Gamepad? Slot(int gamepad)
    {
        return (uint)gamepad < MaxGamepads ? _gamepads[gamepad] : null;
    }

    private Gamepad? Find(uint id)
    {
        int slot = SlotOf(id);
        return slot < 0 ? null : _gamepads[slot];
    }

    /// <summary>
    /// The slot of the open gamepad with SDL id <paramref name="id"/>, or -1: a plain loop, as every button and axis
    /// event asks.
    /// </summary>
    private int SlotOf(uint id)
    {
        for (int i = 0; i < _gamepads.Length; i++)
        {
            if (_gamepads[i]?.Id == id)
                return i;
        }

        return -1;
    }

    private void OnInputEvent(ref SDL.Event e)
    {
        switch ((SDL.EventType)e.Type)
        {
            case SDL.EventType.KeyDown:
            case SDL.EventType.KeyUp:
                Key key = e.Key.Key.ToKey();
                if (!e.Key.Repeat)
                    _keys.Set((int)key, e.Key.Down);
                _events.Publish(new KeyEvent(e.Key.WindowID, key, e.Key.Mod.ToModifiers(), e.Key.Down, e.Key.Repeat));
                break;
            case SDL.EventType.TextInput:
                if (Marshal.PtrToStringUTF8(e.Text.Text) is { Length: > 0 } text)
                    _events.Publish(new TextInput(e.Text.WindowID, text));
                break;
            case SDL.EventType.MouseMotion:
                MousePosition = new Vector2(e.Motion.X, e.Motion.Y);
                _liveMouseDelta += new Vector2(e.Motion.XRel, e.Motion.YRel);
                _events.Publish(new MouseMoved(e.Motion.WindowID, MousePosition));
                break;
            case SDL.EventType.MouseButtonDown:
            case SDL.EventType.MouseButtonUp:
                if (ToMouseButton(e.Button.Button) is not { } button)
                    break;
                _mouseButtons.Set((int)button, e.Button.Down);
                _events.Publish(new MouseButtonEvent(e.Button.WindowID, button, e.Button.Down));
                break;
            case SDL.EventType.MouseWheel:
                Vector2 wheel = new(e.Wheel.X, e.Wheel.Y);
                _liveMouseWheel += wheel;
                _events.Publish(new MouseWheel(e.Wheel.WindowID, wheel));
                break;
            case SDL.EventType.GamepadAdded:
                OnGamepadAdded(e.GDevice.Which);
                break;
            case SDL.EventType.GamepadRemoved:
                OnGamepadRemoved(e.GDevice.Which);
                break;
            case SDL.EventType.GamepadButtonDown:
            case SDL.EventType.GamepadButtonUp:
                if (e.GButton.Button < GamepadButtonCount)
                    Find(e.GButton.Which)?.Buttons.Set(e.GButton.Button, e.GButton.Down);
                break;
            case SDL.EventType.GamepadAxisMotion:
                if (e.GAxis.Axis < GamepadAxisCount && Find(e.GAxis.Which) is { } gamepad)
                    gamepad.LiveAxes[e.GAxis.Axis] = Math.Max(e.GAxis.Value / 32767f, -1f); // SDL's order matches GamepadAxis
                break;
        }
    }

    private void CloseGamepads()
    {
        foreach (Gamepad? gamepad in _gamepads)
        {
            if (gamepad is not null)
                SDL.CloseGamepad(gamepad.Handle);
        }
    }

    private void OnGamepadAdded(uint id)
    {
        int slot = Array.IndexOf(_gamepads, null);
        if (slot < 0 || Find(id) is not null)
            return;

        nint handle = SDL.OpenGamepad(id);
        if (handle == 0)
        {
            Log.Warn($"Gamepad {id} connected but could not be opened: {SDL.GetError()}");
            return;
        }

        _gamepads[slot] = new Gamepad(id, handle);
        Log.Info($"Gamepad {slot} connected: {SDL.GetGamepadName(handle)}.");
    }

    private void OnGamepadRemoved(uint id)
    {
        int slot = SlotOf(id);
        if (slot < 0 || _gamepads[slot] is not { } gamepad)
            return;

        SDL.CloseGamepad(gamepad.Handle);
        _gamepads[slot] = null;
        Log.Info($"Gamepad {slot} disconnected.");
    }

    private static MouseButton? ToMouseButton(byte button) => button switch
    {
        1 => MouseButton.Left,
        2 => MouseButton.Middle,
        3 => MouseButton.Right,
        4 => MouseButton.X1,
        5 => MouseButton.X2,
        _ => null,
    };

    /// <summary>
    /// Down, pressed and released per button: live as events arrive, frame as readers see it. A press stays pressed
    /// until the next <see cref="Latch"/> even if the button was let go again in between.
    /// </summary>
    private sealed class Buttons(int count)
    {
        private readonly bool[] _liveDown = new bool[count];
        private readonly bool[] _livePressed = new bool[count];
        private readonly bool[] _liveReleased = new bool[count];
        private readonly bool[] _down = new bool[count];
        private readonly bool[] _pressed = new bool[count];
        private readonly bool[] _released = new bool[count];

        public bool IsDown(int button) => _down[button];

        public bool WasPressed(int button) => _pressed[button];

        public bool WasReleased(int button) => _released[button];

        public void Set(int button, bool down)
        {
            if (_liveDown[button] == down)
                return;

            _liveDown[button] = down;
            if (down)
                _livePressed[button] = true;
            else
                _liveReleased[button] = true;
        }

        public void Latch()
        {
            _liveDown.CopyTo(_down, 0);
            _livePressed.CopyTo(_pressed, 0);
            _liveReleased.CopyTo(_released, 0);
            Array.Clear(_livePressed);
            Array.Clear(_liveReleased);
        }
    }

    private sealed class Gamepad(uint id, nint handle)
    {
        public uint Id { get; } = id;

        public nint Handle { get; } = handle;

        public Buttons Buttons { get; } = new(GamepadButtonCount);

        public float[] LiveAxes { get; } = new float[GamepadAxisCount];

        public float[] Axes { get; } = new float[GamepadAxisCount];
    }
}
