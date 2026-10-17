using Magic.Contexts.Input;
using Magic.Extensions;

namespace Magic.Tests.Extensions;

public sealed class ControlExtensionsTests
{
    [Theory]
    [InlineData(Control.KeyA, DeviceKind.Keyboard)]
    [InlineData(Control.KeyRightSuper, DeviceKind.Keyboard)]
    [InlineData(Control.MouseLeft, DeviceKind.Mouse)]
    [InlineData(Control.MouseWheelY, DeviceKind.Mouse)]
    [InlineData(Control.GamepadSouth, DeviceKind.Gamepad)]
    [InlineData(Control.GamepadRightTrigger, DeviceKind.Gamepad)]
    public void DeviceKind_is_the_device_the_control_is_on(Control control, DeviceKind expected)
    {
        DeviceKind kind = control.DeviceKind;

        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(Control.KeyA, 0)]
    [InlineData(Control.KeyB, 1)]
    [InlineData(Control.MouseLeft, 0)]
    [InlineData(Control.MouseMiddle, 1)]
    [InlineData(Control.GamepadSouth, 0)]
    [InlineData(Control.GamepadEast, 1)]
    public void Index_counts_from_the_first_control_of_the_device(Control control, int expected)
    {
        int index = control.Index;

        Assert.Equal(expected, index);
    }
}
