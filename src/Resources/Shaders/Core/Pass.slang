// What a pass is handed: the 16 bytes pushed with it, C#'s GpuConstants, as the same four words. A pass whose
// constants fit the header uses the words as it likes; one with more puts the heap id of its constants buffer in x
// and loads its struct from it with LoadConstants.
module "Core/Pass";
import Core.Heap;

public struct PassHeader
{
    public uint x;
    public uint y;
    public uint z;
    public uint w;
}

[[vk::push_constant]] ConstantBuffer<PassHeader> pass;

public PassHeader GetPassHeader() { return pass; }

// The pass's constants struct, which C# wrote at the start of the buffer whose heap id is the header's x.
public T LoadConstants<T>()
{
    BufferRef buffer = { pass.x };
    return buffer.Load<T>(0u);
}

// A constants struct C# wrote at the start of the buffer with that heap id, as a header's other words name one.
public T LoadConstants<T>(uint id)
{
    BufferRef buffer = { id };
    return buffer.Load<T>(0u);
}
