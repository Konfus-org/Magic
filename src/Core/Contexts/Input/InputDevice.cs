namespace Magic.Contexts.Input;

/// <summary>
/// What an input device is, which decides the controls it has: each kind's controls are one block of
/// <see cref="Control"/>.
/// </summary>
public enum DeviceKind
{
    Keyboard,
    Mouse,
    Gamepad,
}

/// <summary>
/// A connected input device. A device that is unplugged and plugged back in comes back as a new one.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Id">Its number among the devices of its kind.</param>
/// <param name="Name">What the device calls itself, for showing to the player.</param>
public readonly record struct InputDevice(DeviceKind Kind, uint Id, string Name);
