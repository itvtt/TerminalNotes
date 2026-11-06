using System.IO;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private bool _updateCheckStarted;

    // The version label's tooltip always says whether this build checks for updates and what the last check found.
    private void ShowUpdateCheckState(string state, string stage)
    {
        AppVersionLabel.ToolTip = state;
        ApplicationDiagnostics.Record(stage, "update");
    }

    // Runs once after the window appears in the real app (tests construct the window without autoStartCodex).
    private async Task CheckForUpdatesAtStartupAsync()
    {
        if (_updateCheckStarted || !_autoStartCodex) return;
        _updateCheckStarted = true;
        UpdateService.SyncInstalledVersion();
        var settings = UpdateService.LoadSettings();
        if (settings is null)
        {
            ShowUpdateCheckState("자동 업데이트 꺼짐 · 이 빌드에 업데이트 저장소(UPDATE_REPOSITORY)가 없습니다.", "update_check_unconfigured");
            return;
        }
        var service = new UpdateService(settings);
        AvailableUpdate? update;
        AppVersionLabel.ToolTip = "업데이트 확인 중…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            update = await service.CheckAsync(UpdateService.CurrentVersion, timeout.Token);
        }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Offline or the release server is unreachable: never block startup or nag with a popup.
            string reason = exception is UpdateAccessException ? exception.Message
                : exception is System.Net.Http.HttpRequestException { StatusCode: { } code } ? $"서버 응답 {(int)code}" : exception.GetType().Name;
            ShowUpdateCheckState($"업데이트 확인 실패 · {DateTime.Now:HH:mm} · {reason}", exception is UpdateAccessException ? "update_check_denied" : "update_check_failed");
            SetStatus("업데이트 확인 실패 · " + reason + " · 다음 실행 때 다시 확인합니다.");
            return;
        }
        if (update is null)
        {
            ShowUpdateCheckState($"최신 버전 · {DateTime.Now:HH:mm} 확인 · {settings.Repository}", "update_check_current");
            return;
        }
        if (_closing || _closePending) return;
        string current = "v" + ApplicationDiagnostics.DisplayVersion, next = "v" + update.Version;
        ShowUpdateCheckState($"새 버전 {next} 있음 · {DateTime.Now:HH:mm} 확인", "update_check_available");
        bool terminalsRunning = TerminalTabs.Items.Cast<TabItem>().Any(tab => ((TerminalTabState)tab.Tag).Pane.IsRunning);
        if (terminalsRunning)
        {
            SetAttentionStatus($"새 버전 {next}이(가) 있습니다 (현재 {current}) · 실행 중인 터미널을 정리한 뒤 다시 실행하면 업데이트합니다.");
            return;
        }
        if (!AppMessageDialog.Confirm(this, "업데이트",
                $"새 버전 {next}이(가) 있습니다. (현재 {current})\n\n지금 내려받아 업데이트한 뒤 다시 시작할까요?\n작업 폴더·플랜·설정·공지는 그대로 유지됩니다.", "업데이트"))
        {
            SetStatus($"새 버전 {next} · 다음 실행 때 다시 묻습니다.");
            return;
        }
        string updates = Path.Combine(Path.GetDirectoryName(_settingsPath) ?? Path.GetTempPath(), "updates");
        try
        {
            SetStatus($"{next} 내려받는 중…");
            using var download = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            string package = await service.DownloadAsync(update, updates, download.Token);
            UpdateService.LaunchUpdater(package, AppContext.BaseDirectory, Path.Combine(updates, "update.log"));
            SetStatus($"{next} 설치 준비 완료 · 앱을 닫으면 업데이트 후 다시 시작합니다.");
            await RequestExitAsync();
        }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppMessageDialog.ShowError(this, "업데이트 실패", $"{next}을(를) 내려받거나 설치를 준비하지 못했습니다.\n\n{exception.Message}\n\n지금 버전({current})을 계속 사용합니다.");
        }
    }
}
