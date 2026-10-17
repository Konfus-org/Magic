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
        public ImVec2 Im => Unsafe.BitCast<Vector2, ImVec2>(vector);
    }

    extension(Vector4 vector)
    {
        public ImVec4 Im => Unsafe.BitCast<Vector4, ImVec4>(vector);
    }

    extension(ImVec2 vector)
    {
        public Vector2 Vector => Unsafe.BitCast<ImVec2, Vector2>(vector);
    }
}
