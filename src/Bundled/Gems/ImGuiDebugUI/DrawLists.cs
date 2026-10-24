using System.Runtime.InteropServices;
using static Magic.ImGuiDebugUI.ImGui;
using static Magic.ImGuiDebugUI.ImTextureStatus;
using Rectangle = System.Drawing.Rectangle;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// One draw: the texture it samples, by the gem's number for it, the window pixels it is clipped to, and its run of
/// indices, from <see cref="FirstIndex"/> in the frame's indices, each added to <see cref="VertexOffset"/>.
/// </summary>
internal readonly record struct DrawCall(uint Texture, Rectangle Clip, uint Indices, uint FirstIndex, int VertexOffset);

/// <summary>
/// A texture ImGui has: its RGBA8 <see cref="Pixels"/> whole, and their <see cref="Version"/>, which goes up each time
/// ImGui changes them.
/// </summary>
internal readonly record struct ImGuiTexture(int Width, int Height, byte[] Pixels, uint Version);

/// <summary>
/// One frame of ImGui's drawing, copied at Sync while both threads hold still, for the render thread to draw a frame
/// behind while ImGui makes the next: the vertices and indices of every draw list in one array each, the draws into
/// them, and ImGui's textures as they are, each known by a number of the gem's own. At Sync the render thread is done
/// with the last copy and has not begun on this one, so the copy is made in place rather than kept twice over.
/// </summary>
internal sealed unsafe class DrawLists
{
    private readonly List<DrawCall> _calls = [];
    private ImDrawVert_t[] _vertices = new ImDrawVert_t[4096];
    private ushort[] _indices = new ushort[8192];
    private int _vertexCount;
    private int _indexCount;
    private uint _nextTexture = 1;
    private uint _nextVersion = 1;

    /// <summary>
    /// In window pixels, as ImGui lays out in them.
    /// </summary>
    internal ReadOnlySpan<ImDrawVert_t> Vertices => _vertices.AsSpan(0, _vertexCount);

    internal ReadOnlySpan<ushort> Indices => _indices.AsSpan(0, _indexCount);

    internal ReadOnlySpan<DrawCall> Calls => CollectionsMarshal.AsSpan(_calls);

    /// <summary>
    /// Every texture ImGui has, by its number, as it last was, each with the version of its pixels, which goes up as
    /// ImGui changes them: what draws them uploads those whose version it has not, and lets go of those gone.
    /// </summary>
    internal Dictionary<uint, ImGuiTexture> Textures { get; } = [];

    /// <summary>
    /// Copies the frame ImGui just ended, for a window of <paramref name="width"/> by <paramref name="height"/> pixels,
    /// and answers ImGui's asks about its textures.
    /// </summary>
    internal void Copy(ImDrawData_t* data, int width, int height)
    {
        CopyTextures(data);
        Grow(ref _vertices, data->TotalVtxCount);
        Grow(ref _indices, data->TotalIdxCount);
        _calls.Clear();
        Rectangle window = new(0, 0, width, height);
        int vertexBase = 0;
        int indexBase = 0;
        for (int listIndex = 0; listIndex < data->CmdLists.Size; listIndex++)
        {
            ImDrawList_t* list = data->CmdLists.Data[listIndex];
            new ReadOnlySpan<ImDrawVert_t>(list->VtxBuffer.Data, list->VtxBuffer.Size).CopyTo(
                _vertices.AsSpan(vertexBase));
            new ReadOnlySpan<ushort>(list->IdxBuffer.Data, list->IdxBuffer.Size).CopyTo(_indices.AsSpan(indexBase));
            for (int commandIndex = 0; commandIndex < list->CmdBuffer.Size; commandIndex++)
            {
                ImDrawCmd_t command = list->CmdBuffer.Data[commandIndex];

                if (command.UserCallback != null || command.ElemCount == 0) continue;

                // Clipped to the window, in whole pixels rounded out.
                Rectangle clip = Rectangle.Intersect(
                    window,
                    Rectangle.FromLTRB(
                        (int)MathF.Floor(command.ClipRect.x),
                        (int)MathF.Floor(command.ClipRect.y),
                        (int)MathF.Ceiling(command.ClipRect.z),
                        (int)MathF.Ceiling(command.ClipRect.w)));

                if (clip.Width <= 0 || clip.Height <= 0) continue;

                // The texture by the number ImGui's own texture was given, or the id the draw names alone; the indices
                // and vertices moved past the lists copied before this one.
                ulong texture = command.TexRef._TexData != null
                    ? command.TexRef._TexData->TexID
                    : command.TexRef._TexID;
                _calls.Add(new DrawCall(
                    (uint)texture,
                    clip,
                    command.ElemCount,
                    (uint)indexBase + command.IdxOffset,
                    vertexBase + (int)command.VtxOffset));
            }

            vertexBase += list->VtxBuffer.Size;
            indexBase += list->IdxBuffer.Size;
        }

        _vertexCount = vertexBase;
        _indexCount = indexBase;
    }

    private static void Grow<T>(ref T[] array, int count)
    {
        if (array.Length < count) Array.Resize(ref array, Math.Max(count, array.Length * 2));
    }

    /// <summary>
    /// The texture's pixels as RGBA8: as they are, or ImGui's one-byte coverage made white with it as alpha.
    /// </summary>
    private static byte[] ToRgba(ImTextureData_t* texture)
    {
        int count = texture->Width * texture->Height;
        ReadOnlySpan<byte> pixels = new(texture->Pixels, count * texture->BytesPerPixel);

        if (texture->BytesPerPixel == 4) return pixels.ToArray();

        byte[] rgba = new byte[count * 4];
        for (int pixel = 0; pixel < count; pixel++)
        {
            rgba[pixel * 4] = 255;
            rgba[(pixel * 4) + 1] = 255;
            rgba[(pixel * 4) + 2] = 255;
            rgba[(pixel * 4) + 3] = pixels[pixel];
        }

        return rgba;
    }

    /// <summary>
    /// ImGui asks its renderer to make, update and let go of its textures: each ask goes into <see cref="Textures"/>, with
    /// the pixels copied, and is answered at once, with a number of the gem's own for a new one.
    /// </summary>
    private void CopyTextures(ImDrawData_t* data)
    {
        // A texture ImGui lets go of is forgotten only once the frame just ended draws nothing with it.
        ImVector_ImTextureDataPtr_t* textures = data->Textures;
        for (int i = 0; textures != null && i < textures->Size; i++)
        {
            ImTextureData_t* texture = textures->Data[i];
            if (texture->Status == ImTextureStatus_WantCreate) ImTextureData_SetTexID(texture, _nextTexture++);

            if (texture->Status is ImTextureStatus_WantCreate or ImTextureStatus_WantUpdates)
            {
                Textures[(uint)texture->TexID] = new ImGuiTexture(
                    texture->Width,
                    texture->Height,
                    ToRgba(texture),
                    _nextVersion++);
                ImTextureData_SetStatus(texture, ImTextureStatus_OK);
            }
            else if (texture->Status == ImTextureStatus_WantDestroy && texture->UnusedFrames > 0)
            {
                Textures.Remove((uint)texture->TexID);
                ImTextureData_SetTexID(texture, 0);
                ImTextureData_SetStatus(texture, ImTextureStatus_Destroyed);
            }
        }
    }
}
