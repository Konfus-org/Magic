using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Magic;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Input;
using Magic.Platform;
using Magic.Plugins;
using Magic.Threading;
using Magic.Utils;
using static SdlPlatform.SDL_EventType;
using static SdlPlatform.SDL_GamepadAxis;
using static SdlPlatform.SDL_MouseWheelDirection;
using static SdlPlatform.SDL3;

namespace SdlPlatform;

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
    /// Whether the window opened with Vulkan, so it can give a surface.
    /// </summary>
    private readonly bool _hasVulkan;

    private string _title;
    private string _clipboard = string.Empty;
    private string _text = string.Empty;

    /// <summary>
    /// Window pixels per screen point, which SDL's mouse positions are in.
    /// </summary>
    private float _pixelDensity = 1;

    public unsafe SdlPlatformGem(App app, Threads threads)
    {
        _app = app;
        _threads = threads;
        _title = app.Project?.Name ?? string.Empty;

        uint systems = app.IsHeadless ? SDL_INIT_GAMEPAD : SDL_INIT_VIDEO | SDL_INIT_GAMEPAD;
        if (!SDL_Init(systems))
            throw new InvalidOperationException($"SDL could not start: {CString.Read(SDL_GetError())}");

        if (app.IsHeadless)
            return;

        // A window a GPU gem can draw into needs Vulkan at its opening; one without still opens on a machine with no
        // Vulkan, for a project that draws nothing.
        fixed (byte* title = CString.ToBytes(_title))
        {
            _window = SDL_CreateWindow(
                title,
                InitialWidth,
                InitialHeight,
                SDL_WINDOW_RESIZABLE | SDL_WINDOW_HIGH_PIXEL_DENSITY | SDL_WINDOW_VULKAN
            );
            _hasVulkan = _window != null;
            if (_window == null)
            {
                Log.Warn(
                    $"The window opens without Vulkan, so nothing can be drawn into it: {CString.Read(SDL_GetError())}"
                );
                _window = SDL_CreateWindow(
                    title,
                    InitialWidth,
                    InitialHeight,
                    SDL_WINDOW_RESIZABLE | SDL_WINDOW_HIGH_PIXEL_DENSITY
                );
            }
        }

        if (_window == null)
        {
            string error = CString.Read(SDL_GetError());
            SDL_Quit();

            throw new InvalidOperationException($"SDL could not open a window: {error}");
        }

        Id = SDL_GetWindowID(_window);
        VulkanExtensions = _hasVulkan ? ReadVulkanExtensions() : [];

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

    // TODO: show it once a gem reads textures; until then the window keeps what the OS gives it.
    public Handle<Icon> Icon { get; set; }

    /// <summary>
    /// 0 when headless.
    /// </summary>
    public int Width { get; private set; }

    /// <summary>
    /// 0 when headless.
    /// </summary>
    public int Height { get; private set; }

    /// <summary>
    /// SDL's id for the window; 0 when headless.
    /// </summary>
    public uint Id { get; }

    public ImmutableArray<string> VulkanExtensions { get; } = [];

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

    public unsafe Result<ulong> CreateVulkanSurface(nint instance)
    {
        ThrowIfNotOnMain();
        if (_window == null || !_hasVulkan)
            return Result.Failure("the window opened without Vulkan.");

        VkSurfaceKHR_T* surface;
        if (!SDL_Vulkan_CreateSurface(_window, (VkInstance_T*)instance, null, &surface))
            return Result.Failure($"SDL could not make a surface: {CString.Read(SDL_GetError())}");

        return Result.Success((ulong)surface);
    }

    public unsafe void Dispose()
    {
        foreach (nint gamepad in _gamepads.Values)
        {
            SDL_CloseGamepad((SDL_Gamepad*)gamepad);
        }

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
    /// The instance extensions a surface on this window needs, as SDL lists them for this platform.
    /// </summary>
    private static unsafe ImmutableArray<string> ReadVulkanExtensions()
    {
        uint count;
        byte** names = SDL_Vulkan_GetInstanceExtensions(&count);
        if (names == null)
        {
            Log.Warn($"SDL lists no Vulkan extensions for the window: {CString.Read(SDL_GetError())}");

            return [];
        }

        ImmutableArray<string>.Builder extensions = ImmutableArray.CreateBuilder<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            extensions.Add(CString.Read(names[i]));
        }

        return extensions.MoveToImmutable();
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
}
