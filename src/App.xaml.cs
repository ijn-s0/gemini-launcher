using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace GeminiLauncher
{
    public partial class App : Application
    {
        public Settings Settings { get; private set; }
        public static bool IsExiting;
        public MainWindow Launcher { get; private set; }
        InputHook _hook;
        WinForms.NotifyIcon _tray;
        WinForms.ToolStripMenuItem _miWin, _miStart;
        Mutex _mutex;
        EventWaitHandle _showSignal;
        DispatcherTimer _trimTimer;
        SettingsWindow _settingsWin;
        long _lastToggle;

        // Before anything can touch a WebView2 type: lets the packed WebView2 DLLs load from inside the exe.
        static App() { WebView2Deps.Init(); }

        const string MutexName = "GeminiLauncher.SingleInstance";
        const string SignalName = "GeminiLauncher.Show";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            bool background = e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
            bool portableArg = e.Args.Any(a => a.Equals("--portable", StringComparison.OrdinalIgnoreCase));

            if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Theme.Init();
                Installer.Uninstall(e.Args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)));
                Shutdown();
                return;
            }
            if (!portableArg && !background && !Installer.RunningInstalled)
            {
                Theme.Init();
                if (!Installer.MaybeInstall()) { Shutdown(); return; }
            }

            // Single instance: a second launch just opens the existing launcher.
            _mutex = new Mutex(true, MutexName, out bool first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(SignalName).Set(); } catch { }
                Shutdown();
                return;
            }
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            var waiter = new Thread(() => { while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(new Action(() => ShowLauncher(false))); })
            { IsBackground = true };
            waiter.Start();

            DispatcherUnhandledException += (s, ex) => { ex.Handled = true; LogError(ex.Exception); };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => LogError(ex.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) => { ex.SetObserved(); };

            Settings = Settings.Load();
            Theme.Mode = Settings.Theme;
            Theme.Init();
            AppIndex.LoadCache();
            AppIndex.RefreshAsync(force: true);
            Http.Warmup();
            Location.Enable(Settings.ToolMaps && Settings.UseLocation);

            Launcher = new MainWindow();
            new WindowInteropHelper(Launcher).EnsureHandle();   // create HWND now so first open is instant
            EnsureDefaultPins();
            Launcher.Prewarm();

            _hook = new InputHook { WinKeyEnabled = Settings.TakeOverWinKey, StartButtonEnabled = Settings.TakeOverStartButton };
            _hook.Triggered += () => Dispatcher.BeginInvoke(new Action(() => ShowLauncher(true)));
            _hook.ClickedOutside += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                var lw = Launcher;
                if (lw != null && lw.IsOpen && !lw.IsHiding && !lw.SuppressHide) HideLauncher();
            }));
            _hook.Start();

            SetupTray();

            _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _trimTimer.Tick += (s, a) => { _trimTimer.Stop(); if (Launcher == null || !Launcher.IsVisible) TrimMemory(); };
            _trimTimer.Start(); // also trim shortly after a background start

            // Command line: --show opens the launcher, --query "text" opens it with a search typed in.
            int qi = Array.FindIndex(e.Args, a => a.Equals("--query", StringComparison.OrdinalIgnoreCase));
            string startQuery = qi >= 0 && qi + 1 < e.Args.Length ? e.Args[qi + 1] : null;
            if (startQuery != null || e.Args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)))
            {
                ShowLauncher(false);
                if (startQuery != null) Launcher.SetQuery(startQuery);
                return;
            }

            if (!Settings.FirstRunDone || string.IsNullOrWhiteSpace(Settings.ApiKey))
            {
                if (!Settings.FirstRunDone)
                {
                    Settings.FirstRunDone = true;
                    Settings.Save();
                    Settings.StartWithWindowsDefault();
                    _tray.ShowBalloonTip(6000, "Gemini Launcher is running", "Press the Windows key to search. Add your Gemini API key in Settings for AI answers.", WinForms.ToolTipIcon.Info);
                }
                if (!background) OpenSettings();
            }
            else if (!background) ShowLauncher(false);
        }

        public void EnsureDefaultPins()
        {
            if (Settings.DefaultPinsDone || AppIndex.Apps.Count == 0) return;
            if (Settings.Pins.Count == 0)
                foreach (var a in AppIndex.DefaultPins().Take(10))
                    Settings.Pins.Add(new Pin { Kind = "app", Name = a.Name, Target = a.Id });
            Settings.DefaultPinsDone = true;
            Settings.Save();
        }

        void SetupTray()
        {
            System.Drawing.Icon icon;
            try
            {
                var sri = GetResourceStream(new Uri("pack://application:,,,/app.ico"));
                icon = new System.Drawing.Icon(sri.Stream);
            }
            catch { icon = System.Drawing.SystemIcons.Application; }

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open launcher", null, (s, e) => ShowLauncher(false));
            menu.Items.Add("Settings…", null, (s, e) => OpenSettings());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            _miWin = new WinForms.ToolStripMenuItem("Windows key opens launcher") { CheckOnClick = true, Checked = Settings.TakeOverWinKey };
            _miWin.CheckedChanged += (s, e) => { Settings.TakeOverWinKey = _miWin.Checked; Settings.Save(); ApplyHookSettings(); };
            _miStart = new WinForms.ToolStripMenuItem("Taskbar Start button opens launcher") { CheckOnClick = true, Checked = Settings.TakeOverStartButton };
            _miStart.CheckedChanged += (s, e) => { Settings.TakeOverStartButton = _miStart.Checked; Settings.Save(); ApplyHookSettings(); };
            menu.Items.Add(_miWin);
            menu.Items.Add(_miStart);
            menu.Items.Add("Open native Start menu", null, (s, e) => OpenNativeStart());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Rebuild app list", null, (s, e) => AppIndex.RefreshAsync(force: true));
            menu.Items.Add("Exit", null, (s, e) => ExitApp());

            _tray = new WinForms.NotifyIcon { Icon = icon, Text = "Gemini Launcher", Visible = true, ContextMenuStrip = menu };
            _tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowLauncher(false); };
            _tray.BalloonTipClicked += (s, e) => OpenSettings();
        }

        public void ApplyHookSettings()
        {
            if (_hook != null)
            {
                _hook.WinKeyEnabled = Settings.TakeOverWinKey;
                _hook.StartButtonEnabled = Settings.TakeOverStartButton;
            }
            if (_miWin != null && _miWin.Checked != Settings.TakeOverWinKey) _miWin.Checked = Settings.TakeOverWinKey;
            if (_miStart != null && _miStart.Checked != Settings.TakeOverStartButton) _miStart.Checked = Settings.TakeOverStartButton;
            Launcher?.UpdateModelBadge();
        }

        /// <summary>Opens the regular Windows Start menu (Ctrl+Esc is never intercepted).</summary>
        public static void OpenNativeStart()
        {
            var inputs = new Native.INPUT[4];
            ushort ctrl = 0x11, esc = 0x1B;
            inputs[0].type = inputs[1].type = inputs[2].type = inputs[3].type = Native.INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = ctrl;
            inputs[1].U.ki.wVk = esc;
            inputs[2].U.ki.wVk = esc; inputs[2].U.ki.dwFlags = Native.KEYEVENTF_KEYUP;
            inputs[3].U.ki.wVk = ctrl; inputs[3].U.ki.dwFlags = Native.KEYEVENTF_KEYUP;
            Native.SendInput(4, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.INPUT)));
        }

        public void ShowLauncher(bool fromHook)
        {
            var w = Launcher;
            if (w == null) return;
            long now = Environment.TickCount;
            if (now - _lastToggle < 150) return; // debounce double events
            _lastToggle = now;

            if (w.IsOpen && !w.IsHiding)
            {
                if (fromHook) { HideLauncher(); return; }
                ForceForeground(w);
                w.FocusSearch();
                return;
            }
            _trimTimer?.Stop();
            if (_hook != null) _hook.WatchClicks = true;
            if (w.IsOpen && w.IsHiding)
            {
                // re-opened while closing: reverse the slide
                ForceForeground(w);
                w.FocusSearch();
                w.SlideIn();
                return;
            }
            AppIndex.RefreshAsync();
            w.EndPrewarm();
            w.PrepareForShow();
            w.Show();
            ForceForeground(w);
            w.FocusSearch();
            w.SlideIn();
        }

        public void HideLauncher()
        {
            var w = Launcher;
            if (w == null || !w.IsOpen || w.IsHiding) return;
            if (_hook != null) _hook.WatchClicks = false;
            w.SlideOut(() => { _trimTimer?.Stop(); _trimTimer?.Start(); });
        }

        static void ForceForeground(Window w)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            w.Activate();
            if (Native.GetForegroundWindow() == hwnd) return;
            var fg = Native.GetForegroundWindow();
            uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
            uint me = Native.GetCurrentThreadId();
            if (fgThread != me && fgThread != 0)
            {
                Native.AttachThreadInput(me, fgThread, true);
                Native.SetForegroundWindow(hwnd);
                Native.BringWindowToTop(hwnd);
                Native.AttachThreadInput(me, fgThread, false);
            }
            if (Native.GetForegroundWindow() != hwnd)
            {
                // Last resort: a synthetic Alt tap lets us take the foreground.
                Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                Native.SetForegroundWindow(hwnd);
            }
            w.Activate();
        }

        public static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                System.IO.Directory.CreateDirectory(GeminiLauncher.Settings.Dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(GeminiLauncher.Settings.Dir, "error.log"),
                    DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Native.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch { }
        }

        public void OpenSettings()
        {
            HideLauncher();
            if (_settingsWin != null && _settingsWin.IsLoaded) { _settingsWin.Activate(); return; }
            _settingsWin = new SettingsWindow(this);
            _settingsWin.Closed += (s, e) => { _settingsWin = null; ApplyHookSettings(); };
            _settingsWin.Show();
            _settingsWin.Activate();
        }

        public void ExitApp()
        {
            IsExiting = true;
            try { _hook?.Dispose(); } catch { }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            IsExiting = true;
            try { _hook?.Dispose(); } catch { }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            base.OnExit(e);
        }
    }
}
