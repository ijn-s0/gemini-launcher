using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace GeminiLauncher
{
    public enum ItemKind { Header, App, File, Folder, Setting, Url, Suggestion, Gemini, GoogleSearch, Calc, Recent, Trending, AddPin }

    public class ResultItem : INotifyPropertyChanged
    {
        public ItemKind Kind { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Target { get; set; }       // app id / file path / url / query
        public string LnkPath { get; set; }      // start-menu shortcut for "run as admin"
        public double Score { get; set; }
        public DateTime When { get; set; }

        ImageSource _icon;
        public ImageSource Icon { get => _icon; set { _icon = value; OnChanged(nameof(Icon)); OnChanged(nameof(HasIcon)); } }
        public bool HasIcon => _icon != null;

        public Geometry Glyph { get; set; }
        public bool IsHeader => Kind == ItemKind.Header;
        public bool IsPinned { get; set; }

        /// <summary>Shown on the home screen with an X button (Recommended items, recent and trending searches).</summary>
        public bool Removable { get; set; }
        public string RemoveTip { get; set; }

        bool _dragging;
        /// <summary>True while this pinned tile is being dragged to a new position.</summary>
        public bool IsDragging { get => _dragging; set { if (_dragging == value) return; _dragging = value; OnChanged(nameof(IsDragging)); } }

        public string PinKind
        {
            get
            {
                switch (Kind)
                {
                    case ItemKind.App: return "app";
                    case ItemKind.File: return "file";
                    case ItemKind.Folder: return "folder";
                    case ItemKind.Url: return "url";
                    case ItemKind.Setting: return "setting";
                    default: return null;
                }
            }
        }

        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case ItemKind.App: return "App";
                    case ItemKind.File: return "File";
                    case ItemKind.Folder: return "Folder";
                    case ItemKind.Setting: return "Settings";
                    case ItemKind.Url: return "Website";
                    case ItemKind.Suggestion: return "Google";
                    case ItemKind.Gemini: return "Gemini";
                    case ItemKind.GoogleSearch: return "Google";
                    case ItemKind.Calc: return "Calculator";
                    default: return "";
                }
            }
        }

        public string LaunchKey => (PinKind ?? "") + ":" + Target;

        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public static ResultItem Header(string title) => new ResultItem { Kind = ItemKind.Header, Title = title };
    }

    /// <summary>Vector glyphs (stroke-drawn, 16x16 box) so we don't depend on icon fonts.</summary>
    public static class Glyphs
    {
        static Geometry G(string s) { var g = Geometry.Parse(s); g.Freeze(); return g; }
        public static readonly Geometry Search = G("M6.5,1.5 A5,5 0 1 1 6.49,1.5 Z M10.2,10.2 L14.5,14.5");
        public static readonly Geometry Globe = G("M8,1 A7,7 0 1 1 7.99,1 Z M1,8 L15,8 M8,1 C4.5,4 4.5,12 8,15 M8,1 C11.5,4 11.5,12 8,15");
        public static readonly Geometry Sparkle = G("M8,0.5 C8.6,5 11,7.4 15.5,8 C11,8.6 8.6,11 8,15.5 C7.4,11 5,8.6 0.5,8 C5,7.4 7.4,5 8,0.5 Z");
        public static readonly Geometry History = G("M8,1.5 A6.5,6.5 0 1 1 1.6,7 M1.5,2.5 L1.5,6.5 L5.5,6.5 M8,4.5 L8,8.5 L11,10");
        public static readonly Geometry Trend = G("M1,12.5 L5.5,8 L8.5,11 L14.5,5 M10.5,5 L14.5,5 L14.5,9");
        public static readonly Geometry Calc = G("M3,1.5 L13,1.5 L13,14.5 L3,14.5 Z M5,4 L11,4 L11,6.5 L5,6.5 Z M5.5,9 L5.6,9 M8,9 L8.1,9 M10.5,9 L10.6,9 M5.5,12 L5.6,12 M8,12 L8.1,12 M10.5,12 L10.6,12");
        public static readonly Geometry Gear = G("M8,5.5 A2.5,2.5 0 1 1 7.99,5.5 Z M8,1 L8,3 M8,13 L8,15 M1,8 L3,8 M13,8 L15,8 M3,3 L4.4,4.4 M11.6,11.6 L13,13 M3,13 L4.4,11.6 M11.6,4.4 L13,3");
        public static readonly Geometry Plus = G("M8,2 L8,14 M2,8 L14,8");
        public static readonly Geometry Doc = G("M3.5,1.5 L10,1.5 L12.5,4 L12.5,14.5 L3.5,14.5 Z M10,1.5 L10,4 L12.5,4");
        public static readonly Geometry Pin = G("M8,15 C8,15 2.5,9.5 2.5,6 A5.5,5.5 0 0 1 13.5,6 C13.5,9.5 8,15 8,15 Z M8,4 A2,2 0 1 1 7.99,4 Z");
        public static readonly Geometry Expand = G("M9.5,1.5 L14.5,1.5 L14.5,6.5 M14.5,1.5 L9.5,6.5 M6.5,14.5 L1.5,14.5 L1.5,9.5 M1.5,14.5 L6.5,9.5");
        public static readonly Geometry Collapse = G("M14.5,1.5 L9.5,6.5 M9.5,2.5 L9.5,6.5 L13.5,6.5 M1.5,14.5 L6.5,9.5 M2.5,9.5 L6.5,9.5 L6.5,13.5");
        public static readonly Geometry Link = G("M6.5,9.5 L9.5,6.5 M7,4.5 L9,2.5 A2.8,2.8 0 0 1 13.5,7 L11.5,9 M9,11.5 L7,13.5 A2.8,2.8 0 0 1 2.5,9 L4.5,7");
    }
}
