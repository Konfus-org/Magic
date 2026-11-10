using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Assets;
using Magic.Debugging;
using Magic.Events;
using Magic.Input;
using Magic.Platform;
using Magic.Projects;
using Magic.SdlBindings;
using Magic.Threading;
using Magic.Utils;
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
/// </summary>
public sealed class SdlPlatformGem : IGem, IPlatform, IWindow, IClipboard, IInput, IDisposable
{
    /// <summary>
    /// The window's size out of fullscreen or maximized until it is first resized.
    /// </summary>
    private const int WindowedWidth = 1280;

    private const int WindowedHeight = 720;

    /// <summary>
    /// How the window opens: a release fills the display without a border, any other build is a maximized window.
    /// </summary>
#if DEBUG
    private const ulong StartMode = SDL_WINDOW_MAXIMIZED;
#else
    private const ulong StartMode = SDL_WINDOW_FULLSCREEN;
#endif

    /// <summary>
    /// <c>set SdlPlatform.windowMode=fullscreen|maximized|windowed</c> shows the window that way.
    /// </summary>
    private const string WindowModeSetting = "SdlPlatform.windowMode";

    /// <summary>
    /// The longest a pump waits for the OS's next event, so what was handed to the platform thread is never kept longer.
    /// </summary>
    private const int PumpWaitMs = 10;

    /// <summary>
    /// How far a stick moves from its centre before it counts, as a part of its full deflection.
    /// </summary>
    private const float StickDeadZone = 0.15f;

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

    /// <summary>
    /// Window pixels to SDL's points, written whole by each pump for the game thread's mouse motion.
    /// </summary>
    private float _pixelDensity = 1;

    /// <summary>
    /// The window's size in pixels, read by the platform thread as the window opens and on each pump, and written whole
    /// for any thread; 0 when headless.
    /// </summary>
    private int _width;

    private int _height;

    private string _title;

    private string _clipboard = string.Empty;

    private string _text = string.Empty;

    public unsafe SdlPlatformGem(App app, Threads threads, AssetManager assets)
    {
        _app = app;
        _threads = threads;
        _assets = assets;
        _title = app.Project?.Name ?? string.Empty;

        uint systems = app.IsHeadless ? SDL_INIT_GAMEPAD : SDL_INIT_VIDEO | SDL_INIT_GAMEPAD;
        if (!SDL_Init(systems))
            throw new InvalidOperationException($"SDL could not start: {Sdl.Error}");

        if (app.IsHeadless)
            return;

        // macOS draws through a Metal layer, which the window must be opened for; elsewhere the OS's window is enough.
        ulong flags =
            StartMode
            | SDL_WINDOW_RESIZABLE
            | SDL_WINDOW_HIGH_PIXEL_DENSITY
            | (OperatingSystem.IsMacOS() ? SDL_WINDOW_METAL : 0);
        fixed (byte* title = CString.ToBytes(_title))
        {
            _window = SDL_CreateWindow(title, WindowedWidth, WindowedHeight, flags);
        }

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

    public WindowHandle Handle { get; }

    public ReadOnlySpan<InputDevice> Devices => CollectionsMarshal.AsSpan(_devices);

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
    /// Makes input this frame's: what happened last frame is over, SDL's events since are applied, and sticks and
    /// triggers are read as they stand now. A window mode set is handed to the platform thread.
    /// </summary>
    public unsafe void Update(in Frame frame)
    {
        foreach (SetCliCommand setting in frame.Events.Get<SetCliCommand>())
        {
            if (setting.Key != WindowModeSetting)
                continue;

            if (setting.Value is not ("fullscreen" or "maximized" or "windowed"))
                Log.Error($"{setting.Key} takes fullscreen, maximized or windowed, not '{setting.Value}'.");
            else if (!_app.IsHeadless)
                ShowModeAsync(setting.Value).FireAndForget();
        }

        // What happened last frame is over: pressed and released clear, and a mouse's motion and wheel go back to 0.
        for (int i = 0; i < _devices.Count; i++)
        {
            ControlState[] states = _controls[i];
            for (int c = 0; c < states.Length; c++)
            {
                states[c] = states[c] with { WasPressed = false, WasReleased = false };
            }

            if (_devices[i].Kind != DeviceKind.Mouse)
                continue;

            states[Control.MouseMotionX.Index] = default;
            states[Control.MouseMotionY.Index] = default;
            states[Control.MouseWheelX.Index] = default;
            states[Control.MouseWheelY.Index] = default;
        }

        _text = string.Empty;
        ApplyEvents();

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
    /// Gives a control a new value, and with it whether it went down or came up. Pressed and released only ever turn on
    /// within a frame, so a tap between two frames shows as both.
    /// </summary>
    private static void SetState(ControlState[] states, Control control, float value)
    {
        ref ControlState state = ref states[control.Index];
        bool isDown = MathF.Abs(value) >= 0.5f;
        state = new ControlState(
            value,
            isDown,
            state.WasPressed || (isDown && !state.IsDown),
            state.WasReleased || (!isDown && state.IsDown)
        );
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
    /// An SDL axis, -32768 to 32767, as -1 to 1: 0 inside the dead zone, and past it growing from 0 to 1 at full
    /// deflection, so the zone's edge is no step. Triggers only reach the positive half.
    /// </summary>
    private static float ToStick(short value)
    {
        float raw = Math.Max(value / 32767f, -1f);
        float past = MathF.Abs(raw) - StickDeadZone;

        return past <= 0 ? 0 : MathF.CopySign(past / (1 - StickDeadZone), raw);
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
    /// Applies SDL's events the platform thread handed over since the last frame to the devices and their controls, in
    /// the order they happened.
    /// </summary>
    [SuppressMessage(
        "Style",
        "IDE0010:Add missing cases",
        Justification = "SDL has far more events than the gem reads; the rest are ignored on purpose."
    )]
    private unsafe void ApplyEvents()
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
                    // SDL's points as window pixels; motion and wheel count up over the frame.
                    float density = Volatile.Read(ref _pixelDensity);
                    ControlState[] mouse = GetOrAddDevice(DeviceKind.Mouse, sdlEvent.motion.which, string.Empty);
                    SetState(mouse, Control.MouseX, sdlEvent.motion.x * density);
                    SetState(mouse, Control.MouseY, sdlEvent.motion.y * density);
                    SetState(
                        mouse,
                        Control.MouseMotionX,
                        mouse[Control.MouseMotionX.Index].Value + (sdlEvent.motion.xrel * density)
                    );
                    SetState(
                        mouse,
                        Control.MouseMotionY,
                        mouse[Control.MouseMotionY.Index].Value + (sdlEvent.motion.yrel * density)
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
    /// Shows the window as <paramref name="mode"/> says on the platform thread, unless the run ended meanwhile.
    /// </summary>
    private async Task ShowModeAsync(string mode)
    {
        CancellationToken lifetime = _app.Lifetime;
        await _threads.Enter(EngineThread.Platform);
        if (!lifetime.IsCancellationRequested)
            SetMode(mode);
    }

    /// <summary>
    /// Fullscreen fills the display without a border; maximized and windowed leave fullscreen first, as a fullscreen
    /// window neither maximizes nor restores. Windowed returns the window to its last size.
    /// </summary>
    private unsafe void SetMode(string mode)
    {
        SDL_SetWindowFullscreen(_window, mode == "fullscreen");
        if (mode == "maximized")
            SDL_MaximizeWindow(_window);
        else if (mode == "windowed")
            SDL_RestoreWindow(_window);
    }

    /// <summary>
    /// The window's size in pixels, written whole for any thread to read. Platform thread.
    /// </summary>
    private unsafe void ReadSize()
    {
        int width = 0;
        int height = 0;
        SDL_GetWindowSizeInPixels(_window, &width, &height);
        Volatile.Write(ref _width, width);
        Volatile.Write(ref _height, height);
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
