using System.Numerics;
using System.Runtime.CompilerServices;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// A copy of the frame that ImGui ended, which the overlay draws without reading ImGui.
/// </summary>
/// <remarks>
/// Every list's vertices are in one run, and their indices, widened to 32 bits, in another. Each draw counts from the
/// start of both.
/// </remarks>
internal sealed class DebugUIFrame
{
    /// <summary>
    /// The frame it was ended in, which the texture changes queued with it name.
    /// </summary>
    internal uint Index { get; private set; }

    internal List<ImDrawVert_t> Vertices { get; } = [];

    internal List<uint> Indices { get; } = [];

    internal List<DebugUIDraw> Draws { get; } = [];

    /// <summary>
    /// Holds <paramref name="data"/>, ended in frame <paramref name="index"/>, in place of what it held.
    /// </summary>
    /// <remarks>
    /// A draw that calls back or draws nothing is left out.
    /// </remarks>
    internal unsafe void Copy(ImDrawData_t* data, uint index)
    {
        Index = index;
        Vertices.Clear();
        Indices.Clear();
        Draws.Clear();
        for (int listIndex = 0; listIndex < data->CmdLists.Size; listIndex++)
        {
            ImDrawList_t* list = data->CmdLists.Data[listIndex];
            uint vertexBase = (uint)Vertices.Count;
            uint indexBase = (uint)Indices.Count;
            Vertices.AddRange(new ReadOnlySpan<ImDrawVert_t>(list->VtxBuffer.Data, list->VtxBuffer.Size));
            for (int i = 0; i < list->IdxBuffer.Size; i++)
            {
                Indices.Add(list->IdxBuffer.Data[i]);
            }

            for (int i = 0; i < list->CmdBuffer.Size; i++)
            {
                ImDrawCmd_t command = list->CmdBuffer.Data[i];
                if (command.UserCallback != null || command.ElemCount == 0)
                    continue;

                // The id ImGui's own texture was given, or the id the draw names alone.
                ulong texture =
                    command.TexRef._TexData != null ? command.TexRef._TexData->TexID : command.TexRef._TexID;
                Draws.Add(
                    new DebugUIDraw(
                        Unsafe.BitCast<ImVec4_t, Vector4>(command.ClipRect),
                        texture,
                        command.ElemCount,
                        indexBase + command.IdxOffset,
                        vertexBase + command.VtxOffset
                    )
                );
            }
        }
    }
}

/// <summary>
/// One draw of a <see cref="DebugUIFrame"/>.
/// </summary>
/// <remarks>
/// The clip rectangle is left, top, right and bottom in target pixels. The texture is the id that the UI gave it. The
/// indices and vertices count from the start of the frame's.
/// </remarks>
internal readonly record struct DebugUIDraw(
    Vector4 Clip,
    ulong Texture,
    uint IndexCount,
    uint FirstIndex,
    uint FirstVertex
);

/// <summary>
/// A change to one of ImGui's textures, queued in the frame that asked for it.
/// </summary>
/// <remarks>
/// The texture is the id that the UI gave it. The pixels are the whole texture as RGBA8, to make it or fill it again,
/// and null when ImGui let go of it.
/// </remarks>
internal readonly record struct DebugUITextureChange(ulong Texture, uint Frame, int Width, int Height, byte[]? Pixels);
