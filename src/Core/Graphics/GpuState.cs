namespace Magic.Graphics;

/// <summary>
/// Whether something that the GPU provider makes in the background is not ready yet, ready, or failed.
/// </summary>
public enum GpuState : byte
{
    Loading,
    Ready,
    Failed,
}
