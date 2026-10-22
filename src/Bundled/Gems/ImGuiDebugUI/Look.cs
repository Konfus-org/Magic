using System.Numerics;
using System.Runtime.InteropServices;
using static Magic.ImGuiDebugUI.ImGui;
using static Magic.ImGuiDebugUI.ImGuiCol_;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The engine's look: violet glass, lilac edges, pink where something is held, white text, everything rounded. Colours
/// are as ImGui takes them, already encoded, as what it draws goes into the window's image as it is.
/// </summary>
internal static unsafe class Look
{
    internal static void Apply(ImGuiStyle_t* style)
    {
        ImGui_StyleColorsDark(style);
        style->WindowRounding = 10f;
        style->ChildRounding = 8f;
        style->PopupRounding = 8f;
        style->FrameRounding = 6f;
        style->GrabRounding = 6f;
        style->TabRounding = 6f;
        style->ScrollbarRounding = 10f;
        style->WindowBorderSize = 1f;
        style->FrameBorderSize = 0f;
        style->WindowPadding = new Vector2(12f, 10f).Im;
        style->FramePadding = new Vector2(8f, 5f).Im;
        style->ItemSpacing = new Vector2(8f, 6f).Im;
        style->WindowTitleAlign = new Vector2(0.5f, 0.5f).Im;

        Vector4 white = new(1f, 1f, 1f, 1f);
        Vector4 pink = new(1f, 0.52f, 0.86f, 1f);
        Vector4 lilac = new(0.80f, 0.56f, 1f, 1f);
        Vector4 violet = new(0.64f, 0.32f, 0.96f, 1f);
        Vector4 plum = new(0.36f, 0.17f, 0.60f, 1f);
        Vector4 night = new(0.17f, 0.08f, 0.32f, 1f);

        Span<Vector4> colors = MemoryMarshal.Cast<ImVec4_t, Vector4>((Span<ImVec4_t>)style->Colors);
        colors[(int)ImGuiCol_Text] = white;
        colors[(int)ImGuiCol_TextDisabled] = lilac with { W = 0.7f };
        colors[(int)ImGuiCol_WindowBg] = night with { W = 0.90f };
        colors[(int)ImGuiCol_ChildBg] = plum with { W = 0.35f };
        colors[(int)ImGuiCol_PopupBg] = night with { W = 0.97f };
        colors[(int)ImGuiCol_Border] = lilac with { W = 0.85f };
        colors[(int)ImGuiCol_FrameBg] = plum with { W = 0.85f };
        colors[(int)ImGuiCol_FrameBgHovered] = violet with { W = 0.6f };
        colors[(int)ImGuiCol_FrameBgActive] = violet with { W = 0.9f };
        colors[(int)ImGuiCol_TitleBg] = violet with { W = 0.85f };
        colors[(int)ImGuiCol_TitleBgActive] = Vector4.Lerp(violet, pink, 0.45f);
        colors[(int)ImGuiCol_TitleBgCollapsed] = plum with { W = 0.7f };
        colors[(int)ImGuiCol_MenuBarBg] = plum;
        colors[(int)ImGuiCol_ScrollbarBg] = night with { W = 0.4f };
        colors[(int)ImGuiCol_ScrollbarGrab] = violet with { W = 0.8f };
        colors[(int)ImGuiCol_ScrollbarGrabHovered] = lilac;
        colors[(int)ImGuiCol_ScrollbarGrabActive] = pink;
        colors[(int)ImGuiCol_CheckMark] = pink;
        colors[(int)ImGuiCol_SliderGrab] = lilac;
        colors[(int)ImGuiCol_SliderGrabActive] = pink;
        colors[(int)ImGuiCol_Button] = violet with { W = 0.85f };
        colors[(int)ImGuiCol_ButtonHovered] = lilac with { W = 0.9f };
        colors[(int)ImGuiCol_ButtonActive] = pink;
        colors[(int)ImGuiCol_Header] = violet with { W = 0.55f };
        colors[(int)ImGuiCol_HeaderHovered] = violet with { W = 0.85f };
        colors[(int)ImGuiCol_HeaderActive] = pink with { W = 0.9f };
        colors[(int)ImGuiCol_Separator] = lilac with { W = 0.4f };
        colors[(int)ImGuiCol_SeparatorHovered] = lilac;
        colors[(int)ImGuiCol_SeparatorActive] = pink;
        colors[(int)ImGuiCol_ResizeGrip] = lilac with { W = 0.3f };
        colors[(int)ImGuiCol_ResizeGripHovered] = lilac with { W = 0.8f };
        colors[(int)ImGuiCol_ResizeGripActive] = pink;
        colors[(int)ImGuiCol_Tab] = plum;
        colors[(int)ImGuiCol_TabHovered] = lilac with { W = 0.9f };
        colors[(int)ImGuiCol_TabSelected] = violet;
        colors[(int)ImGuiCol_TextSelectedBg] = pink with { W = 0.4f };
        colors[(int)ImGuiCol_NavCursor] = pink;
    }
}
