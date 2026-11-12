using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Input;
using Magic.Projects;
using Magic.SdlBindings;
using Magic.Utils;
using Magic.Worlds;
using static Magic.SdlBindings.SDL_EventType;
using static Magic.SdlBindings.SDL_GamepadAxis;
using static Magic.SdlBindings.SDL_MouseWheelDirection;
using static Magic.SdlBindings.SDL3;

namespace Magic.SdlPlatform;

/// <summary>
/// The input, through SDL3: keyboards, mice and gamepads as <see cref="IInput"/>'s controls, and typed text. Headless,
/// it reads gamepads only: keyboards and mice need a window to type and point into.
/// <para>
/// The platform thread hands SDL's events to the game thread as they came (<see cref="Platform.Pump"/>).
/// <see cref="Update"/> applies them to the controls there, before any gem that takes the input runs, as they are made
/// after this one, so input stays fixed for the rest of a frame. Losing the window's focus can pause the world.
/// </para>
/// </summary>
public sealed class Input : IInput, IDisposable
{
    /// <summary>
    /// Each stick and trigger: the control it sets, SDL's axis it is read from, and the sign the value takes. SDL's stick
    /// y grows downward; Magic's is up positive.
    /// </summary>
    private static readonly (Control Control, SDL_GamepadAxis Axis, float Sign)[] _axes =
    [
        (Control.GamepadLeftX, SDL_GAMEPAD_AXIS_LEFTX, 1),
        (Control.GamepadLeftY, SDL_GAMEPAD_AXIS_LEFTY, -1),
        (Control.GamepadRightX, SDL_GAMEPAD_AXIS_RIGHTX, 1),
        (Control.GamepadRightY, SDL_GAMEPAD_AXIS_RIGHTY, -1),
        (Control.GamepadLeftTrigger, SDL_GAMEPAD_AXIS_LEFT_TRIGGER, 1),
        (Control.GamepadRightTrigger, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, 1),
    ];

    private readonly SdlWindow _sdl;

    private readonly SdlPlatformSettings _settings;

    private readonly App _app;

    private readonly World _world;

    /// <summary>
    /// The devices as of this frame, and their controls by <see cref="ControlExtensions"/>' Index, at the same indexes.
    /// </summary>
    private readonly List<InputDevice> _devices = [];

    private readonly List<ControlState[]> _controls = [];

    /// <summary>
    /// The open gamepads, by SDL's id.
    /// </summary>
    private readonly Dictionary<uint, nint> _gamepads = [];

    internal unsafe Input(SdlWindow sdl, SdlPlatformSettings settings, App app, World world)
    {
        _sdl = sdl;
        _settings = settings;
        _app = app;
        _world = world;
        if (sdl.Pointer == null)
            return;

        // The keyboards and mice plugged in as the window opens; those plugged in later arrive as events.
        int keyboardsCount;
        uint* keyboards = SDL_GetKeyboards(&keyboardsCount);
        for (int i = 0; i < keyboardsCount; i++)
        {
            GetOrAddDevice(DeviceKind.Keyboard, keyboards[i], CString.Read(SDL_GetKeyboardNameForID(keyboards[i])));
        }

        SDL_free(keyboards);

        int miceCount;
        uint* mice = SDL_GetMice(&miceCount);
        for (int i = 0; i < miceCount; i++)
        {
            GetOrAddDevice(DeviceKind.Mouse, mice[i], CString.Read(SDL_GetMouseNameForID(mice[i])));
        }

        SDL_free(mice);
    }

    public ReadOnlySpan<InputDevice> Devices => CollectionsMarshal.AsSpan(_devices);

    public string Text { get; private set; } = string.Empty;

    public ControlState this[Control control]
    {
        get
        {
            // The control on every device of its kind as one: the value furthest from 0, and down, pressed or released
            // if any device's is.
            float value = 0;
            bool isDown = false;
            bool wasPressed = false;
            bool wasReleased = false;
            for (int i = 0; i < _devices.Count; i++)
            {
                if (_devices[i].Kind != control.DeviceKind)
                    continue;

                ControlState state = _controls[i][control.Index];
                value = MathF.Abs(state.Value) > MathF.Abs(value) ? state.Value : value;
                isDown |= state.IsDown;
                wasPressed |= state.WasPressed;
                wasReleased |= state.WasReleased;
            }

            // Released only once no device holds it down any more.
            return new ControlState(value, isDown, wasPressed, wasReleased && !isDown);
        }
    }

    public ControlState this[InputDevice device, Control control]
    {
        get
        {
            int index = device.Kind == control.DeviceKind ? FindIndex(device.Kind, device.Id) : -1;

            return index < 0 ? default : _controls[index][control.Index];
        }
    }

    public unsafe void Dispose()
    {
        foreach (nint gamepad in _gamepads.Values)
        {
            SDL_CloseGamepad((SDL_Gamepad*)gamepad);
        }
    }

    /// <summary>
    /// Makes input this frame's: what happened last frame is over, SDL's events since are applied, and sticks and
    /// triggers are read as they stand now.
    /// </summary>
    internal unsafe void Update(in Frame frame)
    {
        // What happened last frame is over: pressed and released clear, and a mouse's wheel goes back to 0. Its motion
        // eases: it keeps what the frame's share of new motion leaves of last frame's, all of it gone with no smoothing.
        // The share grows with the frame's length, so the ease is the same at any frame rate.
        float smoothingSec = _settings.MouseSmoothing;
        float motionShare = smoothingSec > 0 ? 1 - MathF.Exp(-(float)frame.AppDeltaSec / smoothingSec) : 1;
        for (int i = 0; i < _devices.Count; i++)
        {
            ControlState[] states = _controls[i];
            for (int c = 0; c < states.Length; c++)
            {
                states[c] = states[c] with { WasPressed = false, WasReleased = false };
            }

            if (_devices[i].Kind != DeviceKind.Mouse)
                continue;

            states[Control.MouseMotionX.Index] = default(ControlState) with
            {
                Value = states[Control.MouseMotionX.Index].Value * (1 - motionShare),
            };
            states[Control.MouseMotionY.Index] = default(ControlState) with
            {
                Value = states[Control.MouseMotionY.Index].Value * (1 - motionShare),
            };
            states[Control.MouseWheelX.Index] = default;
            states[Control.MouseWheelY.Index] = default;
        }

        Text = string.Empty;
        ApplyEvents(motionShare);

        // Sticks and triggers as they stand now, rather than added up from events. SDL's -32768 to 32767 becomes -1 to
        // 1: 0 inside the dead zone, and past it growing from 0 to 1 at full deflection along the stick's curve, so the
        // zone's edge is no step. Triggers only reach the positive half.
        float deadZone = _settings.StickDeadZone;
        foreach ((uint id, nint gamepad) in _gamepads)
        {
            ControlState[] states = _controls[FindIndex(DeviceKind.Gamepad, id)];
            foreach ((Control control, SDL_GamepadAxis axis, float sign) in _axes)
            {
                float raw = Math.Max(SDL_GetGamepadAxis((SDL_Gamepad*)gamepad, axis) / 32767f, -1f);
                float past = MathF.Abs(raw) - deadZone;
                float stick =
                    past <= 0 ? 0 : MathF.CopySign(MathF.Pow(past / (1 - deadZone), _settings.StickCurve), raw);
                SetState(states, control, sign * stick);
            }
        }
    }

    /// <summary>
    /// Gives a control a new value, and with it whether it went down or came up. Pressed and released only ever turn on
    /// within a frame, so a tap between two frames shows as both.
    /// </summary>
    private void SetState(ControlState[] states, Control control, float value)
    {
        ref ControlState state = ref states[control.Index];
        bool isDown = MathF.Abs(value) >= _settings.PressThreshold;
        state = new ControlState(
            value,
            isDown,
            state.WasPressed || (isDown && !state.IsDown),
            state.WasReleased || (!isDown && state.IsDown)
        );
    }

    /// <summary>
    /// Applies SDL's events the platform thread handed over since the last frame to the devices and their controls, in
    /// the order they happened, with <paramref name="motionShare"/> of the mouse's new motion.
    /// </summary>
    [SuppressMessage(
        "Style",
        "IDE0010:Add missing cases",
        Justification = "SDL has far more events than the input reads; the rest are ignored on purpose."
    )]
    private unsafe void ApplyEvents(float motionShare)
    {
        while (_sdl.Events.Reader.TryRead(out (SDL_Event Event, string Text) pumped))
        {
            SDL_Event sdlEvent = pumped.Event;
            switch ((SDL_EventType)sdlEvent.type)
            {
                case SDL_EVENT_QUIT:
                    Log.Info("Exiting: the window was closed or the system asked Magic to quit.");
                    _app.Exit();
                    break;
                case SDL_EVENT_WINDOW_FOCUS_GAINED:
                case SDL_EVENT_WINDOW_FOCUS_LOST:
                    _sdl.IsFocused = (SDL_EventType)sdlEvent.type == SDL_EVENT_WINDOW_FOCUS_GAINED;

                    // PROTO: coming back resumes a world the game had paused itself, until a game's menu owns the pause.
                    if (_settings.PauseWhenUnfocused)
                        _world.IsPaused = !_sdl.IsFocused;

                    break;
                case SDL_EVENT_KEYBOARD_ADDED:
                    GetOrAddDevice(DeviceKind.Keyboard, sdlEvent.kdevice.which, pumped.Text);
                    break;
                case SDL_EVENT_KEYBOARD_REMOVED:
                    RemoveDevice(DeviceKind.Keyboard, sdlEvent.kdevice.which);
                    break;
                case SDL_EVENT_MOUSE_ADDED:
                    GetOrAddDevice(DeviceKind.Mouse, sdlEvent.mdevice.which, pumped.Text);
                    break;
                case SDL_EVENT_MOUSE_REMOVED:
                    RemoveDevice(DeviceKind.Mouse, sdlEvent.mdevice.which);
                    break;
                case SDL_EVENT_GAMEPAD_ADDED:
                    OpenGamepad(sdlEvent.gdevice.which);
                    break;
                case SDL_EVENT_GAMEPAD_REMOVED:
                    CloseGamepad(sdlEvent.gdevice.which);
                    break;
                case SDL_EVENT_KEY_DOWN:
                case SDL_EVENT_KEY_UP:
                    if (!sdlEvent.key.repeat && sdlEvent.key.scancode.Control is { } key)
                        SetState(
                            GetOrAddDevice(DeviceKind.Keyboard, sdlEvent.key.which, string.Empty),
                            key,
                            sdlEvent.key.down ? 1 : 0
                        );

                    break;
                case SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EVENT_MOUSE_BUTTON_UP:
                    if (SdlControlExtensions.ToMouseControl(sdlEvent.button.button) is { } button)
                    {
                        SetState(
                            GetOrAddDevice(DeviceKind.Mouse, sdlEvent.button.which, string.Empty),
                            button,
                            sdlEvent.button.down ? 1 : 0
                        );
                    }

                    break;
                case SDL_EVENT_MOUSE_MOTION:
                {
                    // SDL's points as window pixels; motion and wheel count up over the frame, the motion scaled by
                    // the sensitivity and taken at the frame's share.
                    float density = Volatile.Read(ref _sdl.PixelDensity);
                    float motionScale = density * _settings.MouseSensitivity * motionShare;
                    ControlState[] mouse = GetOrAddDevice(DeviceKind.Mouse, sdlEvent.motion.which, string.Empty);
                    SetState(mouse, Control.MouseX, sdlEvent.motion.x * density);
                    SetState(mouse, Control.MouseY, sdlEvent.motion.y * density);
                    SetState(
                        mouse,
                        Control.MouseMotionX,
                        mouse[Control.MouseMotionX.Index].Value + (sdlEvent.motion.xrel * motionScale)
                    );
                    SetState(
                        mouse,
                        Control.MouseMotionY,
                        mouse[Control.MouseMotionY.Index].Value + (sdlEvent.motion.yrel * motionScale)
                    );

                    break;
                }

                case SDL_EVENT_MOUSE_WHEEL:
                {
                    // A wheel the OS flips (natural scrolling) is flipped back, so away from the player is positive on
                    // every system.
                    ControlState[] mouse = GetOrAddDevice(DeviceKind.Mouse, sdlEvent.wheel.which, string.Empty);
                    float direction = sdlEvent.wheel.direction == SDL_MOUSEWHEEL_FLIPPED ? -1 : 1;
                    SetState(
                        mouse,
                        Control.MouseWheelX,
                        mouse[Control.MouseWheelX.Index].Value + (sdlEvent.wheel.x * direction)
                    );
                    SetState(
                        mouse,
                        Control.MouseWheelY,
                        mouse[Control.MouseWheelY.Index].Value + (sdlEvent.wheel.y * direction)
                    );

                    break;
                }

                case SDL_EVENT_TEXT_INPUT:
                    Text += pumped.Text;
                    break;
                case SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                case SDL_EVENT_GAMEPAD_BUTTON_UP:
                {
                    // Only a pad that is open: one that could not be opened is no device.
                    int pad = FindIndex(DeviceKind.Gamepad, sdlEvent.gbutton.which);
                    if (pad >= 0 && ((SDL_GamepadButton)sdlEvent.gbutton.button).Control is { } gamepadButton)
                        SetState(_controls[pad], gamepadButton, sdlEvent.gbutton.down ? 1 : 0);

                    break;
                }
            }
        }
    }

    private int FindIndex(DeviceKind kind, uint id)
    {
        for (int i = 0; i < _devices.Count; i++)
        {
            if (_devices[i].Kind == kind && _devices[i].Id == id)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// A keyboard's or mouse's controls, adding it as <paramref name="name"/> the first time it is heard of: input can
    /// arrive before its device does, unnamed, and some from no device at all (id 0).
    /// </summary>
    private ControlState[] GetOrAddDevice(DeviceKind kind, uint id, string name)
    {
        int index = FindIndex(kind, id);

        return index >= 0 ? _controls[index] : AddDevice(kind, id, name);
    }

    /// <summary>
    /// Adds a device and its controls: its kind's block of 256, at each control's index. One that gives no name is
    /// shown as what it is.
    /// </summary>
    private ControlState[] AddDevice(DeviceKind kind, uint id, string name)
    {
        _devices.Add(new InputDevice(kind, id, string.IsNullOrEmpty(name) ? kind.ToString() : name));
        ControlState[] states = new ControlState[256];
        _controls.Add(states);

        return states;
    }

    private void RemoveDevice(DeviceKind kind, uint id)
    {
        int index = FindIndex(kind, id);
        _devices.RemoveAt(index);
        _controls.RemoveAt(index);
    }

    private unsafe void OpenGamepad(uint id)
    {
        if (_gamepads.ContainsKey(id))
            return;

        SDL_Gamepad* gamepad = SDL_OpenGamepad(id);
        if (gamepad == null)
        {
            Log.Warn($"A gamepad was connected but could not be opened: {Sdl.Error}");

            return;
        }

        _gamepads[id] = (nint)gamepad;
        string name = CString.Read(SDL_GetGamepadName(gamepad));
        AddDevice(DeviceKind.Gamepad, id, name);
        Log.Info($"Gamepad connected: {name}");
    }

    private unsafe void CloseGamepad(uint id)
    {
        if (!_gamepads.Remove(id, out nint gamepad))
            return;

        Log.Info($"Gamepad disconnected: {CString.Read(SDL_GetGamepadName((SDL_Gamepad*)gamepad))}");
        SDL_CloseGamepad((SDL_Gamepad*)gamepad);
        RemoveDevice(DeviceKind.Gamepad, id);
    }
}
