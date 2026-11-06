using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TerminalNotes;
using TerminalNotes.Controls;
using TerminalNotes.Services;
using TerminalNotes.TerminalEngine;

internal static partial class Program
{
    private static int _exitCode;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                VerifySnapshotPerformance();
                VerifyTuiRedrawCost();
                await VerifyMultiPaneOutputAsync();
                if (Environment.GetEnvironmentVariable("TERMINALNOTES_NATIVE_MULTIPANE_ONLY") != "1")
                {
                    await VerifyRoutedInputAsync();
                    await VerifyCompositionHostAsync();
                    await VerifyOutputDispatcherFairnessAsync();
                    if (Environment.GetEnvironmentVariable("TERMINALNOTES_NATIVE_OUTPUT_ONLY") == "1")
                    {
                        VerifyStateBackgroundRendering();
                        await VerifyAdaptiveViewportAsync();
                    }
                    if (Environment.GetEnvironmentVariable("TERMINALNOTES_NATIVE_OUTPUT_ONLY") != "1") await VerifyAsync();
                }
                Console.WriteLine("PASS: native WPF terminal surface.");
            }
            catch (Exception exception) { _exitCode = 1; Console.Error.WriteLine("FAIL: " + exception); }
            finally { app.Shutdown(_exitCode); }
        };
        app.Run();
        return _exitCode;
    }

    private static async Task VerifyRoutedInputAsync()
    {
        var first = new NativeTerminalSurface(new TerminalEmulator(40, 24));
        var second = new NativeTerminalSurface(new TerminalEmulator(40, 24));
        var editor = new TextBox();
        var stack = new StackPanel();
        stack.Children.Add(editor);
        first.Height = second.Height = 180;
        stack.Children.Add(first);
        stack.Children.Add(second);
        var window = new Window { Width = 650, Height = 450, Content = stack, ShowInTaskbar = false };
        var firstInputs = new List<string>();
        var secondInputs = new List<string>();
        first.Input += (text, _, _) => { firstInputs.Add(text); return null; };
        second.Input += (text, _, _) => { secondInputs.Add(text); return null; };
        var keyboard = new TestKeyboardDevice();
        KeyEventArgs KeyInput(NativeTerminalSurface surface, Key key)
        {
            var args = new KeyEventArgs(keyboard, PresentationSource.FromVisual(surface)!, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            surface.RaiseEvent(args);
            return args;
        }
        TextCompositionEventArgs TextInput(NativeTerminalSurface surface, TestComposition composition, RoutedEvent? routedEvent = null)
        {
            var args = new TextCompositionEventArgs(keyboard, composition)
            { RoutedEvent = routedEvent ?? TextCompositionManager.TextInputEvent };
            surface.RaiseEvent(args);
            return args;
        }
        try
        {
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            foreach (var target in new[] { first, second, first })
            {
                editor.Focus();
                target.FocusSurface();
                Require(target.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox, "Focus did not return to the terminal composition TextBox.");
                Require(!KeyInput(target, Key.A).Handled && !KeyInput(target, Key.D1).Handled,
                    "A printable key was consumed before WPF could compose its text.");
                TextInput(target, new TestComposition(target, "a1!"));
                int before = firstInputs.Count + secondInputs.Count;
                KeyInput(target, Key.ImeProcessed);
                TextInput(target, new TestComposition(target, preedit: "ㅎ"), TextCompositionManager.TextInputStartEvent);
                TextInput(target, new TestComposition(target, preedit: "한"), TextCompositionManager.TextInputUpdateEvent);
                Require(firstInputs.Count + secondInputs.Count == before, "Uncommitted IME preedit reached the PTY.");
                TextInput(target, new TestComposition(target, "한글"));
            }
            Require(firstInputs.SequenceEqual(new[] { "a1!", "한글", "a1!", "한글" })
                && secondInputs.SequenceEqual(new[] { "a1!", "한글" }), "Focus handoff lost, duplicated, or crossed pane text input.");
            firstInputs.Clear();
            var systemText = TextInput(first, new TestComposition(first, systemText: "a1한"));
            Require(systemText.Handled && firstInputs.SequenceEqual(new[] { "\u001ba1한" }),
                "WPF SystemText was dropped or did not receive exactly one Alt prefix.");

            firstInputs.Clear();
            keyboard.PressedModifiers = ModifierKeys.Control | ModifierKeys.Alt;
            keyboard.RightAlt = true;
            Require(!KeyInput(first, Key.Q).Handled && !KeyInput(first, Key.V).Handled,
                "AltGr layout keys were consumed as a Ctrl sequence or paste shortcut.");
            TextInput(first, new TestComposition(first, "@€한"));
            Require(firstInputs.SequenceEqual(new[] { "@€한" }), "AltGr committed text was lost, prefixed, or duplicated.");

            firstInputs.Clear();
            keyboard.RightAlt = false;
            keyboard.PressedModifiers = ModifierKeys.Control;
            Require(KeyInput(first, Key.C).Handled, "Ctrl+C was not consumed by terminal control encoding.");
            TextInput(first, new TestComposition(first, controlText: "\x03"));
            Require(firstInputs.SequenceEqual(new[] { "\x03" }), "Ctrl+C control input was lost or sent twice.");
            int pastes = 0, copies = 0;
            first.PasteRequested += () => pastes++;
            first.CopyRequested += () => copies++;
            KeyInput(first, Key.V);
            keyboard.PressedModifiers |= ModifierKeys.Shift;
            KeyInput(first, Key.C);
            Require(pastes == 1 && copies == 1 && firstInputs.Count == 1, "Paste/copy shortcuts also wrote terminal input.");
            keyboard.PressedModifiers = ModifierKeys.None;
            KeyInput(first, Key.Up);
            Require(firstInputs.Last() == "\x1b[A", "Arrow-key ANSI encoding changed.");
            Console.WriteLine("PASS: routed letters/digits/Korean commits survive editor and pane focus handoff; SystemText/AltGr, IME preedit, Ctrl+C, paste/copy and ANSI are delivered once.");
        }
        finally
        {
            first.DisposeSurface();
            second.DisposeSurface();
            window.Close();
        }
    }

    private static async Task VerifyCompositionHostAsync()
    {
        var surface = new NativeTerminalSurface(new TerminalEmulator(40, 24)) { Height = 180 };
        var editor = new TextBox();
        var stack = new StackPanel();
        stack.Children.Add(editor);
        stack.Children.Add(surface);
        var window = new Window { Width = 650, Height = 300, Content = stack, ShowInTaskbar = false };
        var inputs = new List<string>();
        surface.Input += (text, _, _) => { inputs.Add(text); return null; };
        var keyboard = new TestKeyboardDevice();
        try
        {
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            surface.FocusSurface();
            var host = VisualTreeHelper.GetChild(surface, 0) as TextBox;
            Require(host is not null && host.IsKeyboardFocused && surface.IsKeyboardFocusWithin,
                "Terminal input must use a real editable TextBox text store.");
            editor.Focus();
            Keyboard.Focus(surface);
            Require(host!.IsKeyboardFocused, "Direct surface focus did not redirect to its composition host.");

            int timestamp = Environment.TickCount;
            // storeHeld: the IME's text store already holds this commit, so the TextBox must not insert it again.
            void Composition(string text, string preedit, RoutedEvent routedEvent, bool storeHeld = false)
            {
                var args = new TextCompositionEventArgs(keyboard, new TestComposition(host, text, preedit))
                { RoutedEvent = routedEvent, Handled = storeHeld };
                host.RaiseEvent(args);
                timestamp = args.Timestamp;
            }
            KeyEventArgs KeyInput(Key key, int time)
            {
                var args = new KeyEventArgs(keyboard, PresentationSource.FromVisual(host)!, time, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                host.RaiseEvent(args);
                return args;
            }
            Composition("", "\uD55C", TextCompositionManager.TextInputStartEvent);
            Composition("", "\uD55C", TextCompositionManager.TextInputUpdateEvent);
            Require(inputs.Count == 0 && !KeyInput(Key.Back, timestamp).Handled,
                "Uncommitted preedit or its editing key reached terminal input.");
            Composition("\uD55C", "", TextCompositionManager.TextInputEvent);
            Require(inputs.SequenceEqual(new[] { "\uD55C" }), "A host commit was lost or duplicated.");
            // The Korean IME commits the last syllable and passes Enter on: one Enter submits (verified with the real IME).
            Require(KeyInput(Key.Enter, timestamp).Handled && inputs.SequenceEqual(new[] { "\uD55C", "\r" }),
                "The Enter that committed a syllable did not submit terminal input exactly once.");
            Require(KeyInput(Key.Enter, unchecked(timestamp + 100)).Handled && inputs.Count == 3 && inputs.Last() == "\r",
                "A subsequent Enter did not submit terminal input.");
            // Some IMEs route Enter before they commit the line's last syllable: Enter is held, then sent after it.
            inputs.Clear();
            int enterTime = unchecked(timestamp + 150);
            Composition("", "한", TextCompositionManager.TextInputStartEvent);
            Require(KeyInput(Key.Enter, enterTime).Handled && inputs.Count == 0, "An Enter during composition was sent before its syllable.");
            Composition("한", "", TextCompositionManager.TextInputEvent);
            Require(inputs.SequenceEqual(new[] { "한", "\r" }), "The held Enter did not follow the committed syllable exactly once.");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Require(KeyInput(Key.Enter, enterTime).Handled && inputs.Count == 2, "The same key press submitted twice.");
            // The IME consumed Enter itself (ImeProcessed) to commit: the line is still submitted exactly once,
            // whether or not the IME passes that Enter on afterwards.
            void ImeEnter(int time, string committed)
            {
                Composition("", "한", TextCompositionManager.TextInputStartEvent);
                var imeEnter = new KeyEventArgs(keyboard, PresentationSource.FromVisual(host)!, time, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                typeof(KeyEventArgs).GetMethod("MarkImeProcessed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(imeEnter, null);
                host.RaiseEvent(imeEnter);
                Require(imeEnter.Key == Key.ImeProcessed && inputs.Count == 0, "A consumed Enter was sent before its syllable.");
                Composition(committed, "", TextCompositionManager.TextInputEvent);
            }
            async Task Settle()
            {
                await Task.Delay(450);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            }
            inputs.Clear();
            int imeTime = unchecked(timestamp + 170);
            ImeEnter(imeTime, "한");
            await Settle();
            Require(inputs.SequenceEqual(new[] { "한", "\r" }) && KeyInput(Key.Enter, imeTime).Handled && inputs.Count == 2,
                "An Enter consumed by the IME did not submit the committed line exactly once.");
            inputs.Clear();
            int passTime = unchecked(timestamp + 175);
            ImeEnter(passTime, "한");
            Require(KeyInput(Key.Enter, unchecked(passTime + 1)).Handled && inputs.SequenceEqual(new[] { "한", "\r" }),
                "The Enter the IME passed on did not submit the committed line.");
            await Settle();
            Require(inputs.Count == 2, "A consumed Enter that the IME passed on was submitted twice.");
            // Enter that picks a Hanja candidate commits the ideograph only; the line stays unsent.
            inputs.Clear();
            ImeEnter(unchecked(timestamp + 180), "韓");
            await Settle();
            Require(inputs.SequenceEqual(new[] { "韓" }), "Choosing a Hanja candidate with Enter submitted the line.");
            // No commit ever arrives: after a forced commit attempt Enter is sent anyway, so the line never sticks.
            inputs.Clear();
            Composition("", "한", TextCompositionManager.TextInputStartEvent);
            Require(KeyInput(Key.Enter, unchecked(timestamp + 190)).Handled && inputs.Count == 0, "A stuck composition sent Enter before its syllable.");
            var enterDeadline = DateTime.UtcNow.AddSeconds(3);
            while (inputs.Count == 0 && DateTime.UtcNow < enterDeadline) await Task.Delay(50);
            Require(inputs.LastOrDefault() == "\r", "Enter stayed held although the IME never committed the syllable.");
            KeyInput(Key.Space, unchecked(timestamp + 200));
            Require(inputs.Last() == " ", "TextBox's editing command swallowed terminal Space.");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Require(host.Text.Length == 0, "Committed host text was not cleared after composition finished.");
            // Some IME setups commit syllables into the TextBox without any TextInput: they still reach the
            // terminal once, in order, and Enter submits the line with its last syllable ("텍스트").
            inputs.Clear();
            async Task Typed(string document, string preedit, RoutedEvent routedEvent)
            {
                host.Text = document;
                host.CaretIndex = document.Length;
                Composition("", preedit, routedEvent);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            await Typed("텍", "텍", TextCompositionManager.TextInputStartEvent);
            Require(inputs.Count == 0, "A composing syllable reached terminal input.");
            await Typed("텍ㅅ", "ㅅ", TextCompositionManager.TextInputStartEvent);
            await Typed("텍스", "스", TextCompositionManager.TextInputUpdateEvent);
            await Typed("텍슽", "슽", TextCompositionManager.TextInputUpdateEvent);
            await Typed("텍스트", "트", TextCompositionManager.TextInputStartEvent);
            Require(string.Concat(inputs) == "텍스", "Syllables committed without TextInput were lost or repeated: " + string.Concat(inputs));
            Require(KeyInput(Key.Enter, unchecked(timestamp + 210)).Handled, "Enter during the last syllable was not held.");
            var silentDeadline = DateTime.UtcNow.AddSeconds(3);
            while (!inputs.Contains("\r") && DateTime.UtcNow < silentDeadline) await Task.Delay(50);
            Require(string.Concat(inputs) == "텍스트\r", "The silently committed line was not submitted as typed: " + string.Concat(inputs));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Require(host.Text.Length == 0, "The silently committed line stayed in the TextBox.");
            // A TextInput that arrives after its fallback delivery is not sent again, and it leaves the next syllable composing.
            inputs.Clear();
            await Typed("한", "한", TextCompositionManager.TextInputStartEvent);
            await Typed("한ㄱ", "ㄱ", TextCompositionManager.TextInputStartEvent);
            Composition("한", "", TextCompositionManager.TextInputEvent, storeHeld: true);
            await Typed("한글", "글", TextCompositionManager.TextInputUpdateEvent);
            Require(string.Concat(inputs) == "한", "A late TextInput repeated its syllable or sent the composing one: " + string.Concat(inputs));
            // The usual order: the commit's TextInput while the TextBox already holds the next syllable sends just the commit.
            Composition("글", "", TextCompositionManager.TextInputEvent, storeHeld: true);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Require(string.Concat(inputs) == "한글" && host.Text.Length == 0, "An announced commit was repeated or left behind: " + string.Concat(inputs));
            int pastes = 0, copies = 0, shortcuts = 0;
            surface.PasteRequested += () => pastes++;
            surface.CopyRequested += () => copies++;
            surface.Shortcut += (_, _) => shortcuts++;
            inputs.Clear();
            Composition("", "\uD55C", TextCompositionManager.TextInputStartEvent);
            keyboard.PressedModifiers = ModifierKeys.Control;
            Require(KeyInput(Key.V, unchecked(timestamp + 300)).Handled && pastes == 1,
                "Active composition bypassed terminal paste handling.");
            Composition("", "\uD55C", TextCompositionManager.TextInputStartEvent);
            Require(KeyInput(Key.C, unchecked(timestamp + 400)).Handled && inputs.SequenceEqual(new[] { "\x03" }),
                "Active composition swallowed terminal Ctrl+C.");
            keyboard.PressedModifiers |= ModifierKeys.Shift;
            Composition("", "\uD55C", TextCompositionManager.TextInputStartEvent);
            Require(KeyInput(Key.C, unchecked(timestamp + 500)).Handled && copies == 1,
                "Active composition bypassed terminal copy handling.");
            Composition("", "\uD55C", TextCompositionManager.TextInputStartEvent);
            Require(KeyInput(Key.T, unchecked(timestamp + 600)).Handled && shortcuts == 1 && host.IsKeyboardFocused,
                "Active composition bypassed terminal shortcuts or lost pane focus.");
            keyboard.PressedModifiers = ModifierKeys.None;
            Composition("", "", TextCompositionManager.TextInputEvent);
            Console.WriteLine("PASS: real composition TextBox focus, preedit isolation, single commits, composition Enter (before, consumed by, or never committed by the IME), Space and composing terminal shortcuts.");
        }
        finally
        {
            surface.DisposeSurface();
            window.Close();
        }
    }

    private sealed class TestKeyboardDevice() : KeyboardDevice(InputManager.Current)
    {
        public ModifierKeys PressedModifiers { get; set; }
        public bool RightAlt { get; set; }
        protected override KeyStates GetKeyStatesFromSystem(Key key) => key switch
        {
            Key.LeftCtrl when PressedModifiers.HasFlag(ModifierKeys.Control) => KeyStates.Down,
            Key.LeftShift when PressedModifiers.HasFlag(ModifierKeys.Shift) => KeyStates.Down,
            Key.LeftAlt when PressedModifiers.HasFlag(ModifierKeys.Alt) && !RightAlt => KeyStates.Down,
            Key.RightAlt when PressedModifiers.HasFlag(ModifierKeys.Alt) && RightAlt => KeyStates.Down,
            _ => KeyStates.None
        };
    }

    private sealed class TestComposition : TextComposition
    {
        public TestComposition(IInputElement source, string text = "", string preedit = "", string systemText = "", string controlText = "")
            : base(InputManager.Current, source, text)
        {
            CompositionText = preedit;
            SystemText = systemText;
            ControlText = controlText;
        }
    }

    private static async Task VerifyOutputDispatcherFairnessAsync()
    {
        const int batches = 120;
        var emulator = new TerminalEmulator(40, 24, 5000);
        var expected = new TerminalEmulator(40, 24, 5000);
        var surface = new NativeTerminalSurface(emulator);
        int parsed = 0, inputAt = -1, backgroundAt = -1;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        double inputMilliseconds = -1, backgroundMilliseconds = -1;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int backgroundHeartbeats = 0, inputHeartbeats = 0;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Background, surface.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        heartbeat.Tick += (_, _) =>
        {
            if (parsed < batches)
            {
                backgroundHeartbeats++;
                surface.Dispatcher.BeginInvoke(() => { if (parsed < batches) inputHeartbeats++; }, DispatcherPriority.Input);
            }
        };
        string Chunk(int index) => $"\x1b[32mBATCH_{index:D3}\x1b[0m 한🙂" + new string('x', 4096) + "\r\n";
        var directFeed = System.Diagnostics.Stopwatch.StartNew();
        for (int index = 0; index < batches; index++) expected.Feed(Chunk(index));
        directFeed.Stop();
        void SupplyOutput()
        {
            if (++parsed < batches) surface.Feed(Chunk(parsed));
            else finished.TrySetResult();
        }
        emulator.Changed += SupplyOutput;
        try
        {
            elapsed.Restart();
            heartbeat.Start();
            surface.Feed(Chunk(0));
            var input = surface.Dispatcher.InvokeAsync(() => { inputAt = parsed; inputMilliseconds = elapsed.Elapsed.TotalMilliseconds; }, DispatcherPriority.Input);
            var background = surface.Dispatcher.InvokeAsync(() => { backgroundAt = parsed; backgroundMilliseconds = elapsed.Elapsed.TotalMilliseconds; }, DispatcherPriority.Background);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(input.Task, background.Task);
            Require(inputAt < batches && backgroundAt < batches,
                $"Sustained Render output starved dispatcher probes: Input={inputAt} ({inputMilliseconds:N0} ms), Background={backgroundAt} ({backgroundMilliseconds:N0} ms), batches={batches}.");
            Require(emulator.GetAllText() == expected.GetAllText(), "Yielding output changed or lost the ordered terminal transcript.");
            Require(backgroundHeartbeats >= 3 && inputHeartbeats >= 3,
                $"Dispatcher heartbeats did not continue during output: Input={inputHeartbeats}, Background={backgroundHeartbeats}.");
            Console.WriteLine($"PASS: sustained output yields to Input at {inputAt} ({inputMilliseconds:N0} ms) and Background at {backgroundAt} ({backgroundMilliseconds:N0} ms), preserving {batches} ordered batches.");
            Console.WriteLine($"MEASURED: sustained output completed in {elapsed.Elapsed.TotalMilliseconds:N0} ms; direct engine parsing took {directFeed.Elapsed.TotalMilliseconds:N0} ms; Input/Background heartbeats={inputHeartbeats}/{backgroundHeartbeats}.");
        }
        finally
        {
            heartbeat.Stop();
            emulator.Changed -= SupplyOutput;
            surface.DisposeSurface();
        }
        await VerifyOversizedOutputAsync();
        await VerifyPendingOutputDisposalAsync();
    }

    private static async Task VerifyPendingOutputDisposalAsync()
    {
        var emulator = new TerminalEmulator(40, 24);
        var surface = new NativeTerminalSurface(emulator);
        try
        {
            surface.Feed(new string('x', 100000));
            await Until(() => emulator.GetSnapshot().Version > 0, "output starts before disposal");
            await surface.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            long version = emulator.GetSnapshot().Version;
            Require(version > 0, "Output did not start before the disposal check.");
            surface.DisposeSurface();
            surface.Feed("IGNORED_AFTER_DISPOSAL");
            await Task.Delay(80);
            Require(emulator.GetSnapshot().Version == version, "Pending output continued after surface disposal.");
            Console.WriteLine("PASS: disposing a surface cancels its pending output timer and rejects late arrivals.");
        }
        finally { surface.DisposeSurface(); }
    }

    private static async Task VerifyOversizedOutputAsync()
    {
        var emulator = new TerminalEmulator(40, 24, 5000);
        var expected = new TerminalEmulator(40, 24, 5000);
        var surface = new NativeTerminalSurface(emulator);
        // A surrogate pair crosses the first batch boundary; ANSI and Unicode
        // state must survive slicing a single large transport chunk.
        const string following = "FOLLOWING_CHUNK\r\n";
        string output = new string('x', 65535) + "🙂\x1b[32m한글\x1b[0m\r\n" + new string('z', 70000) + "\r\nOVERSIZED_DONE\r\n";
        expected.Feed(output + following);
        int changes = 0;
        long previousQueued = output.Length + following.Length, largestBatch = 0;
        emulator.Changed += () =>
        {
            changes++;
            long queued = Field<long>(surface, "_queuedOutputCharacters");
            largestBatch = Math.Max(largestBatch, previousQueued - queued);
            previousQueued = queued;
        };
        try
        {
            surface.Feed(output);
            surface.Feed(following);
            await Until(() => emulator.GetAllText().Contains("FOLLOWING_CHUNK", StringComparison.Ordinal), "oversized output drain");
            Require(changes > 1, "A single oversized chunk was parsed without yielding.");
            Require(largestBatch <= 65536, $"The output drain exceeded its character cap: {largestBatch}.");
            Require(emulator.GetAllText() == expected.GetAllText(), "Splitting oversized output changed Unicode, ANSI state, or FIFO order.");
            Console.WriteLine("PASS: oversized output yields without splitting surrogate pairs or reordering the next chunk.");
        }
        finally { surface.DisposeSurface(); }
    }

    private static async Task VerifyAsync()
    {
        VerifyStateBackgroundRendering();
        await VerifyAdaptiveViewportAsync();
        string? oldRenderer = Environment.GetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER");
        Environment.SetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER", null);
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes native surface " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string onceFile = Path.Combine(root, "send-once.txt");
        string escapedPath = onceFile.Replace("'", "''");
        var first = new TerminalPane(root, startupCommand: "Write-Output 'NATIVE_SURFACE_START'; [Console]::WriteLine('한글✓ 日本語😀');");
        var second = new TerminalPane(root, startupCommand: "Write-Output 'SECOND_SURFACE_START';");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(second, 1);
        grid.Children.Add(first);
        grid.Children.Add(second);
        var window = new Window { Width = 1050, Height = 820, Content = grid, ShowInTaskbar = false };
        try
        {
            window.Show();
            await WaitReady(first);
            await WaitReady(second);
            var firstSession = Field<ConPtySession>(first, "_session");
            var secondSession = Field<ConPtySession>(second, "_session");
            var firstSurface = Field<NativeTerminalSurface>(first, "_nativeTerminalSurface");
            var firstEmulator = Field<TerminalEmulator>(first, "_nativeTerminalEmulator");
            var capturedOutput = new System.Text.StringBuilder();
            firstSession.OutputReceived += text => { lock (capturedOutput) capturedOutput.Append(text); };
            Require(Field<bool>(first, "_useNativeTerminalSurface"), "The generic shell did not select the native WPF surface by default.");
            Require(!firstSession.ProcessId.Equals(secondSession.ProcessId), "Two panes share a ConPTY process.");
            try { await Until(() => Buffer(firstSurface).Contains("NATIVE_SURFACE_START", StringComparison.Ordinal) && Buffer(firstSurface).Contains("한글✓ 日本語😀", StringComparison.Ordinal), "Unicode startup output"); }
            catch
            {
                lock (capturedOutput) Console.WriteLine("Captured PTY output: " + capturedOutput.ToString());
                Console.WriteLine("Rendered buffer: " + Buffer(firstSurface));
                Console.WriteLine($"State: ready={first.IsReady}, running={first.IsRunning}, process={firstSession.ProcessId}");
                throw;
            }
            Console.WriteLine("PASS: generic shell output is parsed and rendered by the native terminal engine.");

            var previous = firstEmulator.GetSnapshot();
            foreach (var size in new[] { (760d, 620d), (1280d, 900d), (890d, 720d), (1120d, 860d) })
            {
                window.Width = size.Item1;
                window.Height = size.Item2;
                await Task.Delay(180);
            }
            await Until(() => firstEmulator.GetSnapshot().Columns != previous.Columns || firstEmulator.GetSnapshot().Rows != previous.Rows, "native resize");
            Require(first.IsReady && first.IsRunning && second.IsReady && second.IsRunning, "A pane stopped during resize.");
            Console.WriteLine("PASS: WPF layout changes update the native viewport while both shell sessions remain live.");

            first.SelectAll();
            first.CopySelection();
            Require(Clipboard.ContainsText() && Clipboard.GetText().Contains("NATIVE_SURFACE_START", StringComparison.Ordinal), "Select All/Copy did not preserve the rendered transcript.");
            first.ClearSelection();

            firstSurface.FocusSurface();
            firstSurface.PasteText($"Add-Content -LiteralPath '{escapedPath}' -Value 'sent-once'");
            var source = PresentationSource.FromVisual(firstSurface) ?? throw new InvalidOperationException("The native surface has no presentation source.");
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            firstSurface.RaiseEvent(enter);
            await Until(() => File.Exists(onceFile) && File.ReadAllLines(onceFile).Length == 1, "exactly-once native terminal send");
            await Task.Delay(500);
            Require(File.ReadAllLines(onceFile).Length == 1, "A single user submission executed more than once.");
            await Until(() => Buffer(firstSurface).Contains("sent-once", StringComparison.Ordinal), "native input echo");
            Console.WriteLine("PASS: clipboard commands and acknowledged shell submission work through the native surface.");

            uint? secondProcess = secondSession.ProcessId;
            first.Dispose();
            await Until(() => !ProcessExists(firstSession.ProcessId), "first pane process close");
            Require(second.IsRunning && secondSession.ProcessId == secondProcess, "Closing one pane affected the other pane.");
            second.Dispose();
            await Until(() => !ProcessExists(secondProcess), "second pane process close");
            Console.WriteLine("PASS: pane close reaps its own ConPTY process and keeps the other pane isolated.");
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            window.Content = null;
            window.Close();
            try { Directory.Delete(root, recursive: true); } catch { }
            Environment.SetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER", oldRenderer);
        }
    }

    private static void VerifyStateBackgroundRendering()
    {
        var emulator = new TerminalEmulator(20, 6);
        emulator.Feed("\x1b[48;5;196;97mX \x1b[0m\r\n");
        emulator.Feed("\x1b[48;2;20;40;180;97mY \x1b[0m\r\n");
        emulator.Feed("\x1b[44;7mZ \x1b[0m\r\n");
        var surface = new NativeTerminalSurface(emulator);
        var window = new Window { Width = 480, Height = 240, Content = surface, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            surface.SetTheme("#FF8A00", "#211000", "#FFC47A", forceStateBackground: true);
            var forced = CaptureSurface(surface);
            Color orange = ParseTestColor("#FF8A00");
            RequireNear(orange, CellPixel(forced, surface, row: 0, column: 1), 3,
                "Indexed ANSI background escaped the forced orange state fill.");
            RequireNear(orange, CellPixel(forced, surface, row: 1, column: 1), 3,
                "Truecolor ANSI background escaped the forced orange state fill.");
            RequireNear(orange, CellPixel(forced, surface, row: 2, column: 1), 3,
                "Inverse ANSI background escaped the forced orange state fill.");
            Require(CellHasDarkGlyph(forced, surface, row: 0),
                "Bright ANSI white text was not corrected for contrast against the orange state fill.");
            Require(CellHasDarkGlyphNearBottom(forced, surface, row: 2, column: 1),
                "Forced inverse video lost its visible underline/menu cue.");

            surface.SelectAllText();
            window.UpdateLayout();
            var selected = CaptureSurface(surface);
            Color selectedPixel = CellPixel(selected, surface, row: 4, column: 4);
            Require(selectedPixel != orange && selectedPixel.R >= 245 && selectedPixel.G > orange.G && selectedPixel.B > orange.B,
                "Selection did not remain visible as a translucent tint of the orange state color.");
            surface.ClearSelectionText();

            surface.SetTheme("#00853F", "#FFFFFF", "#00572D", forceStateBackground: true);
            RequireNear(ParseTestColor("#00853F"), CellPixel(CaptureSurface(surface), surface, row: 1, column: 1), 3,
                "The forced state background did not update to active green.");
            surface.SetTheme("#252D3A", "#FFFFFF", "#43536B", forceStateBackground: true);
            RequireNear(ParseTestColor("#252D3A"), CellPixel(CaptureSurface(surface), surface, row: 1, column: 1), 3,
                "The forced state background did not update to inactive gray.");

            surface.SetTheme("#FF8A00", "#211000", "#FFC47A", forceStateBackground: false);
            var ordinaryShell = CaptureSurface(surface);
            RequireNear(ParseTestColor("#FF0000"), CellPixel(ordinaryShell, surface, row: 0, column: 1), 3,
                "Opt-out shell mode changed an indexed ANSI background.");
            RequireNear(ParseTestColor("#1428B4"), CellPixel(ordinaryShell, surface, row: 1, column: 1), 3,
                "Opt-out shell mode changed an ANSI truecolor background.");
            RequireNear(ParseTestColor("#211000"), CellPixel(ordinaryShell, surface, row: 2, column: 1), 3,
                "Opt-out shell mode changed the background of an inverse ANSI run.");
            VerifyViewportThemeFill();
            Console.WriteLine("PASS: native ANSI backgrounds follow status color only when forced, with contrast, inverse, and selection cues preserved.");
        }
        finally
        {
            window.Content = null;
            window.Close();
            surface.DisposeSurface();
        }
    }

    private static void VerifyViewportThemeFill()
    {
        var surface = new NativeTerminalSurface(new TerminalEmulator(40, 24));
        surface.SetTheme("#00853F", "#FFFFFF", "#00572D", forceStateBackground: true);
        var viewport = NativeTerminalSurface.CreateScrollableViewport(surface);
        viewport.Padding = new Thickness(14);
        var window = new Window { Width = 480, Height = 520, Content = viewport, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var active = CaptureViewport(viewport);
            RequireNear(ParseTestColor("#00853F"), Pixel(active, 3, 3), 3,
                "Uncovered active-state viewport space did not use the terminal theme background.");

            surface.SetTheme("#252D3A", "#FFFFFF", "#43536B", forceStateBackground: true);
            window.UpdateLayout();
            var idle = CaptureViewport(viewport);
            RequireNear(ParseTestColor("#252D3A"), Pixel(idle, 3, 3), 3,
                "Uncovered viewport space did not update with the terminal state background.");

            surface.SetTheme("#FF8A00", "#211000", "#FFC47A", forceStateBackground: false);
            window.UpdateLayout();
            var shell = CaptureViewport(viewport);
            RequireNear(ParseTestColor("#FF8A00"), Pixel(shell, 3, 3), 3,
                "Ordinary shell viewport space did not follow the current theme background.");
        }
        finally
        {
            window.Content = null;
            window.Close();
            surface.DisposeSurface();
        }
    }

    private static async Task VerifyAdaptiveViewportAsync()
    {
        var emulator = new TerminalEmulator(40, 24);
        var surface = new NativeTerminalSurface(emulator);
        surface.DimensionsChanged += (columns, rows) => emulator.Resize(columns, rows);
        surface.SetTheme("#101010", "#FFFFFF", "#43536B");
        var viewport = NativeTerminalSurface.CreateScrollableViewport(surface);
        var host = new Grid();
        host.Children.Add(viewport);
        viewport.HorizontalAlignment = HorizontalAlignment.Left;
        viewport.VerticalAlignment = VerticalAlignment.Top;
        var window = new Window { Width = 1060, Height = 760, Content = host, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            foreach (var size in new[] { (240d, 160d), (120d, 500d), (640d, 200d), (960d, 600d), (240d, 160d), (960d, 600d) })
            {
                viewport.Width = size.Item1;
                viewport.Height = size.Item2;
                window.UpdateLayout();
                await Task.Delay(100);
                window.UpdateLayout();
                Rect bounds = surface.TransformToAncestor(viewport).TransformBounds(new Rect(surface.RenderSize));
                Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1,
                    $"The complete native TUI surface is clipped at {size}: surface={bounds}, viewport={viewport.RenderSize}.");
                Require(viewport.ScrollableWidth < 1 && viewport.ScrollableHeight < 1, "Fitted terminal requires outer scrolling.");
                var snapshot = emulator.GetSnapshot();
                Require(snapshot.Columns >= 40 && snapshot.Rows >= 24, "Fitting reduced the safe logical VT grid.");
                double expectedScale = Math.Min(1, Math.Min(viewport.ViewportWidth / surface.MinimumViewportWidth, viewport.ViewportHeight / surface.MinimumViewportHeight));
                Require(Math.Abs(bounds.Width / surface.ActualWidth - expectedScale) < 0.001,
                    "Terminal scale did not follow the viewport or return to normal at a larger size.");

                // Render colored cells at both ends of the terminal, including the bottom prompt row.
                surface.Feed($"\x1b[2J\x1b[1;1H\x1b[48;2;255;0;0m \x1b[{snapshot.Rows};{snapshot.Columns}H\x1b[48;2;0;255;0m \x1b[0m");
                await Task.Delay(60);
                var bitmap = CaptureViewport(viewport);
                double cellWidth = Field<double>(surface, "_cellWidth");
                double cellHeight = Field<double>(surface, "_cellHeight");
                var dpi = VisualTreeHelper.GetDpi(viewport);
                foreach (var marker in new[] { (0, 0, Colors.Red), (snapshot.Columns - 1, snapshot.Rows - 1, Colors.Lime) })
                {
                    var local = new Point((marker.Item1 + 0.5) * cellWidth, (marker.Item2 + 0.5) * cellHeight);
                    Point visible = surface.TranslatePoint(local, viewport);
                    Require(visible.X >= 0 && visible.Y >= 0 && visible.X < viewport.ActualWidth && visible.Y < viewport.ActualHeight,
                        "A terminal corner is outside the visible pane.");
                    RequireNear(marker.Item3, Pixel(bitmap, (int)(visible.X * dpi.DpiScaleX), (int)(visible.Y * dpi.DpiScaleY)), 8,
                        "A terminal corner was not rendered inside the small pane.");
                    Point input = viewport.TranslatePoint(visible, surface);
                    Require((input - local).Length < 0.01, "Scaled terminal pointer coordinates no longer map to the original cell.");
                }
            }
            Console.WriteLine("PASS: small native TUI panes fit both terminal corners, retain the minimum grid, and restore normal scale.");
        }
        finally
        {
            window.Content = null;
            window.Close();
            surface.DisposeSurface();
        }
    }

    private static BitmapSource CaptureViewport(FrameworkElement viewport)
    {
        viewport.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(viewport);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(viewport.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(viewport.ActualHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(viewport);
        return bitmap;
    }

    private static BitmapSource CaptureSurface(NativeTerminalSurface surface)
    {
        surface.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(surface);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(surface);
        return bitmap;
    }

    private static Color CellPixel(BitmapSource bitmap, NativeTerminalSurface surface, int row, int column)
    {
        double cellWidth = Field<double>(surface, "_cellWidth");
        double cellHeight = Field<double>(surface, "_cellHeight");
        var dpi = VisualTreeHelper.GetDpi(surface);
        int x = Math.Clamp((int)Math.Round((column * cellWidth + cellWidth * 0.65) * dpi.DpiScaleX), 0, bitmap.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round((row * cellHeight + cellHeight * 0.55) * dpi.DpiScaleY), 0, bitmap.PixelHeight - 1);
        return Pixel(bitmap, x, y);
    }

    private static bool CellHasDarkGlyph(BitmapSource bitmap, NativeTerminalSurface surface, int row)
    {
        double cellWidth = Field<double>(surface, "_cellWidth");
        double cellHeight = Field<double>(surface, "_cellHeight");
        var dpi = VisualTreeHelper.GetDpi(surface);
        int left = 0;
        int top = Math.Clamp((int)Math.Floor(row * cellHeight * dpi.DpiScaleY), 0, bitmap.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(cellWidth * dpi.DpiScaleX), left + 1, bitmap.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling((row + 1) * cellHeight * dpi.DpiScaleY), top + 1, bitmap.PixelHeight);
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
            if (RelativeLuminance(Pixel(bitmap, x, y)) < 0.10) return true;
        return false;
    }

    private static bool CellHasDarkGlyphNearBottom(BitmapSource bitmap, NativeTerminalSurface surface, int row, int column)
    {
        double cellWidth = Field<double>(surface, "_cellWidth");
        double cellHeight = Field<double>(surface, "_cellHeight");
        var dpi = VisualTreeHelper.GetDpi(surface);
        int left = Math.Clamp((int)Math.Floor(column * cellWidth * dpi.DpiScaleX), 0, bitmap.PixelWidth - 1);
        int right = Math.Clamp((int)Math.Ceiling((column + 1) * cellWidth * dpi.DpiScaleX), left + 1, bitmap.PixelWidth);
        int top = Math.Clamp((int)Math.Floor((row + 1) * cellHeight * dpi.DpiScaleY) - 3, 0, bitmap.PixelHeight - 1);
        int bottom = Math.Clamp(top + 4, top + 1, bitmap.PixelHeight);
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
            if (RelativeLuminance(Pixel(bitmap, x, y)) < 0.10) return true;
        return false;
    }

    private static Color Pixel(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        byte alpha = pixel[3];
        if (alpha == 0) return Colors.Transparent;
        byte Unpremultiply(byte value) => alpha == 255 ? value : (byte)Math.Clamp((int)Math.Round(value * 255.0 / alpha), 0, 255);
        return Color.FromArgb(alpha, Unpremultiply(pixel[2]), Unpremultiply(pixel[1]), Unpremultiply(pixel[0]));
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte component)
        {
            double value = component / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Color ParseTestColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value)!;

    private static void RequireNear(Color expected, Color actual, int tolerance, string message)
    {
        if (Math.Abs(expected.R - actual.R) > tolerance || Math.Abs(expected.G - actual.G) > tolerance || Math.Abs(expected.B - actual.B) > tolerance)
            throw new InvalidOperationException($"{message} Expected #{expected.R:X2}{expected.G:X2}{expected.B:X2}; got #{actual.R:X2}{actual.G:X2}{actual.B:X2}.");
    }

    private static string Buffer(NativeTerminalSurface surface) => (string)Call(surface, "GetEntireBufferText")!;

    private static async Task WaitReady(TerminalPane pane)
    {
        var method = typeof(TerminalPane).GetMethod("EnsureReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("EnsureReadyAsync");
        await (Task)method.Invoke(pane, new object[] { CancellationToken.None })!;
    }

    private static async Task Until(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token);
        }
        Console.WriteLine("PASS: " + label);
    }

    private static bool ProcessExists(uint? pid)
    {
        if (pid is null) return false;
        try { _ = System.Diagnostics.Process.GetProcessById((int)pid.Value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static T Field<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().Name, name));

    private static object? Call(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, null);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
