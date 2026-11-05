using System.Numerics;
using System.Runtime.CompilerServices;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// Magic's vectors as ImGui's, and back: the two lie the same in memory, so each is the other's bits.
/// </summary>
internal static class ImGuiDebugUIExtensions
{
    extension(Vector2 vector)
    {
        internal ImVec2_t Im => Unsafe.BitCast<Vector2, ImVec2_t>(vector);
    }

    extension(ImVec2_t vector)
    {
        internal Vector2 Vector => Unsafe.BitCast<ImVec2_t, Vector2>(vector);
    }
}
