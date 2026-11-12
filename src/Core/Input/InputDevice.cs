namespace Magic.Input;

/// <summary>
/// The kind of an input device, which sets the controls it has. The controls of each kind are one block of
/// <see cref="Control"/>.
/// </summary>
public enum DeviceKind
{
    Keyboard,
    Mouse,
    Gamepad,
}

/// <summary>
/// A connected input device.
/// </summary>
/// <remarks>
/// If a device is disconnected and connected again, it comes back as a new device.
/// </remarks>
/// <param name="Kind">The kind of device.</param>
/// <param name="Id">Its number among the devices of its kind.</param>
/// <param name="Name">What the device calls itself, to show to the player.</param>
public readonly record struct InputDevice(DeviceKind Kind, uint Id, string Name);
