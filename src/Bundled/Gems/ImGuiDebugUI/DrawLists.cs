using Hexa.NET.ImGui;
using Rectangle = System.Drawing.Rectangle;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// One frame of ImGui's drawing, copied on the main thread as the frame ends, for the render thread to draw a frame
/// behind while ImGui makes the next: the vertices and indices of every draw list in one array each, the draws into
/// them, and what changed in ImGui's textures, each known by a number of the gem's own. The main thread copies only once
/// the render thread is done with the last copy, as it always is by LateUpdate.
/// </summary>
internal sealed unsafe class DrawLists
{
    private readonly List<DrawCall> _calls = [];
    private ImDrawVert[] _vertices = new ImDrawVert[4096];
    private ushort[] _indices = new ushort[8192];
    private int _vertexCount;
    private int _indexCount;
    private uint _nextTexture = 1;

    /// <summary>
    /// In window pixels, as ImGui lays out in them.
    /// </summary>
    public ReadOnlySpan<ImDrawVert> Vertices => _vertices.AsSpan(0, _vertexCount);

    public ReadOnlySpan<ushort> Indices => _indices.AsSpan(0, _indexCount);

    public IReadOnlyList<DrawCall> Calls => _calls;

    /// <summary>
    /// What changed in ImGui's textures since the last copy, in order, for the render thread to take in and clear.
    /// </summary>
    public List<TextureChange> Textures { get; } = [];

    /// <summary>
    /// Every texture ImGui has, by its number, as it last was: what a renderer that starts drawing now takes in first,
    /// the changes before it having been taken in and cleared by whatever drew before.
    /// </summary>
    public Dictionary<uint, TextureChange> Known { get; } = [];

    /// <summary>
    /// Copies the frame ImGui just ended, for a window of <paramref name="width"/> by <paramref name="height"/> pixels,
    /// and answers ImGui's asks about its textures.
    /// </summary>
    public void Copy(ImDrawDataPtr data, int width, int height)
    {
        CopyTextures(data);
        Grow(ref _vertices, data.TotalVtxCount);
        Grow(ref _indices, data.TotalIdxCount);
        _calls.Clear();
        Rectangle window = new(0, 0, width, height);
        int vertexBase = 0;
        int indexBase = 0;
        for (int listIndex = 0; listIndex < data.CmdListsCount; listIndex++)
        {
            ImDrawListPtr list = data.CmdLists[listIndex];
            new ReadOnlySpan<ImDrawVert>(list.VtxBuffer.Data, list.VtxBuffer.Size).CopyTo(_vertices.AsSpan(vertexBase));
            new ReadOnlySpan<ushort>(list.IdxBuffer.Data, list.IdxBuffer.Size).CopyTo(_indices.AsSpan(indexBase));
            for (int commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
            {
                ImDrawCmd command = list.CmdBuffer.Data[commandIndex];
                if (command.UserCallback != null || command.ElemCount == 0)
                {
                    continue;
                }

                Rectangle clip = Rectangle.Intersect(window, Rectangle.FromLTRB(
                    (int)MathF.Floor(command.ClipRect.X),
                    (int)MathF.Floor(command.ClipRect.Y),
                    (int)MathF.Ceiling(command.ClipRect.Z),
                    (int)MathF.Ceiling(command.ClipRect.W)));
                if (clip.Width <= 0 || clip.Height <= 0)
                {
                    continue;
                }

                ImTextureID texture = command.TexRef.TexData != null ? command.TexRef.TexData->TexID : command.TexRef.TexID;
                _calls.Add(new DrawCall((uint)texture.Handle, clip, command.ElemCount, (uint)indexBase + command.IdxOffset, vertexBase + (int)command.VtxOffset));
            }

            vertexBase += list.VtxBuffer.Size;
            indexBase += list.IdxBuffer.Size;
        }

        _vertexCount = vertexBase;
        _indexCount = indexBase;
    }

    private static void Grow<T>(ref T[] array, int count)
    {
        if (array.Length < count)
        {
            Array.Resize(ref array, Math.Max(count, array.Length * 2));
        }
    }

    /// <summary>
    /// The texture's pixels as RGBA8: as they are, or ImGui's one-byte coverage made white with it as alpha.
    /// </summary>
    private static byte[] Rgba(ImTextureDataPtr texture)
    {
        int count = texture.Width * texture.Height;
        ReadOnlySpan<byte> pixels = new(texture.Pixels, count * texture.BytesPerPixel);
        if (texture.BytesPerPixel == 4)
        {
            return pixels.ToArray();
        }

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
    /// ImGui asks its renderer to make, update and let go of its textures: each ask becomes a change for the render
    /// thread, with the pixels copied, and is answered at once, with a number of the gem's own for a new one.
    /// </summary>
    private void CopyTextures(ImDrawDataPtr data)
    {
        ImVector<ImTextureDataPtr>* textures = data.Handle->Textures;
        for (int i = 0; textures != null && i < textures->Size; i++)
        {
            ImTextureDataPtr texture = textures->Data[i];
            if (texture.Status == ImTextureStatus.WantCreate)
            {
                uint id = _nextTexture++;
                Change(new TextureChange(id, texture.Width, texture.Height, Rgba(texture)));
                texture.SetTexID(new ImTextureID(id));
                texture.SetStatus(ImTextureStatus.Ok);
            }
            else if (texture.Status == ImTextureStatus.WantUpdates)
            {
                Change(new TextureChange((uint)texture.TexID.Handle, texture.Width, texture.Height, Rgba(texture)));
                texture.SetStatus(ImTextureStatus.Ok);
            }
            else if (texture.Status == ImTextureStatus.WantDestroy && texture.UnusedFrames > 0)
            {
                Change(new TextureChange((uint)texture.TexID.Handle, 0, 0, null));
                texture.SetTexID(ImTextureID.Null);
                texture.SetStatus(ImTextureStatus.Destroyed);
            }
        }
    }

    private void Change(TextureChange change)
    {
        Textures.Add(change);
        if (change.Pixels is null)
        {
            Known.Remove(change.Id);
        }
        else
        {
            Known[change.Id] = change;
        }
    }
}

/// <summary>
/// One draw: the texture it samples, by the gem's number for it, the window pixels it is clipped to, and its run of
/// indices, from <see cref="FirstIndex"/> in the frame's indices, each added to <see cref="VertexOffset"/>.
/// </summary>
internal readonly record struct DrawCall(uint Texture, Rectangle Clip, uint Indices, uint FirstIndex, int VertexOffset);

/// <summary>
/// A texture ImGui made or changed, its RGBA8 <see cref="Pixels"/> whole, or let go of when they are null.
/// </summary>
internal readonly record struct TextureChange(uint Id, int Width, int Height, byte[]? Pixels);
