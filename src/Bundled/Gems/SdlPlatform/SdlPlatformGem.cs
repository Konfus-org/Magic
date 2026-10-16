using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Input;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using SDL;

namespace Magic.SdlPlatform;

/// <summary>
/// The window and input, through SDL3. Opens the window, shows its icon, loaded through <see cref="Assets"/>, keeps the
/// clipboard, and turns keyboards, mice and gamepads into <see cref="IInput"/>'s controls and typed text. Headless, it
/// opens no window and reads gamepads only: keyboards and mice need a window to type and point into.
/// <para>
/// SDL's windows and events belong to the thread that started it, the render thread: <see cref="Render"/> pumps the
/// events there and writes what they change, as plain data, to a channel, and <see cref="Update"/> applies it on the
/// main thread, where <see cref="IInput"/> and <see cref="IWindow"/> are read, before the gems that take them. So input
/// stays fixed for the rest of a frame.
/// </para>
/// </summary>
internal sealed class SdlPlatformGem : IGem, IWindow, IInput, IDisposable
{
    private readonly Lifetime _lifetime;
    private readonly Threads _threads;
    private readonly Assets _assets;
    private readonly unsafe SDL_Window* _window;

    /// <summary>
    /// What the render thread's pump saw, in the order it happened, for the main thread to apply.
    /// </summary>
    private readonly Channel<Change> _changes = Channel.CreateUnbounded<Change>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>
    /// The main thread's devices, and their controls by <see cref="ControlExtensions"/>' Index, at the same indexes.
    /// </summary>
    private readonly List<InputDevice> _devices = [];
    private readonly List<ControlState[]> _controls = [];

    /// <summary>
    /// The render thread's keyboards and mice the main thread has been told of: input can arrive before its device does,
    /// and some from no device at all (id 0).
    /// </summary>
    private readonly HashSet<(DeviceKind Kind, uint Id)> _announced = [];

    /// <summary>
    /// The render thread's open gamepads, by SDL's id.
    /// </summary>
    private readonly Dictionary<uint, nint> _gamepads = [];

    /// <summary>
    /// What was typed this frame, put together on the main thread from the pump's pieces.
    /// </summary>
    private readonly SdlPlatformSettings _settings;
    private readonly StringBuilder _typed = new();

    private string _title;

    private string _clipboard = string.Empty;

    private Handle<Icon> _icon;

    /// <summary>
    /// Window pixels per screen point, which SDL's mouse positions are in. The render thread's.
    /// </summary>
    private float _pixelDensity = 1;

    /// <summary>
    /// The size the settings last asked the window to be, so a drag that changes it is left alone.
    /// </summary>
    private int _askedWidth;
    private int _askedHeight;

    public unsafe SdlPlatformGem(SdlPlatformSettings settings, Args run, Lifetime lifetime, Threads threads, Project project, Assets assets)
    {
        _lifetime = lifetime;
        _threads = threads;
        _settings = settings;
        _askedWidth = settings.Width;
        _askedHeight = settings.Height;
        _assets = assets;
        _title = project.Name;
        _icon = assets.Default<Icon>();

        // Ctrl+C is Core's to handle (--headless); left alone, SDL would turn it into a quit event.
        SDL3.SDL_SetHint(SDL3.SDL_HINT_NO_SIGNAL_HANDLERS, "1");
        SDL_InitFlags systems = run.Headless
            ? SDL_InitFlags.SDL_INIT_GAMEPAD
            : SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_GAMEPAD;
        if (!SDL3.SDL_Init(systems))
        {
            throw new InvalidOperationException($"SDL could not start: {SDL3.SDL_GetError()}");
        }

        if (run.Headless)
        {
            return;
        }

        _window = SDL3.SDL_CreateWindow(project.Name, settings.Width, settings.Height, SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY);
        if (_window == null)
        {
            string? error = SDL3.SDL_GetError();
            SDL3.SDL_Quit();
            throw new InvalidOperationException($"SDL could not open a window: {error}");
        }

        Id = (uint)SDL3.SDL_GetWindowID(_window);

        // Typed characters come as text events only while text input is on: always, as a desktop has no on-screen
        // keyboard for it to bring up.
        if (!SDL3.SDL_StartTextInput(_window))
        {
            Debugging.Warn($"Typed text is not read: {SDL3.SDL_GetError()}");
        }

        // From the first frame, once every gem is made: loaded now, it would make the gems that read it, on a worker.
        _threads.Post(EngineThread.Main, () => ShowAsync(_icon).FireAndForget());

        // The main thread has not started: its state is set here, before anything reads it.
        if (ReadWindowSize(out int width, out int height))
        {
            Width = width;
            Height = height;
        }

        _clipboard = SDL3.SDL_GetClipboardText() ?? string.Empty;

        using SDLArray<SDL_KeyboardID>? keyboards = SDL3.SDL_GetKeyboards();
        if (keyboards is not null)
        {
            foreach (SDL_KeyboardID keyboard in keyboards)
            {
                Announce(DeviceKind.Keyboard, (uint)keyboard);
            }
        }

        using SDLArray<SDL_MouseID>? mice = SDL3.SDL_GetMice();
        if (mice is not null)
        {
            foreach (SDL_MouseID mouse in mice)
            {
                Announce(DeviceKind.Mouse, (uint)mouse);
            }
        }
    }

    public unsafe void Dispose()
    {
        _changes.Writer.TryComplete();
        foreach (nint gamepad in _gamepads.Values)
        {
            SDL3.SDL_CloseGamepad((SDL_Gamepad*)gamepad);
        }

        if (_window != null)
        {
            SDL3.SDL_DestroyWindow(_window);
        }

        SDL3.SDL_Quit();
    }

    /// <summary>
    /// Set on the main thread, it reaches the window once the render thread gets to it.
    /// </summary>
    public unsafe string Title
    {
        get => _title;
        set
        {
            _title = value;
            if (_window != null)
            {
                _threads.Post(EngineThread.Render, () => SDL3.SDL_SetWindowTitle(_window, value));
            }
        }
    }

    /// <summary>
    /// Set on the main thread, it reaches the window once it and its image have loaded and the render thread gets to it.
    /// </summary>
    public unsafe Handle<Icon> Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            if (_window != null)
            {
                ShowAsync(value).FireAndForget();
            }
        }
    }

    /// <summary>
    /// As of this frame's Update; 0 when headless.
    /// </summary>
    public int Width { get; private set; }

    /// <summary>
    /// As of this frame's Update; 0 when headless.
    /// </summary>
    public int Height { get; private set; }

    /// <summary>
    /// As of this frame's Update; set on the main thread, it reaches the OS once the render thread gets to it.
    /// </summary>
    public unsafe string Clipboard
    {
        get => _clipboard;
        set
        {
            _clipboard = value;
            if (_window != null)
            {
                _threads.Post(EngineThread.Render, () => SDL3.SDL_SetClipboardText(value));
            }
        }
    }

    /// <summary>
    /// SDL's id for the window; 0 when headless.
    /// </summary>
    public uint Id { get; }

    public ReadOnlySpan<InputDevice> Devices => CollectionsMarshal.AsSpan(_devices);

    public string Text { get; private set; } = string.Empty;

    public ControlState this[Control control]
    {
        get
        {
            float value = 0;
            bool isDown = false;
            bool wasPressed = false;
            bool wasReleased = false;
            for (int i = 0; i < _devices.Count; i++)
            {
                if (_devices[i].Kind != control.DeviceKind)
                {
                    continue;
                }

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
            int index = device.Kind == control.DeviceKind ? _devices.IndexOf(device) : -1;
            return index < 0 ? default : _controls[index][control.Index];
        }
    }

    /// <summary>
    /// Resizes the window when the settings ask for a size they did not before, on the window thread.
    /// </summary>
    private unsafe void Resize()
    {
        if (_window == null || (_settings.Width == _askedWidth && _settings.Height == _askedHeight))
        {
            return;
        }

        (_askedWidth, _askedHeight) = (_settings.Width, _settings.Height);
        (int width, int height) = (_askedWidth, _askedHeight);
        _threads.Post(EngineThread.Render, () => SDL3.SDL_SetWindowSize(_window, width, height));
    }

    /// <summary>
    /// Applies what the render thread's pump saw since the last frame. Main thread.
    /// </summary>
    public void Update(in Frame frame)
    {
        Resize();
        for (int i = 0; i < _devices.Count; i++)
        {
            StartFrame(_devices[i].Kind, _controls[i]);
        }

        _typed.Clear();
        while (_changes.Reader.TryRead(out Change change))
        {
            Apply(change);
        }

        Text = _typed.Length == 0 ? string.Empty : _typed.ToString();
    }

    /// <summary>
    /// Pumps SDL's events into changes for the main thread. Render thread.
    /// </summary>
    [SuppressMessage("Style", "IDE0010:Add missing cases", Justification = "SDL has far more events than the gem reads; the rest are ignored on purpose.")]
    public unsafe void Render(in Frame frame)
    {
        SDL_Event sdlEvent;
        while (SDL3.SDL_PollEvent(&sdlEvent))
        {
            switch (sdlEvent.Type)
            {
                case SDL_EventType.SDL_EVENT_QUIT:
                    _lifetime.Stop("the system asked Magic to quit");
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    _lifetime.Stop("window closed");
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                    if (ReadWindowSize(out int width, out int height))
                    {
                        Write(new Change(ChangeKind.Resized, Width: width, Height: height));
                    }

                    break;
                case SDL_EventType.SDL_EVENT_KEYBOARD_ADDED:
                    Announce(DeviceKind.Keyboard, (uint)sdlEvent.kdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_KEYBOARD_REMOVED:
                    Forget(DeviceKind.Keyboard, (uint)sdlEvent.kdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_ADDED:
                    Announce(DeviceKind.Mouse, (uint)sdlEvent.mdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_REMOVED:
                    Forget(DeviceKind.Mouse, (uint)sdlEvent.mdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                    OpenGamepad(sdlEvent.gdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                    CloseGamepad((uint)sdlEvent.gdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                    if (!sdlEvent.key.repeat && sdlEvent.key.scancode.Control is { } key)
                    {
                        Set(DeviceKind.Keyboard, (uint)sdlEvent.key.which, key, sdlEvent.key.down ? 1 : 0);
                    }

                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                    if (sdlEvent.button.Button.Control is { } button)
                    {
                        Set(DeviceKind.Mouse, (uint)sdlEvent.button.which, button, sdlEvent.button.down ? 1 : 0);
                    }

                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                {
                    uint mouse = (uint)sdlEvent.motion.which;
                    Set(DeviceKind.Mouse, mouse, Control.MouseX, sdlEvent.motion.x * _pixelDensity);
                    Set(DeviceKind.Mouse, mouse, Control.MouseY, sdlEvent.motion.y * _pixelDensity);
                    float motion = _pixelDensity * Math.Clamp(_settings.MouseSensitivity, 0.1f, 10);
                    Add(mouse, Control.MouseMotionX, sdlEvent.motion.xrel * motion);
                    Add(mouse, Control.MouseMotionY, sdlEvent.motion.yrel * motion * (_settings.InvertMouseY ? -1 : 1));
                    break;
                }

                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                {
                    uint mouse = (uint)sdlEvent.wheel.which;
                    float direction = sdlEvent.wheel.direction == SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED ? -1 : 1;
                    Add(mouse, Control.MouseWheelX, sdlEvent.wheel.x * direction);
                    Add(mouse, Control.MouseWheelY, sdlEvent.wheel.y * direction);
                    break;
                }

                case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                    Write(new Change(ChangeKind.Typed, Text: sdlEvent.text.GetText()));
                    break;
                case SDL_EventType.SDL_EVENT_CLIPBOARD_UPDATE:
                    Write(new Change(ChangeKind.Clipboard, Text: SDL3.SDL_GetClipboardText() ?? string.Empty));
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
                    // Only a pad that is open: one that could not be opened is no device.
                    if (_gamepads.ContainsKey((uint)sdlEvent.gbutton.which) && sdlEvent.gbutton.Button.Control is { } gamepadButton)
                    {
                        Set(DeviceKind.Gamepad, (uint)sdlEvent.gbutton.which, gamepadButton, sdlEvent.gbutton.down ? 1 : 0);
                    }

                    break;
            }
        }

        // Sticks and triggers are read as they stand now, rather than added up from events.
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
        {
            return;
        }

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
        state = new ControlState(value, isDown, state.WasPressed || (isDown && !state.IsDown), state.WasReleased || (!isDown && state.IsDown));
    }

    /// <summary>
    /// An SDL axis, -32768 to 32767, as -1 to 1: 0 inside the settings' dead zone, and past it growing from 0 to 1 at
    /// full deflection, so the zone's edge is no step. Triggers only reach the positive half.
    /// </summary>
    private float Stick(short value)
    {
        float raw = Math.Max(value / 32767f, -1f);
        float deadZone = Math.Clamp(_settings.StickDeadZone, 0, 0.5f);
        float past = MathF.Abs(raw) - deadZone;
        return past <= 0 ? 0 : MathF.CopySign(past / (1 - deadZone), raw);
    }

    /// <summary>
    /// One change, on the main thread.
    /// </summary>
    private void Apply(in Change change)
    {
        int index = IndexOf(change.Device, change.Id);
        switch (change.Kind)
        {
            case ChangeKind.Added:
                if (index < 0)
                {
                    _devices.Add(new InputDevice(change.Device, change.Id, change.Name ?? change.Device.ToString()));
                    _controls.Add(new ControlState[256]);
                }

                break;
            case ChangeKind.Removed:
                if (index >= 0)
                {
                    _devices.RemoveAt(index);
                    _controls.RemoveAt(index);
                }

                break;
            case ChangeKind.Set:
                if (index >= 0)
                {
                    SetState(_controls[index], change.Control, change.Value);
                }

                break;
            case ChangeKind.Add:
                if (index >= 0)
                {
                    ControlState[] states = _controls[index];
                    SetState(states, change.Control, states[change.Control.Index].Value + change.Value);
                }

                break;
            case ChangeKind.Resized:
                Width = change.Width;
                Height = change.Height;
                break;
            case ChangeKind.Typed:
                _typed.Append(change.Text);
                break;
            case ChangeKind.Clipboard:
                _clipboard = change.Text ?? string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "There is no such input change.");
        }
    }

    private int IndexOf(DeviceKind kind, uint id)
    {
        for (int i = 0; i < _devices.Count; i++)
        {
            if (_devices[i].Kind == kind && _devices[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Hands a change to the main thread. Render thread, like everything below.
    /// </summary>
    private void Write(in Change change)
    {
        _changes.Writer.TryWrite(change);
    }

    /// <summary>
    /// A keyboard or mouse control's new value, its device announced first if this is the first the main thread hears
    /// of it.
    /// </summary>
    private void Set(DeviceKind kind, uint id, Control control, float value)
    {
        if (kind != DeviceKind.Gamepad)
        {
            Announce(kind, id);
        }

        Write(new Change(ChangeKind.Set, kind, id, control, value));
    }

    /// <summary>
    /// Adds to a mouse control that counts up over the frame: motion and wheel.
    /// </summary>
    private void Add(uint mouse, Control control, float amount)
    {
        Announce(DeviceKind.Mouse, mouse);
        Write(new Change(ChangeKind.Add, DeviceKind.Mouse, mouse, control, amount));
    }

    /// <summary>
    /// Tells the main thread of a keyboard or mouse, named as SDL names it, unless it has been told already.
    /// </summary>
    private void Announce(DeviceKind kind, uint id)
    {
        if (!_announced.Add((kind, id)))
        {
            return;
        }

        string? name = kind == DeviceKind.Keyboard
            ? SDL3.SDL_GetKeyboardNameForID((SDL_KeyboardID)id)
            : SDL3.SDL_GetMouseNameForID((SDL_MouseID)id);
        Write(new Change(ChangeKind.Added, kind, id, Name: string.IsNullOrEmpty(name) ? kind.ToString() : name));
    }

    private void Forget(DeviceKind kind, uint id)
    {
        if (_announced.Remove((kind, id)))
        {
            Write(new Change(ChangeKind.Removed, kind, id));
        }
    }

    private unsafe void OpenGamepad(SDL_JoystickID id)
    {
        if (_gamepads.ContainsKey((uint)id))
        {
            return;
        }

        SDL_Gamepad* gamepad = SDL3.SDL_OpenGamepad(id);
        if (gamepad == null)
        {
            Debugging.Warn($"A gamepad was connected but could not be opened: {SDL3.SDL_GetError()}");
            return;
        }

        _gamepads[(uint)id] = (nint)gamepad;
        string? name = SDL3.SDL_GetGamepadName(gamepad);
        string shown = string.IsNullOrEmpty(name) ? "Gamepad" : name;
        Write(new Change(ChangeKind.Added, DeviceKind.Gamepad, (uint)id, Name: shown));
        Debugging.Info($"Gamepad connected: {shown}");
    }

    private unsafe void CloseGamepad(uint id)
    {
        if (!_gamepads.Remove(id, out nint gamepad))
        {
            return;
        }

        Debugging.Info($"Gamepad disconnected: {SDL3.SDL_GetGamepadName((SDL_Gamepad*)gamepad)}");
        SDL3.SDL_CloseGamepad((SDL_Gamepad*)gamepad);
        Write(new Change(ChangeKind.Removed, DeviceKind.Gamepad, id));
    }

    private unsafe void ReadAxes(uint id, SDL_Gamepad* gamepad)
    {
        // SDL's stick y grows downward; Magic's is up positive, unless the settings invert it.
        float up = _settings.InvertStickY ? 1 : -1;
        Set(DeviceKind.Gamepad, id, Control.GamepadLeftX, Stick(SDL3.SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX)));
        Set(DeviceKind.Gamepad, id, Control.GamepadLeftY, up * Stick(SDL3.SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY)));
        Set(DeviceKind.Gamepad, id, Control.GamepadRightX, Stick(SDL3.SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX)));
        Set(DeviceKind.Gamepad, id, Control.GamepadRightY, up * Stick(SDL3.SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY)));
        Set(DeviceKind.Gamepad, id, Control.GamepadLeftTrigger, Stick(SDL3.SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER)));
        Set(DeviceKind.Gamepad, id, Control.GamepadRightTrigger, Stick(SDL3.SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER)));
    }

    /// <summary>
    /// The window's size in pixels, and the pixel density mouse positions are scaled by.
    /// </summary>
    private unsafe bool ReadWindowSize(out int width, out int height)
    {
        float density = SDL3.SDL_GetWindowPixelDensity(_window);
        _pixelDensity = density > 0 ? density : 1;

        int pixelsWide;
        int pixelsHigh;
        bool isRead = SDL3.SDL_GetWindowSizeInPixels(_window, &pixelsWide, &pixelsHigh);
        width = pixelsWide;
        height = pixelsHigh;
        return isRead;
    }

    /// <summary>
    /// Loads <paramref name="icon"/> and its image on workers, then has the render thread give the image to the window,
    /// unless another icon was set meanwhile. One that does not load is logged, and the window keeps what it shows.
    /// </summary>
    private async Task ShowAsync(Handle<Icon> icon)
    {
        if (icon == Handle<Icon>.None)
        {
            return;
        }

        Result<Icon> loaded = await _assets.LoadAsync(icon, _lifetime.Stopping).ConfigureAwait(false);
        Result<Texture> image = loaded.Ok
            ? await _assets.LoadAsync(loaded.Value.Image, _lifetime.Stopping).ConfigureAwait(false)
            : Result.Failure<Texture>(loaded.Error);
        if (!image.Ok)
        {
            Debugging.Error($"The window's icon is not shown: {image.Error}");
            return;
        }

        if (icon == _icon)
        {
            _threads.Post(EngineThread.Render, () => SetIcon(image.Value));
        }
    }

    /// <summary>
    /// Gives the window <paramref name="image"/>'s first level, which SDL copies. Render thread.
    /// </summary>
    private unsafe void SetIcon(Texture image)
    {
        if (image.Format is not (GpuFormat.Rgba8Srgb or GpuFormat.Rgba8Unorm))
        {
            Debugging.Error($"The window's icon is not shown: its image is {image.Format}, not RGBA8.");
            return;
        }

        TextureLevel level = image.Levels[0];
        fixed (byte* pixels = image.Pixels.Span)
        {
            SDL_Surface* surface = SDL3.SDL_CreateSurfaceFrom(level.Width, level.Height, SDL3.SDL_PIXELFORMAT_RGBA32, (nint)(pixels + level.Offset), level.Width * 4);
            if (surface == null || !SDL3.SDL_SetWindowIcon(_window, surface))
            {
                Debugging.Error($"The window's icon is not shown: {SDL3.SDL_GetError()}");
            }

            SDL3.SDL_DestroySurface(surface);
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
        /// The window's new <see cref="Change.Width"/> and <see cref="Change.Height"/>, in pixels.
        /// </summary>
        Resized,

        /// <summary>
        /// Characters typed, in <see cref="Change.Text"/>.
        /// </summary>
        Typed,

        /// <summary>
        /// What the clipboard holds now, in <see cref="Change.Text"/>.
        /// </summary>
        Clipboard,
    }

    /// <summary>
    /// One thing the pump saw, as plain data for the main thread: which fields mean something depends on
    /// <see cref="Kind"/>.
    /// </summary>
    private readonly record struct Change(ChangeKind Kind, DeviceKind Device = default, uint Id = 0, Control Control = default, float Value = 0, string? Name = null, int Width = 0, int Height = 0, string? Text = null);
}
