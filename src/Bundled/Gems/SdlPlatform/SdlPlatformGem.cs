using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Input;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.SdlBindings;
using Magic.Services;

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
    private readonly SdlPlatformSettings _settings;
    private readonly Lifetime _lifetime;
    private readonly Assets _assets;
    private readonly unsafe SDL.Window* _window;

    /// <summary>
    /// What the render thread's pump saw, in the order it happened, for the main thread to apply.
    /// </summary>
    private readonly Channel<Change> _changes = Channel.CreateUnbounded<Change>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>
    /// The main thread's devices, and their controls by <see cref="ControlExtensions"/>' Index, at the same indexes.
    /// </summary>
    private readonly List<InputDevice> _devices = [];
    private readonly List<ControlState[]> _controls = [];

    /// <summary>
    /// The render thread's keyboards and mice the main thread has been told of: input can arrive before its device
    /// does, and some from no device at all (id 0).
    /// </summary>
    private readonly HashSet<(DeviceKind Kind, uint Id)> _announced = [];

    /// <summary>
    /// The render thread's open gamepads, by SDL's id.
    /// </summary>
    private readonly Dictionary<uint, nint> _gamepads = [];

    /// <summary>
    /// What was typed this frame, put together on the main thread from the pump's pieces.
    /// </summary>
    private readonly StringBuilder _typed = new();

    private string _title;

    private string _clipboard = string.Empty;

    private Handle<Icon> _icon;

    /// <summary>
    /// Set on the main thread for the render thread, SDL's, to give the window as it next pumps, each taken whole
    /// (<see cref="ApplyWanted"/>): a title, the clipboard's text, a size the settings ask for, and the icon's image
    /// once loaded; null for nothing new.
    /// </summary>
    private string? _wantedTitle;
    private string? _wantedClipboard;
    private WindowSize? _wantedSize;
    private Texture? _wantedIcon;

    /// <summary>
    /// The icon is loaded from the first frame, once every gem is made, so the gems that read icons and images are
    /// there; never headless, as there is no window to show it.
    /// </summary>
    private bool _isIconShown;

    /// <summary>
    /// Window pixels per screen point, which SDL's mouse positions are in. The render thread's.
    /// </summary>
    private float _pixelDensity = 1;

    /// <summary>
    /// The size the settings last asked the window to be, so a drag that changes it is left alone.
    /// </summary>
    private int _askedWidth;
    private int _askedHeight;

    public unsafe SdlPlatformGem(
        SdlPlatformSettings settings,
        Args run,
        Lifetime lifetime,
        Project project,
        Assets assets)
    {
        _lifetime = lifetime;
        _settings = settings;
        _askedWidth = settings.Width;
        _askedHeight = settings.Height;
        _assets = assets;
        _title = project.Name;
        _icon = assets.Default<Icon>();

        // Ctrl+C is Core's to handle (--headless); left alone, SDL would turn it into a quit event.
        fixed (byte* on = "1"u8)
        {
            SDL.SetHint(SDL.HintNoSignalHandlers, on);
        }

        uint systems = run.Headless
            ? SDL.InitGamepad
            : SDL.InitVideo | SDL.InitGamepad;

        if (!SDL.Init(systems))
            throw new InvalidOperationException($"SDL could not start: {CString.Read(SDL.GetError())}");

        // The headers the binding was generated from are the oldest SDL that serves it; SDL keeps its ABI, so a newer
        // one does.
        int version = SDL.GetVersion();

        if (version < SDL.VERSION)
        {
            throw new InvalidOperationException($"SDL {version / 1000000}.{version / 1000 % 1000}.{version % 1000} is "
                + $"older than the {SDL.MajorVersion}.{SDL.MinorVersion}.{SDL.MicroVersion} Magic was built against.");
        }

        if (run.Headless) return;

        fixed (byte* title = CString.ToBytes(project.Name))
        {
            _window = SDL.CreateWindow(
                title,
                settings.Width,
                settings.Height,
                SDL.WindowResizable | SDL.WindowHighPixelDensity);
        }

        if (_window == null)
        {
            string error = CString.Read(SDL.GetError());
            SDL.Quit();

            throw new InvalidOperationException($"SDL could not open a window: {error}");
        }

        Id = SDL.GetWindowID(_window);

        // Typed characters come as text events only while text input is on: always, as a desktop has no on-screen
        // keyboard for it to bring up.
        if (!SDL.StartTextInput(_window)) Debugging.Warn($"Typed text is not read: {CString.Read(SDL.GetError())}");

        // The main thread has not started: its state is set here, before anything reads it.
        if (TryReadWindowSize(out int width, out int height))
        {
            Width = width;
            Height = height;
        }

        _clipboard = ReadClipboard();

        int keyboardsCount;
        uint* keyboards = SDL.GetKeyboards(&keyboardsCount);
        for (int i = 0; i < keyboardsCount; i++)
        {
            Announce(DeviceKind.Keyboard, keyboards[i]);
        }

        SDL.Free(keyboards);

        int miceCount;
        uint* mice = SDL.GetMice(&miceCount);
        for (int i = 0; i < miceCount; i++)
        {
            Announce(DeviceKind.Mouse, mice[i]);
        }

        SDL.Free(mice);
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
            if (_window != null) Volatile.Write(ref _wantedTitle, value);
        }
    }

    /// <summary>
    /// Set on the main thread, it reaches the window once it and its image have loaded and the render thread gets to
    /// it.
    /// </summary>
    public unsafe Handle<Icon> Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            if (_window != null) ShowAsync(value).FireAndForget();
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
            if (_window != null) Volatile.Write(ref _wantedClipboard, value);
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
                if (_devices[i].Kind != control.DeviceKind) continue;

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
            SDL.CloseGamepad((SDL.Gamepad*)gamepad);
        }

        if (_window != null) SDL.DestroyWindow(_window);

        SDL.Quit();
    }

    /// <summary>
    /// Applies what the render thread's pump saw since the last frame. Main thread.
    /// </summary>
    public void Update(in Frame frame)
    {
        if (!_isIconShown && Id != 0)
        {
            _isIconShown = true;
            ShowAsync(_icon).FireAndForget();
        }

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
    [SuppressMessage(
        "Style",
        "IDE0010:Add missing cases",
        Justification = "SDL has far more events than the gem reads; the rest are ignored on purpose.")]
    public unsafe void Render(in Frame frame)
    {
        ApplyWanted();
        SDL.Event sdlEvent;
        while (SDL.PollEvent(&sdlEvent))
        {
            switch ((SDL.EventType)sdlEvent.Type)
            {
                case SDL.EventType.Quit:
                    _lifetime.Stop("the system asked Magic to quit");
                    break;
                case SDL.EventType.WindowCloseRequested:
                    _lifetime.Stop("window closed");
                    break;
                case SDL.EventType.WindowPixelSizeChanged:
                    if (TryReadWindowSize(out int width, out int height))
                        Write(new Change(ChangeKind.Resized, Width: width, Height: height));

                    break;
                case SDL.EventType.KeyboardAdded:
                    Announce(DeviceKind.Keyboard, sdlEvent.Kdevice.Which);
                    break;
                case SDL.EventType.KeyboardRemoved:
                    Forget(DeviceKind.Keyboard, sdlEvent.Kdevice.Which);
                    break;
                case SDL.EventType.MouseAdded:
                    Announce(DeviceKind.Mouse, sdlEvent.Mdevice.Which);
                    break;
                case SDL.EventType.MouseRemoved:
                    Forget(DeviceKind.Mouse, sdlEvent.Mdevice.Which);
                    break;
                case SDL.EventType.GamepadAdded:
                    OpenGamepad(sdlEvent.Gdevice.Which);
                    break;
                case SDL.EventType.GamepadRemoved:
                    CloseGamepad(sdlEvent.Gdevice.Which);
                    break;
                case SDL.EventType.KeyDown:
                case SDL.EventType.KeyUp:
                    if (!sdlEvent.Key.Repeat && sdlEvent.Key.Scancode.Control is { } key)
                        Set(DeviceKind.Keyboard, sdlEvent.Key.Which, key, sdlEvent.Key.Down ? 1 : 0);

                    break;
                case SDL.EventType.MouseButtonDown:
                case SDL.EventType.MouseButtonUp:
                    if (SdlControlExtensions.ToMouseControl(sdlEvent.Button.Button) is { } button)
                        Set(DeviceKind.Mouse, sdlEvent.Button.Which, button, sdlEvent.Button.Down ? 1 : 0);

                    break;
                case SDL.EventType.MouseMotion:
                {
                    uint mouse = sdlEvent.Motion.Which;
                    Set(DeviceKind.Mouse, mouse, Control.MouseX, sdlEvent.Motion.X * _pixelDensity);
                    Set(DeviceKind.Mouse, mouse, Control.MouseY, sdlEvent.Motion.Y * _pixelDensity);
                    float motion = _pixelDensity * Math.Clamp(_settings.MouseSensitivity, 0.1f, 10);
                    Add(mouse, Control.MouseMotionX, sdlEvent.Motion.Xrel * motion);
                    Add(mouse, Control.MouseMotionY, sdlEvent.Motion.Yrel * motion * (_settings.InvertMouseY ? -1 : 1));

                    break;
                }

                case SDL.EventType.MouseWheel:
                {
                    uint mouse = sdlEvent.Wheel.Which;
                    float direction = sdlEvent.Wheel.Direction == SDL.MouseWheelDirection.Flipped ? -1 : 1;
                    Add(mouse, Control.MouseWheelX, sdlEvent.Wheel.X * direction);
                    Add(mouse, Control.MouseWheelY, sdlEvent.Wheel.Y * direction);

                    break;
                }

                case SDL.EventType.TextInput:
                    Write(new Change(ChangeKind.Typed, Text: CString.Read(sdlEvent.Text.Text)));
                    break;
                case SDL.EventType.ClipboardUpdate:
                    Write(new Change(ChangeKind.Clipboard, Text: ReadClipboard()));
                    break;
                case SDL.EventType.GamepadButtonDown:
                case SDL.EventType.GamepadButtonUp:
                    // Only a pad that is open: one that could not be opened is no device.
                    if (_gamepads.ContainsKey(sdlEvent.Gbutton.Which)
                        && ((SDL.GamepadButton)sdlEvent.Gbutton.Button).Control is { } gamepadButton)
                    {
                        Set(DeviceKind.Gamepad, sdlEvent.Gbutton.Which, gamepadButton, sdlEvent.Gbutton.Down ? 1 : 0);
                    }

                    break;
            }
        }

        // Sticks and triggers are read as they stand now, rather than added up from events.
        foreach ((uint id, nint gamepad) in _gamepads)
        {
            ReadAxes(id, (SDL.Gamepad*)gamepad);
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

        if (kind != DeviceKind.Mouse) return;

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
            state.WasReleased || (!isDown && state.IsDown));
    }

    /// <summary>
    /// The clipboard's text, which SDL hands out as a copy that is freed once read.
    /// </summary>
    private static unsafe string ReadClipboard()
    {
        byte* text = SDL.GetClipboardText();
        string value = CString.Read(text);
        SDL.Free(text);

        return value;
    }

    /// <summary>
    /// Asks the window thread to resize the window when the settings ask for a size they did not before.
    /// </summary>
    private unsafe void Resize()
    {
        if (_window == null || (_settings.Width == _askedWidth && _settings.Height == _askedHeight)) return;

        (_askedWidth, _askedHeight) = (_settings.Width, _settings.Height);
        Volatile.Write(ref _wantedSize, new WindowSize(_askedWidth, _askedHeight));
    }

    /// <summary>
    /// Gives the window what the main thread set since the last frame. Render thread.
    /// </summary>
    private unsafe void ApplyWanted()
    {
        if (Interlocked.Exchange(ref _wantedTitle, null) is { } title)
        {
            fixed (byte* text = CString.ToBytes(title))
            {
                SDL.SetWindowTitle(_window, text);
            }
        }

        if (Interlocked.Exchange(ref _wantedClipboard, null) is { } clipboard)
        {
            fixed (byte* text = CString.ToBytes(clipboard))
            {
                SDL.SetClipboardText(text);
            }
        }

        if (Interlocked.Exchange(ref _wantedSize, null) is { } size)
            SDL.SetWindowSize(_window, size.Width, size.Height);

        if (Interlocked.Exchange(ref _wantedIcon, null) is { } icon) SetIcon(icon);
    }

    /// <summary>
    /// An SDL axis, -32768 to 32767, as -1 to 1: 0 inside the settings' dead zone, and past it growing from 0 to 1 at
    /// full deflection, so the zone's edge is no step. Triggers only reach the positive half.
    /// </summary>
    private float ToStick(short value)
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
        int index;
        switch (change.Kind)
        {
            case ChangeKind.Added:
                index = FindIndex(change.Device, change.Id);
                if (index < 0)
                {
                    // A device that gives no name is shown as what it is.
                    _devices.Add(new InputDevice(
                        change.Device,
                        change.Id,
                        string.IsNullOrEmpty(change.Name) ? change.Device.ToString() : change.Name));
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
                if (index >= 0) SetState(_controls[index], change.Control, change.Value);

                break;
            case ChangeKind.Add:
                index = FindIndex(change.Device, change.Id);
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

    private int FindIndex(DeviceKind kind, uint id)
    {
        for (int i = 0; i < _devices.Count; i++)
        {
            if (_devices[i].Kind == kind && _devices[i].Id == id) return i;
        }

        return -1;
    }

    /// <summary>
    /// Hands a change to the main thread. Render thread, like everything below but <see cref="ShowAsync"/>.
    /// </summary>
    private void Write(in Change change) => _changes.Writer.TryWrite(change);

    /// <summary>
    /// A control's new value. A keyboard or mouse is announced first if this is the first the main thread hears of it;
    /// a gamepad was as it opened.
    /// </summary>
    private void Set(DeviceKind kind, uint id, Control control, float value)
    {
        if (kind != DeviceKind.Gamepad) Announce(kind, id);

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
    private unsafe void Announce(DeviceKind kind, uint id)
    {
        if (!_announced.Add((kind, id))) return;

        byte* named = kind == DeviceKind.Keyboard ? SDL.GetKeyboardNameForID(id) : SDL.GetMouseNameForID(id);
        Write(new Change(ChangeKind.Added, kind, id, Name: CString.Read(named)));
    }

    private void Forget(DeviceKind kind, uint id)
    {
        if (_announced.Remove((kind, id))) Write(new Change(ChangeKind.Removed, kind, id));
    }

    private unsafe void OpenGamepad(uint id)
    {
        if (_gamepads.ContainsKey(id)) return;

        SDL.Gamepad* gamepad = SDL.OpenGamepad(id);
        if (gamepad == null)
        {
            Debugging.Warn($"A gamepad was connected but could not be opened: {CString.Read(SDL.GetError())}");

            return;
        }

        _gamepads[id] = (nint)gamepad;
        string name = CString.Read(SDL.GetGamepadName(gamepad));
        Write(new Change(ChangeKind.Added, DeviceKind.Gamepad, id, Name: name));
        Debugging.Info($"Gamepad connected: {name}");
    }

    private unsafe void CloseGamepad(uint id)
    {
        if (!_gamepads.Remove(id, out nint gamepad)) return;

        Debugging.Info($"Gamepad disconnected: {CString.Read(SDL.GetGamepadName((SDL.Gamepad*)gamepad))}");
        SDL.CloseGamepad((SDL.Gamepad*)gamepad);
        Write(new Change(ChangeKind.Removed, DeviceKind.Gamepad, id));
    }

    private unsafe void ReadAxes(uint id, SDL.Gamepad* gamepad)
    {
        // SDL's stick y grows downward; Magic's is up positive, unless the settings invert it.
        float up = _settings.InvertStickY ? 1 : -1;
        Set(DeviceKind.Gamepad, id, Control.GamepadLeftX, ToStick(SDL.GetGamepadAxis(gamepad, SDL.GamepadAxis.Leftx)));
        Set(
            DeviceKind.Gamepad,
            id,
            Control.GamepadLeftY,
            up * ToStick(SDL.GetGamepadAxis(gamepad, SDL.GamepadAxis.Lefty)));
        Set(
            DeviceKind.Gamepad,
            id,
            Control.GamepadRightX,
            ToStick(SDL.GetGamepadAxis(gamepad, SDL.GamepadAxis.Rightx)));
        Set(
            DeviceKind.Gamepad,
            id,
            Control.GamepadRightY,
            up * ToStick(SDL.GetGamepadAxis(gamepad, SDL.GamepadAxis.Righty)));
        Set(
            DeviceKind.Gamepad,
            id,
            Control.GamepadLeftTrigger,
            ToStick(SDL.GetGamepadAxis(gamepad, SDL.GamepadAxis.LeftTrigger)));
        Set(
            DeviceKind.Gamepad,
            id,
            Control.GamepadRightTrigger,
            ToStick(SDL.GetGamepadAxis(gamepad, SDL.GamepadAxis.RightTrigger)));
    }

    /// <summary>
    /// The window's size in pixels, and the pixel density mouse positions are scaled by.
    /// </summary>
    private unsafe bool TryReadWindowSize(out int width, out int height)
    {
        float density = SDL.GetWindowPixelDensity(_window);
        _pixelDensity = density > 0 ? density : 1;

        int pixelsWide;
        int pixelsHigh;
        bool isRead = SDL.GetWindowSizeInPixels(_window, &pixelsWide, &pixelsHigh);
        width = pixelsWide;
        height = pixelsHigh;

        return isRead;
    }

    /// <summary>
    /// Loads <paramref name="icon"/> and its image on workers, for the render thread to give the image to the window,
    /// unless another icon was set meanwhile. One that does not load is logged, and the window keeps what it shows.
    /// </summary>
    private async Task ShowAsync(Handle<Icon> icon)
    {
        if (icon == Handle<Icon>.None) return;

        Result<Icon> loaded = await _assets.LoadAsync(icon, _lifetime.Stopping).ConfigureAwait(false);
        Result<Texture> image = loaded.Ok
            ? await _assets.LoadAsync(loaded.Value.Image, _lifetime.Stopping).ConfigureAwait(false)
            : Result.Failure<Texture>(loaded.Error);
        if (!image.Ok)
        {
            Debugging.Error($"The window's icon is not shown: {image.Error}");

            return;
        }

        if (icon == _icon) Volatile.Write(ref _wantedIcon, image.Value);
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
            SDL.Surface* surface = SDL.CreateSurfaceFrom(
                level.Width,
                level.Height,
                SDL.PixelFormat.Rgba32,
                pixels + level.Offset,
                level.Width * 4);
            if (surface == null || !SDL.SetWindowIcon(_window, surface))
                Debugging.Error($"The window's icon is not shown: {CString.Read(SDL.GetError())}");

            SDL.DestroySurface(surface);
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
    private readonly record struct Change(
        ChangeKind Kind,
        DeviceKind Device = default,
        uint Id = 0,
        Control Control = default,
        float Value = 0,
        string? Name = null,
        int Width = 0,
        int Height = 0,
        string? Text = null);

    /// <summary>
    /// A size the settings ask the window to be, in screen points, set whole for the render thread.
    /// </summary>
    private sealed record WindowSize(int Width, int Height);
}
