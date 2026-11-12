using System.Threading.Channels;
using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using static Magic.ImGuiDebugUI.ImGui;
using static Magic.ImGuiDebugUI.ImTextureStatus;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The debug UI over the window's image: <see cref="Publish"/> copies each frame ImGui ended on the game thread, and
/// <see cref="Render"/> records the newest copy in the overlay layer, so it shows over whatever drew the world.
/// </summary>
public sealed class Renderer : IRender
{
    private readonly DebugUIOverlay _overlay;

    /// <summary>
    /// Each frame ImGui ends, copied for the overlay, and what ImGui asks of its textures, in the order it asks.
    /// </summary>
    private readonly Mailbox<DebugUIFrame> _frames = new();
    private readonly Channel<DebugUITextureChange> _textureChanges = Channel.CreateUnbounded<DebugUITextureChange>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );

    /// <summary>
    /// The id the last of ImGui's textures was given, which the overlay knows it by.
    /// </summary>
    private ulong _lastTextureId;

    internal Renderer(AssetManager assets) => _overlay = new DebugUIOverlay(assets, _frames, _textureChanges.Reader);

    /// <summary>
    /// The newest frame ImGui ended, over the window's image.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph) => _overlay.Record(graph);

    /// <summary>
    /// Publishes a copy of the frame ImGui just ended in <paramref name="frame"/> for the overlay. Its textures get their
    /// ids before the copy, which reads them. Game thread.
    /// </summary>
    internal unsafe void Publish(uint frame)
    {
        ImDrawData_t* data = ImGui_GetDrawData();
        QueueTextureChanges(data, frame);
        _frames.Back.Copy(data, frame);
        _frames.Publish();
    }

    /// <summary>
    /// What ImGui asks of its textures, queued for the overlay with <paramref name="frame"/> and done as far as ImGui
    /// knows: a new one given an id and filled, a changed one filled again, and one it let go of dropped once the frame
    /// just ended draws nothing with it.
    /// </summary>
    private unsafe void QueueTextureChanges(ImDrawData_t* data, uint frame)
    {
        ImVector_ImTextureDataPtr_t* textures = data->Textures;
        for (int i = 0; textures != null && i < textures->Size; i++)
        {
            ImTextureData_t* texture = textures->Data[i];
            if (texture->Status == ImTextureStatus_WantCreate)
                ImTextureData_SetTexID(texture, ++_lastTextureId);

            // RGBA8 whole, which is how ImGui's atlas holds it.
            if (texture->Status is ImTextureStatus_WantCreate or ImTextureStatus_WantUpdates)
            {
                byte[] pixels = new ReadOnlySpan<byte>(texture->Pixels, texture->Width * texture->Height * 4).ToArray();
                _textureChanges.Writer.TryWrite(
                    new DebugUITextureChange(texture->TexID, frame, texture->Width, texture->Height, pixels)
                );
                ImTextureData_SetStatus(texture, ImTextureStatus_OK);
            }
            else if (texture->Status == ImTextureStatus_WantDestroy && texture->UnusedFrames > 0)
            {
                _textureChanges.Writer.TryWrite(new DebugUITextureChange(texture->TexID, frame, 0, 0, null));
                ImTextureData_SetTexID(texture, 0);
                ImTextureData_SetStatus(texture, ImTextureStatus_Destroyed);
            }
        }
    }
}
