using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PartyPulse.Notifications;

namespace PartyPulse.Windows;

public sealed class NotificationToastWindow : Window, IDisposable
{
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(20);
    private readonly Plugin plugin;
    private QueuedPartyPulseNotification? current;
    private DateTimeOffset hideAt;

    public NotificationToastWindow(Plugin plugin)
        : base("PartyPulse notification###PartyPulseNotificationToast")
    {
        this.plugin = plugin;
        IsOpen = false;
        Flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav;
    }

    public void Tick()
    {
        if (current is not null || !plugin.Notifications.TryDequeue(out var next) || next is null)
        {
            return;
        }

        current = next;
        hideAt = DateTimeOffset.UtcNow.Add(DisplayDuration);
        IsOpen = true;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        var position = viewport.WorkPos + viewport.WorkSize - new Vector2(18, 18);
        ImGui.SetNextWindowPos(position, ImGuiCond.Always, Vector2.One);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(340 * ImGuiHelpers.GlobalScale, 0),
            new Vector2(460 * ImGuiHelpers.GlobalScale, float.MaxValue));
    }

    public override void Draw()
    {
        if (current is null)
        {
            IsOpen = false;
            return;
        }

        ImGui.TextUnformatted(current.Notification.Title);
        ImGui.Separator();
        ImGui.TextWrapped(current.Notification.Message);
        ImGui.Spacing();

        if (!string.IsNullOrWhiteSpace(current.Notification.ActionKey) && ImGui.Button("Open"))
        {
            var notification = current;
            plugin.OpenNotificationAction(notification);
            plugin.MarkNotificationSeen(notification, false);
            CloseCurrent();
            return;
        }

        if (!string.IsNullOrWhiteSpace(current.Notification.ActionKey))
        {
            ImGui.SameLine();
        }

        if (ImGui.Button("Dismiss"))
        {
            var notification = current;
            plugin.MarkNotificationSeen(notification, true);
            CloseCurrent();
            return;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Non-blocking notification");

        if (DateTimeOffset.UtcNow >= hideAt)
        {
            // Hiding the toast does not mark it seen. It remains pending in the finance screen.
            CloseCurrent();
        }
    }

    public void Dispose()
    {
        current = null;
    }

    private void CloseCurrent()
    {
        current = null;
        IsOpen = false;
    }
}
