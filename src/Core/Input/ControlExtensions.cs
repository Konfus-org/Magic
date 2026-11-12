namespace Magic.Input;

/// <summary>
/// Reads the device kind and the number in that kind from the value of a <see cref="Control"/>.
/// </summary>
/// <remarks>
/// The device kind is in the second byte. The number in the kind is in the first byte.
/// </remarks>
public static class ControlExtensions
{
    extension(Control control)
    {
        /// <summary>
        /// The kind of device the control is on.
        /// </summary>
        public DeviceKind DeviceKind => (DeviceKind)((int)control >> 8);

        /// <summary>
        /// The number of the control in its device kind, from 0 to 255, which is where a device keeps its state.
        /// </summary>
        public int Index => (int)control & 0xFF;
    }
}
