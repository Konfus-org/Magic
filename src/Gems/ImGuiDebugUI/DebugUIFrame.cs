using System.Numerics;
using System.Runtime.CompilerServices;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// A copy of the frame ImGui ended, which the overlay draws without reading ImGui: every list's vertices in one run, their
/// indices widened to the 32 bits a draw takes in another, and the draws counted from the start of both.
/// </summary>
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
    /// Holds <paramref name="data"/>, ended in frame <paramref name="index"/>, in place of what it held. A draw that calls
    /// back or draws nothing is left out.
    /// </summary>
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
/// One draw of a <see cref="DebugUIFrame"/>: its clip rectangle as left, top, right and bottom in target pixels, the
/// texture by the id the UI gave it, and its indices and vertices from the start of the frame's.
/// </summary>
internal readonly record struct DebugUIDraw(
    Vector4 Clip,
    ulong Texture,
    uint IndexCount,
    uint FirstIndex,
    uint FirstVertex
);

/// <summary>
/// A change to one of ImGui's textures, queued in the frame it was asked for: the texture by the id the UI gave it, and
/// its pixels whole as RGBA8, to make it or fill it again; none when ImGui let go of it.
/// </summary>
internal readonly record struct DebugUITextureChange(ulong Texture, uint Frame, int Width, int Height, byte[]? Pixels);
