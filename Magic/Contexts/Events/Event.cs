using Magic.Contexts.Input;
using System.Numerics;

namespace Magic.Contexts.Events;

/// <summary>
/// What an <see cref="Event"/> is; the comment on each group says which fields it uses.
/// </summary>
public enum EventType : ushort
{
    /// <summary>
    /// A file appeared under a watched asset folder and has an id now. <see cref="Event.Id"/>, <see cref="Event.Text"/> = path.
    /// </summary>
    AssetAdded,

    /// <summary>
    /// The file or its sidecar was written; whoever holds the asset loads it again. Id, Text = path.
    /// </summary>
    AssetModified,

    /// <summary>
    /// The file moved or was renamed; the id is the same. Id, Text = path, <see cref="Event.OldText"/> = old path.
    /// </summary>
    AssetMoved,

    /// <summary>
    /// The file is gone; its id no longer resolves. Id, Text = path.
    /// </summary>
    AssetRemoved,

    /// <summary>
    /// A gem was loaded, reloaded or unloaded, so the set of types in the process changed.
    /// </summary>
    GemsChanged,

    /// <summary>
    /// The game was asked to end; the frame loop stops after this frame.
    /// </summary>
    Quit,

    /// <summary>
    /// An open domain is there: its globals and the chunks its cameras wanted are spawned. Id = the domain's.
    /// </summary>
    DomainLoaded,

    /// <summary>
    /// <see cref="Event.Window"/>, <see cref="Event.Key"/>, <see cref="Event.Modifiers"/>, <see cref="Event.Repeat"/>.
    /// </summary>
    KeyDown,

    /// <summary>
    /// Window, Key, Modifiers.
    /// </summary>
    KeyUp,

    /// <summary>
    /// Window, Text: what was typed, after the keyboard layout and any input method.
    /// </summary>
    TextInput,

    /// <summary>
    /// Window, <see cref="Event.Value"/> = position in window points, top left (0, 0).
    /// </summary>
    MouseMotion,

    /// <summary>
    /// Window, <see cref="Event.Button"/>.
    /// </summary>
    MouseButtonDown,

    /// <summary>
    /// Window, Button.
    /// </summary>
    MouseButtonUp,

    /// <summary>
    /// Window, Value = wheel movement: +Y away from the user, +X to the right.
    /// </summary>
    MouseWheel,

    /// <summary>
    /// Window.
    /// </summary>
    FocusGained,

    /// <summary>
    /// Window.
    /// </summary>
    FocusLost,
}

/// <summary>
/// Any event, like an <c>SDL_Event</c>: plain data whose <see cref="Type"/> says which fields mean something; the
/// rest are default. <see cref="Window"/> is an <see cref="Interfaces.IWindow.Handle"/>. Published on
/// <see cref="Services.Events"/> and read from <see cref="Frame.Events"/>.
/// </summary>
public readonly record struct Event(
    EventType Type,
    uint Window = 0,
    ulong Id = 0,
    Key Key = Key.Unknown,
    KeyModifiers Modifiers = KeyModifiers.None,
    bool Repeat = false,
    MouseButton Button = MouseButton.Left,
    Vector2 Value = default,
    string? Text = null,
    string? OldText = null);
