using System.Runtime.ExceptionServices;
using TerminalNotes.Services;

namespace TerminalNotes;

// Only the host/UI surface is substituted; tests compile the actual admission partial.
public sealed partial class TerminalPane
{
    private readonly bool _usesNativeOpenCodeStatus = true;
    private bool _ready = false, _disposed = false, _failed = false;
    private readonly CancellationTokenSource _headlessLifetimeCancellation = new();
    private CancellationToken HeadlessLifetimeCancellation => _headlessLifetimeCancellation.Token;
    private readonly TaskCompletionSource<Exception?> _terminalStartedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FixtureDispatcher Dispatcher = new();
    private event Action<string>? StatusChanged;
    internal bool FixtureFailed => _failed;
    internal bool FixtureLifetimeCancelled => HeadlessLifetimeCancellation.IsCancellationRequested;
    internal void FixtureQueue()
    {
        _resourceAdmissionRequested.TrySetResult();
        UpdateResourceAdmissionWait(new("queue", "터미널 시작 대기"));
    }
    internal void FixtureAdmit() => _resourceAdmissionCompletion.TrySetResult(null);
    internal void FixtureWait(StartupResourceWait wait) => UpdateResourceAdmissionWait(wait);
    internal void FixtureReady() => _ready = true;
    internal void FixtureNativeCancellation() => MarkCancelledResourceStartup(new OperationCanceledException());
    private async Task EnsureReadyAsync(CancellationToken token)
    {
        var failure = await _terminalStartedCompletion.Task.WaitAsync(token);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private void ShowResourceAdmissionWait(string? reason) { }
    private void ApplyActivityChange() { }
    private void ReportLifecycleStage(string stage) { }
    private sealed class FixtureDispatcher
    {
        internal bool CheckAccess() => true;
        internal bool HasShutdownStarted => false;
        internal bool HasShutdownFinished => false;
        internal void BeginInvoke(Action action) => action();
    }
}
