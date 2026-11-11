using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Files;
using Magic.Input;
using Magic.Platform;
using Magic.Projects;
using Magic.SdlBindings;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;
using static Magic.SdlBindings.SDL_EventType;
using static Magic.SdlBindings.SDL_GamepadAxis;
using static Magic.SdlBindings.SDL_MouseWheelDirection;
using static Magic.SdlBindings.SDL_PixelFormat;
using static Magic.SdlBindings.SDL3;

namespace Magic.SdlPlatform;

/// <summary>
/// The window, clipboard and input, through SDL3. Opens the window, keeps the clipboard, and turns keyboards, mice and
/// gamepads into <see cref="IInput"/>'s controls and typed text. Headless, it opens no window, keeps the clipboard's text
/// to itself and reads gamepads only: keyboards and mice need a window to type and point into.
/// <para>
/// SDL's windows and events belong to the process's first thread, the platform thread, which makes the gem and pumps
/// SDL's events (<see cref="Pump"/>), handing them to the game thread as they came. <see cref="Update"/> applies them to
/// the controls there, before any gem that takes the input runs, as they are made after this one, so input stays fixed
/// for the rest of a frame. What the game thread sets on the window and clipboard is handed to the platform thread
/// the same way. It is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists
/// the gem.
/// </para>
/// <para>
/// It also paces the frames: <see cref="Update"/> first waits out the frame rate its settings cap the frames at, one cap
/// while the window has the focus and another while it hasn't, and losing the focus can pause the world.
/// </para>
/// </summary>
public sealed class SdlPlatformGem
    : IGem,
        IHasSettings<SdlPlatformSettings>,
        IPlatform,
        IWindow,
        IClipboard,
        IInput,
        IDisposable
{
    /// <summary>
    /// The longest a pump waits for the OS's next event, so what was handed to the platform thread is never kept longer.
    /// </summary>
    private const int PumpWaitMs = 10;

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

    private readonly App _app;

    private readonly Threads _threads;

    private readonly AssetManager _assets;

    private readonly World _world;

    private readonly Meter _meter;

    private readonly unsafe SDL_Window* _window;

    /// <summary>
    /// The devices as of this frame, and their controls by <see cref="ControlExtensions"/>' Index, at the same indexes.
    /// </summary>
    private readonly List<InputDevice> _devices = [];

    private readonly List<ControlState[]> _controls = [];

    /// <summary>
    /// The open gamepads, by SDL's id.
    /// </summary>
    private readonly Dictionary<uint, nint> _gamepads = [];

    /// <summary>
    /// What holds the window's Metal layer on macOS, so its handle has one to name; null elsewhere.
    /// </summary>
    private readonly unsafe void* _metalView;

    /// <summary>
    /// SDL's events, pumped on the platform thread and applied on the game thread, each with the text SDL holds only until
    /// the platform thread's next pump: the name of the keyboard or mouse it adds, or what was typed.
    /// </summary>
    private readonly Channel<(SDL_Event Event, string Text)> _events = Channel.CreateUnbounded<(SDL_Event, string)>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    /// <summary>
    /// What the game thread set on the window and clipboard, each written whole and taken by the platform thread's next
    /// pump; null once taken.
    /// </summary>
    private string? _titleToShow;

    private string? _clipboardToSet;

    private SdlPlatformSettings _settings;

    /// <summary>
    /// Window pixels to SDL's points, written whole by each pump for the game thread's mouse motion.
    /// </summary>
    private float _pixelDensity = 1;

    /// <summary>
    /// The window's size in pixels and its display's scale, read by the platform thread as the window opens and on each
    /// pump, and written whole for any thread; 0 and 1 when headless.
    /// </summary>
    private int _width;

    private int _height;

    private float _displayScale = 1;

    private string _title;

    private string _clipboard = string.Empty;

    private string _text = string.Empty;

    /// <summary>
    /// Whether the window has the keyboard's focus, as its last focus event said; always, headless.
    /// </summary>
    private bool _isFocused = true;

    /// <summary>
    /// When the last frame the frame cap held back was let go, 0 before the first.
    /// </summary>
    private long _pacedTimestamp;

    public unsafe SdlPlatformGem(
        SdlPlatformSettings settings,
        App app,
        Threads threads,
        AssetManager assets,
        IFileSystem files,
        World world
    )
    {
        _settings = settings;
        _app = app;
        _threads = threads;
        _assets = assets;
        _world = world;

        // The gem's meter goes with it, so nothing holds the gem's code once it unloads. The OS is asked only when the
        // meter is read, as the number is the process's rather than a frame's.
        _meter = new Meter("Magic.Gems.SdlPlatform");
        _meter.CreateObservableGauge<long>(
            "magic.gems.sdl_platform.process.memory.usage",
            () => ProcessMemory.GetPrivateResidentBytes(files) is { } bytes ? [new(bytes)] : [],
            "By",
            "RAM only this process holds, as the OS's tools show it."
        );
        _title = app.Project?.Name ?? string.Empty;

        uint systems = app.IsHeadless ? SDL_INIT_GAMEPAD : SDL_INIT_VIDEO | SDL_INIT_GAMEPAD;
        if (!SDL_Init(systems))
            throw new InvalidOperationException($"SDL could not start: {Sdl.Error}");

        if (app.IsHeadless)
            return;

        ulong mode = settings.Mode switch
        {
            WindowMode.Fullscreen => SDL_WINDOW_FULLSCREEN,
            WindowMode.Maximized => SDL_WINDOW_MAXIMIZED,
            WindowMode.Windowed => 0,
            _ => throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.Mode,
                "There is no such window mode."
            ),
        };

        // macOS draws through a Metal layer, which the window must be opened for; elsewhere the OS's window is enough. The
        // window opens centred on its display, as one already fullscreen or maximized can't be moved there.
        ulong flags =
            mode
            | (settings.IsResizable ? SDL_WINDOW_RESIZABLE : 0)
            | SDL_WINDOW_HIGH_PIXEL_DENSITY
            | (OperatingSystem.IsMacOS() ? SDL_WINDOW_METAL : 0);
        long centered = SDL_WINDOWPOS_CENTERED_MASK | FindDisplay(settings.Display);
        uint properties = SDL_CreateProperties();
        SetNumber(properties, SDL_PROP_WINDOW_CREATE_FLAGS_NUMBER, (long)flags);
        SetNumber(properties, SDL_PROP_WINDOW_CREATE_X_NUMBER, centered);
        SetNumber(properties, SDL_PROP_WINDOW_CREATE_Y_NUMBER, centered);
        SetNumber(properties, SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER, settings.WindowedSize.Width);
        SetNumber(properties, SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER, settings.WindowedSize.Height);
        fixed (
            byte* name = SDL_PROP_WINDOW_CREATE_TITLE_STRING,
                title = CString.ToBytes(_title)
        )
        {
            SDL_SetStringProperty(properties, name, title);
        }

        _window = SDL_CreateWindowWithProperties(properties);
        SDL_DestroyProperties(properties);

        if (_window == null)
        {
            string error = Sdl.Error;
            SDL_Quit();

            throw new InvalidOperationException($"SDL could not open a window: {error}");
        }

        if (OperatingSystem.IsMacOS())
            _metalView = SDL_Metal_CreateView(_window);

        Handle = ReadHandle();
        ReadSize();
        _isFocused = (SDL_GetWindowFlags(_window) & SDL_WINDOW_INPUT_FOCUS) != 0;

        // Typed characters come as text events only while text input is on: always, as a desktop has no on-screen
        // keyboard for it to bring up.
        if (!SDL_StartTextInput(_window))
            Log.Warn($"Typed text is not read: {Sdl.Error}");

        _clipboard = ReadClipboard();

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
        Icon = assets.GetDefault<Icon>();
    }

    public string Title
    {
        get => _title;
        set
        {
            ThrowIfNotOnGame();
            _title = value;
            Volatile.Write(ref _titleToShow, value);
        }
    }

    /// <summary>
    /// Set on the game thread, it reaches the window once it and its image have loaded, unless another was set meanwhile.
    /// </summary>
    public unsafe Handle<Icon> Icon
    {
        get => field;
        set
        {
            ThrowIfNotOnGame();
            field = value;
            if (_window != null)
                ShowAsync(value).FireAndForget();
        }
    }

    /// <summary>
    /// 0 when headless.
    /// </summary>
    public int Width => Volatile.Read(ref _width);

    /// <summary>
    /// 0 when headless.
    /// </summary>
    public int Height => Volatile.Read(ref _height);

    /// <summary>
    /// 1 when headless.
    /// </summary>
    public float DisplayScale => Volatile.Read(ref _displayScale);

    public WindowHandle Handle { get; }

    public ReadOnlySpan<InputDevice> Devices => CollectionsMarshal.AsSpan(_devices);

    /// <summary>
    /// The window's and input's settings. A new window mode, display or resizability is shown on the platform thread.
    /// Game thread.
    /// </summary>
    public SdlPlatformSettings Settings
    {
        get => _settings;
        set
        {
            bool isWindowChanged =
                value.Mode != _settings.Mode
                || value.Display != _settings.Display
                || value.IsResizable != _settings.IsResizable;
            if (isWindowChanged && !_app.IsHeadless)
                ShowWindowAsync(_settings, value).FireAndForget();

            _settings = value;
        }
    }

    /// <summary>
    /// What the window's clipboard holds, as its last event said; headless, only what was set here.
    /// </summary>
    unsafe string IClipboard.Text
    {
        get => Volatile.Read(ref _clipboard);
        set
        {
            ThrowIfNotOnGame();
            Volatile.Write(ref _clipboard, value);
            if (_window != null)
                Volatile.Write(ref _clipboardToSet, value);
        }
    }

    string IInput.Text => _text;

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
        _meter.Dispose();
        foreach (nint gamepad in _gamepads.Values)
        {
            SDL_CloseGamepad((SDL_Gamepad*)gamepad);
        }

        if (_metalView != null)
            SDL_Metal_DestroyView(_metalView);

        if (_window != null)
            SDL_DestroyWindow(_window);

        SDL_Quit();
    }

    /// <summary>
    /// Waits out the frame cap, then makes input this frame's: what happened last frame is over, SDL's events since are
    /// applied, and sticks and triggers are read as they stand now.
    /// </summary>
    public unsafe void Update(in Frame frame)
    {
        WaitForFrameCap();

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

        _text = string.Empty;
        ApplyEvents(motionShare);

        // Sticks and triggers as they stand now, rather than added up from events.
        foreach ((uint id, nint gamepad) in _gamepads)
        {
            ControlState[] states = _controls[FindIndex(DeviceKind.Gamepad, id)];
            foreach ((Control control, SDL_GamepadAxis axis, float sign) in _axes)
            {
                SetState(states, control, sign * ToStick(SDL_GetGamepadAxis((SDL_Gamepad*)gamepad, axis)));
            }
        }
    }

    /// <summary>
    /// Gives the window and clipboard what the game thread set, then hands the game thread SDL's events since the last
    /// pump, waiting up to <see cref="PumpWaitMs"/> for one when there are none. Platform thread.
    /// </summary>
    public unsafe void Pump()
    {
        if (_window != null)
        {
            if (Interlocked.Exchange(ref _titleToShow, null) is { } title)
            {
                fixed (byte* text = CString.ToBytes(title))
                {
                    SDL_SetWindowTitle(_window, text);
                }
            }

            if (Interlocked.Exchange(ref _clipboardToSet, null) is { } clipboard)
            {
                fixed (byte* text = CString.ToBytes(clipboard))
                {
                    SDL_SetClipboardText(text);
                }
            }

            Volatile.Write(ref _pixelDensity, SDL_GetWindowPixelDensity(_window));
            ReadSize();
        }

        SDL_Event sdlEvent;
        if (!SDL_WaitEventTimeout(&sdlEvent, PumpWaitMs))
            return;

        do
        {
            SDL_EventType type = (SDL_EventType)sdlEvent.type;
            string text = string.Empty;
            if (type == SDL_EVENT_KEYBOARD_ADDED)
                text = CString.Read(SDL_GetKeyboardNameForID(sdlEvent.kdevice.which));
            else if (type == SDL_EVENT_MOUSE_ADDED)
                text = CString.Read(SDL_GetMouseNameForID(sdlEvent.mdevice.which));
            else if (type == SDL_EVENT_TEXT_INPUT)
                text = CString.Read(sdlEvent.text.text);
            else if (type == SDL_EVENT_CLIPBOARD_UPDATE)
                Volatile.Write(ref _clipboard, ReadClipboard());

            _events.Writer.TryWrite((sdlEvent, text));
        } while (SDL_PollEvent(&sdlEvent));
    }

    /// <summary>
    /// The clipboard's text, which SDL hands out as a copy that is freed once read.
    /// </summary>
    private static unsafe string ReadClipboard()
    {
        byte* text = SDL_GetClipboardText();
        string value = CString.Read(text);
        SDL_free(text);

        return value;
    }

    /// <summary>
    /// The named pointer in a window's <paramref name="properties"/>; 0 when it has none.
    /// </summary>
    private static unsafe nint GetPointer(uint properties, ReadOnlySpan<byte> name)
    {
        fixed (byte* key = name)
        {
            return (nint)SDL_GetPointerProperty(properties, key, null);
        }
    }

    /// <summary>
    /// Sets the named number in <paramref name="properties"/>.
    /// </summary>
    private static unsafe void SetNumber(uint properties, ReadOnlySpan<byte> name, long value)
    {
        fixed (byte* key = name)
        {
            SDL_SetNumberProperty(properties, key, value);
        }
    }

    /// <summary>
    /// SDL's id of the display at <paramref name="index"/> in its list; the main display, with a warning, when there is
    /// no such display.
    /// </summary>
    private static unsafe uint FindDisplay(int index)
    {
        int count;
        uint* displays = SDL_GetDisplays(&count);
        uint display = index >= 0 && index < count ? displays[index] : SDL_GetPrimaryDisplay();
        SDL_free(displays);
        if (index < 0 || index >= count)
            Log.Warn($"There is no display {index}: there are {count}, from 0. The window shows on the main one.");

        return display;
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
    /// Holds the frame back until a frame at the cap's rate is due after the last one held back, so frames start no
    /// faster than the cap; a frame already late is let go at once, and the next is due a whole frame after it.
    /// </summary>
    private void WaitForFrameCap()
    {
        int framesPerSecond = _isFocused ? _settings.MaxFrameRate : _settings.BackgroundFrameRate;
        if (framesPerSecond <= 0)
            return;

        long now = Stopwatch.GetTimestamp();
        long due = _pacedTimestamp + (Stopwatch.Frequency / framesPerSecond);
        if (due > now)
            SDL_DelayPrecise((ulong)Stopwatch.GetElapsedTime(now, due).Ticks * 100);

        _pacedTimestamp = Math.Max(due, now);
    }

    /// <summary>
    /// An SDL axis, -32768 to 32767, as -1 to 1: 0 inside the dead zone, and past it growing from 0 to 1 at full
    /// deflection along the stick's curve, so the zone's edge is no step. Triggers only reach the positive half.
    /// </summary>
    private float ToStick(short value)
    {
        float deadZone = _settings.StickDeadZone;
        float raw = Math.Max(value / 32767f, -1f);
        float past = MathF.Abs(raw) - deadZone;

        return past <= 0 ? 0 : MathF.CopySign(MathF.Pow(past / (1 - deadZone), _settings.StickCurve), raw);
    }

    /// <summary>
    /// Applies SDL's events the platform thread handed over since the last frame to the devices and their controls, in
    /// the order they happened, with <paramref name="motionShare"/> of the mouse's new motion.
    /// </summary>
    [SuppressMessage(
        "Style",
        "IDE0010:Add missing cases",
        Justification = "SDL has far more events than the gem reads; the rest are ignored on purpose."
    )]
    private unsafe void ApplyEvents(float motionShare)
    {
        while (_events.Reader.TryRead(out (SDL_Event Event, string Text) pumped))
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
                    _isFocused = (SDL_EventType)sdlEvent.type == SDL_EVENT_WINDOW_FOCUS_GAINED;

                    // PROTO: coming back resumes a world the game had paused itself, until a game's menu owns the pause.
                    if (_settings.PauseWhenUnfocused)
                        _world.IsPaused = !_isFocused;

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
                    float density = Volatile.Read(ref _pixelDensity);
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
                    _text += pumped.Text;
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

    /// <summary>
    /// What a worker calling this would get away with on some systems and crash on others; SDL's window and clipboard are the
    /// game thread's.
    /// </summary>
    private void ThrowIfNotOnGame()
    {
        if (!_threads.IsCurrent(EngineThread.Game))
            throw new InvalidOperationException("The window and clipboard are the game thread's.");
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

    /// <summary>
    /// Loads <paramref name="icon"/> and its image on workers, then gives the image to the window on the platform thread,
    /// unless the run ended or another icon was set meanwhile. One that does not load is a warning, and the window keeps
    /// what it shows.
    /// </summary>
    private async Task ShowAsync(Handle<Icon> icon)
    {
        if (icon == Handle<Icon>.None)
            return;

        CancellationToken lifetime = _app.Lifetime;
        Result<Icon> loaded = await _assets.LoadAsync(icon, null, lifetime).ConfigureAwait(false);
        Result<Texture> image = loaded.Ok
            ? await _assets.LoadAsync(loaded.Value.Image, null, lifetime).ConfigureAwait(false)
            : Result.Failure(loaded.Error);
        if (!image.Ok)
        {
            Log.Warn($"The window's icon is not shown: {image.Error}");

            return;
        }

        await _threads.Enter(EngineThread.Platform);
        if (!lifetime.IsCancellationRequested && Icon == icon)
            SetIcon(image.Value);
    }

    /// <summary>
    /// Gives the window <paramref name="image"/>'s first level, which SDL copies.
    /// </summary>
    private unsafe void SetIcon(Texture image)
    {
        if (image.Format is not (TextureFormat.Rgba8Srgb or TextureFormat.Rgba8Unorm))
        {
            Log.Warn($"The window's icon is not shown: its image is {image.Format}, not uncompressed.");

            return;
        }

        TextureLevel top = image.Levels[0];
        fixed (byte* pixels = image.Pixels.Span)
        {
            SDL_Surface* surface = SDL_CreateSurfaceFrom(
                top.Width,
                top.Height,
                SDL_PIXELFORMAT_RGBA32,
                pixels,
                top.Width * 4
            );
            if (surface == null || !SDL_SetWindowIcon(_window, surface))
                Log.Warn($"The window's icon is not shown: {Sdl.Error}");

            SDL_DestroySurface(surface);
        }
    }

    /// <summary>
    /// Shows the window as <paramref name="settings"/> say where they differ from <paramref name="old"/>, on the platform
    /// thread, unless the run ended meanwhile.
    /// </summary>
    private async Task ShowWindowAsync(SdlPlatformSettings old, SdlPlatformSettings settings)
    {
        CancellationToken lifetime = _app.Lifetime;
        await _threads.Enter(EngineThread.Platform);
        if (!lifetime.IsCancellationRequested)
            SetWindow(old, settings);
    }

    /// <summary>
    /// Gives the window the display, mode and resizability <paramref name="settings"/> change from <paramref name="old"/>.
    /// It moves to another display as a window, as one fullscreen or maximized can't be moved: once it is a window,
    /// which some systems finish later, centred there, then back in its mode.
    /// </summary>
    private unsafe void SetWindow(SdlPlatformSettings old, SdlPlatformSettings settings)
    {
        bool isMoving = settings.Display != old.Display;
        if (isMoving)
        {
            SetMode(WindowMode.Windowed);
            SDL_SyncWindow(_window);
            int centered = (int)(SDL_WINDOWPOS_CENTERED_MASK | FindDisplay(settings.Display));
            SDL_SetWindowPosition(_window, centered, centered);
        }

        if (isMoving || settings.Mode != old.Mode)
            SetMode(settings.Mode);

        if (settings.IsResizable != old.IsResizable)
            SDL_SetWindowResizable(_window, settings.IsResizable);
    }

    /// <summary>
    /// Fullscreen fills the display without a border; maximized and windowed leave fullscreen first, as a fullscreen
    /// window neither maximizes nor restores. Windowed returns the window to its last size.
    /// </summary>
    private unsafe void SetMode(WindowMode mode)
    {
        SDL_SetWindowFullscreen(_window, mode == WindowMode.Fullscreen);
        if (mode == WindowMode.Maximized)
            SDL_MaximizeWindow(_window);
        else if (mode == WindowMode.Windowed)
            SDL_RestoreWindow(_window);
    }

    /// <summary>
    /// The window's size in pixels and its display's scale, written whole for any thread to read. Platform thread.
    /// </summary>
    private unsafe void ReadSize()
    {
        int width = 0;
        int height = 0;
        SDL_GetWindowSizeInPixels(_window, &width, &height);
        Volatile.Write(ref _width, width);
        Volatile.Write(ref _height, height);
        Volatile.Write(ref _displayScale, SDL_GetWindowDisplayScale(_window));
    }

    /// <summary>
    /// What the OS knows the window by, which SDL keeps among its properties.
    /// </summary>
    private unsafe WindowHandle ReadHandle()
    {
        if (OperatingSystem.IsMacOS())
            return new WindowHandle(WindowSystem.Cocoa, 0, (nint)SDL_Metal_GetLayer(_metalView));

        uint properties = SDL_GetWindowProperties(_window);
        if (OperatingSystem.IsWindows())
        {
            return new WindowHandle(
                WindowSystem.Windows,
                GetPointer(properties, SDL_PROP_WINDOW_WIN32_INSTANCE_POINTER),
                GetPointer(properties, SDL_PROP_WINDOW_WIN32_HWND_POINTER)
            );
        }

        // Linux runs on either, whichever SDL found.
        nint wayland = GetPointer(properties, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER);
        if (wayland != 0)
            return new WindowHandle(
                WindowSystem.Wayland,
                wayland,
                GetPointer(properties, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER)
            );

        fixed (byte* window = SDL_PROP_WINDOW_X11_WINDOW_NUMBER)
        {
            return new WindowHandle(
                WindowSystem.X11,
                GetPointer(properties, SDL_PROP_WINDOW_X11_DISPLAY_POINTER),
                (nint)SDL_GetNumberProperty(properties, window, 0)
            );
        }
    }
}
