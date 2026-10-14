using System.Numerics;
using Hexa.NET.ImGui;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The engine's look: violet glass, lilac edges, pink where something is held, white text, everything rounded. Colours
/// are as ImGui takes them, already encoded, as what it draws goes into the window's image as it is.
/// </summary>
internal static class Look
{
    public static void Apply(ImGuiStylePtr style)
    {
        ImGui.StyleColorsDark(style);
        style.WindowRounding = 10f;
        style.ChildRounding = 8f;
        style.PopupRounding = 8f;
        style.FrameRounding = 6f;
        style.GrabRounding = 6f;
        style.TabRounding = 6f;
        style.ScrollbarRounding = 10f;
        style.WindowBorderSize = 1f;
        style.FrameBorderSize = 0f;
        style.WindowPadding = new Vector2(12f, 10f);
        style.FramePadding = new Vector2(8f, 5f);
        style.ItemSpacing = new Vector2(8f, 6f);
        style.WindowTitleAlign = new Vector2(0.5f, 0.5f);

        Vector4 white = new(1f, 1f, 1f, 1f);
        Vector4 pink = new(1f, 0.52f, 0.86f, 1f);
        Vector4 lilac = new(0.80f, 0.56f, 1f, 1f);
        Vector4 violet = new(0.64f, 0.32f, 0.96f, 1f);
        Vector4 plum = new(0.36f, 0.17f, 0.60f, 1f);
        Vector4 night = new(0.17f, 0.08f, 0.32f, 1f);

        Span<Vector4> colors = style.Colors;
        colors[(int)ImGuiCol.Text] = white;
        colors[(int)ImGuiCol.TextDisabled] = lilac with { W = 0.7f };
        colors[(int)ImGuiCol.WindowBg] = night with { W = 0.90f };
        colors[(int)ImGuiCol.ChildBg] = plum with { W = 0.35f };
        colors[(int)ImGuiCol.PopupBg] = night with { W = 0.97f };
        colors[(int)ImGuiCol.Border] = lilac with { W = 0.85f };
        colors[(int)ImGuiCol.FrameBg] = plum with { W = 0.85f };
        colors[(int)ImGuiCol.FrameBgHovered] = violet with { W = 0.6f };
        colors[(int)ImGuiCol.FrameBgActive] = violet with { W = 0.9f };
        colors[(int)ImGuiCol.TitleBg] = violet with { W = 0.85f };
        colors[(int)ImGuiCol.TitleBgActive] = Vector4.Lerp(violet, pink, 0.45f);
        colors[(int)ImGuiCol.TitleBgCollapsed] = plum with { W = 0.7f };
        colors[(int)ImGuiCol.MenuBarBg] = plum;
        colors[(int)ImGuiCol.ScrollbarBg] = night with { W = 0.4f };
        colors[(int)ImGuiCol.ScrollbarGrab] = violet with { W = 0.8f };
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = lilac;
        colors[(int)ImGuiCol.ScrollbarGrabActive] = pink;
        colors[(int)ImGuiCol.CheckMark] = pink;
        colors[(int)ImGuiCol.SliderGrab] = lilac;
        colors[(int)ImGuiCol.SliderGrabActive] = pink;
        colors[(int)ImGuiCol.Button] = violet with { W = 0.85f };
        colors[(int)ImGuiCol.ButtonHovered] = lilac with { W = 0.9f };
        colors[(int)ImGuiCol.ButtonActive] = pink;
        colors[(int)ImGuiCol.Header] = violet with { W = 0.55f };
        colors[(int)ImGuiCol.HeaderHovered] = violet with { W = 0.85f };
        colors[(int)ImGuiCol.HeaderActive] = pink with { W = 0.9f };
        colors[(int)ImGuiCol.Separator] = lilac with { W = 0.4f };
        colors[(int)ImGuiCol.SeparatorHovered] = lilac;
        colors[(int)ImGuiCol.SeparatorActive] = pink;
        colors[(int)ImGuiCol.ResizeGrip] = lilac with { W = 0.3f };
        colors[(int)ImGuiCol.ResizeGripHovered] = lilac with { W = 0.8f };
        colors[(int)ImGuiCol.ResizeGripActive] = pink;
        colors[(int)ImGuiCol.Tab] = plum;
        colors[(int)ImGuiCol.TabHovered] = lilac with { W = 0.9f };
        colors[(int)ImGuiCol.TabSelected] = violet;
        colors[(int)ImGuiCol.TextSelectedBg] = pink with { W = 0.4f };
        colors[(int)ImGuiCol.NavCursor] = pink;
    }
}
