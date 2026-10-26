using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace GeminiLauncher
{
    public partial class SettingsWindow : Window
    {
        readonly App _app;
        Settings S => _app.Settings;
        readonly bool _wasElevatedTask;
        static readonly int[] Delays = { 500, 700, 900, 1200, 1600 };
        static readonly (string label, string val)[] Thinkings =
        {
            ("Minimal", "minimal"), ("Low", "low"), ("Medium", "medium"), ("High", "high"), ("Model default", "default")
        };

        string SelectedThinking => (ThinkingBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "medium";

        GeminiOptions BuildOptions(string model)
        {
            var o = new GeminiOptions
            {
                Model = model, Thinking = SelectedThinking,
                Search = ToolSearchBox.IsChecked == true, Maps = ToolMapsBox.IsChecked == true, UrlContext = ToolUrlBox.IsChecked == true,
                SafetyOff = SafetyBox.IsChecked != true
            };
            return o;
        }

        public SettingsWindow(App app)
        {
            _app = app;
            InitializeComponent();
            var wa = SystemParameters.WorkArea;
            if (Height > wa.Height - 40) Height = Math.Max(MinHeight, wa.Height - 40);
            SourceInitialized += (s, e) => Theme.ApplyBackdrop(this, true, 2);
            Action onTheme = () => Theme.ApplyBackdrop(this, true, 2);
            Theme.Changed += onTheme;
            Closed += (s, e) => Theme.Changed -= onTheme;

            KeyBox.Password = S.ApiKey ?? "";
            foreach (var m in Settings.KnownModels) ModelBox.Items.Add(m);
            ModelBox.Text = S.Model;
            InstantBox.IsChecked = S.InstantAnswers;
            foreach (var (label, val) in Thinkings) ThinkingBox.Items.Add(new ComboBoxItem { Content = label, Tag = val });
            ThinkingBox.SelectedIndex = Math.Max(0, Array.FindIndex(Thinkings, t => t.val == S.Thinking));
            ToolSearchBox.IsChecked = S.ToolSearch;
            ToolMapsBox.IsChecked = S.ToolMaps;
            ToolUrlBox.IsChecked = S.ToolUrlContext;
            LocationBox.IsChecked = S.UseLocation;
            SafetyBox.IsChecked = !S.SafetyOff;
            foreach (var d in Delays) DelayBox.Items.Add(new ComboBoxItem { Content = d + " ms", Tag = d });
            int di = Array.IndexOf(Delays, S.InstantDelayMs);
            DelayBox.SelectedIndex = di >= 0 ? di : 2;
            WinKeyBox.IsChecked = S.TakeOverWinKey;
            StartBtnBox.IsChecked = S.TakeOverStartButton;
            _wasElevatedTask = Settings.ElevatedTaskExists;
            ElevatedBox.IsChecked = _wasElevatedTask;
            StartupBox.IsChecked = Settings.StartWithWindows || _wasElevatedTask;
            ContentsBox.IsChecked = S.SearchFileContents;
            TrendingBox.IsChecked = S.ShowTrending;
            RegionBox.Text = S.TrendsRegion;
            foreach (var t in new[] { "System default", "Light", "Dark" }) ThemeBox.Items.Add(t);
            ThemeBox.SelectedIndex = S.Theme == "light" ? 1 : S.Theme == "dark" ? 2 : 0;
            foreach (var t in new[] { "Google results", "Gemini answer" }) WebPreviewBox.Items.Add(t);
            WebPreviewBox.SelectedIndex = S.GoogleFirst ? 0 : 1;
            VersionText.Text = "Gemini Launcher " + typeof(App).Assembly.GetName().Version.ToString(3) +
                               (Settings.IsElevated ? "  ·  running with admin rights" : "") + "  ·  Region " + S.EffectiveRegion;
        }

        void KeyLink_Click(object sender, RoutedEventArgs e) => Launcher.OpenUrl("https://aistudio.google.com/apikey");

        async void Test_Click(object sender, RoutedEventArgs e)
        {
            var key = KeyBox.Password.Trim();
            var model = (ModelBox.Text ?? "").Trim();
            if (key.Length == 0) { TestResult.Text = "Paste your API key first."; return; }
            TestResult.Text = "Testing " + model + " with Google Search…";
            var opt = BuildOptions(model);
            var ans = Gemini.Ask(key, opt, "What is today's date? Answer in one short sentence. (test " + DateTime.Now.Ticks + ")", CancellationToken.None);
            var sw = Stopwatch.StartNew();
            while (!ans.Done && sw.Elapsed.TotalSeconds < 45) await Task.Delay(150);
            if (ans.Error != null) { TestResult.Text = "✗ " + ans.Error; return; }
            var u = ans.Used ?? opt;
            var tools = new System.Collections.Generic.List<string>();
            if (u.Search) tools.Add("Google Search");
            if (u.Maps) tools.Add("Google Maps");
            if (u.UrlContext) tools.Add("URL context");
            var dropped = new System.Collections.Generic.List<string>();
            if (opt.Search && !u.Search) dropped.Add("Google Search");
            if (opt.Maps && !u.Maps) dropped.Add("Google Maps");
            if (opt.UrlContext && !u.UrlContext) dropped.Add("URL context");
            if (opt.SafetyOff && !u.SafetyOff) dropped.Add("safety OFF");
            if (opt.Thinking != u.Thinking) dropped.Add("thinking " + opt.Thinking + " (used " + u.Thinking + ")");
            TestResult.Text = "✓ Works in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s with " + (tools.Count > 0 ? string.Join(", ", tools) : "no tools") +
                              ", thinking " + u.Thinking + ", safety filters " + (u.SafetyOff ? "off" : "default") + "." +
                              (dropped.Count > 0 ? "\n⚠ This model didn't accept: " + string.Join(", ", dropped) + "." : "") +
                              "\n" + Markdown.ToPlain(ans.Snapshot()).Trim();
        }

        void NativeStart_Click(object sender, RoutedEventArgs e) => App.OpenNativeStart();

        void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            S.RecentSearches.Clear(); S.Save();
            ((Button)sender).Content = "History cleared";
        }

        void RestoreHidden_Click(object sender, RoutedEventArgs e)
        {
            int n = S.HiddenRecommended.Count + S.HiddenSearches.Count;
            S.HiddenRecommended.Clear(); S.HiddenSearches.Clear(); S.Save();
            ((Button)sender).Content = n == 1 ? "1 suggestion restored" : n + " suggestions restored";
        }

        void Rebuild_Click(object sender, RoutedEventArgs e)
        {
            AppIndex.RefreshAsync(force: true);
            ((Button)sender).Content = "Rebuilding…";
        }

        void DataFolder_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(Settings.Dir);
            Launcher.OpenUrl(Settings.Dir);
        }

        void Exit_Click(object sender, RoutedEventArgs e) { Close(); _app.ExitApp(); }

        void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        void Save_Click(object sender, RoutedEventArgs e)
        {
            S.ApiKey = KeyBox.Password.Trim();
            var model = (ModelBox.Text ?? "").Trim();
            if (model.Length > 0) S.Model = model;
            S.InstantAnswers = InstantBox.IsChecked == true;
            S.Thinking = SelectedThinking;
            S.ToolSearch = ToolSearchBox.IsChecked == true;
            S.ToolMaps = ToolMapsBox.IsChecked == true;
            S.ToolUrlContext = ToolUrlBox.IsChecked == true;
            S.UseLocation = LocationBox.IsChecked == true;
            S.SafetyOff = SafetyBox.IsChecked != true;
            Location.Enable(S.ToolMaps && S.UseLocation);
            if (DelayBox.SelectedItem is ComboBoxItem ci) S.InstantDelayMs = (int)ci.Tag;
            S.TakeOverWinKey = WinKeyBox.IsChecked == true;
            S.TakeOverStartButton = StartBtnBox.IsChecked == true;
            S.SearchFileContents = ContentsBox.IsChecked == true;
            S.ShowTrending = TrendingBox.IsChecked == true;
            S.TrendsRegion = (RegionBox.Text ?? "").Trim().ToUpperInvariant();
            S.WebPreview = WebPreviewBox.SelectedIndex == 1 ? "gemini" : "google";
            var theme = ThemeBox.SelectedIndex == 1 ? "light" : ThemeBox.SelectedIndex == 2 ? "dark" : "system";
            bool themeChanged = theme != S.Theme;
            S.Theme = theme;
            S.FirstRunDone = true;
            S.Save();

            bool elevated = ElevatedBox.IsChecked == true;
            if (elevated != _wasElevatedTask)
            {
                if (!Settings.SetElevatedStartup(elevated))
                    MessageBox.Show(this, "Windows didn't allow the change (the admin prompt was cancelled).", "Gemini Launcher");
            }
            if (!elevated) Settings.StartWithWindows = StartupBox.IsChecked == true;
            else if (StartupBox.IsChecked != true) Settings.SetElevatedStartup(false);

            if (themeChanged) { Theme.Mode = theme; Theme.ForceApply(); }
            _app.ApplyHookSettings();
            Close();
        }
    }
}
