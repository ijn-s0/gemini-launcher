using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GeminiLauncher
{
    /// <summary>
    /// The WebView2 SDK (two managed DLLs + the native WebView2Loader.dll) is packed into GeminiLauncher.exe as
    /// gzip resources, so the app stays a single file. Managed DLLs are loaded from memory on demand; the small
    /// native loader for the current CPU is unpacked once to %LOCALAPPDATA%\GeminiLauncher\WebView2Loader.
    /// The browser engine itself is the Microsoft Edge WebView2 Runtime that ships with Windows.
    /// </summary>
    public static class WebView2Deps
    {
        static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        static bool _hooked;

        public static readonly string DataRoot =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeminiLauncher");

        /// <summary>Call before any WebView2 type is touched (done in App's static constructor).</summary>
        public static void Init()
        {
            if (_hooked) return;
            _hooked = true;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string name;
            try { name = new AssemblyName(e.Name).Name; } catch { return null; }
            if (!name.StartsWith("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase)) return null;
            lock (Loaded)
            {
                if (Loaded.TryGetValue(name, out var a)) return a;
                var bytes = ReadResource("wv2." + name + ".dll");
                if (bytes == null) return null;
                a = Assembly.Load(bytes);
                Loaded[name] = a;
                return a;
            }
        }

        static byte[] ReadResource(string logicalName)
        {
            using (var s = typeof(WebView2Deps).Assembly.GetManifestResourceStream(logicalName + ".gz"))
            {
                if (s == null) return null;
                using (var gz = new GZipStream(s, CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    gz.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        static string Arch
        {
            get
            {
                switch (RuntimeInformation.ProcessArchitecture)
                {
                    case Architecture.X86: return "x86";
                    case Architecture.Arm64: return "arm64";
                    default: return "x64";
                }
            }
        }

        /// <summary>Folder that contains WebView2Loader.dll for this process (unpacked if needed).</summary>
        public static string LoaderFolder()
        {
            var bytes = ReadResource("wv2." + Arch + ".WebView2Loader.dll");
            if (bytes == null) throw new FileNotFoundException("WebView2Loader.dll isn't packed into this build.");
            // One folder per loader build, so an update never fights with a file that's still in use.
            var dir = Path.Combine(DataRoot, "WebView2Loader", Arch + "-" + bytes.Length);
            var file = Path.Combine(dir, "WebView2Loader.dll");
            if (!File.Exists(file) || new FileInfo(file).Length != bytes.Length)
            {
                Directory.CreateDirectory(dir);
                var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    if (!File.Exists(file)) throw;
                }
            }
            return dir;
        }
    }
}
