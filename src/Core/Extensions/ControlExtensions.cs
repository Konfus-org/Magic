using Magic.Contexts.Input;

namespace Magic.Extensions;

/// <summary>
/// What a <see cref="Control"/>'s value holds: its device kind in the second byte, its number within the kind in
/// the first.
/// </summary>
public static class ControlExtensions
{
    extension(Control control)
    {
        /// <summary>
        /// The kind of device the control is on.
        /// </summary>
        public DeviceKind DeviceKind => (DeviceKind)((int)control >> 8);

        /// <summary>
        /// The control's number among its kind's, 0 to 255: where a device keeps its state.
        /// </summary>
        public int Index => (int)control & 0xFF;
    }
}
