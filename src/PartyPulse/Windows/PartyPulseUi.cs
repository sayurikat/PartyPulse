using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace PartyPulse.Windows;

internal static class PartyPulseUi
{
    public static readonly Vector4 Success = new(0.35f, 0.85f, 0.45f, 1f);
    public static readonly Vector4 Warning = new(1f, 0.72f, 0.25f, 1f);
    public static readonly Vector4 Danger = new(0.95f, 0.32f, 0.32f, 1f);
    public static readonly Vector4 Info = new(0.35f, 0.70f, 1f, 1f);
    public static readonly Vector4 Muted = new(0.62f, 0.64f, 0.68f, 1f);
    public static readonly Vector4 Accent = new(0.78f, 0.52f, 0.95f, 1f);

    public static void PageHeader(string title, string description)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.SetWindowFontScale(1.18f);
        ImGui.TextUnformatted(title);
        ImGui.SetWindowFontScale(1f);
        ImGui.PopStyleColor();

        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Muted);
            ImGui.TextWrapped(description);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    public static void SectionHeader(string title, string? description = null)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Muted);
            ImGui.TextWrapped(description);
            ImGui.PopStyleColor();
        }
        ImGui.Spacing();
    }

    public static void InlineStatus(string text, Vector4 color)
    {
        var radius = 3.5f * ImGuiHelpers.GlobalScale;
        var cursor = ImGui.GetCursorScreenPos();
        var center = new Vector2(cursor.X + radius, cursor.Y + (ImGui.GetTextLineHeight() / 2f));
        ImGui.GetWindowDrawList().AddCircleFilled(center, radius, ImGui.GetColorU32(color));
        ImGui.Dummy(new Vector2((radius * 2f) + (6f * ImGuiHelpers.GlobalScale), ImGui.GetTextLineHeight()));
        ImGui.SameLine(0, 0);
        ImGui.TextColored(color, text);
    }

    public static bool NavigationButton(string label, string id, bool selected, Vector2 size)
    {
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.40f, 0.24f, 0.52f, 0.95f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.48f, 0.30f, 0.62f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.52f, 0.34f, 0.68f, 1f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.30f, 0.34f, 0.75f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.38f, 0.38f, 0.42f, 0.85f));
        }

        var clicked = ImGui.Button($"{label}##{id}", size);
        ImGui.PopStyleColor(3);
        return clicked;
    }
}
