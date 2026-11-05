using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
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
/// SDL's windows and events belong to the process's main thread, which everything here runs on: <see cref="Update"/>
/// turns SDL's events into changes and applies them, before any gem that takes the input runs, as they are made after
/// this one, so input stays fixed for the rest of a frame. It is public because nothing in its assembly makes it: Magic
/// does, by reflection, when a project lists the gem.
/// </para>
/// </summary>
public sealed class SdlPlatformGem : IGem, IWindow, IClipboard, IInput, IDisposable
{
    private const int InitialWidth = 1280;

    private const int InitialHeight = 720;

    /// <summary>
    /// How far a stick moves from its centre before it counts, as a part of its full deflection.
    /// </summary>
    private const float StickDeadZone = 0.15f;

    /// <summary>
    /// Each stick and trigger: the control it sets, SDL's axis it is read from, and whether it is a stick's y, which
    /// SDL grows downward.
    /// </summary>
    private static readonly (Control Control, SDL_GamepadAxis Axis, bool IsY)[] _axes =
    [
        (Control.GamepadLeftX, SDL_GAMEPAD_AXIS_LEFTX, false),
        (Control.GamepadLeftY, SDL_GAMEPAD_AXIS_LEFTY, true),
        (Control.GamepadRightX, SDL_GAMEPAD_AXIS_RIGHTX, false),
        (Control.GamepadRightY, SDL_GAMEPAD_AXIS_RIGHTY, true),
        (Control.GamepadLeftTrigger, SDL_GAMEPAD_AXIS_LEFT_TRIGGER, false),
        (Control.GamepadRightTrigger, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, false),
    ];

    private readonly App _app;

    private readonly Threads _threads;

    private readonly Assets _assets;

    private readonly unsafe SDL_Window* _window;

    /// <summary>
    /// What the pump saw since the last frame, in the order it happened.
    /// </summary>
    private readonly List<Change> _changes = [];

    /// <summary>
    /// The devices as of this frame, and their controls by <see cref="ControlExtensions"/>' Index, at the same indexes.
    /// </summary>
    private readonly List<InputDevice> _devices = [];

    private readonly List<ControlState[]> _controls = [];

    /// <summary>
    /// The keyboards and mice the pump has seen: input can arrive before its device does, and some from no device at all
    /// (id 0).
    /// </summary>
    private readonly HashSet<(DeviceKind Kind, uint Id)> _announced = [];

    /// <summary>
    /// The open gamepads, by SDL's id.
    /// </summary>
    private readonly Dictionary<uint, nint> _gamepads = [];

    /// <summary>
    /// What was typed this frame, put together from the pump's pieces.
    /// </summary>
    private readonly StringBuilder _typed = new();

    /// <summary>
    /// What holds the window's Metal layer on macOS, so its handle has one to name; null elsewhere.
    /// </summary>
    private readonly unsafe void* _metalView;

    private string _title;

    private string _clipboard = string.Empty;

    private string _text = string.Empty;

    /// <summary>
    /// An icon that has loaded, for the next frame to give the window: set by a worker, taken on the main thread.
    /// </summary>
    private WantedIcon? _wantedIcon;

    /// <summary>
    /// Window pixels per screen point, which SDL's mouse positions are in.
    /// </summary>
    private float _pixelDensity = 1;

    public unsafe SdlPlatformGem(App app, Threads threads, Assets assets)
    {
        _app = app;
        _threads = threads;
        _assets = assets;
        _title = app.Project?.Name ?? string.Empty;

        uint systems = app.IsHeadless ? SDL_INIT_GAMEPAD : SDL_INIT_VIDEO | SDL_INIT_GAMEPAD;
        if (!SDL_Init(systems))
            throw new InvalidOperationException($"SDL could not start: {CString.Read(SDL_GetError())}");

        if (app.IsHeadless)
            return;

        // macOS draws through a Metal layer, which the window must be opened for; elsewhere the OS's window is enough.
        ulong flags =
            SDL_WINDOW_RESIZABLE | SDL_WINDOW_HIGH_PIXEL_DENSITY | (OperatingSystem.IsMacOS() ? SDL_WINDOW_METAL : 0);
        fixed (byte* title = CString.ToBytes(_title))
        {
            _window = SDL_CreateWindow(title, InitialWidth, InitialHeight, flags);
        }

        if (_window == null)
        {
            string error = CString.Read(SDL_GetError());
            SDL_Quit();

            throw new InvalidOperationException($"SDL could not open a window: {error}");
        }

        if (OperatingSystem.IsMacOS())
            _metalView = SDL_Metal_CreateView(_window);

        Handle = ReadHandle();

        // Typed characters come as text events only while text input is on: always, as a desktop has no on-screen
        // keyboard for it to bring up.
        if (!SDL_StartTextInput(_window))
            Log.Warn($"Typed text is not read: {CString.Read(SDL_GetError())}");

        _pixelDensity = ReadPixelDensity();
        ReadWindowSize();
        _clipboard = ReadClipboard();

        // The keyboards and mice plugged in as the window opens; those plugged in later arrive as events.
        int keyboardsCount;
        uint* keyboards = SDL_GetKeyboards(&keyboardsCount);
        for (int i = 0; i < keyboardsCount; i++)
        {
            Announce(DeviceKind.Keyboard, keyboards[i]);
        }

        SDL_free(keyboards);

        int miceCount;
        uint* mice = SDL_GetMice(&miceCount);
        for (int i = 0; i < miceCount; i++)
        {
            Announce(DeviceKind.Mouse, mice[i]);
        }

        SDL_free(mice);
        Icon = assets.GetDefault<Icon>();
    }

    public unsafe string Title
    {
        get => _title;
        set
        {
            ThrowIfNotOnMain();
            _title = value;
            if (_window == null)
                return;

            fixed (byte* text = CString.ToBytes(value))
            {
                SDL_SetWindowTitle(_window, text);
            }
        }
    }

    /// <summary>
    /// Set on the main thread, it reaches the window once it and its image have loaded, unless another was set meanwhile.
    /// </summary>
    public unsafe Handle<Icon> Icon
    {
        get => field;
        set
        {
            ThrowIfNotOnMain();
            field = value;
            if (_window != null)
                ShowAsync(value).FireAndForget();
        }
    }

    /// <summary>
    /// 0 when headless.
    /// </summary>
    public int Width { get; private set; }

    /// <summary>
    /// 0 when headless.
    /// </summary>
    public int Height { get; private set; }

    public WindowHandle Handle { get; }

    public ReadOnlySpan<InputDevice> Devices => CollectionsMarshal.AsSpan(_devices);

    /// <summary>
    /// What the window's clipboard holds, as its last event said; headless, only what was set here.
    /// </summary>
    unsafe string IClipboard.Text
    {
        get => _clipboard;
        set
        {
            ThrowIfNotOnMain();
            _clipboard = value;
            if (_window == null)
                return;

            fixed (byte* text = CString.ToBytes(value))
            {
                SDL_SetClipboardText(text);
            }
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
    /// Makes input this frame's: what happened last frame is over, SDL's events since are turned into changes and
    /// applied, and sticks and triggers are read as they stand now, rather than added up from events.
    /// </summary>
    public unsafe void Update(in Frame frame)
    {
        if (Interlocked.Exchange(ref _wantedIcon, null) is { } wanted && wanted.Icon == Icon)
            SetIcon(wanted.Image);

        for (int i = 0; i < _devices.Count; i++)
        {
            StartFrame(_devices[i].Kind, _controls[i]);
        }

        PollEvents();
        _typed.Clear();
        foreach (Change change in _changes)
        {
            Apply(change);
        }

        _changes.Clear();
        _text = _typed.Length == 0 ? string.Empty : _typed.ToString();
        foreach ((uint id, nint gamepad) in _gamepads)
        {
            ReadAxes(id, (SDL_Gamepad*)gamepad);
        }
    }

    /// <summary>
    /// What happened last frame is over: pressed and released clear, and a mouse's motion and wheel go back to 0.
    /// </summary>
    private static void StartFrame(DeviceKind kind, ControlState[] states)
    {
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = states[i] with { WasPressed = false, WasReleased = false };
        }

        if (kind != DeviceKind.Mouse)
            return;

        states[Control.MouseMotionX.Index] = default;
        states[Control.MouseMotionY.Index] = default;
        states[Control.MouseWheelX.Index] = default;
        states[Control.MouseWheelY.Index] = default;
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
    /// Turns SDL's events since the last frame into changes, in the order they happened.
    /// </summary>
    [SuppressMessage(
        "Style",
        "IDE0010:Add missing cases",
        Justification = "SDL has far more events than the gem reads; the rest are ignored on purpose."
    )]
    private unsafe void PollEvents()
    {
        SDL_Event sdlEvent;
        while (SDL_PollEvent(&sdlEvent))
        {
            switch ((SDL_EventType)sdlEvent.type)
            {
                case SDL_EVENT_QUIT:
                    ExitOnce("the system asked Magic to quit");
                    break;
                case SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    ExitOnce("the window was closed");
                    break;
                case SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                    _pixelDensity = ReadPixelDensity();
                    ReadWindowSize();
                    break;
                case SDL_EVENT_KEYBOARD_ADDED:
                    Announce(DeviceKind.Keyboard, sdlEvent.kdevice.which);
                    break;
                case SDL_EVENT_KEYBOARD_REMOVED:
                    Forget(DeviceKind.Keyboard, sdlEvent.kdevice.which);
                    break;
                case SDL_EVENT_MOUSE_ADDED:
                    Announce(DeviceKind.Mouse, sdlEvent.mdevice.which);
                    break;
                case SDL_EVENT_MOUSE_REMOVED:
                    Forget(DeviceKind.Mouse, sdlEvent.mdevice.which);
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
                        Set(DeviceKind.Keyboard, sdlEvent.key.which, key, sdlEvent.key.down ? 1 : 0);

                    break;
                case SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EVENT_MOUSE_BUTTON_UP:
                    if (SdlControlExtensions.ToMouseControl(sdlEvent.button.button) is { } button)
                        Set(DeviceKind.Mouse, sdlEvent.button.which, button, sdlEvent.button.down ? 1 : 0);

                    break;
                case SDL_EVENT_MOUSE_MOTION:
                {
                    // SDL's points as window pixels.
                    uint mouse = sdlEvent.motion.which;
                    Set(DeviceKind.Mouse, mouse, Control.MouseX, sdlEvent.motion.x * _pixelDensity);
                    Set(DeviceKind.Mouse, mouse, Control.MouseY, sdlEvent.motion.y * _pixelDensity);
                    Add(mouse, Control.MouseMotionX, sdlEvent.motion.xrel * _pixelDensity);
                    Add(mouse, Control.MouseMotionY, sdlEvent.motion.yrel * _pixelDensity);

                    break;
                }

                case SDL_EVENT_MOUSE_WHEEL:
                {
                    // A wheel the OS flips (natural scrolling) is flipped back, so away from the player is positive on
                    // every system.
                    uint mouse = sdlEvent.wheel.which;
                    float direction = sdlEvent.wheel.direction == SDL_MOUSEWHEEL_FLIPPED ? -1 : 1;
                    Add(mouse, Control.MouseWheelX, sdlEvent.wheel.x * direction);
                    Add(mouse, Control.MouseWheelY, sdlEvent.wheel.y * direction);

                    break;
                }

                case SDL_EVENT_TEXT_INPUT:
                    _changes.Add(new Change(ChangeKind.Typed, Text: CString.Read(sdlEvent.text.text)));
                    break;
                case SDL_EVENT_CLIPBOARD_UPDATE:
                    _clipboard = ReadClipboard();
                    break;
                case SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                case SDL_EVENT_GAMEPAD_BUTTON_UP:
                    // Only a pad that is open: one that could not be opened is no device.
                    if (
                        _gamepads.ContainsKey(sdlEvent.gbutton.which)
                        && ((SDL_GamepadButton)sdlEvent.gbutton.button).Control is { } gamepadButton
                    )
                    {
                        Set(DeviceKind.Gamepad, sdlEvent.gbutton.which, gamepadButton, sdlEvent.gbutton.down ? 1 : 0);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// What a worker calling this would get away with on some systems and crash on others; SDL's window and clipboard are the
    /// main thread's.
    /// </summary>
    private void ThrowIfNotOnMain()
    {
        if (!_threads.IsCurrent(EngineThread.Main))
            throw new InvalidOperationException("The window and clipboard are the main thread's.");
    }

    /// <summary>
    /// Ends the run once: closing the last window is both a close and a quit.
    /// </summary>
    private void ExitOnce(string why)
    {
        if (_app.IsExiting)
            return;

        Log.Info($"Exiting: {why}.");
        _app.Exit();
    }

    /// <summary>
    /// One change, as a frame begins.
    /// </summary>
    private void Apply(in Change change)
    {
        int index;
        switch (change.Kind)
        {
            case ChangeKind.Added:
                index = FindIndex(change.Device, change.Id);
                if (index < 0)
                {
                    // A device that gives no name is shown as what it is. Its controls are its kind's block of 256, at
                    // each control's index.
                    string name = string.IsNullOrEmpty(change.Name) ? change.Device.ToString() : change.Name;
                    _devices.Add(new InputDevice(change.Device, change.Id, name));
                    _controls.Add(new ControlState[256]);
                }

                break;
            case ChangeKind.Removed:
                index = FindIndex(change.Device, change.Id);
                if (index >= 0)
                {
                    _devices.RemoveAt(index);
                    _controls.RemoveAt(index);
                }

                break;
            case ChangeKind.Set:
                index = FindIndex(change.Device, change.Id);
                if (index >= 0)
                    SetState(_controls[index], change.Control, change.Value);

                break;
            case ChangeKind.Add:
                index = FindIndex(change.Device, change.Id);
                if (index >= 0)
                {
                    ControlState[] states = _controls[index];
                    SetState(states, change.Control, states[change.Control.Index].Value + change.Value);
                }

                break;
            case ChangeKind.Typed:
                _typed.Append(change.Text);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "There is no such input change.");
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
    /// A control's new value. A keyboard or mouse is announced first if this is the first heard of it; a gamepad was as it
    /// opened.
    /// </summary>
    private void Set(DeviceKind kind, uint id, Control control, float value)
    {
        if (kind != DeviceKind.Gamepad)
            Announce(kind, id);

        _changes.Add(new Change(ChangeKind.Set, kind, id, control, value));
    }

    /// <summary>
    /// Adds to a mouse control that counts up over the frame: motion and wheel.
    /// </summary>
    private void Add(uint mouse, Control control, float amount)
    {
        Announce(DeviceKind.Mouse, mouse);
        _changes.Add(new Change(ChangeKind.Add, DeviceKind.Mouse, mouse, control, amount));
    }

    /// <summary>
    /// Adds a keyboard or mouse, named as SDL names it, unless it is known already.
    /// </summary>
    private unsafe void Announce(DeviceKind kind, uint id)
    {
        if (!_announced.Add((kind, id)))
            return;

        byte* named = kind == DeviceKind.Keyboard ? SDL_GetKeyboardNameForID(id) : SDL_GetMouseNameForID(id);
        _changes.Add(new Change(ChangeKind.Added, kind, id, Name: CString.Read(named)));
    }

    private void Forget(DeviceKind kind, uint id)
    {
        if (_announced.Remove((kind, id)))
            _changes.Add(new Change(ChangeKind.Removed, kind, id));
    }

    private unsafe void OpenGamepad(uint id)
    {
        if (_gamepads.ContainsKey(id))
            return;

        SDL_Gamepad* gamepad = SDL_OpenGamepad(id);
        if (gamepad == null)
        {
            Log.Warn($"A gamepad was connected but could not be opened: {CString.Read(SDL_GetError())}");

            return;
        }

        _gamepads[id] = (nint)gamepad;
        string name = CString.Read(SDL_GetGamepadName(gamepad));
        _changes.Add(new Change(ChangeKind.Added, DeviceKind.Gamepad, id, Name: name));
        Log.Info($"Gamepad connected: {name}");
    }

    private unsafe void CloseGamepad(uint id)
    {
        if (!_gamepads.Remove(id, out nint gamepad))
            return;

        Log.Info($"Gamepad disconnected: {CString.Read(SDL_GetGamepadName((SDL_Gamepad*)gamepad))}");
        SDL_CloseGamepad((SDL_Gamepad*)gamepad);
        _changes.Add(new Change(ChangeKind.Removed, DeviceKind.Gamepad, id));
    }

    private unsafe void ReadAxes(uint id, SDL_Gamepad* gamepad)
    {
        int index = FindIndex(DeviceKind.Gamepad, id);
        if (index < 0)
            return;

        foreach ((Control control, SDL_GamepadAxis axis, bool isY) in _axes)
        {
            // SDL's stick y grows downward; Magic's is up positive.
            float value = ToStick(SDL_GetGamepadAxis(gamepad, axis));
            SetState(_controls[index], control, isY ? -value : value);
        }
    }

    /// <summary>
    /// Window pixels per screen point, which SDL's mouse positions are in; 1 when SDL cannot say.
    /// </summary>
    private unsafe float ReadPixelDensity()
    {
        float density = SDL_GetWindowPixelDensity(_window);

        return density > 0 ? density : 1;
    }

    private unsafe void ReadWindowSize()
    {
        int width;
        int height;
        if (!SDL_GetWindowSizeInPixels(_window, &width, &height))
            return;

        Width = width;
        Height = height;
    }

    /// <summary>
    /// Loads <paramref name="icon"/> and its image on workers, for the next frame to give the image to the window. One that
    /// does not load is a warning, and the window keeps what it shows.
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

        Volatile.Write(ref _wantedIcon, new WantedIcon(icon, image.Value));
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
                Log.Warn($"The window's icon is not shown: {CString.Read(SDL_GetError())}");

            SDL_DestroySurface(surface);
        }
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

    /// <summary>
    /// What a <see cref="Change"/> does; each says which fields it uses.
    /// </summary>
    private enum ChangeKind : byte
    {
        /// <summary>
        /// A device, with its <see cref="Change.Name"/>.
        /// </summary>
        Added,

        Removed,

        /// <summary>
        /// A control's new <see cref="Change.Value"/>.
        /// </summary>
        Set,

        /// <summary>
        /// Added to a control's value, for what counts up over the frame.
        /// </summary>
        Add,

        /// <summary>
        /// Characters typed, in <see cref="Change.Text"/>.
        /// </summary>
        Typed,
    }

    /// <summary>
    /// One thing the pump saw, as plain data for the frame: which fields mean something depends on <see cref="Kind"/>.
    /// </summary>
    private readonly record struct Change(
        ChangeKind Kind,
        DeviceKind Device = default,
        uint Id = 0,
        Control Control = default,
        float Value = 0,
        string? Name = null,
        string? Text = null
    );

    /// <summary>
    /// An icon and the image it names, loaded.
    /// </summary>
    private sealed record WantedIcon(Handle<Icon> Icon, Texture Image);
}
