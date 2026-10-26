using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GeminiLauncher
{
    public partial class MainWindow : Window
    {
        App AppRef => (App)Application.Current;
        Settings S => AppRef.Settings;

        readonly ObservableCollection<ResultItem> _results = new ObservableCollection<ResultItem>();
        // Home lists are kept and updated in place, so removing or moving one item doesn't rebuild (and flicker) the rest.
        readonly ObservableCollection<ResultItem> _pinItems = new ObservableCollection<ResultItem>();
        readonly ObservableCollection<ResultItem> _recItems = new ObservableCollection<ResultItem>();
        readonly ObservableCollection<ResultItem> _searchItems = new ObservableCollection<ResultItem>();
        int _homeVer;
        int _ver;
        CancellationTokenSource _cts, _answerCts;
        string _q = "";
        bool _userNavigated;
        List<string> _sugs = new List<string>();
        List<FileHit> _files = new List<FileHit>();
        List<(AppEntry app, double score)> _apps = new List<(AppEntry, double)>();
        List<((string name, string keywords, string uri) page, double score)> _pages = new List<((string, string, string), double)>();
        string _calc, _url;
        GeminiAnswer _bound;
        int _renderQueued;
        readonly DispatcherTimer _instantTimer;
        public bool SuppressHide;

        // Google results page in the preview pane
        readonly GoogleView _google;
        string _googleQuery;
        bool _expanded;               // full-size Google page: list hidden, window enlarged
        const double CompactW = 800, CompactH = 620;
        /// <summary>Google results are the web preview, unless this PC can't show them (no WebView2 Runtime).</summary>
        bool GoogleFirst => S.GoogleFirst && GoogleView.RuntimeAvailable != false;
        static bool GoogleUnavailable => GoogleView.RuntimeAvailable == false;

        public MainWindow()
        {
            InitializeComponent();
            ResultsList.ItemsSource = _results;
            PinsList.ItemsSource = _pinItems;
            RecommendedList.ItemsSource = _recItems;
            SearchesList.ItemsSource = _searchItems;
            SearchBox.TextChanged += (s, e) => OnQueryChanged();
            SearchBox.SelectionChanged += (s, e) => UpdateGhost();
            PreviewKeyDown += OnKey;
            PreviewTextInput += OnTextInput;
            Closing += (s, e) => { if (!App.IsExiting) { e.Cancel = true; AppRef.HideLauncher(); } };
            Deactivated += (s, e) =>
            {
                if (SuppressHide || !IsVisible) return;
                if (GooglePanel.IsVisible)
                {
                    // Clicking inside the Google page (a browser window hosted in ours) can look like a deactivation.
                    // Only close if another window really took over.
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        if (!SuppressHide && IsVisible && Native.GetForegroundWindow() != Hwnd) AppRef.HideLauncher();
                    }));
                    return;
                }
                AppRef.HideLauncher();
            };
            StateChanged += (s, e) => { if (WindowState != WindowState.Normal) WindowState = WindowState.Normal; };
            SourceInitialized += (s, e) =>
            {
                // Remove the system menu so DWM doesn't draw a caption close button on the glass frame.
                var h = new WindowInteropHelper(this).Handle;
                Native.SetWindowLong(h, -16 /*GWL_STYLE*/, Native.GetWindowLong(h, -16) & ~0x00080000 /*WS_SYSMENU*/);
                Theme.ApplyBackdrop(this, true, 3, popup: true);
            };
            Theme.Changed += () => { Theme.ApplyBackdrop(this, true, 3, popup: true); if (_bound != null) RenderAnswer(); _google?.SetDark(Theme.IsDark); };
            _instantTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _instantTimer.Tick += (s, e) => { _instantTimer.Stop(); MaybeInstantAnswer(); };
            AppIndex.Updated += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                AppRef.EnsureDefaultPins();
                if (IsVisible && HomeView.Visibility == Visibility.Visible) RefreshHome();
            }));
            UpdateModelBadge();

            _google = new GoogleView(GoogleHost);
            _google.SetDark(Theme.IsDark);
            _google.StatusChanged += st => GoogleStatus.Text = st ?? "";
            _google.Failed += () => { UpdateGoogleMessage(); UpdateFooter(); };
            _google.OpenExternal += url => { Launcher.OpenUrl(url); AppRef.HideLauncher(); };
            _google.EscapePressed += () => FocusSearch();
            IsVisibleChanged += (s, e) => { if (!IsVisible && !_prewarming) _google.Sleep(); };
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(GoogleView.CheckRuntime));
        }

        public void UpdateModelBadge()
        {
            ModelBadge.Text = string.IsNullOrWhiteSpace(S.ApiKey) ? "Gemini not connected" : S.Model;
        }

        // ======================= show / position =======================

        public void PrepareForShow()
        {
            _answerCts?.Cancel();
            Unbind();
            SetExpanded(false);
            GoogleView.CheckRuntime();   // quick (no browser starts): picks up a runtime installed since last time
            SearchBox.Text = "";
            ShowHome();
            RefreshHome();
            Position();
        }

        // Geometry of the monitor we open on (physical pixels)
        System.Drawing.Rectangle _wa, _bounds;
        bool _fromTop;
        int _margin = 12;
        double _scale = 1;
        IntPtr _tray;
        bool _animating, _hiding;
        readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        int _fromY, _durMs;
        double _elapsed, _lastTick;
        bool _slideOut;
        Action _onSlideDone;
        public bool IsHiding => _hiding;

        IntPtr Hwnd => new WindowInteropHelper(this).EnsureHandle();

        /// <summary>Picks the monitor under the cursor and parks the window just below its taskbar, ready to slide up.</summary>
        public void Position()
        {
            var hwnd = Hwnd;
            Native.GetCursorPos(out var pt);
            var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pt.x, pt.y));
            _wa = scr.WorkingArea; _bounds = scr.Bounds;
            _fromTop = _wa.Top > _bounds.Top && _wa.Bottom == _bounds.Bottom;   // taskbar at the top
            double scale = 1;
            try
            {
                var mon = Native.MonitorFromPoint(pt, 2);
                if (Native.GetDpiForMonitor(mon, 0, out uint dx, out uint dy) == 0) scale = dx / 96.0;
            }
            catch { }
            _margin = (int)Math.Round(12 * scale);
            _scale = scale;
            _tray = FindTray(_bounds);
            int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
            Native.SetWindowPos(hwnd, IntPtr.Zero, CenterX(w), OffY(h), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        int CenterX(int w) => _wa.Left + (_wa.Width - w) / 2;
        int TargetY(int h) => _fromTop ? _wa.Top + _margin : _wa.Bottom - h - _margin;
        int OffY(int h) => _fromTop ? _bounds.Top - h : _bounds.Bottom;   // hidden behind the taskbar / below the screen edge

        static IntPtr FindTray(System.Drawing.Rectangle screen)
        {
            var list = new List<IntPtr>();
            var main = Native.FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(main);
            IntPtr sec = IntPtr.Zero;
            while ((sec = Native.FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) list.Add(sec);
            foreach (var t in list)
                if (Native.GetWindowRect(t, out var r))
                {
                    int cx = (r.left + r.right) / 2, cy = (r.top + r.bottom) / 2;
                    if (screen.Contains(cx, cy)) return t;
                }
            return IntPtr.Zero;
        }

        /// <summary>Start-menu style entrance: the whole window (glass included) slides up from behind the taskbar.</summary>
        public void SlideIn()
        {
            Root.BeginAnimation(OpacityProperty, null); Root.Opacity = 1;
            Native.GetWindowRect(Hwnd, out var r);
            int h = r.bottom - r.top;
            _fromY = (_animating || _hiding) ? r.top : OffY(h);
            _hiding = false;
            StartSlide(false, SystemParameters.ClientAreaAnimation ? 300 : 0, null);
        }

        /// <summary>Start-menu style exit: quick slide back down behind the taskbar, then hide.</summary>
        public void SlideOut(Action done)
        {
            if (_hiding) return;
            _hiding = true;
            Native.GetWindowRect(Hwnd, out var r);
            _fromY = r.top;
            StartSlide(true, SystemParameters.ClientAreaAnimation ? 160 : 0, () =>
            {
                _hiding = false;
                Hide();
                done?.Invoke();
            });
        }

        void StartSlide(bool outwards, int ms, Action done)
        {
            _slideOut = outwards;
            _durMs = ms;
            _onSlideDone = done;
            _sw.Restart();
            _elapsed = 0; _lastTick = 0;
            if (!_animating) { _animating = true; CompositionTarget.Rendering += OnSlideFrame; }
            OnSlideFrame(null, EventArgs.Empty);
        }

        void OnSlideFrame(object sender, EventArgs e)
        {
            var hwnd = Hwnd;
            if (!Native.GetWindowRect(hwnd, out var r)) return;
            int w = r.right - r.left, h = r.bottom - r.top;
            // Advance by real frame time, but never more than ~2 frames at once, so a slow first
            // render can't make the slide skip straight to the end.
            double now = _sw.Elapsed.TotalMilliseconds;
            _elapsed += Math.Min(34, now - _lastTick);
            _lastTick = now;
            double p = _durMs <= 0 ? 1 : Math.Min(1, _elapsed / _durMs);
            int to = _slideOut ? OffY(h) : TargetY(h);
            // Enter: strong deceleration like Start (quartic ease-out). Exit: accelerate away (cubic ease-in).
            double k = _slideOut ? p * p * p : 1 - Math.Pow(1 - p, 4);
            int y = (int)Math.Round(_fromY + (to - _fromY) * k);
            // Keep the window just under the taskbar in Z-order so it emerges from behind it.
            if (_tray != IntPtr.Zero)
                Native.SetWindowPos(hwnd, _tray, CenterX(w), y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            else
                Native.SetWindowPos(hwnd, IntPtr.Zero, CenterX(w), y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            if (p >= 1)
            {
                CompositionTarget.Rendering -= OnSlideFrame;
                _animating = false;
                var d = _onSlideDone; _onSlideDone = null;
                d?.Invoke();
            }
        }

        /// <summary>Render once off-screen at startup so the first open is instant and smooth.</summary>
        bool _prewarming;
        public bool IsOpen => IsVisible && !_prewarming;

        public void Prewarm()
        {
            _prewarming = true;
            Position();
            ShowActivated = false;
            Show();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (_prewarming) { Hide(); EndPrewarm(); }
            }));
        }

        public void EndPrewarm()
        {
            _prewarming = false;
            ShowActivated = true;
        }

        public void SetQuery(string q)
        {
            SearchBox.Text = q ?? "";
            FocusSearch();
        }

        public void FocusSearch()
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }

        // ======================= HOME =======================

        void ShowHome()
        {
            SetExpanded(false);
            _google?.Cancel();
            HomeView.Visibility = Visibility.Visible;
            ResultsView.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
            ClearButton.Visibility = Visibility.Collapsed;
            GhostTyped.Text = GhostRest.Text = "";
            FooterHint.Text = "Type to search  ·  Drag pins to rearrange  ·  Del removes a suggestion  ·  Right-click for more";
            HomeView.ScrollToTop();
        }

        void ShowResults()
        {
            HomeView.Visibility = Visibility.Collapsed;
            ResultsView.Visibility = Visibility.Visible;
            Placeholder.Visibility = Visibility.Collapsed;
            ClearButton.Visibility = Visibility.Visible;
            UpdateFooter();
        }

        void UpdateFooter()
        {
            if (ResultsView.Visibility != Visibility.Visible) return;
            if (_expanded)
                FooterHint.Text = "Click a result to open it in your browser  ·  Esc back to the list  ·  Ctrl+Enter open in browser";
            else if (GooglePanel.Visibility == Visibility.Visible && _google.FailMessage != null)
                FooterHint.Text = "↑↓ move  ·  Enter open Google in your browser  ·  Tab complete  ·  Right-click for more";
            else if (GooglePanel.Visibility == Visibility.Visible)
                FooterHint.Text = "↑↓ move  ·  Enter full page  ·  Ctrl+Enter open in browser  ·  Tab complete  ·  Right-click for more";
            else
                FooterHint.Text = "↑↓ move  ·  Enter open  ·  Tab complete  ·  Ctrl+Enter Google  ·  Right-click for more";
        }

        public void RefreshHome()
        {
            int hv = ++_homeVer;

            // Pinned
            var pins = new List<ResultItem>();
            foreach (var p in S.Pins)
            {
                var it = FromPin(p);
                if (it != null) pins.Add(it);
            }
            if (!_dragging) Sync(_pinItems, pins);   // don't undo a drag in progress
            NoPins.Visibility = pins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Recommended: frequent apps + recent files (minus anything the user removed)
            var apps = new List<ResultItem>();
            foreach (var a in AppIndex.Frequent(S, 3))
            {
                var it = MakeApp(a, 0);
                it.Subtitle = "Frequently used";
                MakeRemovable(it, "Remove from list");
                apps.Add(it);
            }
            // Show the apps now, keep the files we already had until the fresh list arrives.
            Sync(_recItems, apps.Concat(_recItems.Where(x => x.Kind == ItemKind.File && !S.IsHiddenRecommended(x.LaunchKey))).ToList());
            Task.Run(() => FileSearch.Recent(20)).ContinueWith(t =>
            {
                if (hv != _homeVer) return;
                var files = new List<ResultItem>();
                if (!t.IsFaulted)
                    foreach (var f in t.Result)
                    {
                        if (files.Count >= 6) break;
                        var it = new ResultItem { Kind = ItemKind.File, Title = f.Name, Target = f.Path, Subtitle = FileSearch.Ago(f.Modified), When = f.Modified };
                        if (S.IsHiddenRecommended(it.LaunchKey)) continue;
                        MakeRemovable(it, "Remove from list");
                        IconLoader.Request(it);
                        files.Add(it);
                    }
                Sync(_recItems, apps.Concat(files).ToList());
            }, TaskScheduler.FromCurrentSynchronizationContext());

            // Searches: recent + trending
            var recent = S.RecentSearches.Take(3)
                .Select(q => MakeRemovable(new ResultItem { Kind = ItemKind.Recent, Title = q, Target = q, Subtitle = "Recent search", Glyph = Glyphs.History }, "Remove from history"))
                .ToList();
            List<ResultItem> WithTrending(IEnumerable<ResultItem> trending)
            {
                var seen = new HashSet<string>(recent.Select(r => r.Target), StringComparer.OrdinalIgnoreCase);
                var l = new List<ResultItem>(recent);
                foreach (var tr in trending)
                    if (l.Count < 8 && !S.IsHiddenSearch(tr.Target) && seen.Add(tr.Target)) l.Add(tr);
                return l;
            }
            if (!S.ShowTrending) { Sync(_searchItems, recent); return; }
            Sync(_searchItems, WithTrending(_searchItems.Where(x => x.Kind == ItemKind.Trending)));
            var geo = S.EffectiveRegion;
            Trends.GetAsync(geo).ContinueWith(t =>
            {
                if (hv != _homeVer || t.IsFaulted) return;
                Sync(_searchItems, WithTrending(t.Result.Select(tr =>
                    MakeRemovable(new ResultItem { Kind = ItemKind.Trending, Title = tr, Target = tr, Subtitle = "Trending in " + geo, Glyph = Glyphs.Trend }, "Don't show this search"))));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static ResultItem MakeRemovable(ResultItem it, string tip) { it.Removable = true; it.RemoveTip = tip; return it; }

        static string HomeKey(ResultItem x) => x.Kind + "|" + (x.Target ?? "").ToLowerInvariant();

        /// <summary>Makes <paramref name="col"/> match <paramref name="want"/> with the fewest changes,
        /// reusing existing items (keeps icons loaded, keyboard focus and avoids flicker).</summary>
        static void Sync(ObservableCollection<ResultItem> col, List<ResultItem> want)
        {
            var seen = new HashSet<string>();
            want = want.Where(w => seen.Add(HomeKey(w))).ToList();
            for (int i = col.Count - 1; i >= 0; i--)
                if (!seen.Contains(HomeKey(col[i]))) col.RemoveAt(i);
            for (int i = 0; i < want.Count; i++)
            {
                var k = HomeKey(want[i]);
                int at = -1;
                for (int j = i; j < col.Count; j++) if (HomeKey(col[j]) == k) { at = j; break; }
                if (at < 0) col.Insert(i, want[i]);
                else
                {
                    if (at != i) col.Move(at, i);
                    var cur = col[i];
                    if (cur.Subtitle != want[i].Subtitle || cur.Title != want[i].Title) col[i] = want[i]; // refresh "3 min ago" etc.
                }
            }
        }

        ResultItem FromPin(Pin p)
        {
            ResultItem it;
            switch (p.Kind)
            {
                case "app":
                    var a = AppIndex.FindById(p.Target);
                    it = new ResultItem { Kind = ItemKind.App, Title = p.Name, Target = p.Target, LnkPath = a?.Lnk, Subtitle = p.Name };
                    break;
                case "file":
                    it = new ResultItem { Kind = ItemKind.File, Title = p.Name, Target = p.Target, Subtitle = p.Target }; break;
                case "folder":
                    it = new ResultItem { Kind = ItemKind.Folder, Title = p.Name, Target = p.Target, Subtitle = p.Target }; break;
                case "url":
                    it = new ResultItem { Kind = ItemKind.Url, Title = p.Name, Target = p.Target, Subtitle = p.Target, Glyph = Glyphs.Globe }; break;
                case "setting":
                    it = new ResultItem { Kind = ItemKind.Setting, Title = p.Name, Target = p.Target, Subtitle = "Settings", Glyph = Glyphs.Gear }; break;
                default: return null;
            }
            it.IsPinned = true;
            IconLoader.Request(it);
            return it;
        }

        // ======================= QUERY PIPELINE =======================

        void OnQueryChanged()
        {
            var q = SearchBox.Text ?? "";
            var qt = q.Trim();
            if (qt.Length == 0)
            {
                _ver++;
                _cts?.Cancel();
                _instantTimer.Stop();
                _results.Clear();
                ShowHome();
                return;
            }
            ShowResults();
            if (GoogleFirst) _google.Prepare();   // start the browser while the first letters are typed
            _ver++;
            int v = _ver;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _q = qt;
            _userNavigated = false;

            // Keep still-matching async results to avoid flicker while typing
            _sugs = _sugs.Where(x => x.StartsWith(qt, StringComparison.OrdinalIgnoreCase)).ToList();
            var tokens = qt.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            _files = _files.Where(f => tokens.All(t => (f.Name ?? "").ToLowerInvariant().Contains(t))).ToList();

            _calc = Calc.TryEval(qt, out var cr) ? cr : null;
            _url = Launcher.LooksLikeUrl(qt, out var u) ? u : null;
            _apps = AppIndex.Search(qt, S, 6);
            _pages = SettingsPages.Search(qt, 3);
            Rebuild();
            UpdateGhost();

            _ = LoadSuggestions(qt, v, ct);
            _ = LoadFiles(qt, v, ct);

            _instantTimer.Stop();
            _instantTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(250, S.InstantDelayMs));
            _instantTimer.Start();
        }

        async Task LoadSuggestions(string q, int v, CancellationToken ct)
        {
            try
            {
                await Task.Delay(30, ct);
                var list = await GoogleSuggest.GetAsync(q, ct);
                if (v != _ver) return;
                _sugs = list.Where(x => !S.IsHiddenSearch(x)).ToList();
                Rebuild();
                UpdateGhost();
            }
            catch { }
        }

        async Task LoadFiles(string q, int v, CancellationToken ct)
        {
            try
            {
                await Task.Delay(110, ct);
                var hits = await Task.Run(() => FileSearch.Search(q, S.SearchFileContents, 8, ct), ct);
                if (v != _ver) return;
                _files = hits;
                Rebuild();
            }
            catch { }
        }

        ResultItem MakeApp(AppEntry a, double score)
        {
            var it = new ResultItem { Kind = ItemKind.App, Title = a.Name, Target = a.Id, LnkPath = a.Lnk, Score = score, Subtitle = "App" };
            it.IsPinned = S.Pins.Any(p => p.Kind == "app" && string.Equals(p.Target, a.Id, StringComparison.OrdinalIgnoreCase));
            IconLoader.Request(it);
            return it;
        }

        ResultItem MakeFile(FileHit f)
        {
            var dir = System.IO.Path.GetDirectoryName(f.Path) ?? "";
            var sub = (f.ContentMatch ? "Contains match  ·  " : "") + ShortenPath(dir) + (f.Modified != DateTime.MinValue ? "  ·  " + FileSearch.Ago(f.Modified) : "");
            var it = new ResultItem { Kind = f.IsFolder ? ItemKind.Folder : ItemKind.File, Title = f.Name, Target = f.Path, Subtitle = sub, Score = f.Score, When = f.Modified };
            it.IsPinned = S.Pins.Any(p => string.Equals(p.Target, f.Path, StringComparison.OrdinalIgnoreCase));
            IconLoader.Request(it);
            return it;
        }

        static string ShortenPath(string p)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (p.StartsWith(home, StringComparison.OrdinalIgnoreCase)) p = "~" + p.Substring(home.Length);
            return p;
        }

        void Rebuild()
        {
            var prev = ResultsList.SelectedItem as ResultItem;
            string prevKey = prev != null ? prev.Kind + "|" + prev.Target : null;
            var items = new List<ResultItem>();
            ResultItem def = null;
            var q = _q;

            if (_calc != null)
            {
                var c = new ResultItem { Kind = ItemKind.Calc, Title = "= " + _calc, Subtitle = q + "   ·   Enter to copy", Target = _calc, Glyph = Glyphs.Calc };
                items.Add(ResultItem.Header("Calculator")); items.Add(c); def = c;
            }
            if (_url != null)
            {
                var host = _url; try { host = new Uri(_url).Host; } catch { }
                var ui = new ResultItem { Kind = ItemKind.Url, Title = host, Subtitle = "Open website  ·  " + _url, Target = _url, Glyph = Glyphs.Globe };
                ui.IsPinned = S.Pins.Any(p => p.Target == _url);
                IconLoader.Request(ui);
                items.Add(ResultItem.Header("Website")); items.Add(ui); if (def == null) def = ui;
            }

            var appItems = _apps.Select(a => MakeApp(a.app, a.score)).ToList();
            var appNames = new HashSet<string>(appItems.Select(a => a.Title), StringComparer.OrdinalIgnoreCase);
            var pageItems = _pages.Where(p => !appNames.Contains(p.page.name)).Select(p => new ResultItem
            {
                Kind = ItemKind.Setting, Title = p.page.name, Subtitle = "System settings", Target = p.page.uri, Score = p.score, Glyph = Glyphs.Gear,
                IsPinned = S.Pins.Any(x => x.Target == p.page.uri)
            }).ToList();
            var fileItems = _files.Select(MakeFile).ToList();

            // Best match
            var candidates = appItems.Concat(pageItems).Concat(fileItems.Where(f => f.Score >= 80)).OrderByDescending(x => x.Score).ToList();
            var best = candidates.FirstOrDefault();
            if (best != null && best.Score >= 65 && _calc == null)
            {
                items.Add(ResultItem.Header("Best match"));
                items.Add(best);
                if (def == null) def = best;
                appItems.Remove(best); pageItems.Remove(best); fileItems.Remove(best);
            }

            // Local results first (apps, settings, files), like Windows search. Weak fuzzy app matches go below the web.
            var strongApps = appItems.Where(a => a.Score >= 45).ToList();
            var weakApps = appItems.Where(a => a.Score < 45).ToList();
            var strongPages = pageItems.Where(p => p.Score >= 45).ToList();
            var weakPages = pageItems.Where(p => p.Score < 45).ToList();
            if (strongApps.Count > 0) { items.Add(ResultItem.Header("Apps")); items.AddRange(strongApps.Take(3)); }
            if (strongPages.Count > 0) { items.Add(ResultItem.Header("Settings")); items.AddRange(strongPages.Take(2)); }
            if (fileItems.Count > 0) { items.Add(ResultItem.Header("Documents & files")); items.AddRange(fileItems.Take(3)); }
            bool anyLocal = strongApps.Count + strongPages.Count + fileItems.Count > 0;
            if (def == null) def = strongApps.FirstOrDefault() ?? strongPages.FirstOrDefault();

            // ...then the web: Gemini + Google suggestions (fewer when there are local results)
            items.Add(ResultItem.Header("Search the web"));
            var gem = new ResultItem { Kind = ItemKind.Gemini, Title = q, Target = q, Glyph = Glyphs.Sparkle, Subtitle = "Ask Gemini  ·  answers grounded in Google Search" };
            var goo = new ResultItem { Kind = ItemKind.GoogleSearch, Title = q, Target = q, Glyph = Glyphs.Globe,
                Subtitle = GoogleUnavailable ? "Search Google  ·  opens in your browser" : "Google results  ·  right here, Enter for full page" };
            if (GoogleFirst) { items.Add(goo); items.Add(gem); }
            else { items.Add(gem); items.Add(goo); }
            if (def == null) def = GoogleFirst ? goo : gem;
            if (_expanded) { def = goo; _userNavigated = false; }   // full-size page follows what you type
            foreach (var s in _sugs.Where(s => !string.Equals(s.Trim(), q, StringComparison.OrdinalIgnoreCase)).Take(anyLocal ? 3 : 6))
                items.Add(new ResultItem { Kind = ItemKind.Suggestion, Title = s, Target = s, Glyph = Glyphs.Search });

            var more = strongApps.Skip(3).Concat(weakApps).Take(4).ToList();
            if (more.Count > 0) { items.Add(ResultItem.Header("More apps")); items.AddRange(more); }
            var morePages = strongPages.Skip(2).Concat(weakPages).Take(2).ToList();
            if (morePages.Count > 0) { items.Add(ResultItem.Header("More settings")); items.AddRange(morePages); }
            if (fileItems.Count > 3) { items.Add(ResultItem.Header("More files")); items.AddRange(fileItems.Skip(3).Take(4)); }

            _results.Clear();
            foreach (var it in items) _results.Add(it);

            ResultItem sel = null;
            if (_userNavigated && prevKey != null) sel = items.FirstOrDefault(i => !i.IsHeader && i.Kind + "|" + i.Target == prevKey);
            if (sel == null) { sel = def; _userNavigated = false; }
            ResultsList.SelectedItem = sel;
            if (!_userNavigated && items.Count > 0) ResultsList.ScrollIntoView(items[0]);
            else if (sel != null && items.IndexOf(sel) <= 2) ResultsList.ScrollIntoView(items[0]);
        }

        ResultItem Selected => ResultsList.SelectedItem as ResultItem;

        void MoveSel(int d)
        {
            if (_results.Count == 0) return;
            int i = ResultsList.SelectedIndex;
            int steps = Math.Abs(d), dir = Math.Sign(d);
            for (int s = 0; s < steps; s++)
            {
                int j = i;
                do { j += dir; } while (j >= 0 && j < _results.Count && _results[j].IsHeader);
                if (j < 0 || j >= _results.Count) break;
                i = j;
            }
            if (i < 0) return;
            ResultsList.SelectedIndex = i;
            int view = i;
            if (dir < 0 && i > 0 && _results[i - 1].IsHeader) view = i - 1; // keep header visible
            ResultsList.ScrollIntoView(_results[view]);
            _userNavigated = true;
        }

        // ======================= GHOST AUTOCOMPLETE =======================

        string GhostCompletion()
        {
            var t = SearchBox.Text ?? "";
            if (t.Length == 0 || SearchBox.CaretIndex != t.Length || SearchBox.SelectionLength > 0 || t.EndsWith("  ")) return null;
            var s = _sugs.FirstOrDefault(x => x.Length > t.Length && x.StartsWith(t, StringComparison.OrdinalIgnoreCase));
            if (s == null)
            {
                var a = _apps.Select(x => x.app.Name).FirstOrDefault(n => n.Length > t.Length && n.StartsWith(t, StringComparison.OrdinalIgnoreCase));
                s = a;
            }
            return s == null ? null : s.Substring(t.Length);
        }

        void UpdateGhost()
        {
            var rest = GhostCompletion();
            GhostTyped.Text = rest == null ? "" : SearchBox.Text;
            GhostRest.Text = rest ?? "";
        }

        bool AcceptGhost()
        {
            var rest = GhostCompletion();
            if (rest == null) return false;
            SearchBox.Text = SearchBox.Text + rest;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            return true;
        }

        // ======================= PREVIEW / ANSWER =======================

        void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

        void UpdatePreview()
        {
            var it = Selected;
            if (it == null) { AnswerPanel.Visibility = DetailPanel.Visibility = GooglePanel.Visibility = Visibility.Collapsed; _google.Cancel(); UpdateFooter(); return; }
            if (it.Kind == ItemKind.GoogleSearch || (it.Kind == ItemKind.Suggestion && GoogleFirst))
                ShowGoogleFor(it.Target, _userNavigated ? 150 : 400);
            else if (it.Kind == ItemKind.Gemini || it.Kind == ItemKind.Suggestion) ShowAnswerFor(it.Target, false);
            else ShowDetail(it);
            UpdateFooter();
        }

        // ======================= GOOGLE RESULTS PAGE =======================

        void ShowGoogleFor(string query, int delayMs)
        {
            Unbind();
            AnswerPanel.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Collapsed;
            GooglePanel.Visibility = Visibility.Visible;
            _googleQuery = query;
            UpdateGoogleMessage();
            _google.Show(query, _expanded, delayMs);
        }

        void HideGoogle()
        {
            if (GooglePanel.Visibility == Visibility.Visible) GooglePanel.Visibility = Visibility.Collapsed;
            _google.Cancel();
            if (_expanded) SetExpanded(false);
        }

        void UpdateGoogleMessage()
        {
            if (_google.FailMessage != null)
            {
                GoogleMessage.Text = _google.RuntimeMissing ? _google.FailMessage : _google.FailMessage + "\n\nCtrl+Enter still opens Google in your browser.";
                GoogleInstallButton.Visibility = _google.RuntimeMissing ? Visibility.Visible : Visibility.Collapsed;
                GoogleExpandButton.Visibility = Visibility.Collapsed;
                GoogleStatus.Text = "";
            }
            else
            {
                GoogleMessage.Text = "Loading Google…";
                GoogleInstallButton.Visibility = Visibility.Collapsed;
                GoogleExpandButton.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Full-size Google page: hides the results list and makes the launcher bigger, so Google shows its desktop layout.</summary>
        void SetExpanded(bool on)
        {
            if (_expanded == on) return;
            if (on && (_google.FailMessage != null || GoogleUnavailable)) return;
            _expanded = on;
            ListColumn.Width = new GridLength(on ? 0 : 320);
            GapColumn.Width = new GridLength(on ? 0 : 14);
            ResultsList.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            GoogleExpandGlyph.Data = on ? Glyphs.Collapse : Glyphs.Expand;
            GoogleExpandButton.ToolTip = on ? "Back to the results list (Esc)" : "Full-size page (Enter or Ctrl+E)";

            double w = CompactW, h = CompactH;
            if (on && _wa.Width > 0)
            {
                double m = 2.0 * _margin / _scale;
                w = Math.Max(CompactW, Math.Min(1280, _wa.Width / _scale - m));
                h = Math.Max(CompactH, Math.Min(960, _wa.Height / _scale - m));
            }
            ResizeTo(w, h);
            UpdateFooter();
            if (GooglePanel.Visibility == Visibility.Visible && _googleQuery != null) _google.Show(_googleQuery, on, 0);
        }

        void ResizeTo(double w, double h)
        {
            MinWidth = 0; MinHeight = 0; MaxWidth = double.PositiveInfinity; MaxHeight = double.PositiveInfinity;
            if (IsVisible && !_animating && !_hiding)
            {
                // Resize and re-center in one step so the window grows around its middle, sitting on the taskbar.
                int pw = (int)Math.Round(w * _scale), ph = (int)Math.Round(h * _scale);
                Native.SetWindowPos(Hwnd, IntPtr.Zero, CenterX(pw), TargetY(ph), pw, ph, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            Width = w; Height = h;
            MinWidth = MaxWidth = w; MinHeight = MaxHeight = h;
        }

        void GoogleExpand_Click(object sender, RoutedEventArgs e)
        {
            SetExpanded(!_expanded);
            FocusSearch();
        }

        void InstallWebView2_Click(object sender, RoutedEventArgs e)
        {
            Launcher.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
            AppRef.HideLauncher();
        }

        /// <summary>Shows Google results for any query (context menu "Show Google results here").</summary>
        void ShowGoogleInPane(string query)
        {
            var goo = _results.FirstOrDefault(r => r.Kind == ItemKind.GoogleSearch);
            if (goo != null && string.Equals(goo.Target, query, StringComparison.Ordinal))
            {
                ResultsList.SelectedItem = goo; _userNavigated = true;
            }
            ShowGoogleFor(query, 0);
            UpdateFooter();
        }

        void MaybeInstantAnswer()
        {
            if (!S.InstantAnswers || string.IsNullOrWhiteSpace(S.ApiKey)) return;
            if (!IsVisible || ResultsView.Visibility != Visibility.Visible) return;
            var it = Selected;
            if (it == null || it.Kind != ItemKind.Gemini) return;    // don't spend API calls when an app/file is the obvious match
            if (it.Target.Length < 3 || _calc != null) return;
            ShowAnswerFor(it.Target, true);
        }

        void ShowAnswerFor(string query, bool start)
        {
            HideGoogle();
            AnswerPanel.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(S.ApiKey))
            {
                Unbind();
                RenderStatic("**Connect Gemini** to get instant AI answers grounded in Google Search, plus page summaries for links you paste.\n\n" +
                             "1. Get a free API key at https://aistudio.google.com/apikey\n" +
                             "2. Open Settings (gear icon below, or Ctrl+,) and paste it.\n\n" +
                             "Until then, press Ctrl+Enter to search Google in your browser.");
                AnswerStatus.Text = "";
                return;
            }

            var opt = S.GeminiOptions();
            var ans = Gemini.Cached(opt, query);
            if (ans == null && start)
            {
                _answerCts?.Cancel();
                _answerCts = new CancellationTokenSource();
                ans = Gemini.Ask(S.ApiKey, opt, query, _answerCts.Token);
            }
            if (ans == null)
            {
                Unbind();
                bool pending = S.InstantAnswers && Selected?.Kind == ItemKind.Gemini && query == _q && _instantTimer.IsEnabled;
                RenderStatic(pending ? "" : "Press **Enter** to ask Gemini about “" + query + "”.\n\nCtrl+Enter opens Google search in your browser.");
                AnswerStatus.Text = pending ? "Waiting for you to finish typing…" : "";
                return;
            }
            Bind(ans);
        }

        void Bind(GeminiAnswer ans)
        {
            if (_bound != ans)
            {
                Unbind();
                _bound = ans;
                ans.Changed += OnAnswerChanged;
            }
            RenderAnswer();
        }

        void Unbind()
        {
            if (_bound != null) _bound.Changed -= OnAnswerChanged;
            _bound = null;
            SetSpinning(false);
        }

        void OnAnswerChanged()
        {
            if (Interlocked.Exchange(ref _renderQueued, 1) == 1) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                if (_bound != null) RenderAnswer();
            }));
        }

        Brush Res(string k) => (Brush)FindResource(k);

        void RenderStatic(string md)
        {
            Markdown.Render(AnswerDoc, md, Res("Fg1"), Res("Fg2"), Res("AccentText"), Res("ChipBg"));
            SourcesPanel.Visibility = Visibility.Collapsed;
            SearchChips.Children.Clear();
            CopyAnswer.Visibility = Visibility.Collapsed;
            SetSpinning(false);
        }

        void RenderAnswer()
        {
            var a = _bound;
            if (a == null) return;
            string text; List<GeminiSource> sources, places; List<string> queries; List<string> urls;
            lock (a)
            {
                text = a.Text.ToString();
                places = a.Places.ToList();
                sources = a.Sources.ToList();
                queries = a.SearchQueries.ToList();
                urls = a.UrlsRead.ToList();
            }
            bool done = a.Done;

            if (a.Error != null && text.Length == 0)
            {
                var err = a.Error;
                string hint = "";
                if (err.Contains("API key") || err.Contains(" 400") || err.Contains(" 403")) hint = "\n\nCheck your API key in Settings (Ctrl+,).";
                else if (err.Contains(" 404")) hint = "\n\nThe model “" + S.Model + "” isn't available for your key. Pick another model in Settings.";
                else if (err.Contains(" 429")) hint = "\n\nYou've hit the Gemini rate limit for now. Try again in a minute, or turn off instant answers in Settings.";
                RenderStatic(err == "Cancelled" ? "Press **Enter** to ask Gemini." : "**Couldn't get an answer.** " + err + hint);
                AnswerStatus.Text = "";
                return;
            }

            Markdown.Render(AnswerDoc, done ? text : text + " ▍", Res("Fg1"), Res("Fg2"), Res("AccentText"), Res("ChipBg"));
            if (!done) AnswerStatus.Text = text.Length == 0 ? (urls.Count > 0 ? "Reading page…" : "Searching Google…") : "Writing…";
            else
            {
                var parts = new List<string>();
                if (sources.Count > 0) parts.Add("Google Search");
                if (places.Count > 0) parts.Add("Google Maps");
                if (urls.Count > 0) parts.Add("the page you linked");
                AnswerStatus.Text = parts.Count > 0 ? "Grounded in " + string.Join(" + ", parts) : "";
            }
            SetSpinning(!done);
            CopyAnswer.Visibility = done && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            SourceChips.Children.Clear();
            foreach (var pl in places.Take(6))
            {
                // Google Maps attribution: source name + "Google Maps", linked to the place.
                var b = new Button { Style = (Style)FindResource("ChipButton"), Focusable = false, Tag = pl.Uri, ToolTip = pl.Title + " on Google Maps" };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new System.Windows.Shapes.Path { Data = Glyphs.Pin, Stroke = Res("AccentText"), StrokeThickness = 1.3, Stretch = Stretch.Uniform, Width = 9, Height = 11, Margin = new Thickness(0, 0, 6, 0) });
                sp.Children.Add(new TextBlock { Text = pl.Title, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis });
                sp.Children.Add(new TextBlock { Text = "  Google Maps", Foreground = Res("Fg3"), TextWrapping = TextWrapping.NoWrap });
                b.Content = sp;
                b.Click += (o, e) => { Launcher.OpenUrl((string)((Button)o).Tag); AppRef.HideLauncher(); };
                SourceChips.Children.Add(b);
            }
            foreach (var s in sources.Take(8))
            {
                var b = new Button { Style = (Style)FindResource("ChipButton"), Content = s.Title, ToolTip = s.Title, Focusable = false, Tag = s.Uri };
                b.Click += (o, e) => { Launcher.OpenUrl((string)((Button)o).Tag); AppRef.HideLauncher(); };
                SourceChips.Children.Add(b);
            }
            foreach (var u in urls.Take(3))
            {
                string host = u; try { host = new Uri(u).Host; } catch { }
                var b = new Button { Style = (Style)FindResource("ChipButton"), Content = host, ToolTip = u, Focusable = false, Tag = u };
                b.Click += (o, e) => { Launcher.OpenUrl((string)((Button)o).Tag); AppRef.HideLauncher(); };
                SourceChips.Children.Add(b);
            }
            SourcesPanel.Visibility = SourceChips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Google Search suggestions (required display for grounded results): the queries Gemini ran, linked to Google.
            SearchChips.Children.Clear();
            foreach (var q in queries.Take(2))
            {
                var b = new Button { Style = (Style)FindResource("ChipButton"), Focusable = false, Tag = q, ToolTip = "Search Google for “" + q + "”" };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new System.Windows.Shapes.Path { Data = Glyphs.Search, Stroke = Res("Fg2"), StrokeThickness = 1.3, Stretch = Stretch.Uniform, Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0) });
                sp.Children.Add(new TextBlock { Text = q, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis });
                b.Content = sp;
                b.Click += (o, e) => { Launcher.GoogleSearch((string)((Button)o).Tag); AppRef.HideLauncher(); };
                SearchChips.Children.Add(b);
            }
        }

        void SetSpinning(bool on)
        {
            if (on)
            {
                if (SparkleSpin.HasAnimatedProperties) return;
                SparkleSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else SparkleSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        void ShowDetail(ResultItem it)
        {
            Unbind();
            HideGoogle();
            AnswerPanel.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;
            DetailIcon.Source = it.Icon;
            DetailGlyph.Data = it.Icon == null ? it.Glyph : null;
            if (it.Icon == null && (it.Kind == ItemKind.App || it.Kind == ItemKind.File || it.Kind == ItemKind.Folder))
            {
                System.ComponentModel.PropertyChangedEventHandler h = null;
                h = (s, e) => { if (e.PropertyName == "Icon" && Selected == it) DetailIcon.Source = it.Icon; it.PropertyChanged -= h; };
                it.PropertyChanged += h;
            }
            DetailTitle.Text = it.Kind == ItemKind.Calc ? it.Title : it.Title;
            DetailKind.Text = it.KindLabel;
            string path = null;
            switch (it.Kind)
            {
                case ItemKind.File: case ItemKind.Folder: path = it.Target; break;
                case ItemKind.App: path = it.LnkPath != null ? ShortenPath(it.LnkPath) : (it.Target.Contains("!") ? "Microsoft Store app" : null); break;
                case ItemKind.Url: path = it.Target; break;
                case ItemKind.Calc: path = it.Subtitle?.Split(new[] { "   ·" }, StringSplitOptions.None)[0]; break;
            }
            DetailPath.Text = path ?? "";
            DetailPath.Visibility = path == null ? Visibility.Collapsed : Visibility.Visible;

            DetailActions.Children.Clear();
            foreach (var (label, act) in ActionsFor(it, false))
            {
                if (label == "-") continue;
                var b = new Button { Style = (Style)FindResource("SubtleButton"), HorizontalContentAlignment = HorizontalAlignment.Left, Focusable = false, Padding = new Thickness(12, 8, 12, 8) };
                b.Content = new TextBlock { Text = label, Foreground = Res("Fg1") };
                var a = act;
                b.Click += (s, e) => a();
                DetailActions.Children.Add(b);
            }
        }

        // ======================= ACTIONS =======================

        List<(string, Action)> ActionsFor(ResultItem it, bool tile)
        {
            var l = new List<(string, Action)>();
            switch (it.Kind)
            {
                case ItemKind.App:
                    l.Add(("Open", () => Execute(it)));
                    if (it.LnkPath != null)
                    {
                        l.Add(("Run as administrator", () => Execute(it, admin: true)));
                        l.Add(("Open file location", () => { Launcher.ShowInFolder(it.LnkPath); AppRef.HideLauncher(); }));
                    }
                    break;
                case ItemKind.File:
                case ItemKind.Folder:
                    l.Add(("Open", () => Execute(it)));
                    if (it.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) l.Add(("Run as administrator", () => Execute(it, admin: true)));
                    l.Add(("Open file location", () => { Launcher.ShowInFolder(it.Target); AppRef.HideLauncher(); }));
                    l.Add(("Copy path", () => { try { Clipboard.SetText(it.Target); } catch { } }));
                    break;
                case ItemKind.Setting:
                    l.Add(("Open", () => Execute(it)));
                    break;
                case ItemKind.Url:
                    l.Add(("Open website", () => Execute(it)));
                    if (!tile) l.Add(("Summarize with Gemini", () => { ShowAnswerFor(it.Target, true); }));
                    l.Add(("Copy link", () => { try { Clipboard.SetText(it.Target); } catch { } }));
                    break;
                case ItemKind.Calc:
                    l.Add(("Copy result", () => Execute(it)));
                    break;
                case ItemKind.GoogleSearch:
                    l.Add((_expanded ? "Back to the results list" : "Show full-size page", () => { SetExpanded(!_expanded); FocusSearch(); }));
                    l.Add(("Open in browser", () => { Launcher.GoogleSearch(it.Target); S.AddRecentSearch(it.Target); AppRef.HideLauncher(); }));
                    l.Add(("Reload", () => _google.Reload()));
                    l.Add(("-", null));
                    l.Add(("Ask Gemini", () => { SetExpanded(false); var gem = _results.FirstOrDefault(r => r.Kind == ItemKind.Gemini); if (gem != null) { ResultsList.SelectedItem = gem; _userNavigated = true; } S.AddRecentSearch(it.Target); ShowAnswerFor(it.Target, true); }));
                    break;
                case ItemKind.Gemini:
                case ItemKind.Suggestion:
                case ItemKind.Recent:
                case ItemKind.Trending:
                    l.Add(("Ask Gemini", () => { if (it.Kind == ItemKind.Suggestion && GoogleFirst) AskGemini(it.Target); else Execute(it); }));
                    if (!tile && ResultsView.Visibility == Visibility.Visible)
                        l.Add(("Show Google results here", () => { S.AddRecentSearch(it.Target); ShowGoogleInPane(it.Target); }));
                    l.Add(("Search on Google in browser", () => { Launcher.GoogleSearch(it.Target); S.AddRecentSearch(it.Target); AppRef.HideLauncher(); }));
                    if (it.Kind == ItemKind.Recent)
                    {
                        l.Add(("-", null));
                        l.Add(("Remove from history", () => RemoveSuggestion(it)));
                        l.Add(("Clear all search history", () => { S.RecentSearches.Clear(); S.Save(); RefreshHome(); FocusSearch(); }));
                    }
                    else if (it.Kind == ItemKind.Trending)
                    {
                        l.Add(("-", null));
                        l.Add(("Don't show this search", () => RemoveSuggestion(it)));
                        l.Add(("Turn off trending searches", () => { S.ShowTrending = false; S.Save(); RefreshHome(); FocusSearch(); }));
                    }
                    else if (it.Kind == ItemKind.Suggestion)
                    {
                        l.Add(("-", null));
                        l.Add(("Remove suggestion", () => RemoveSuggestion(it)));
                    }
                    break;
            }
            if (it.Removable && (it.Kind == ItemKind.App || it.Kind == ItemKind.File))
            {
                l.Add(("-", null));
                l.Add(("Remove from list", () => RemoveSuggestion(it)));
            }
            if (it.PinKind != null)
            {
                l.Add(("-", null));
                if (it.IsPinned || S.Pins.Any(p => p.Target == it.Target))
                    l.Add(("Unpin from launcher", () => TogglePin(it)));
                else
                    l.Add(("Pin to launcher", () => TogglePin(it)));
                int pi = tile ? _pinItems.IndexOf(it) : -1;
                if (pi >= 0)
                {
                    if (pi > 0) l.Add(("Move to front", () => MovePinTo(it, 0)));
                    if (pi > 0) l.Add(("Move left", () => MovePin(it, -1)));
                    if (pi < _pinItems.Count - 1) l.Add(("Move right", () => MovePin(it, +1)));
                    if (pi < _pinItems.Count - 1) l.Add(("Move to end", () => MovePinTo(it, _pinItems.Count - 1)));
                }
            }
            return l;
        }

        void ShowMenu(ResultItem it, UIElement target, bool tile)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
            foreach (var (label, act) in ActionsFor(it, tile))
            {
                if (label == "-")
                {
                    if (menu.Items.Count > 0 && !(menu.Items[menu.Items.Count - 1] is Separator)) menu.Items.Add(new Separator());
                    continue;
                }
                var mi = new MenuItem { Header = label };
                var a = act;
                mi.Click += (s, e) => a();
                menu.Items.Add(mi);
            }
            if (menu.Items.Count > 0 && menu.Items[menu.Items.Count - 1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
            if (menu.Items.Count > 0) menu.IsOpen = true;
        }

        void TogglePin(ResultItem it)
        {
            var existing = S.Pins.FirstOrDefault(p => string.Equals(p.Target, it.Target, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { S.Pins.Remove(existing); it.IsPinned = false; }
            else
            {
                string name = it.Title;
                if (it.Kind == ItemKind.Url) { try { name = new Uri(it.Target).Host.Replace("www.", ""); } catch { } }
                S.Pins.Add(new Pin { Kind = it.PinKind, Name = name, Target = it.Target });
                it.IsPinned = true;
            }
            S.Save();
            if (HomeView.Visibility == Visibility.Visible) RefreshHome();
            else if (Selected == it) ShowDetail(it);
        }

        void MovePin(ResultItem it, int d)
        {
            int i = PinIndexOf(it);
            if (i >= 0) MovePinTo(it, i + d);
        }

        void MovePinTo(ResultItem it, int j)
        {
            int i = PinIndexOf(it);
            if (i < 0) return;
            j = Math.Max(0, Math.Min(_pinItems.Count - 1, j));
            if (j == i) return;
            bool hadFocus = FocusedHomeItem() == _pinItems[i];
            _pinItems.Move(i, j);
            SavePinOrder();
            if (hadFocus) FocusHomeItem(PinsList, j);
        }

        int PinIndexOf(ResultItem it)
        {
            for (int i = 0; i < _pinItems.Count; i++)
                if (_pinItems[i] == it || string.Equals(_pinItems[i].Target, it.Target, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Writes the on-screen tile order back to settings (pins that couldn't be shown keep their place at the end).</summary>
        void SavePinOrder()
        {
            var order = _pinItems.Select(x => x.Target).ToList();
            int Rank(Pin p) { int k = order.FindIndex(t => string.Equals(t, p.Target, StringComparison.OrdinalIgnoreCase)); return k < 0 ? int.MaxValue : k; }
            var sorted = S.Pins.Select((p, n) => (p, n)).OrderBy(x => Rank(x.p)).ThenBy(x => x.n).Select(x => x.p).ToList();
            S.Pins.Clear();
            S.Pins.AddRange(sorted);
            S.Save();
        }

        // ======================= REMOVE SUGGESTIONS =======================

        /// <summary>Removes a Recommended item, a recent search, a trending search or a Google suggestion.</summary>
        void RemoveSuggestion(ResultItem it)
        {
            if (it == null) return;
            ObservableCollection<ResultItem> list = null;
            ItemsControl ic = null;
            if (_recItems.Contains(it)) { list = _recItems; ic = RecommendedList; }
            else if (_searchItems.Contains(it)) { list = _searchItems; ic = SearchesList; }
            bool hadFocus = FocusedHomeItem() == it;
            int index = list?.IndexOf(it) ?? -1;

            switch (it.Kind)
            {
                case ItemKind.Recent: S.RemoveRecentSearch(it.Target); break;
                case ItemKind.Trending: S.HideSearch(it.Target); break;
                case ItemKind.Suggestion:
                    S.HideSearch(it.Target);
                    _sugs.RemoveAll(x => string.Equals(x, it.Target, StringComparison.OrdinalIgnoreCase));
                    Rebuild(); UpdateGhost();
                    return;
                case ItemKind.App:
                case ItemKind.File:
                case ItemKind.Folder:
                    S.HideRecommended(it.LaunchKey); break;
                default: return;
            }
            list?.Remove(it);  // instant feedback; RefreshHome then backfills
            RefreshHome();
            if (hadFocus && ic != null)
            {
                if (ic.Items.Count > 0) FocusHomeItem(ic, Math.Min(index, ic.Items.Count - 1));
                else FocusSearch();
            }
        }

        void RemoveSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ResultItem it) RemoveSuggestion(it);
            e.Handled = true;
        }

        ResultItem FocusedHomeItem() => (Keyboard.FocusedElement as Button)?.Content as ResultItem;

        void FocusHomeItem(ItemsControl ic, int index)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (index < 0 || index >= ic.Items.Count) return;
                var c = ic.ItemContainerGenerator.ContainerFromIndex(index) as DependencyObject;
                var b = c != null ? FindChild<Button>(c) : null;
                if (b != null) Keyboard.Focus(b);
            }));
        }

        // ======================= DRAG TO REARRANGE PINS =======================

        ResultItem _dragItem;
        Point _dragStart;
        bool _dragging;
        List<ResultItem> _dragOriginal;

        ResultItem PinFromElement(object source)
        {
            if (!(source is DependencyObject d)) return null;
            if (!(d is Visual) && !(d is System.Windows.Media.Media3D.Visual3D))
            {
                // e.g. a Run inside a TextBlock: walk up the logical tree to a visual
                while (d != null && !(d is Visual)) d = LogicalTreeHelper.GetParent(d);
                if (d == null) return null;
            }
            var c = ItemsControl.ContainerFromElement(PinsList, d) as FrameworkElement;
            return c?.DataContext as ResultItem;
        }

        void Pins_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            EndPinDrag(true);
            _dragItem = PinFromElement(e.OriginalSource);
            _dragStart = e.GetPosition(PinsList);
        }

        void Pins_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragItem == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndPinDrag(true); return; }
            var p = e.GetPosition(PinsList);
            if (!_dragging)
            {
                if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragging = true;
                _dragOriginal = _pinItems.ToList();
                _dragItem.IsDragging = true;
                PinsList.CaptureMouse();          // takes capture from the tile, so releasing won't "click" it
                Mouse.OverrideCursor = Cursors.SizeAll;
            }
            int target = PinIndexAt(p);
            int cur = _pinItems.IndexOf(_dragItem);
            if (target >= 0 && cur >= 0 && target != cur) _pinItems.Move(cur, target);
            e.Handled = true;
        }

        void Pins_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragging) { EndPinDrag(true); e.Handled = true; }
            else _dragItem = null;
        }

        void Pins_LostMouseCapture(object sender, MouseEventArgs e)
        {
            // Only our own capture ending counts: when the drag starts, the tile's LostMouseCapture bubbles up here too.
            if (_dragging && e.OriginalSource == PinsList) EndPinDrag(true);
        }

        void EndPinDrag(bool commit)
        {
            bool was = _dragging;
            _dragging = false;
            if (_dragItem != null) _dragItem.IsDragging = false;
            _dragItem = null;
            if (!was) return;
            Mouse.OverrideCursor = null;
            if (PinsList.IsMouseCaptured) PinsList.ReleaseMouseCapture();
            if (commit) SavePinOrder();
            else if (_dragOriginal != null) Sync(_pinItems, _dragOriginal);
            _dragOriginal = null;
        }

        /// <summary>Index of the tile under <paramref name="p"/> (PinsList coordinates). Past the last tile = last position.</summary>
        int PinIndexAt(Point p)
        {
            Rect last = Rect.Empty;
            for (int i = 0; i < _pinItems.Count; i++)
            {
                if (!(PinsList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c) || !c.IsVisible) continue;
                var r = new Rect(c.TranslatePoint(new Point(0, 0), PinsList), new Size(c.ActualWidth, c.ActualHeight));
                if (r.Contains(p)) return i;
                last = r;
            }
            if (!last.IsEmpty && ((p.Y >= last.Top && p.X > last.Right) || p.Y > last.Bottom)) return _pinItems.Count - 1;
            if (p.Y < 0) return 0;
            return -1;
        }

        public void Execute(ResultItem it, bool admin = false)
        {
            if (it == null || it.IsHeader) return;
            switch (it.Kind)
            {
                case ItemKind.App:
                case ItemKind.File:
                case ItemKind.Folder:
                case ItemKind.Setting:
                case ItemKind.Url:
                    if (Launcher.Open(it, admin))
                    {
                        S.CountLaunch(it.LaunchKey);
                        AppRef.HideLauncher();
                    }
                    break;
                case ItemKind.Calc:
                    try { Clipboard.SetText(it.Target); } catch { }
                    AppRef.HideLauncher();
                    break;
                case ItemKind.Gemini:
                    {
                        S.AddRecentSearch(it.Target);
                        var a = Gemini.Cached(S.GeminiOptions(), it.Target);
                        if (a != null && a.Done && _bound == a && AnswerPanel.Visibility == Visibility.Visible)
                        {
                            Launcher.GoogleSearch(it.Target); // Enter again on a finished answer -> full Google results
                            AppRef.HideLauncher();
                        }
                        else ShowAnswerFor(it.Target, true);
                        break;
                    }
                case ItemKind.GoogleSearch:
                    S.AddRecentSearch(it.Target);
                    if (!_expanded && _google.FailMessage == null && !GoogleUnavailable)
                    {
                        if (GooglePanel.Visibility != Visibility.Visible) ShowGoogleFor(it.Target, 0);
                        SetExpanded(true);   // Enter: full-size page
                    }
                    else { Launcher.GoogleSearch(it.Target); AppRef.HideLauncher(); }   // Enter again: open in browser
                    break;
                case ItemKind.Suggestion:
                case ItemKind.Recent:
                case ItemKind.Trending:
                    SetQueryAndAsk(it.Target);
                    break;
            }
        }

        /// <summary>Types the query and asks Gemini, whatever the web preview setting is.</summary>
        void AskGemini(string text)
        {
            SetExpanded(false);
            SearchBox.Text = text;
            SearchBox.CaretIndex = text.Length;
            FocusSearch();
            var gem = _results.FirstOrDefault(r => r.Kind == ItemKind.Gemini);
            if (gem != null) { ResultsList.SelectedItem = gem; _userNavigated = true; }
            _instantTimer.Stop();
            S.AddRecentSearch(text);
            ShowAnswerFor(text.Trim(), true);
        }

        void SetQueryAndAsk(string text)
        {
            if (GoogleFirst)
            {
                // Picking a suggestion / recent / trending search shows its Google results.
                SearchBox.Text = text;
                SearchBox.CaretIndex = text.Length;
                FocusSearch();
                var goo = _results.FirstOrDefault(r => r.Kind == ItemKind.GoogleSearch);
                if (goo != null) { ResultsList.SelectedItem = goo; _userNavigated = true; }
                S.AddRecentSearch(text);
                ShowGoogleFor(text.Trim(), 0);
                UpdateFooter();
                return;
            }
            SearchBox.Text = text;
            SearchBox.CaretIndex = text.Length;
            FocusSearch();
            var gem = _results.FirstOrDefault(r => r.Kind == ItemKind.Gemini);
            if (gem != null) { ResultsList.SelectedItem = gem; _userNavigated = true; }
            _instantTimer.Stop();
            S.AddRecentSearch(text);
            ShowAnswerFor(text.Trim(), true);
        }

        // ======================= INPUT =======================

        void OnTextInput(object sender, TextCompositionEventArgs e)
        {
            if (SearchBox.IsKeyboardFocusWithin || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            FocusSearch();
            SearchBox.Text += e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool results = ResultsView.Visibility == Visibility.Visible;
            bool inBox = SearchBox.IsKeyboardFocusWithin;

            switch (key)
            {
                case Key.Escape:
                    if (_dragging) { EndPinDrag(false); e.Handled = true; return; }
                    if (_google.HasFocus) { FocusSearch(); e.Handled = true; return; }   // Esc in the page: back to the search box
                    if (_expanded) { SetExpanded(false); FocusSearch(); e.Handled = true; return; }
                    if (SearchBox.Text.Length > 0) { SearchBox.Text = ""; FocusSearch(); }
                    else AppRef.HideLauncher();
                    e.Handled = true; return;

                case Key.Down:
                case Key.Up:
                    if (results) { SetExpanded(false); MoveSel(key == Key.Down ? 1 : -1); e.Handled = true; return; }
                    if (inBox && key == Key.Down) { FocusFirstHomeButton(); e.Handled = true; return; }
                    if (!inBox && Keyboard.FocusedElement is UIElement fe)
                    {
                        var before = Keyboard.FocusedElement;
                        fe.MoveFocus(new TraversalRequest(key == Key.Down ? FocusNavigationDirection.Down : FocusNavigationDirection.Up));
                        if (key == Key.Up && Keyboard.FocusedElement == before) FocusSearch();
                        e.Handled = true;
                    }
                    return;

                case Key.Left:
                case Key.Right:
                    // Alt+Shift+Left/Right moves the focused pin (same shortcut as the Windows 11 Start menu)
                    if (!inBox && mods == (ModifierKeys.Alt | ModifierKeys.Shift) && FocusedHomeItem() is ResultItem mp && _pinItems.Contains(mp))
                    {
                        MovePin(mp, key == Key.Right ? 1 : -1);
                        e.Handled = true; return;
                    }
                    if (!inBox && Keyboard.FocusedElement is UIElement fe2)
                    {
                        fe2.MoveFocus(new TraversalRequest(key == Key.Right ? FocusNavigationDirection.Right : FocusNavigationDirection.Left));
                        e.Handled = true; return;
                    }
                    if (key == Key.Right && inBox && SearchBox.CaretIndex == SearchBox.Text.Length && AcceptGhost()) { e.Handled = true; }
                    return;

                case Key.PageDown: if (results && !_expanded) { MoveSel(5); e.Handled = true; } return;
                case Key.PageUp: if (results && !_expanded) { MoveSel(-5); e.Handled = true; } return;

                case Key.E:
                    if (mods == ModifierKeys.Control && results && GooglePanel.Visibility == Visibility.Visible)
                    {
                        SetExpanded(!_expanded);
                        FocusSearch();
                        e.Handled = true;
                    }
                    return;

                case Key.Tab:
                    if (mods == ModifierKeys.None && results)
                    {
                        if (!AcceptGhost() && Selected != null && (Selected.Kind == ItemKind.Suggestion))
                        {
                            SearchBox.Text = Selected.Target; SearchBox.CaretIndex = SearchBox.Text.Length;
                        }
                        e.Handled = true;
                    }
                    return;

                case Key.Enter:
                    if (!inBox && Keyboard.FocusedElement is Button b && b.Content is ResultItem hi) { Execute(hi); e.Handled = true; return; }
                    if (!results) { e.Handled = true; return; }
                    if (mods == ModifierKeys.Control) { S.AddRecentSearch(_q); Launcher.GoogleSearch(_q); AppRef.HideLauncher(); }
                    else if (mods == (ModifierKeys.Control | ModifierKeys.Shift)) Execute(Selected, admin: true);
                    else if (mods == ModifierKeys.Alt && Selected != null)
                    {
                        var p = Selected.Kind == ItemKind.App ? Selected.LnkPath : Selected.Target;
                        if (p != null && (Selected.Kind == ItemKind.App || Selected.Kind == ItemKind.File || Selected.Kind == ItemKind.Folder)) { Launcher.ShowInFolder(p); AppRef.HideLauncher(); }
                    }
                    else Execute(Selected);
                    e.Handled = true; return;

                case Key.P:
                    if (mods == ModifierKeys.Control)
                    {
                        var target = results ? Selected : (Keyboard.FocusedElement as Button)?.Content as ResultItem;
                        if (target?.PinKind != null) TogglePin(target);
                        e.Handled = true;
                    }
                    return;

                case Key.OemComma:
                    if (mods == ModifierKeys.Control) { AppRef.OpenSettings(); e.Handled = true; }
                    return;

                case Key.Apps:
                    if (results && Selected != null)
                    {
                        var c = ResultsList.ItemContainerGenerator.ContainerFromItem(Selected) as UIElement;
                        ShowMenu(Selected, c ?? ResultsList, false);
                        e.Handled = true;
                    }
                    else if (!inBox && Keyboard.FocusedElement is Button mb && mb.Content is ResultItem mi)
                    {
                        ShowMenu(mi, mb, _pinItems.Contains(mi));
                        e.Handled = true;
                    }
                    return;

                case Key.Delete:
                    // Home: Del removes the focused suggestion (or unpins the focused pin).
                    if (!inBox && FocusedHomeItem() is ResultItem di)
                    {
                        if (di.Removable) RemoveSuggestion(di);
                        else if (_pinItems.Contains(di))
                        {
                            int at = _pinItems.IndexOf(di);
                            TogglePin(di);
                            if (_pinItems.Count > 0) FocusHomeItem(PinsList, Math.Min(at, _pinItems.Count - 1)); else FocusSearch();
                        }
                        e.Handled = true; return;
                    }
                    // Results: Shift+Del removes the selected Google suggestion (like Chrome's address bar).
                    if (results && mods == ModifierKeys.Shift && Selected?.Kind == ItemKind.Suggestion)
                    {
                        RemoveSuggestion(Selected);
                        e.Handled = true;
                    }
                    return;
            }
        }

        void FocusFirstHomeButton()
        {
            foreach (var ic in new ItemsControl[] { PinsList, RecommendedList, SearchesList })
            {
                if (ic.Items.Count == 0) continue;
                var c = ic.ItemContainerGenerator.ContainerFromIndex(0) as DependencyObject;
                var b = c != null ? FindChild<Button>(c) : null;
                if (b != null) { b.Focus(); return; }
            }
        }

        static T FindChild<T>(DependencyObject d) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is T t) return t;
                var r = FindChild<T>(c);
                if (r != null) return r;
            }
            return null;
        }

        // ======================= MOUSE / BUTTONS =======================

        ResultItem ItemFromEvent(MouseButtonEventArgs e)
        {
            var c = ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
            return c?.DataContext as ResultItem;
        }

        void ResultsList_Click(object sender, MouseButtonEventArgs e)
        {
            var it = ItemFromEvent(e);
            if (it == null || it.IsHeader) return;
            ResultsList.SelectedItem = it;
            _userNavigated = true;
            Execute(it);
            FocusSearch();
        }

        void ResultsList_RightClick(object sender, MouseButtonEventArgs e)
        {
            var it = ItemFromEvent(e);
            if (it == null || it.IsHeader) return;
            ResultsList.SelectedItem = it;
            _userNavigated = true;
            ShowMenu(it, ResultsList, false);
            e.Handled = true;
        }

        void HomeItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Content is ResultItem it) Execute(it);
        }

        void HomeItem_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as Button)?.Content is ResultItem it)) return;
            bool tile = PinsList.Items.Contains(it);
            ShowMenu(it, (UIElement)sender, tile);
            e.Handled = true;
        }

        void Clear_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; FocusSearch(); }

        void Settings_Click(object sender, RoutedEventArgs e) => AppRef.OpenSettings();

        void CopyAnswer_Click(object sender, RoutedEventArgs e)
        {
            if (_bound == null) return;
            var txt = Markdown.ToPlain(_bound.Snapshot());
            lock (_bound)
                if (_bound.Sources.Count > 0)
                    txt += "\n\nSources: " + string.Join(", ", _bound.Sources.Select(s => s.Title));
            try { Clipboard.SetText(txt); } catch { }
            AnswerStatus.Text = "Copied";
        }

        void OpenGoogle_Click(object sender, RoutedEventArgs e)
        {
            var q = GooglePanel.Visibility == Visibility.Visible ? (_googleQuery ?? _q) : (_bound?.Query ?? _q);
            S.AddRecentSearch(q);
            Launcher.GoogleSearch(q);
            AppRef.HideLauncher();
        }

        void AddPin_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
            var f = new MenuItem { Header = "Browse for an app or file…" };
            f.Click += (s, a) => PinFromFile();
            var d = new MenuItem { Header = "Pin a folder…" };
            d.Click += (s, a) => PinFromFolder();
            var w = new MenuItem { Header = "Pin a website…" };
            w.Click += (s, a) => PinWebsite();
            var tip = new MenuItem { Header = "Tip: right-click any search result → Pin", IsEnabled = false };
            menu.Items.Add(f); menu.Items.Add(d); menu.Items.Add(w); menu.Items.Add(new Separator()); menu.Items.Add(tip);
            menu.IsOpen = true;
        }

        void PinFromFile()
        {
            SuppressHide = true;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Pin an app or file",
                    Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd;*.ps1;*.appref-ms|All files|*.*",
                    DereferenceLinks = false,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                };
                if (dlg.ShowDialog(this) == true)
                {
                    S.Pins.Add(new Pin { Kind = "file", Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), Target = dlg.FileName });
                    S.Save();
                }
            }
            finally { SuppressHide = false; Activate(); RefreshHome(); }
        }

        void PinFromFolder()
        {
            SuppressHide = true;
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Pin a folder", ShowNewFolderButton = false })
                {
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
                    {
                        var name = new DirectoryInfo(dlg.SelectedPath).Name;
                        S.Pins.Add(new Pin { Kind = "folder", Name = string.IsNullOrEmpty(name) ? dlg.SelectedPath : name, Target = dlg.SelectedPath });
                        S.Save();
                    }
                }
            }
            finally { SuppressHide = false; Activate(); RefreshHome(); }
        }

        void PinWebsite()
        {
            SuppressHide = true;
            try
            {
                var url = InputDialog.Ask(this, "Pin a website", "Website address", "https://");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    url = url.Trim();
                    if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
                    string name = url;
                    try { name = new Uri(url).Host.Replace("www.", ""); } catch { }
                    S.Pins.Add(new Pin { Kind = "url", Name = name, Target = url });
                    S.Save();
                }
            }
            finally { SuppressHide = false; Activate(); FocusSearch(); RefreshHome(); }
        }
    }
}
