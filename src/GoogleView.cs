using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GeminiLauncher
{
    /// <summary>
    /// The real Google results page, shown inside the launcher with Microsoft Edge WebView2.
    /// The page is Google's own, unmodified. Clicking a result opens it in your default browser.
    /// The browser starts when the launcher opens and is shut down after the launcher has been
    /// hidden for a few minutes, so it doesn't use memory while you aren't searching.
    /// </summary>
    public sealed class GoogleView
    {
        readonly Panel _host;
        WebView2 _wv;
        Task _init;
        bool _ready;
        string _desktopUA, _mobileUA;
        string _shown;            // query we last navigated to
        bool _shownDesktop;
        string _pending;          // query waiting for the debounce / the browser to start
        bool _pendingDesktop;
        bool _dark;
        int _gen;                 // bumped by Release(), so a browser that was still starting is ignored
        readonly DispatcherTimer _debounce, _release;
        static bool _loaderSet;

        /// <summary>Loading state for the header ("Loading…", or "" when done).</summary>
        public event Action<string> StatusChanged;
        /// <summary>WebView2 couldn't start (runtime missing, etc.). <see cref="FailMessage"/> has the details.</summary>
        public event Action Failed;
        /// <summary>A link outside Google search was clicked: open it in the default browser.</summary>
        public event Action<string> OpenExternal;
        /// <summary>Esc was pressed inside the page.</summary>
        public event Action EscapePressed;

        public string FailMessage { get; private set; }
        public bool RuntimeMissing { get; private set; }
        public bool IsReady => _ready;
        public bool HasFocus => _wv != null && _wv.IsKeyboardFocusWithin;
        public string ShownQuery => _shown;

        public GoogleView(Panel host)
        {
            _host = host;
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _debounce.Tick += (s, e) => Flush();
            _release = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _release.Tick += (s, e) => { _release.Stop(); Release(); };
        }

        /// <summary>false = no WebView2 Runtime on this PC (checked by <see cref="CheckRuntime"/>), null = not checked yet.</summary>
        public static bool? RuntimeAvailable { get; private set; }

        /// <summary>Optional portable runtime ("Fixed Version" from Microsoft, extracted here): used instead of an installed one.</summary>
        public static string FixedRuntimeRoot => Path.Combine(WebView2Deps.DataRoot, "WebView2Runtime");

        const string MissingMessage =
            "Google results need the Microsoft Edge WebView2 Runtime, and it isn't on this PC. It's separate from the Edge browser: " +
            "installing it doesn't bring Edge back. Until then, Enter opens Google in your browser.";

        static void EnsureLoader()
        {
            if (_loaderSet) return;
            CoreWebView2Environment.SetLoaderDllFolderPath(WebView2Deps.LoaderFolder());
            _loaderSet = true;
        }

        static string FindFixedRuntime()
        {
            try
            {
                var root = FixedRuntimeRoot;
                if (!Directory.Exists(root)) return null;
                if (File.Exists(Path.Combine(root, "msedgewebview2.exe"))) return root;
                // Microsoft's package extracts to a versioned subfolder (Microsoft.WebView2.FixedVersionRuntime.<ver>.<arch>)
                return Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "msedgewebview2.exe")));
            }
            catch { return null; }
        }

        /// <summary>The browser's sandboxed processes need read access to a portable runtime folder.</summary>
        static void GrantSandboxAccess(string dir)
        {
            try
            {
                var di = new DirectoryInfo(FixedRuntimeRoot);
                var acl = di.GetAccessControl();
                foreach (var sid in new[] { "S-1-15-2-1" /*ALL APPLICATION PACKAGES*/, "S-1-15-2-2" /*ALL RESTRICTED APPLICATION PACKAGES*/ })
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                di.SetAccessControl(acl);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        /// <summary>Cheap check (no browser is started) whether Google results can be shown on this PC.</summary>
        public static void CheckRuntime()
        {
            if (Environment.GetEnvironmentVariable("GEMINILAUNCHER_NO_WEBVIEW2") == "1") { RuntimeAvailable = false; return; }   // for testing
            try
            {
                EnsureLoader();
                var v = CoreWebView2Environment.GetAvailableBrowserVersionString(FindFixedRuntime());
                RuntimeAvailable = !string.IsNullOrEmpty(v);
            }
            catch (WebView2RuntimeNotFoundException) { RuntimeAvailable = false; }
            catch (Exception ex) { App.LogError(ex); RuntimeAvailable = null; }
        }

        public static string SearchUrl(string q) => "https://www.google.com/search?q=" + Uri.EscapeDataString(q ?? "");

        /// <summary>Starts the browser in the background (no page yet) so the first search shows up fast.</summary>
        public void Prepare()
        {
            _release.Stop();
            if (RuntimeAvailable == false && _wv == null)
            {
                if (FailMessage == null) { FailMessage = MissingMessage; RuntimeMissing = true; Failed?.Invoke(); }
                return;
            }
            if (_wv == null && FailMessage == null && _init == null) _init = InitAsync();
            else if (_ready) Wake();
        }

        /// <summary>Show Google results for <paramref name="q"/>. Waits <paramref name="delayMs"/> so
        /// typing doesn't load a page for every key press. <paramref name="desktop"/> = full desktop layout.</summary>
        public void Show(string q, bool desktop, int delayMs)
        {
            _release.Stop();
            // Same search already waiting: let the running timer finish (results refreshing while you type
            // shouldn't keep pushing the page load back).
            if (_debounce.IsEnabled && q == _pending && desktop == _pendingDesktop) return;
            _pending = q;
            _pendingDesktop = desktop;
            _debounce.Stop();
            if (_ready && q == _shown && desktop == _shownDesktop) return;
            if (delayMs <= 0 || (_ready && _shown == null)) Flush();
            else { _debounce.Interval = TimeSpan.FromMilliseconds(delayMs); _debounce.Start(); }
        }

        /// <summary>Don't load anything that is still waiting (the preview moved to something else).</summary>
        public void Cancel()
        {
            _debounce.Stop();
            _pending = null;
        }

        /// <summary>The launcher was hidden: drop memory now and shut the browser down if it stays hidden.</summary>
        public void Sleep()
        {
            Cancel();
            // Try again next time (e.g. the WebView2 Runtime was installed in the meantime).
            if (FailMessage != null) { FailMessage = null; RuntimeMissing = false; _init = null; }
            if (_ready)
            {
                try { _wv.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
                try { _ = _wv.CoreWebView2.TrySuspendAsync(); } catch { }
            }
            if (_wv != null || _init != null) { _release.Stop(); _release.Start(); }
        }

        void Wake()
        {
            try { _wv.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
        }

        public void Release()
        {
            _release.Stop();
            _debounce.Stop();
            _gen++;
            _ready = false;
            _init = null;
            _shown = null;
            var wv = _wv;
            _wv = null;
            if (wv != null)
            {
                _host.Children.Remove(wv);
                try { wv.Dispose(); } catch { }
            }
        }

        public void SetDark(bool dark)
        {
            _dark = dark;
            if (_wv == null) return;
            try { _wv.DefaultBackgroundColor = PageBackground(dark); } catch { }
            if (_ready)
                try { _wv.CoreWebView2.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light; } catch { }
        }

        public void Reload()
        {
            if (!_ready) return;
            try { _wv.CoreWebView2.Reload(); } catch { }
        }

        public void FocusPage()
        {
            try { _wv?.Focus(); } catch { }
        }

        static System.Drawing.Color PageBackground(bool dark) =>
            dark ? System.Drawing.Color.FromArgb(255, 0x1F, 0x1F, 0x1F) : System.Drawing.Color.White;

        void Flush()
        {
            _debounce.Stop();
            if (_pending == null) return;
            if (!_ready) { Prepare(); return; }   // InitAsync shows the pending query when it's done
            var q = _pending; var desktop = _pendingDesktop;
            _pending = null;
            Wake();
            NavigateTo(q, desktop);
        }

        void NavigateTo(string q, bool desktop)
        {
            var core = _wv.CoreWebView2;
            core.Settings.UserAgent = desktop ? _desktopUA : _mobileUA;
            if (q == _shown && desktop != _shownDesktop)
            {
                // Same search, only the layout changed: stay on the page the user is on (Images tab, page 2…).
                _shownDesktop = desktop;
                var src = core.Source;
                core.Navigate(Uri.TryCreate(src, UriKind.Absolute, out var su) && IsGoogleSearchPage(su) ? src : SearchUrl(q));
                return;
            }
            _shown = q;
            _shownDesktop = desktop;
            core.Navigate(SearchUrl(q));
        }

        async Task InitAsync()
        {
            int gen = _gen;
            try
            {
                StatusChanged?.Invoke("Starting…");
                await CreateAsync(gen);
            }
            catch (Exception ex)
            {
                if (gen != _gen) return;   // released while starting: not an error
                var wv = _wv; _wv = null; _ready = false; _init = null;
                if (wv != null) { _host.Children.Remove(wv); try { wv.Dispose(); } catch { } }
                RuntimeMissing = ex is WebView2RuntimeNotFoundException || ex.GetType().Name == "WebView2RuntimeNotFoundException";
                if (RuntimeMissing) RuntimeAvailable = false;
                FailMessage = RuntimeMissing ? MissingMessage : "Couldn't start the built-in browser: " + ex.Message;
                App.LogError(ex);
                StatusChanged?.Invoke("");
                Failed?.Invoke();
                return;
            }
            if (gen != _gen) return;
            StatusChanged?.Invoke("");
            if (_pending != null) Flush();
        }

        // Kept separate so the WebView2 assemblies only load once Google results are actually used.
        async Task CreateAsync(int gen)
        {
            EnsureLoader();
            var dataDir = Path.Combine(WebView2Deps.DataRoot, "WebView2");
            Directory.CreateDirectory(dataDir);
            var fixedDir = FindFixedRuntime();
            if (fixedDir != null) GrantSandboxAccess(fixedDir);
            var env = await CoreWebView2Environment.CreateAsync(fixedDir, dataDir, new CoreWebView2EnvironmentOptions());
            if (gen != _gen) return;

            var wv = new WebView2 { DefaultBackgroundColor = PageBackground(_dark), Focusable = true };
            _wv = wv;
            _host.Children.Add(wv);
            await wv.EnsureCoreWebView2Async(env);
            if (gen != _gen || _wv != wv) return;   // released while starting

            var core = wv.CoreWebView2;
            var st = core.Settings;
            st.AreDevToolsEnabled = false;
            st.IsStatusBarEnabled = false;
            st.IsPasswordAutosaveEnabled = false;
            st.IsSwipeNavigationEnabled = false;
            st.AreDefaultContextMenusEnabled = true;
            st.IsZoomControlEnabled = true;

            _desktopUA = st.UserAgent;
            // The preview pane is phone-sized, so ask Google for its phone layout there (same engine, same version).
            var m = Regex.Match(_desktopUA ?? "", @"Chrome/(\d+)");
            var major = m.Success ? m.Groups[1].Value : "130";
            _mobileUA = "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + major + ".0.0.0 Mobile Safari/537.36";

            try { core.Profile.PreferredColorScheme = _dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light; } catch { }

            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += (s, e) =>
            {
                StatusChanged?.Invoke(e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled
                    ? "" : "Couldn't load Google (" + e.WebErrorStatus + ")");
            };
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var nu) && (nu.Scheme == Uri.UriSchemeHttp || nu.Scheme == Uri.UriSchemeHttps))
                    OpenExternal?.Invoke(ExternalTarget(e.Uri));
            };
            core.WebMessageReceived += (s, e) =>
            {
                string msg = null;
                try { msg = e.TryGetWebMessageAsString(); } catch { }
                if (msg == "escape") EscapePressed?.Invoke();
            };
            // Esc inside the page hands the keyboard back to the launcher's search box.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('keydown',function(e){if(e.key==='Escape'){try{chrome.webview.postMessage('escape')}catch(_){}}},true);");
            _ready = true;
        }

        void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) return;
            if (u.Scheme == "about" || u.Scheme == "data" || u.Scheme == "blob") return;
            if (IsAllowedInside(u)) { StatusChanged?.Invoke("Loading…"); return; }
            e.Cancel = true;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return;   // intent://, app links… meant for phones
            // A result (or anything that isn't Google search) opens in the default browser, like clicking a result on google.com.
            OpenExternal?.Invoke(ExternalTarget(e.Uri));
        }

        static readonly Regex GoogleHost = new Regex(@"^(www\.)?google\.[a-z]{2,3}(\.[a-z]{2})?$", RegexOptions.IgnoreCase);

        static bool IsGoogleSearchPage(Uri u)
        {
            if (!GoogleHost.IsMatch(u.Host)) return false;
            var p = u.AbsolutePath.ToLowerInvariant();
            return p == "/" || p == "/search" || p == "/webhp" || p == "/imgres" || p.StartsWith("/sorry") ||
                   p == "/preferences" || p == "/setprefs" || p == "/advanced_search";
        }

        static bool IsAllowedInside(Uri u)
        {
            if (IsGoogleSearchPage(u)) return true;
            var h = u.Host.ToLowerInvariant();
            // Cookie consent (EU), the "unusual traffic" check and signing in to Google stay inside.
            return Regex.IsMatch(h, @"^consent\.google\.[a-z.]+$") || h == "accounts.google.com" || h == "www.recaptcha.net" ||
                   (GoogleHost.IsMatch(h) && u.AbsolutePath.StartsWith("/recaptcha", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>google.com/url?q=… redirect links: open the real address directly.</summary>
        static string ExternalTarget(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
            if (GoogleHost.IsMatch(u.Host) && u.AbsolutePath == "/url")
            {
                var t = QueryParam(u.Query, "q") ?? QueryParam(u.Query, "url");
                if (!string.IsNullOrEmpty(t) && Uri.TryCreate(t, UriKind.Absolute, out var tu) && (tu.Scheme == "http" || tu.Scheme == "https"))
                    return t;
            }
            return url;
        }

        static string QueryParam(string query, string key)
        {
            foreach (var part in (query ?? "").TrimStart('?').Split('&'))
            {
                int i = part.IndexOf('=');
                if (i <= 0 || part.Substring(0, i) != key) continue;
                try { return Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' ')); } catch { return null; }
            }
            return null;
        }
    }
}
