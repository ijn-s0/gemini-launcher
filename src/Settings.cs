using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace GeminiLauncher
{
    public class Pin
    {
        public string Kind;   // app | file | folder | url | setting
        public string Name;
        public string Target; // AppUserModelID / path / url
    }

    public class Settings
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GeminiLauncher");
        static readonly string FilePath = Path.Combine(Dir, "settings.json");

        public string ApiKey = "";
        public string Model = "gemini-3.5-flash-lite";
        public bool InstantAnswers = true;
        public string Thinking = "medium";
        public bool ToolSearch = true, ToolMaps = true, ToolUrlContext = true;
        public bool SafetyOff = true;
        public bool UseLocation = false;
        public int InstantDelayMs = 900;
        public bool TakeOverWinKey = true;
        public bool TakeOverStartButton = true;
        public bool SearchFileContents = true;
        public bool ShowTrending = true;
        public string TrendsRegion = "";
        public string Theme = "system"; // system | dark | light
        /// <summary>What the preview shows for a web search while you type: "google" (results page) or "gemini" (AI answer).</summary>
        public string WebPreview = "google";
        public bool GoogleFirst => WebPreview != "gemini";
        public bool FirstRunDone;
        public bool DefaultPinsDone;
        public List<Pin> Pins = new List<Pin>();
        public List<string> RecentSearches = new List<string>();
        /// <summary>Recommended items the user removed ("app:ID" / "file:path").</summary>
        public List<string> HiddenRecommended = new List<string>();
        /// <summary>Trending searches and Google suggestions the user removed.</summary>
        public List<string> HiddenSearches = new List<string>();
        public Dictionary<string, int> LaunchCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] KnownModels =
        {
            "gemini-3.5-flash-lite", "gemini-3.8-flash", "gemini-3.6-flash",
            "gemini-4-argon", "gemini-3.1-pro-preview", "gemini-2.5-flash", "gemini-2.5-flash-lite"
        };

        public string EffectiveRegion
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(TrendsRegion)) return TrendsRegion.Trim().ToUpperInvariant();
                try { return RegionInfo.CurrentRegion.TwoLetterISORegionName.ToUpperInvariant(); } catch { return "US"; }
            }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var o = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                s.ApiKey = Unprotect(o.Str("apiKey")) ?? "";
                s.Model = o.Str("model") ?? s.Model;
                s.InstantAnswers = o.Bool("instantAnswers", s.InstantAnswers);
                s.Thinking = o.Str("thinking") ?? s.Thinking;
                s.ToolSearch = o.Bool("toolSearch", s.ToolSearch);
                s.ToolMaps = o.Bool("toolMaps", s.ToolMaps);
                s.ToolUrlContext = o.Bool("toolUrlContext", s.ToolUrlContext);
                s.SafetyOff = o.Bool("safetyOff", s.SafetyOff);
                s.UseLocation = o.Bool("useLocation", s.UseLocation);
                s.InstantDelayMs = (int)o.Num("instantDelayMs", s.InstantDelayMs);
                s.TakeOverWinKey = o.Bool("takeOverWinKey", s.TakeOverWinKey);
                s.TakeOverStartButton = o.Bool("takeOverStartButton", s.TakeOverStartButton);
                s.SearchFileContents = o.Bool("searchFileContents", s.SearchFileContents);
                s.ShowTrending = o.Bool("showTrending", s.ShowTrending);
                s.TrendsRegion = o.Str("trendsRegion") ?? "";
                s.Theme = o.Str("theme") ?? "system";
                s.WebPreview = o.Str("webPreview") == "gemini" ? "gemini" : "google";
                s.FirstRunDone = o.Bool("firstRunDone");
                s.DefaultPinsDone = o.Bool("defaultPinsDone");
                foreach (var p in o.Get("pins").Arr())
                    s.Pins.Add(new Pin { Kind = p.Str("kind"), Name = p.Str("name"), Target = p.Str("target") });
                foreach (var r in o.Get("recentSearches").Arr())
                    if (r is string rs) s.RecentSearches.Add(rs);
                foreach (var r in o.Get("hiddenRecommended").Arr())
                    if (r is string hr) s.HiddenRecommended.Add(hr);
                foreach (var r in o.Get("hiddenSearches").Arr())
                    if (r is string hs) s.HiddenSearches.Add(hs);
                if (o.Get("launchCounts") is Dictionary<string, object> lc)
                    foreach (var kv in lc) if (kv.Value is double d) s.LaunchCounts[kv.Key] = (int)d;
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var o = new Dictionary<string, object>
                {
                    ["apiKey"] = Protect(ApiKey),
                    ["model"] = Model,
                    ["instantAnswers"] = InstantAnswers,
                    ["thinking"] = Thinking,
                    ["toolSearch"] = ToolSearch,
                    ["toolMaps"] = ToolMaps,
                    ["toolUrlContext"] = ToolUrlContext,
                    ["safetyOff"] = SafetyOff,
                    ["useLocation"] = UseLocation,
                    ["instantDelayMs"] = InstantDelayMs,
                    ["takeOverWinKey"] = TakeOverWinKey,
                    ["takeOverStartButton"] = TakeOverStartButton,
                    ["searchFileContents"] = SearchFileContents,
                    ["showTrending"] = ShowTrending,
                    ["trendsRegion"] = TrendsRegion,
                    ["theme"] = Theme,
                    ["webPreview"] = WebPreview,
                    ["firstRunDone"] = FirstRunDone,
                    ["defaultPinsDone"] = DefaultPinsDone,
                    ["pins"] = Pins.Select(p => (object)new Dictionary<string, object> { ["kind"] = p.Kind, ["name"] = p.Name, ["target"] = p.Target }).ToList(),
                    ["recentSearches"] = RecentSearches.Cast<object>().ToList(),
                    ["hiddenRecommended"] = HiddenRecommended.Cast<object>().ToList(),
                    ["hiddenSearches"] = HiddenSearches.Cast<object>().ToList(),
                    ["launchCounts"] = LaunchCounts.ToDictionary(k => k.Key, k => (object)k.Value),
                };
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Json.Write(o), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch { }
        }

        public GeminiOptions GeminiOptions()
        {
            var o = new GeminiOptions
            {
                Model = Model, Thinking = Thinking, Search = ToolSearch, Maps = ToolMaps, UrlContext = ToolUrlContext, SafetyOff = SafetyOff
            };
            if (ToolMaps && UseLocation && Location.Current is System.Device.Location.GeoCoordinate c && !c.IsUnknown)
            { o.Lat = Math.Round(c.Latitude, 3); o.Lng = Math.Round(c.Longitude, 3); }
            return o;
        }

        public void AddRecentSearch(string q)
        {
            q = (q ?? "").Trim();
            if (q.Length == 0) return;
            RecentSearches.RemoveAll(x => string.Equals(x, q, StringComparison.OrdinalIgnoreCase));
            RecentSearches.Insert(0, q);
            if (RecentSearches.Count > 20) RecentSearches.RemoveRange(20, RecentSearches.Count - 20);
            Save();
        }

        public void RemoveRecentSearch(string q)
        {
            RecentSearches.RemoveAll(x => string.Equals(x, q, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        public bool IsHiddenRecommended(string key) =>
            key != null && HiddenRecommended.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));

        public void HideRecommended(string key)
        {
            if (string.IsNullOrEmpty(key) || IsHiddenRecommended(key)) return;
            HiddenRecommended.Add(key);
            if (HiddenRecommended.Count > 500) HiddenRecommended.RemoveRange(0, HiddenRecommended.Count - 500);
            Save();
        }

        public bool IsHiddenSearch(string q) =>
            q != null && HiddenSearches.Any(x => string.Equals(x, q.Trim(), StringComparison.OrdinalIgnoreCase));

        public void HideSearch(string q)
        {
            q = (q ?? "").Trim();
            if (q.Length == 0 || IsHiddenSearch(q)) return;
            HiddenSearches.Add(q);
            if (HiddenSearches.Count > 500) HiddenSearches.RemoveRange(0, HiddenSearches.Count - 500);
            Save();
        }

        public void CountLaunch(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            LaunchCounts.TryGetValue(key, out int n);
            LaunchCounts[key] = n + 1;
            Save();
        }

        public int LaunchCount(string key) => key != null && LaunchCounts.TryGetValue(key, out int n) ? n : 0;

        // API key is encrypted with Windows DPAPI (only your Windows account can decrypt it).
        static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try { return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try
            {
                if (s.StartsWith("dpapi:"))
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.Substring(6)), null, DataProtectionScope.CurrentUser));
                return s;
            }
            catch { return ""; }
        }

        public static bool IsElevated
        {
            get
            {
                try { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        const string TaskName = "GeminiLauncher (elevated)";

        public static bool ElevatedTaskExists
        {
            get
            {
                try
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\"")
                    { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                    p.WaitForExit(4000);
                    return p.ExitCode == 0;
                }
                catch { return false; }
            }
        }

        /// <summary>Creates/removes a logon scheduled task that runs the launcher with highest privileges (one UAC prompt).
        /// Needed only so the Windows key also works while an admin app is focused.</summary>
        public static bool SetElevatedStartup(bool on)
        {
            try
            {
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string args;
                if (on)
                {
                    var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                    var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                        "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                        "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><Delay>PT3S</Delay></LogonTrigger></Triggers>" +
                        "<Principals><Principal id=\"Author\"><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                        "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                        "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority>" +
                        "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings></Settings>" +
                        "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>--background</Arguments></Exec></Actions></Task>";
                    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GeminiLauncherTask.xml");
                    System.IO.File.WriteAllText(tmp, xml, Encoding.Unicode);
                    args = "/Create /F /TN \"" + TaskName + "\" /XML \"" + tmp + "\"";
                }
                else args = "/Delete /F /TN \"" + TaskName + "\"";
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
                { Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
                p.WaitForExit(15000);
                if (on && p.ExitCode == 0) StartWithWindows = false; // the task replaces the Run key
                return p.ExitCode == 0;
            }
            catch { return false; } // user cancelled UAC
        }

        // ---- Start with Windows (HKCU Run key, no admin needed) ----
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "GeminiLauncher";

        public void StartWithWindowsDefault()
        {
            if (!StartWithWindows && !ElevatedTaskExists) StartWithWindows = true;
        }

        public static bool StartWithWindows
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue(RunName) != null; }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue(RunName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --background");
                        else k.DeleteValue(RunName, false);
                    }
                }
                catch { }
            }
        }
    }
}
