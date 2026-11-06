using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TerminalNotes.Setup;

// TerminalNotes-Setup.exe [/S] [/dir=PATH] [/nodesktop] [/nolaunch]   install (or update an existing install)
// uninstall.exe /uninstall [/S]                                         remove (Windows "Apps" calls this)
// Advanced (deployment scripts, tests): /noshortcuts /noregistry
// Silent runs report to %TEMP%\TerminalNotes-Setup.log and exit 0 on success, 1 on failure.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool Flag(string name) => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        string? Value(string name) => args.Select(arg => arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) ? arg.Substring(name.Length + 1).Trim('"') : null)
            .LastOrDefault(value => value is not null);
        bool silent = Flag("/S") || Flag("/silent");
        bool uninstall = Flag("/uninstall");
        string directory = Value("/dir") ?? (uninstall ? AppContext.BaseDirectory : Installer.RegisteredInstallDirectory() ?? Installer.DefaultInstallDirectory);

        if (silent)
        {
            string log = Path.Combine(Path.GetTempPath(), "TerminalNotes-Setup.log");
            // UTF-8 with BOM so Windows PowerShell 5.1 (Get-Content) shows the Korean text correctly.
            void Log(string line) { try { File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}", System.Text.Encoding.UTF8); } catch (IOException) { } }
            try
            {
                if (uninstall) Installer.Uninstall(directory, Log);
                else Installer.Install(new Installer.Options(directory, !Flag("/noshortcuts"), !Flag("/nodesktop"), !Flag("/noregistry"), !Flag("/nolaunch")), Log);
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                Log("실패: " + error.Message);
                return 1;
            }
        }

        // DPI awareness comes from app.manifest (.NET Framework 4.8 has no Application.SetHighDpiMode).
        Application.EnableVisualStyles();
        if (uninstall)
        {
            if (MessageBox.Show("Terminal Notes를 제거할까요?\n\n설정·작업 폴더·플랜은 그대로 남습니다.", "Terminal Notes 제거",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return 0;
            try
            {
                Installer.Uninstall(directory, _ => { });
                MessageBox.Show("Terminal Notes를 제거했습니다.", "Terminal Notes 제거", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                MessageBox.Show(error.Message, "Terminal Notes 제거", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
        // No questions: the window installs straight away to the per-user folder (or the existing install) and starts the app.
        using var form = new SetupForm(new Installer.Options(directory, shortcuts: !Flag("/noshortcuts"), desktop: !Flag("/nodesktop"),
            register: !Flag("/noregistry"), launch: !Flag("/nolaunch")));
        Application.Run(form);
        return form.Succeeded ? 0 : 1;
    }
}

/// <summary>One small dark window in the app's palette that installs right away and shows progress; buttons appear only on failure.</summary>
internal sealed class SetupForm : Form
{
    private static readonly Color Back = ColorTranslator.FromHtml("#1A1A1A");
    private static readonly Color Line = ColorTranslator.FromHtml("#3A3A3A");
    private static readonly Color Text1 = ColorTranslator.FromHtml("#F2F2F2");
    private static readonly Color Muted = ColorTranslator.FromHtml("#BDBDBD");
    private static readonly Color Gold = ColorTranslator.FromHtml("#E5C07B");
    private static readonly Color GoldInk = ColorTranslator.FromHtml("#212121");

    private readonly Installer.Options _options;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Button _retry, _close;
    private bool _installing;
    public bool Succeeded { get; private set; }

    public SetupForm(Installer.Options options)
    {
        _options = options;
        Text = "Terminal Notes 설치";
        Icon = Icon.ExtractAssociatedIcon(Installer.SelfPath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 196);
        BackColor = Back;
        ForeColor = Text1;
        Font = new Font("Malgun Gothic", 10f);

        var title = new Label { Text = "Terminal Notes", Font = new Font("Malgun Gothic", 16f, FontStyle.Bold), ForeColor = Gold, AutoSize = true, Location = new Point(24, 18) };
        var version = new Label { Text = "v" + Installer.Version, ForeColor = Muted, AutoSize = true, Location = new Point(28, 56) };
        _status = new Label { Text = Installer.RegisteredInstallDirectory() is not null ? "설치된 버전을 바꾸는 중…" : "설치 중…",
            ForeColor = Muted, Location = new Point(24, 88), Size = new Size(432, 44) };
        _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Location = new Point(24, 136), Size = new Size(432, 6) };
        _retry = MakeButton("다시 시도", new Point(258, 148), 96, primary: true);
        _close = MakeButton("닫기", new Point(362, 148), 94, primary: false);
        _retry.Visible = _close.Visible = false;
        _retry.Click += async (_, _) => await InstallAsync();
        _close.Click += (_, _) => Close();
        CancelButton = _close;
        Controls.AddRange([title, version, _status, _progress, _retry, _close]);
        Shown += async (_, _) => await InstallAsync();
        // Closing mid-copy would leave a half-written install folder.
        FormClosing += (_, e) => e.Cancel = _installing;
    }

    private static Button MakeButton(string text, Point location, int width, bool primary)
    {
        var button = new Button
        {
            Text = text, Location = location, Size = new Size(width, 32), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? Gold : ColorTranslator.FromHtml("#242424"), ForeColor = primary ? GoldInk : Text1,
            Font = new Font("Malgun Gothic", 10f, primary ? FontStyle.Bold : FontStyle.Regular)
        };
        button.FlatAppearance.BorderColor = primary ? Gold : Line;
        return button;
    }

    private async Task InstallAsync()
    {
        if (_installing) return;
        _installing = true;
        _retry.Visible = _close.Visible = false;
        _progress.Visible = true;
        _status.ForeColor = Muted;
        try
        {
            await Task.Run(() => Installer.Install(_options, message => BeginInvoke(new Action(() => _status.Text = message))));
            Succeeded = true;
            _installing = false;
            if (_options.Launch && Installer.HasWebView2Runtime()) { Close(); return; }
            _progress.Visible = false;
            _status.Text = (_options.Launch ? "설치했습니다." : "설치했습니다. 시작 메뉴의 Terminal Notes로 실행하세요.") +
                (Installer.HasWebView2Runtime() ? "" : "\nWebView2 Runtime이 없습니다. 일부 화면을 쓰려면 설치가 필요합니다.");
            _close.Visible = true;
            AcceptButton = _close;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            _installing = false;
            _progress.Visible = false;
            _status.Text = error.Message;
            _status.ForeColor = Text1;
            _retry.Visible = _close.Visible = true;
            AcceptButton = _retry;
        }
    }
}
