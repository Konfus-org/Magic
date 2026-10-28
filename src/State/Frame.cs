namespace Magic.State;

public readonly record struct Frame
{
    public uint Index { get; }
    public ulong SecsRunning { get; }
    public uint MsDeltaTime { get; }
}
