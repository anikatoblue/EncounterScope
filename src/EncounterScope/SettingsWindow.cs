using Dalamud.Bindings.ImGui;

namespace EncounterScope;

internal sealed record CaptureStatusSnapshot(
    bool Enabled,
    bool HookAvailable,
    bool DutyBound,
    string? SessionId,
    string Path,
    long SessionBytes,
    long RawDrops,
    long NormalizedDrops,
    long StatusDrops,
    long HookErrors,
    string? WriterError);

internal sealed class SettingsWindow(
    Func<CaptureStatusSnapshot> snapshot,
    Action<bool> setEnabled,
    Action openCaptureFolder)
{
    private bool open;

    public void Open() => open = true;

    public void Draw()
    {
        if (!open)
            return;

        if (!ImGui.Begin("EncounterScope###EncounterScopeSettings", ref open))
        {
            ImGui.End();
            return;
        }

        var status = snapshot();
        var enabled = status.Enabled;
        if (!status.HookAvailable)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox("Automatically log duties", ref enabled))
            setEnabled(enabled);
        if (!status.HookAvailable)
            ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextUnformatted($"Action-effect hook: {(status.HookAvailable ? "Ready" : "Unavailable")}");
        ImGui.TextUnformatted($"Duty state: {(status.DutyBound ? "Bound by duty" : "Outside duty")}");
        ImGui.TextUnformatted(status.SessionId is null
            ? "Capture: Idle"
            : $"Capture: Recording {status.SessionId}");
        ImGui.TextUnformatted($"Session bytes: {status.SessionBytes:N0}");
        ImGui.TextUnformatted(
            $"Dropped events: raw {status.RawDrops:N0}, normalized {status.NormalizedDrops:N0}");
        ImGui.TextUnformatted($"Dropped status events: {status.StatusDrops:N0}");
        ImGui.TextUnformatted($"Hook errors: {status.HookErrors:N0}");
        ImGui.TextWrapped($"Path: {status.Path}");
        if (status.WriterError is not null)
            ImGui.TextWrapped($"Writer error: {status.WriterError}");

        if (ImGui.Button("Open capture folder"))
            openCaptureFolder();

        ImGui.End();
    }
}
