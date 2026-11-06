using System.Windows;
using System.Windows.Threading;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private AnnouncementService? _announcementService;
    private AnnouncementDialog? _announcementDialog;
    private CancellationTokenSource? _announcementLifetime;
    private bool _announcementsRefreshing;
    private bool _announcementsRefreshPending;
    private bool _announcementOpening;
    private DispatcherTimer? _announcementTimer;
    private DateTime _announcementsCheckedAt;

    private void InitializeAnnouncements()
    {
        // With the report relay configured the board is shared: what an editor adds, every user sees. Test windows
        // (autoStartCodex=false) carry the build's report-relay.json too but never reach that real relay unless a test
        // names its own address.
        var (relayUrl, relayKey) = _autoStartCodex || AppContext.GetData("TerminalNotes.ReportRelayUrl") is string
            ? ReportRelayClient.Endpoint() : ((string?)null, (string?)null);
        _announcementService = new AnnouncementService(AnnouncementRelay.From(relayUrl, relayKey));
        _announcementLifetime = new CancellationTokenSource();
        Activated += Announcements_Activated;
        if (_announcementService.CanEdit) AnnouncementButton.ToolTip = "공지\n" + _announcementService.SourceLabel;
        // A shared board changes on other PCs: look again every minute while the window shows (an unchanged board is a
        // short 304 reply), and when the window is activated. 30 minutes left a colleague without the notice for too long.
        if (_announcementService.UsesRelay)
        {
            _announcementTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) =>
            {
                if (IsVisible && WindowState != WindowState.Minimized) _ = RefreshAnnouncementsAsync();
            }, Dispatcher);
            _announcementTimer.Start();
        }
        _ = RefreshAnnouncementsAsync();
    }

    private void Announcements_Activated(object? sender, EventArgs e)
    {
        // Switching windows often must not query the relay every time.
        if (_announcementService?.UsesRelay == true && DateTime.UtcNow - _announcementsCheckedAt < TimeSpan.FromMinutes(1)) return;
        _ = RefreshAnnouncementsAsync();
    }

    private async Task RefreshAnnouncementsAsync()
    {
        if (_announcementLifetime is not { IsCancellationRequested: false } lifetime || _announcementService is null) return;
        if (_announcementsRefreshing) { _announcementsRefreshPending = true; return; }
        CancellationToken token = lifetime.Token;
        _announcementsRefreshing = true;
        try
        {
            do
            {
                _announcementsRefreshPending = false;
                _announcementsCheckedAt = DateTime.UtcNow;
                AnnouncementSnapshot current = await _announcementService.ReadAsync(token);
                bool unread = await _announcementService.HasUnreadAsync(current, token);
                if (lifetime.IsCancellationRequested) return;
                AnnouncementUnreadIndicator.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
                AnnouncementButton.ToolTip = (unread ? "읽지 않은 공지" : "공지") +
                    (_announcementService.ShareStatus(current) is { } share ? "\n" + share : "");
            } while (_announcementsRefreshPending && !lifetime.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) AnnouncementButton.ToolTip = "공지 읽기 시간이 초과되었습니다. 클릭하여 다시 시도하세요.";
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            if (!lifetime.IsCancellationRequested) AnnouncementButton.ToolTip = "공지를 읽을 수 없습니다. 클릭하여 다시 시도하세요.";
            ApplicationDiagnostics.Record("announcement_read_failed", "announcements", "unavailable");
        }
        finally { _announcementsRefreshing = false; }
    }

    private async void Announcements_Click(object sender, RoutedEventArgs e)
    {
        if (_announcementLifetime is not { IsCancellationRequested: false } lifetime || _announcementService is null) return;
        if (_announcementDialog is { } existing) { existing.Activate(); return; }
        if (_announcementOpening) return;
        _announcementOpening = true;
        try
        {
            AnnouncementSnapshot snapshot = await _announcementService.ReadAsync(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            var dialog = new AnnouncementDialog(this, _announcementService, snapshot, lifetime.Token);
            _announcementDialog = dialog;
            dialog.SnapshotDisplayed += Announcement_Displayed;
            dialog.Closed += (_, _) =>
            {
                dialog.SnapshotDisplayed -= Announcement_Displayed;
                if (ReferenceEquals(_announcementDialog, dialog)) _announcementDialog = null;
                _ = RefreshAnnouncementsAsync();
            };
            ApplicationDiagnostics.Record("announcement_opened", "announcements");
            dialog.Show();
        }
        catch (OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested)
                AppMessageDialog.ShowError(this, "공지", "공지 읽기 시간이 초과되었습니다. 다시 시도해 주세요.");
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            if (!lifetime.IsCancellationRequested)
                AppMessageDialog.ShowError(this, "공지", error is System.IO.InvalidDataException ? error.Message :
                    "공지 파일을 읽을 수 없습니다. 파일 형식과 권한을 확인해 주세요.");
            ApplicationDiagnostics.Record("announcement_read_failed", "announcements", "unavailable");
        }
        finally { _announcementOpening = false; }
    }

    private async void Announcement_Displayed(AnnouncementSnapshot displayed)
    {
        if (_announcementLifetime is not { IsCancellationRequested: false } lifetime || _announcementService is null) return;
        try
        {
            // Persist only the snapshot actually rendered, so an update during viewing remains unread.
            await _announcementService.MarkReadAsync(displayed, lifetime.Token);
            await RefreshAnnouncementsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            ApplicationDiagnostics.Record("announcement_read_state_failed", "announcements", "unavailable");
        }
    }

    private void DisposeAnnouncements()
    {
        Activated -= Announcements_Activated;
        _announcementTimer?.Stop();
        _announcementTimer = null;
        _announcementLifetime?.Cancel();
        _announcementDialog?.Close();
        _announcementDialog = null;
        _announcementLifetime?.Dispose();
        _announcementLifetime = null;
    }
}
