namespace Magic.Gpu;

/// <summary>
/// Where something the GPU provider makes in the background is: not yet, ready to use, or never.
/// </summary>
public enum GpuState : byte
{
    Loading,
    Ready,
    Failed,
}
