namespace Magic.State;

public readonly record struct Frame
{
    public uint Index { get; }
    public ulong SecsSinceStart { get; }
    public uint MsDeltaTime { get; }
}
