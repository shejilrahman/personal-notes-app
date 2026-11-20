// Personal Notes - a tray app that shows/hides with Ctrl+Shift+A and stays pinned
// above all other windows while shown. Every note has a name and details; it is
// saved automatically as "<name>.txt" in Documents\Personal Notes.
//
// The interface is WPF: Styles.xaml (look), MainView.xaml (layout) and
// PasteBar.xaml are embedded in the exe and loaded at start-up.
// Build with build.cmd (uses the C# compiler that ships with Windows).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using IOPath = System.IO.Path;
using WinForms = System.Windows.Forms;

namespace PersonalNotes
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool startHidden = Array.IndexOf(args, "--hidden") >= 0;

            bool isFirstInstance;
            using (new System.Threading.Mutex(true, "Local\\PersonalNotes.SingleInstance", out isFirstInstance))
            {
                if (!isFirstInstance)
                {
                    // Already running: ask the running copy to show its window.
                    if (!startHidden)
                    {
                        Native.AllowSetForegroundWindow(-1);
                        Native.PostMessage((IntPtr)Native.HWND_BROADCAST, Native.ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    }
                    return;
                }

                try
                {
                    WinForms.Application.EnableVisualStyles();   // for the tray icon's menu
                    Application app = new Application();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        LogError(e.Exception);
                        e.Handled = true;   // keep the notes available rather than crash
                    };
                    app.Resources.MergedDictionaries.Add((ResourceDictionary)LoadXaml("Styles.xaml"));

                    MainWindow window = new MainWindow();
                    if (!startHidden) window.ShowNotes();
                    app.Run();
                }
                catch (Exception ex)
                {
                    LogError(ex);
                    MessageBox.Show("Personal Notes could not start.\n\n" + ex.Message, "Personal Notes");
                }
            }
        }

        // The .xaml files are embedded in the exe by build.cmd.
        public static object LoadXaml(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new FileNotFoundException("Missing embedded file " + name);
                return XamlReader.Load(stream);
            }
        }

        public static void LogError(Exception ex)
        {
            try
            {
                File.AppendAllText(IOPath.Combine(IOPath.GetTempPath(), "PersonalNotes-error.log"),
                    DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }

    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_CLOSE = 0xF060;
        public const int HWND_BROADCAST = 0xFFFF;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public static readonly int ShowMessage = RegisterWindowMessage("PersonalNotes.ShowWindow");

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLong64(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLong64(IntPtr hWnd, int index, IntPtr value);

        public static void AddExtendedStyle(IntPtr hWnd, int style)
        {
            if (IntPtr.Size == 8)
            {
                long current = GetWindowLong64(hWnd, GWL_EXSTYLE).ToInt64();
                SetWindowLong64(hWnd, GWL_EXSTYLE, new IntPtr(current | (long)(uint)style));
            }
            else
            {
                SetWindowLong32(hWnd, GWL_EXSTYLE, GetWindowLong32(hWnd, GWL_EXSTYLE) | style);
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        // Windows 11: rounded window corners and a border in the theme's colour. Ignored on older Windows.
        public static void StyleWindowFrame(IntPtr hWnd, Color border)
        {
            try
            {
                int round = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(hWnd, 33, ref round, sizeof(int));
                int colour = border.R | (border.G << 8) | (border.B << 16);
                DwmSetWindowAttribute(hWnd, 34, ref colour, sizeof(int));
            }
            catch (Exception) { }
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

        // Presses Win+H, the Windows shortcut that starts voice typing in the focused text box.
        public static void PressVoiceTypingShortcut()
        {
            const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_LWIN = 0x5B, VK_H = 0x48;
            const uint KEYUP = 0x0002;
            // If this was triggered by Ctrl+M, Ctrl is still held; release it so Windows sees plain Win+H.
            keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_H, 0, 0, UIntPtr.Zero);
            keybd_event(VK_H, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYUP, UIntPtr.Zero);
        }

        // Keyboard hook used only while "paste one by one" is running, to notice each paste.
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_INSERT = 0x2D, VK_V = 0x56, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        public static bool IsKeyDown(int key)
        {
            return GetAsyncKeyState(key) < 0;
        }
    }

    // Dark and light colour sets. Every brush is looked up by name from the XAML
    // (DynamicResource), so switching theme repaints the whole app at once.
    internal static class Theme
    {
        public static void Apply(bool dark)
        {
            Set("BgEditor", dark ? "#14161B" : "#FFFFFF");
            Set("BgSidebar", dark ? "#0E1014" : "#F5F6F8");
            Set("Surface", dark ? "#1B1E25" : "#FFFFFF");
            Set("Card", dark ? "#1B1E25" : "#FFFFFF");
            Set("Hover", dark ? "#262A34" : "#E9ECF1");
            Set("Border", dark ? "#282C36" : "#E1E4EA");
            Set("SegmentBg", dark ? "#181B21" : "#E9ECF1");
            Set("SegmentOn", dark ? "#2B303B" : "#FFFFFF");
            Set("Text", dark ? "#EDEFF3" : "#15181E");
            Set("Muted", dark ? "#939CAB" : "#5E6878");
            Set("Faint", dark ? "#626A79" : "#9AA3B2");
            Set("AccentSolid", dark ? "#8B5CF6" : "#7C3AED");
            Set("AccentSoft", dark ? "#368B5CF6" : "#1F7C3AED");
            Set("Gold", dark ? "#FFC53D" : "#F2A100");
            Set("Danger", dark ? "#EF4E5E" : "#E5484D");
            Set("Good", dark ? "#34D399" : "#12A150");
            Set("GoodSoft", dark ? "#4034D399" : "#3312A150");
            Set("CardFill", dark ? "#338B5CF6" : "#1C7C3AED");
            Set("CardEdge", dark ? "#998B5CF6" : "#807C3AED");
            Set("CardText", dark ? "#DDD2FF" : "#5B21B6");
            Set("Warn", dark ? "#FBBF24" : "#E69A00");
            Set("Dim", dark ? "#A6080A0D" : "#66121418");
        }

        public static Color ColorOf(string key)
        {
            return ((SolidColorBrush)Application.Current.Resources[key]).Color;
        }

        private static void Set(string key, string colour)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }

    // Cards are parts of a note's details wrapped in [[ ]], e.g. "phone : [[889355556]]".
    internal static class Cards
    {
        public static readonly Regex Pattern = new Regex(@"\[\[(.+?)\]\]");

        // The text as you would read it, without the [[ ]] marks.
        public static string Plain(string text)
        {
            return Pattern.Replace(text, "$1");
        }

        public static List<string> Values(string text)
        {
            List<string> values = new List<string>();
            foreach (Match card in Pattern.Matches(text)) values.Add(card.Groups[1].Value);
            return values;
        }
    }

    // One note: its name is the file name, its details are the file's text.
    public sealed class Note : INotifyPropertyChanged
    {
        private string filePath;            // null until the note is first saved
        private string name = "";
        private string body = "";
        private DateTime modified = DateTime.Now;
        private bool favourite;

        public event PropertyChangedEventHandler PropertyChanged;

        public string FilePath
        {
            get { return filePath; }
            set { filePath = value; Raise("Preview", "DateText"); }
        }

        public string Name
        {
            get { return name; }
            set { name = value ?? ""; Raise("DisplayName"); }
        }

        public string Body
        {
            get { return body; }
            set { body = value ?? ""; Raise("Preview"); }
        }

        public DateTime Modified
        {
            get { return modified; }
            set { modified = value; Raise("DateText"); }
        }

        public bool Favourite
        {
            get { return favourite; }
            set { favourite = value; Raise("Favourite"); }
        }

        public bool IsBlank
        {
            get { return name.Trim().Length == 0 && body.Trim().Length == 0; }
        }

        // What the list shows.
        public string DisplayName
        {
            get { return name.Trim().Length == 0 ? "Untitled note" : name.Trim(); }
        }

        public string Preview
        {
            get
            {
                using (StringReader reader = new StringReader(body))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = Cards.Plain(line).Trim();
                        if (line.Length > 0) return line.Length > 120 ? line.Substring(0, 120) : line;
                    }
                }
                return filePath == null ? "Not saved yet" : "No details yet";
            }
        }

        public string DateText
        {
            get
            {
                if (filePath == null) return "";
                DateTime now = DateTime.Now;
                if (modified.Date == now.Date) return modified.ToString("t");
                return modified.Year == now.Year ? modified.ToString("d MMM") : modified.ToString("d MMM yyyy");
            }
        }

        private void Raise(params string[] properties)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler == null) return;
            foreach (string property in properties) handler(this, new PropertyChangedEventArgs(property));
        }
    }

    internal sealed class MainWindow : Window
    {
        private const int HotkeyId = 1;
        private const string AppName = "PersonalNotes";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string SettingsKeyPath = @"Software\PersonalNotes";
        private const string UntitledName = "Untitled note";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string notesDir;
        private List<Note> notes = new List<Note>();
        private readonly ObservableCollection<Note> visible = new ObservableCollection<Note>();
        private Note current;
        private bool dirty;
        private bool loadingEditor;
        private bool syncingList;
        private bool exiting;
        private bool hotkeyRegistered;
        private bool favouritesOnly;
        private bool darkTheme = true;
        private bool offlineDictation;
        private IntPtr handle;

        // Elements from MainView.xaml
        private readonly Grid root;
        private readonly TextBox titleBox, bodyBox, searchBox;
        private readonly ListBox notesList;
        private readonly Button newButton, favButton, deleteButton, micButton, pasteButton, themeButton, eyeButton, dialogOk, dialogCancel;
        private readonly RadioButton allTab, favTab;
        private readonly TextBlock metaText, statusText, countText, micIcon, micLabel, emptyTitle, emptyText, emptyIcon, dialogTitle, dialogText;
        private readonly Ellipse statusDot;
        private readonly StackPanel emptyState, pasteItems, shortcutRows;
        private readonly ScrollViewer bodyView;   // the details drawn with their cards (shown while not typing)
        private readonly TextBlock bodyText;
        private readonly Button modeButton, cardButton;
        private readonly Popup pastePopup, shortcutsPopup;
        private readonly Grid dialogLayer;
        private Action dialogConfirm;
        private DateTime pastePopupClosed, shortcutsPopupClosed;

        private WinForms.NotifyIcon tray;
        private WinForms.ToolStripMenuItem autoStartItem;
        private WinForms.ToolStripMenuItem offlineDictationItem;
        private readonly DispatcherTimer saveTimer;

        private SpeechRecognitionEngine speech;
        private bool listening;

        // Paste one by one: the texts still to paste, and which one is on the clipboard now.
        private List<string> pasteQueue;
        private int pasteIndex;
        private IntPtr keyboardHook;
        private Native.LowLevelKeyboardProc keyboardProc;
        private bool pasteKeyDown;
        private int pasteKey;
        private readonly DispatcherTimer pasteTimer;
        private PasteBar pasteBar;

        public MainWindow()
        {
            notesDir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Personal Notes");
            ReadSettings();
            Theme.Apply(darkTheme);

            Title = "Personal Notes";
            Width = 1060;
            Height = 690;
            MinWidth = 860;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // Pinned: once shown, the window stays above every other window, even while
            // you work in another program. Only Ctrl+Shift+A (or the tray icon) hides it.
            Topmost = true;
            SetResourceReference(BackgroundProperty, "BgEditor");
            Icon = MakeWindowIcon();

            // No Windows title bar: the content fills the whole window, which can still be resized at its edges.
            WindowChrome chrome = new WindowChrome();
            chrome.CaptionHeight = 0;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.GlassFrameThickness = new Thickness(-1);
            chrome.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, chrome);

            root = (Grid)Program.LoadXaml("MainView.xaml");
            Content = root;

            titleBox = Find<TextBox>("TitleBox");
            bodyBox = Find<TextBox>("BodyBox");
            searchBox = Find<TextBox>("SearchBox");
            notesList = Find<ListBox>("NotesList");
            newButton = Find<Button>("NewButton");
            favButton = Find<Button>("FavButton");
            deleteButton = Find<Button>("DeleteButton");
            micButton = Find<Button>("MicButton");
            pasteButton = Find<Button>("PasteButton");
            themeButton = Find<Button>("ThemeButton");
            eyeButton = Find<Button>("EyeButton");
            dialogOk = Find<Button>("DialogOk");
            dialogCancel = Find<Button>("DialogCancel");
            allTab = Find<RadioButton>("AllTab");
            favTab = Find<RadioButton>("FavTab");
            metaText = Find<TextBlock>("MetaText");
            statusText = Find<TextBlock>("StatusText");
            countText = Find<TextBlock>("CountText");
            micIcon = Find<TextBlock>("MicIcon");
            micLabel = Find<TextBlock>("MicLabel");
            emptyTitle = Find<TextBlock>("EmptyTitle");
            emptyText = Find<TextBlock>("EmptyText");
            emptyIcon = Find<TextBlock>("EmptyIcon");
            dialogTitle = Find<TextBlock>("DialogTitle");
            dialogText = Find<TextBlock>("DialogText");
            statusDot = Find<Ellipse>("StatusDot");
            emptyState = Find<StackPanel>("EmptyState");
            pasteItems = Find<StackPanel>("PasteItems");
            shortcutRows = Find<StackPanel>("ShortcutRows");
            pastePopup = Find<Popup>("PastePopup");
            shortcutsPopup = Find<Popup>("ShortcutsPopup");
            dialogLayer = Find<Grid>("DialogLayer");
            bodyView = Find<ScrollViewer>("BodyView");
            bodyText = Find<TextBlock>("BodyText");
            modeButton = Find<Button>("ModeButton");
            cardButton = Find<Button>("CardButton");
            modeButton.Click += delegate { ToggleBodyMode(); };
            cardButton.Click += delegate { MakeCardFromSelection(); };
            bodyView.MouseLeftButtonDown += BodyViewMouseDown;
            // When typing stops (focus goes to another program or another part of the app), show the cards.
            bodyBox.LostKeyboardFocus += delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { if (!bodyBox.IsKeyboardFocused && !ViewingCards) ShowCards(); });
            };

            notesList.ItemsSource = visible;
            if (favouritesOnly) favTab.IsChecked = true;
            UpdateThemeButton();
            BuildShortcutRows();

            // Editing
            titleBox.TextChanged += delegate { EditorChanged(); };
            bodyBox.TextChanged += delegate { EditorChanged(); };
            titleBox.LostKeyboardFocus += delegate { SaveCurrent(); ShowStoredName(); };
            searchBox.TextChanged += delegate { RebuildList(false); };
            notesList.SelectionChanged += delegate { ListSelectionChanged(); };

            // Buttons
            newButton.Click += delegate { NewNote(); };
            favButton.Click += delegate { ToggleFavourite(); };
            deleteButton.Click += delegate { AskDelete(); };
            micButton.Click += delegate { ToggleDictation(); };
            pasteButton.Click += delegate { ShowPasteMenu(); };
            themeButton.Click += delegate { ToggleTheme(); };
            eyeButton.Click += delegate { ShowShortcuts(); };
            allTab.Checked += delegate { ShowFavouritesOnly(false); };
            favTab.Checked += delegate { ShowFavouritesOnly(true); };
            dialogOk.Click += delegate { CloseDialog(true); };
            dialogCancel.Click += delegate { CloseDialog(false); };
            pastePopup.Closed += delegate { pastePopupClosed = DateTime.UtcNow; };
            shortcutsPopup.Closed += delegate { shortcutsPopupClosed = DateTime.UtcNow; };

            // The window has no title bar, so any empty area can be used to drag it.
            root.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (dialogLayer.Visibility == Visibility.Visible || e.ButtonState != MouseButtonState.Pressed) return;
                try { DragMove(); }
                catch (InvalidOperationException) { }
            };
            PreviewKeyDown += HandleKey;
            StateChanged += delegate
            {
                // A pinned window is never minimized or maximized; put it straight back.
                if (WindowState != WindowState.Normal)
                    Dispatcher.BeginInvoke((Action)delegate { WindowState = WindowState.Normal; });
            };

            saveTimer = new DispatcherTimer();
            saveTimer.Interval = TimeSpan.FromMilliseconds(800);
            saveTimer.Tick += delegate { SaveCurrent(); SortNotes(); RebuildList(true); };

            pasteTimer = new DispatcherTimer();
            pasteTimer.Tick += delegate { AfterPaste(); };

            Application.Current.SessionEnding += delegate { SaveCurrent(); };

            BuildTray();

            // Create the window handle now (without showing the window) so the
            // global hotkey works while the app sits hidden in the tray.
            handle = new WindowInteropHelper(this).EnsureHandle();
            HwndSource.FromHwnd(handle).AddHook(WindowMessage);
            hotkeyRegistered = Native.RegisterHotKey(handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.A));
            Native.StyleWindowFrame(handle, Theme.ColorOf("Border"));

            MoveOldNotesToNamedFormat();
            MoveQuickItemsIntoText();
            ApplyStartupSettings();
        }

        private T Find<T>(string name) where T : class
        {
            T element = root.FindName(name) as T;
            if (element == null) throw new InvalidOperationException("MainView.xaml has no " + typeof(T).Name + " named " + name);
            return element;
        }

        // ------------------------------------------------------------ Settings

        private void ReadSettings()
        {
            try
            {
                using (RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                {
                    darkTheme = Convert.ToInt32(settings.GetValue("DarkTheme", 1)) == 1;
                    favouritesOnly = Convert.ToInt32(settings.GetValue("FavouritesOnly", 0)) == 1;
                    offlineDictation = Convert.ToInt32(settings.GetValue("OfflineDictation", 0)) == 1;
                }
            }
            catch (Exception) { }
        }

        private static void SaveSetting(string name, int value)
        {
            try
            {
                using (RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                    settings.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch (Exception) { }
        }

        private void ApplyStartupSettings()
        {
            bool firstRun = false;
            try
            {
                using (RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                {
                    firstRun = settings.GetValue("Initialized") == null;
                    if (firstRun) settings.SetValue("Initialized", 1, RegistryValueKind.DWord);
                }
                // Start with Windows by default; afterwards only keep the stored path up to date.
                if (firstRun || IsAutoStart()) SetAutoStart(true);
            }
            catch (Exception) { }

            autoStartItem.Checked = IsAutoStart();
            offlineDictationItem.Checked = offlineDictation;

            if (!hotkeyRegistered)
            {
                tray.ShowBalloonTip(8000, "Personal Notes",
                    "Ctrl+Shift+A is already used by another program, so the shortcut is not available. Click this icon to open your notes.",
                    WinForms.ToolTipIcon.Warning);
            }
            else if (firstRun)
            {
                tray.ShowBalloonTip(6000, "Personal Notes is running",
                    "Press Ctrl+Shift+A any time to show or hide your notes.", WinForms.ToolTipIcon.Info);
            }
        }

        private static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return run != null && run.GetValue(AppName) != null;
            }
            catch (Exception) { return false; }
        }

        private static void SetAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (enabled) run.SetValue(AppName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --hidden");
                    else run.DeleteValue(AppName, false);
                }
            }
            catch (Exception) { }
        }

        // Earlier versions kept the note's name as the first line of the file. Now the
        // file name is the note's name, so that repeated first line is removed, once.
        private void MoveOldNotesToNamedFormat()
        {
            try
            {
                using (RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                {
                    if (Convert.ToInt32(settings.GetValue("NoteFormat", 1)) >= 2) return;
                    if (Directory.Exists(notesDir))
                    {
                        foreach (string file in Directory.GetFiles(notesDir, "*.txt"))
                        {
                            string text = File.ReadAllText(file);
                            int lineEnd = text.IndexOfAny(new char[] { '\r', '\n' });
                            string firstLine = lineEnd < 0 ? text : text.Substring(0, lineEnd);
                            if (firstLine.Trim() != IOPath.GetFileNameWithoutExtension(file)) continue;

                            string rest = lineEnd < 0 ? "" : text.Substring(lineEnd).TrimStart('\r', '\n');
                            DateTime stamp = File.GetLastWriteTime(file);
                            File.WriteAllText(file, rest, Utf8NoBom);
                            File.SetLastWriteTime(file, stamp);
                        }
                    }
                    settings.SetValue("NoteFormat", 2, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        // --------------------------------------------------------------- Theme

        private void ToggleTheme()
        {
            darkTheme = !darkTheme;
            Theme.Apply(darkTheme);
            SaveSetting("DarkTheme", darkTheme ? 1 : 0);
            Native.StyleWindowFrame(handle, Theme.ColorOf("Border"));
            UpdateThemeButton();
        }

        private void UpdateThemeButton()
        {
            themeButton.Content = darkTheme ? "" : "";   // sun while dark, moon while light
            themeButton.ToolTip = darkTheme ? "Switch to the light look" : "Switch to the dark look";
        }

        // ---------------------------------------------------------------- Icon

        private static Drawing.Icon MakeIcon(int size)
        {
            using (Drawing.Bitmap bitmap = new Drawing.Bitmap(size, size))
            {
                using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap))
                {
                    float u = size / 16f;
                    g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Drawing.Color.Transparent);

                    Drawing.RectangleF tile = new Drawing.RectangleF(0.5f * u, 0.5f * u, 15f * u, 15f * u);
                    float r = 7f * u;
                    using (Drawing.Drawing2D.GraphicsPath path = new Drawing.Drawing2D.GraphicsPath())
                    {
                        path.AddArc(tile.X, tile.Y, r, r, 180, 90);
                        path.AddArc(tile.Right - r, tile.Y, r, r, 270, 90);
                        path.AddArc(tile.Right - r, tile.Bottom - r, r, r, 0, 90);
                        path.AddArc(tile.X, tile.Bottom - r, r, r, 90, 90);
                        path.CloseFigure();
                        using (Drawing.Drawing2D.LinearGradientBrush fill = new Drawing.Drawing2D.LinearGradientBrush(
                            tile, Drawing.Color.FromArgb(139, 92, 246), Drawing.Color.FromArgb(59, 130, 246), 45f))
                        {
                            g.FillPath(fill, path);
                        }
                    }
                    using (Drawing.Pen pen = new Drawing.Pen(Drawing.Color.White, Math.Max(1f, 1.5f * u)))
                    {
                        pen.StartCap = pen.EndCap = Drawing.Drawing2D.LineCap.Round;
                        g.DrawLine(pen, 4.5f * u, 5.3f * u, 11.5f * u, 5.3f * u);
                        g.DrawLine(pen, 4.5f * u, 8f * u, 11.5f * u, 8f * u);
                        g.DrawLine(pen, 4.5f * u, 10.7f * u, 8.8f * u, 10.7f * u);
                    }
                }

                IntPtr iconHandle = bitmap.GetHicon();
                try
                {
                    using (Drawing.Icon temp = Drawing.Icon.FromHandle(iconHandle)) return (Drawing.Icon)temp.Clone();
                }
                finally
                {
                    Native.DestroyIcon(iconHandle);
                }
            }
        }

        private static ImageSource MakeWindowIcon()
        {
            using (Drawing.Icon icon = MakeIcon(64))
            {
                ImageSource image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
        }

        // ---------------------------------------------------------------- Tray

        private void BuildTray()
        {
            WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Show / Hide    (Ctrl+Shift+A)", null, delegate { ToggleFromTray(); });
            menu.Items.Add("New note", null, delegate { ShowNotes(); NewNote(); });
            menu.Items.Add("Open notes folder", null, delegate { OpenNotesFolder(); });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            autoStartItem = new WinForms.ToolStripMenuItem("Start with Windows");
            autoStartItem.CheckOnClick = true;
            autoStartItem.Click += delegate { SetAutoStart(autoStartItem.Checked); };
            menu.Items.Add(autoStartItem);
            offlineDictationItem = new WinForms.ToolStripMenuItem("Use offline dictation (less accurate)");
            offlineDictationItem.CheckOnClick = true;
            offlineDictationItem.Click += delegate
            {
                StopDictation(true);
                offlineDictation = offlineDictationItem.Checked;
                SaveSetting("OfflineDictation", offlineDictation ? 1 : 0);
            };
            menu.Items.Add(offlineDictationItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });

            tray = new WinForms.NotifyIcon();
            tray.Icon = MakeIcon(WinForms.SystemInformation.SmallIconSize.Width);
            tray.Text = "Personal Notes (Ctrl+Shift+A)";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseClick += delegate(object sender, WinForms.MouseEventArgs e)
            {
                if (e.Button == WinForms.MouseButtons.Left) ToggleFromTray();
            };
        }

        private void ToggleFromTray()
        {
            if (IsVisible) HideNotes(); else ShowNotes();
        }

        private void OpenNotesFolder()
        {
            try
            {
                Directory.CreateDirectory(notesDir);
                System.Diagnostics.Process.Start(notesDir);
            }
            catch (Exception ex)
            {
                SetStatus("Could not open the notes folder: " + ex.Message, "Danger");
            }
        }

        private void ExitApp()
        {
            exiting = true;
            StopPasteQueue(null);
            StopDictation(true);
            SaveCurrent();
            if (speech != null) { speech.Dispose(); speech = null; }
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }

        // ------------------------------------------------- Showing and hiding

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                if (IsVisible) HideNotes(); else ShowNotes();
                handled = true;
            }
            else if (message == Native.ShowMessage)
            {
                ShowNotes();
                handled = true;
            }
            else if (message == Native.WM_SYSCOMMAND)
            {
                // Pinned: the window cannot be closed, minimized or maximized (Alt+F4, taskbar, Win+arrows).
                int command = (int)(wParam.ToInt64() & 0xFFF0);
                if (command == Native.SC_CLOSE || command == Native.SC_MINIMIZE || command == Native.SC_MAXIMIZE) handled = true;
            }
            return IntPtr.Zero;
        }

        public void ShowNotes()
        {
            RefreshFromDisk();
            bool wasVisible = IsVisible;
            if (!wasVisible) Show();
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            Activate();
            Native.SetForegroundWindow(handle);
            FocusEditor();
            if (!wasVisible) PlayEntrance();
        }

        private void HideNotes()
        {
            StopDictation(true);
            SaveCurrent();
            pastePopup.IsOpen = false;
            shortcutsPopup.IsOpen = false;
            if (dialogLayer.Visibility == Visibility.Visible) CloseDialog(false);
            Hide();
        }

        // A quick fade and rise each time the notes appear.
        private void PlayEntrance()
        {
            TranslateTransform shift = new TranslateTransform();
            root.RenderTransform = shift;
            CubicEase ease = new CubicEase();
            ease.EasingMode = EasingMode.EaseOut;
            DoubleAnimation rise = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(240));
            rise.EasingFunction = ease;
            shift.BeginAnimation(TranslateTransform.YProperty, rise);
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        private void FocusEditor()
        {
            // A new note starts with its name; an existing one continues in the details.
            TextBox target = current != null && current.IsBlank ? titleBox : bodyBox;
            if (target == bodyBox && ViewingCards) return;   // the cards are showing, ready to be clicked
            target.Focus();
            Keyboard.Focus(target);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // The window cannot be closed by the user; it is hidden with Ctrl+Shift+A
            // and the app is quit from the tray icon's menu.
            if (!exiting) e.Cancel = true;
            base.OnClosing(e);
        }

        private void HandleKey(object sender, KeyEventArgs e)
        {
            bool control = Keyboard.Modifiers == ModifierKeys.Control;

            if (dialogLayer.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape) CloseDialog(false);
                else if (e.Key == Key.Enter) CloseDialog(true);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                // Esc only closes popups and leaves the search box; it never hides the pinned window.
                if (pastePopup.IsOpen || shortcutsPopup.IsOpen)
                {
                    pastePopup.IsOpen = false;
                    shortcutsPopup.IsOpen = false;
                }
                else if (searchBox.IsKeyboardFocused)
                {
                    if (searchBox.Text.Length > 0) searchBox.Clear();
                    else FocusEditor();
                }
                else return;
                e.Handled = true;
            }
            else if (e.Key == Key.F1)
            {
                ShowShortcuts();
                e.Handled = true;
            }
            else if (control && e.Key == Key.N) { NewNote(); e.Handled = true; }
            else if (control && e.Key == Key.F) { searchBox.Focus(); searchBox.SelectAll(); e.Handled = true; }
            else if (control && e.Key == Key.D) { ToggleFavourite(); e.Handled = true; }
            else if (control && e.Key == Key.M) { ToggleDictation(); e.Handled = true; }
            else if (control && e.Key == Key.Q) { MakeCardFromSelection(); e.Handled = true; }
            else if (control && e.Key == Key.S)
            {
                SaveCurrent();
                SortNotes();
                RebuildList(true);
                e.Handled = true;
            }
            else if ((e.Key == Key.Enter || e.Key == Key.Down) && titleBox.IsKeyboardFocused)
            {
                // Enter in the name moves on to the details.
                bodyBox.Focus();
                e.Handled = true;
            }
            else if ((e.Key == Key.Enter || e.Key == Key.Down) && searchBox.IsKeyboardFocused)
            {
                if (visible.Count > 0)
                {
                    Note first = visible[0];
                    if (notesList.SelectedItems.Count != 1 || notesList.SelectedItem != first)
                    {
                        notesList.SelectedItems.Clear();
                        notesList.SelectedItems.Add(first);
                    }
                    ListBoxItem item = notesList.ItemContainerGenerator.ContainerFromItem(first) as ListBoxItem;
                    if (item != null) item.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && notesList.IsKeyboardFocusWithin)
            {
                AskDelete();
                e.Handled = true;
            }
        }

        // -------------------------------------------------------------- Dialog

        // An in-app card instead of a Windows message box. confirm runs when the main button is chosen.
        private void ShowDialogCard(string title, string text, string okText, bool danger, bool canCancel, Action confirm)
        {
            dialogTitle.Text = title;
            dialogText.Text = text;
            dialogOk.Content = okText;
            if (danger) dialogOk.SetResourceReference(BackgroundProperty, "Danger");
            else dialogOk.ClearValue(BackgroundProperty);
            dialogCancel.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
            dialogConfirm = confirm;
            dialogLayer.Visibility = Visibility.Visible;
            dialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            // Take the keyboard away from the note so typing cannot change it behind the card.
            dialogLayer.Focusable = true;
            Keyboard.Focus(dialogLayer);
        }

        private void CloseDialog(bool confirmed)
        {
            dialogLayer.Visibility = Visibility.Collapsed;
            Action action = dialogConfirm;
            dialogConfirm = null;
            if (confirmed && action != null) action();
            FocusEditor();
        }

        // ----------------------------------------------------------- Shortcuts

        private void BuildShortcutRows()
        {
            string[,] rows =
            {
                { "Ctrl+Shift+A", "Show or hide the notes. Once shown they stay on top of everything" },
                { "Ctrl+N", "New note" },
                { "Enter", "From the note name, move to the details" },
                { "Ctrl+F", "Search by note name or details" },
                { "Ctrl+D", "Add to or remove from favourites" },
                { "Ctrl+Q", "Turn the selected text into a card (again to undo it)" },
                { "Click a card", "Copy it" },
                { "Double-click", "On a card: edit it. On the text: edit the note" },
                { "Ctrl+M", "Dictate with the microphone" },
                { "Win+H", "Windows voice typing" },
                { "Ctrl+V", "Paste the next item (during Paste one by one)" },
                { "Ctrl+click", "Select several notes in the list" },
                { "Shift+click", "Select a range of notes in the list" },
                { "Delete", "Delete the selected notes (when the list is active)" },
                { "Ctrl+S", "Save now (notes also save automatically)" },
                { "F1", "Show this list" },
            };

            for (int i = 0; i < rows.GetLength(0); i++)
            {
                Grid row = new Grid();
                row.Margin = new Thickness(0, 4, 0, 4);
                ColumnDefinition keyColumn = new ColumnDefinition();
                keyColumn.Width = new GridLength(132);
                row.ColumnDefinitions.Add(keyColumn);
                row.ColumnDefinitions.Add(new ColumnDefinition());

                TextBlock key = new TextBlock();
                key.Text = rows[i, 0];
                key.FontFamily = (FontFamily)FindResource("MonoFont");
                key.FontSize = 11.5;
                Border cap = new Border();
                cap.Style = (Style)FindResource("KeyCap");
                cap.VerticalAlignment = VerticalAlignment.Center;
                cap.Child = key;

                TextBlock what = new TextBlock();
                what.Text = rows[i, 1];
                what.FontSize = 12.5;
                what.TextWrapping = TextWrapping.Wrap;
                what.VerticalAlignment = VerticalAlignment.Center;
                what.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                Grid.SetColumn(what, 1);

                row.Children.Add(cap);
                row.Children.Add(what);
                shortcutRows.Children.Add(row);
            }
        }

        private void ShowShortcuts()
        {
            if ((DateTime.UtcNow - shortcutsPopupClosed).TotalMilliseconds < 300) return;   // the click that closed it
            shortcutsPopup.PlacementTarget = eyeButton;
            shortcutsPopup.IsOpen = true;
        }

        // --------------------------------------------------------------- Notes

        private static string NormalizeNewlines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private void SortNotes()
        {
            notes.Sort(delegate(Note a, Note b)
            {
                bool aNew = a.FilePath == null, bNew = b.FilePath == null;
                if (aNew != bNew) return aNew ? -1 : 1;
                return b.Modified.CompareTo(a.Modified);
            });
        }

        // Re-reads the notes folder so files added or edited outside the app show up.
        private void RefreshFromDisk()
        {
            SaveCurrent();
            if (dirty)
            {
                // The last save failed; keep what is in memory rather than overwrite it.
                RebuildList(false);
                return;
            }

            string keepPath = current != null ? current.FilePath : null;
            Note unsaved = current != null && current.FilePath == null ? current : null;

            HashSet<string> favourites = LoadFavouriteNames();
            List<Note> loaded = new List<Note>();
            try
            {
                if (Directory.Exists(notesDir))
                {
                    foreach (string file in Directory.GetFiles(notesDir, "*.txt"))
                    {
                        if (!string.Equals(IOPath.GetExtension(file), ".txt", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            Note note = new Note();
                            note.Name = IOPath.GetFileNameWithoutExtension(file);
                            note.Body = NormalizeNewlines(File.ReadAllText(file));
                            note.Modified = File.GetLastWriteTime(file);
                            note.Favourite = favourites.Contains(IOPath.GetFileName(file));
                            note.FilePath = file;
                            loaded.Add(note);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            notes = loaded;
            if (unsaved != null) notes.Add(unsaved);
            SortNotes();

            Note target = unsaved;
            if (target == null && keepPath != null) target = notes.Find(delegate(Note n) { return SamePath(n.FilePath, keepPath); });
            if (target == null && notes.Count > 0) target = notes[0];
            if (target == null)
            {
                target = new Note();
                notes.Add(target);
            }

            current = target;
            if (titleBox.Text != current.Name || bodyBox.Text != current.Body) LoadEditor();
            RebuildList(false);
            OpenFirstListedIfHidden();
            UpdateMeta();
            UpdateStatus();
        }

        private void LoadEditor()
        {
            loadingEditor = true;
            // Switching undo off and on empties it, so Ctrl+Z cannot bring back another note's text.
            titleBox.IsUndoEnabled = false;
            bodyBox.IsUndoEnabled = false;
            titleBox.Text = current.Name;
            bodyBox.Text = current.Body;
            titleBox.IsUndoEnabled = true;
            bodyBox.IsUndoEnabled = true;
            titleBox.CaretIndex = titleBox.Text.Length;
            bodyBox.CaretIndex = bodyBox.Text.Length;
            bodyBox.ScrollToEnd();
            loadingEditor = false;
            dirty = false;
            ShowCards();   // cards if the note has any, otherwise the editing box (also refreshes the line under the name)
        }

        // Fills the list from the search box and the All/Favourites switch. With
        // keepMultiSelection the highlighted group of notes survives the refresh;
        // otherwise only the note open in the editor ends up highlighted.
        private void RebuildList(bool keepMultiSelection)
        {
            string query = searchBox.Text.Trim();
            List<Note> wanted = new List<Note>();
            foreach (Note note in notes)
            {
                if (favouritesOnly && !note.Favourite) continue;
                if (query.Length == 0
                    || note.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || Cards.Plain(note.Body).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) wanted.Add(note);
            }

            syncingList = true;
            bool sameNotes = wanted.Count == visible.Count && wanted.All(visible.Contains);
            if (sameNotes)
            {
                // Only the order may have changed; moving keeps selection and scroll position.
                for (int i = 0; i < wanted.Count; i++)
                {
                    int at = visible.IndexOf(wanted[i]);
                    if (at != i) visible.Move(at, i);
                }
            }
            else
            {
                List<Note> selected = keepMultiSelection ? notesList.SelectedItems.Cast<Note>().ToList() : new List<Note>();
                visible.Clear();
                foreach (Note note in wanted) visible.Add(note);
                foreach (Note note in selected)
                {
                    if (wanted.Contains(note)) notesList.SelectedItems.Add(note);
                }
            }

            bool multi = keepMultiSelection && notesList.SelectedItems.Count > 1;
            if (!multi)
            {
                bool listed = current != null && wanted.Contains(current);
                bool onlyCurrent = notesList.SelectedItems.Count == (listed ? 1 : 0)
                    && (!listed || ReferenceEquals(notesList.SelectedItems[0], current));
                if (!onlyCurrent)
                {
                    notesList.SelectedItems.Clear();
                    if (listed) notesList.SelectedItems.Add(current);
                }
                if (listed && !keepMultiSelection) notesList.ScrollIntoView(current);
            }
            syncingList = false;

            UpdateEmptyState(query);
            UpdateCounts();
            UpdateFavouriteButton();
        }

        private void UpdateCounts()
        {
            int saved = notes.Count(delegate(Note n) { return n.FilePath != null; });
            int favourites = notes.Count(delegate(Note n) { return n.Favourite; });
            countText.Text = (saved == 1 ? "1 note" : saved + " notes") + "  ·  " + (favourites == 1 ? "1 favourite" : favourites + " favourites");
        }

        private void UpdateEmptyState(string query)
        {
            if (visible.Count > 0)
            {
                emptyState.Visibility = Visibility.Collapsed;
                return;
            }
            if (query.Length > 0)
            {
                emptyIcon.Text = "";
                emptyTitle.Text = "Nothing found";
                emptyText.Text = favouritesOnly ? "No favourite note matches that name." : "No note matches that name or text.";
            }
            else
            {
                emptyIcon.Text = "";
                emptyTitle.Text = "No favourites yet";
                emptyText.Text = "Open a note and press the star (Ctrl+D). Your favourites are then listed here by name.";
            }
            emptyState.Visibility = Visibility.Visible;
        }

        // The notes an action applies to: the highlighted group, or just the open note.
        private List<Note> TargetNotes()
        {
            if (notesList.SelectedItems.Count > 1)
            {
                // SelectedItems is in click order; keep the order of the list instead.
                return visible.Where(delegate(Note n) { return notesList.SelectedItems.Contains(n); }).ToList();
            }
            List<Note> targets = new List<Note>();
            if (current != null) targets.Add(current);
            return targets;
        }

        private bool SwitchTo(Note note)
        {
            SaveCurrent();
            if (dirty) return false;   // the save failed; stay on the note rather than lose its text
            // An untouched blank note is simply dropped when you move away from it.
            if (current != null && current.FilePath == null) notes.Remove(current);
            current = note;
            LoadEditor();
            return true;
        }

        // When a filter hides the open note, open the first listed note instead.
        private void OpenFirstListedIfHidden()
        {
            if (visible.Count == 0 || visible.Contains(current)) return;
            if (SwitchTo(visible[0]))
            {
                SortNotes();
                RebuildList(false);
            }
        }

        private void ListSelectionChanged()
        {
            if (syncingList) return;

            // One highlighted note opens in the editor; while several are highlighted
            // the editor keeps showing the note that was already open.
            if (notesList.SelectedItems.Count == 1)
            {
                Note selected = notesList.SelectedItem as Note;
                if (selected != null && selected != current)
                {
                    SwitchTo(selected);
                    // Refresh after this event finishes: the saved note may have moved in the order.
                    Dispatcher.BeginInvoke((Action)delegate { SortNotes(); RebuildList(true); UpdateStatus(); });
                }
            }
            UpdateStatus();
            UpdateFavouriteButton();
        }

        private void EditorChanged()
        {
            if (loadingEditor || current == null) return;
            current.Name = titleBox.Text;
            current.Body = bodyBox.Text;
            dirty = true;
            saveTimer.Stop();
            saveTimer.Start();
            UpdateModeButton();
            UpdateStatus();
        }

        private void NewNote()
        {
            SaveCurrent();
            if (dirty) return;
            if (current == null || current.FilePath != null)
            {
                current = new Note();
                notes.Add(current);
                SortNotes();
                LoadEditor();
            }
            if (favouritesOnly) allTab.IsChecked = true;   // a new note is not a favourite yet, so show all notes
            if (searchBox.Text.Length > 0) searchBox.Clear();
            RebuildList(false);
            UpdateStatus();
            titleBox.Focus();   // start with the note's name
        }

        // Deletes the open note, or every highlighted note when several are selected.
        private void AskDelete()
        {
            if (current == null) return;
            SaveCurrent();
            List<Note> targets = TargetNotes();
            if (targets.Count == 1 && targets[0].FilePath == null && targets[0].IsBlank)
            {
                FocusEditor();
                return;
            }

            string title = targets.Count == 1 ? "Delete \"" + targets[0].DisplayName + "\"?" : "Delete these " + targets.Count + " notes?";
            string text = (targets.Count == 1 ? "It" : "They") + " will be moved to the Recycle Bin, so you can still get "
                + (targets.Count == 1 ? "it" : "them") + " back from there.";
            ShowDialogCard(title, text, "Delete", true, true, delegate { DeleteNotes(targets); });
        }

        private void DeleteNotes(List<Note> targets)
        {
            int index = notes.IndexOf(current);
            bool favouritesChanged = false;
            string error = null;
            foreach (Note note in targets)
            {
                try
                {
                    if (note.FilePath != null && File.Exists(note.FilePath))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(note.FilePath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                }
                catch (Exception ex)
                {
                    error = "Could not delete: " + ex.Message;
                    break;
                }
                favouritesChanged |= note.Favourite;
                notes.Remove(note);
                if (note == current) current = null;
            }
            if (favouritesChanged) SaveFavourites();

            if (current == null)
            {
                if (notes.Count == 0) notes.Add(new Note());
                current = notes[Math.Max(0, Math.Min(index, notes.Count - 1))];
                LoadEditor();
            }
            RebuildList(false);
            OpenFirstListedIfHidden();
            if (error != null) SetStatus(error, "Danger");
            else UpdateStatus();
        }

        // Writes the open note to Documents\Personal Notes as "<note name>.txt";
        // changing the name renames the file.
        private void SaveCurrent()
        {
            saveTimer.Stop();
            if (!dirty || current == null) return;

            string name = titleBox.Text.Trim();
            string body = bodyBox.Text;
            if (current.FilePath == null && name.Length == 0 && body.Trim().Length == 0)
            {
                dirty = false;
                return;
            }

            try
            {
                Directory.CreateDirectory(notesDir);
                string wanted = ChoosePath(current, name);
                if (current.FilePath == null)
                {
                    File.WriteAllText(wanted, body, Utf8NoBom);
                    current.FilePath = wanted;
                }
                else
                {
                    File.WriteAllText(current.FilePath, body, Utf8NoBom);
                    if (!SamePath(wanted, current.FilePath))
                    {
                        File.Move(current.FilePath, wanted);
                        current.FilePath = wanted;
                        if (current.Favourite) SaveFavourites();   // favourites are stored by file name
                    }
                }
                current.Modified = DateTime.Now;
                dirty = false;
                ShowStoredName();
                UpdateMeta();
                UpdateStatus();
            }
            catch (Exception ex)
            {
                SetStatus("Could not save: " + ex.Message, "Danger");
            }
        }

        // The name as stored can differ from what was typed: Windows forbids some characters
        // in file names, and a second note with the same name gets " (2)". Show the real one,
        // but never while the name is being typed.
        private void ShowStoredName()
        {
            if (current == null || current.FilePath == null || dirty) return;
            string stored = IOPath.GetFileNameWithoutExtension(current.FilePath);
            bool wasLoading = loadingEditor;
            loadingEditor = true;
            current.Name = stored;
            if (!titleBox.IsKeyboardFocused && titleBox.Text != stored) titleBox.Text = stored;
            loadingEditor = wasLoading;
        }

        private string ChoosePath(Note note, string name)
        {
            string fileName = SafeFileName(name);
            string candidate = IOPath.Combine(notesDir, fileName + ".txt");
            for (int n = 2; ; n++)
            {
                if (note.FilePath != null && SamePath(candidate, note.FilePath)) return note.FilePath;
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
                candidate = IOPath.Combine(notesDir, fileName + " (" + n + ").txt");
            }
        }

        private static string SafeFileName(string name)
        {
            StringBuilder builder = new StringBuilder();
            char[] invalid = IOPath.GetInvalidFileNameChars();
            foreach (char c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0 || char.IsControl(c)) continue;
                builder.Append(char.IsWhiteSpace(c) ? ' ' : c);
            }
            string result = builder.ToString().Trim();
            while (result.Contains("  ")) result = result.Replace("  ", " ");
            if (result.Length > 80) result = result.Substring(0, 80);
            result = result.TrimEnd(' ', '.');
            if (result.Length == 0) return UntitledName;

            // Windows reserves a few device names (CON, NUL, COM1...) as file names.
            string stem = result.Split('.')[0].Trim().ToUpperInvariant();
            string[] reserved = { "CON", "PRN", "AUX", "NUL" };
            bool isDevice = Array.IndexOf(reserved, stem) >= 0
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]));
            return isDevice ? "Note " + result : result;
        }

        // ----------------------------------------------------- Status and meta

        private void UpdateMeta()
        {
            if (current == null || current.FilePath == null)
            {
                metaText.Text = "New note  ·  give it a name, then write the details. It is saved automatically in Documents\\Personal Notes";
                return;
            }
            DateTime when = current.Modified;
            string day = when.Date == DateTime.Now.Date ? "today"
                : when.Date == DateTime.Now.Date.AddDays(-1) ? "yesterday"
                : when.ToString(when.Year == DateTime.Now.Year ? "d MMMM" : "d MMMM yyyy");
            string edited = "Edited " + day + " at " + when.ToString("t");
            metaText.Text = ViewingCards
                ? "Click a card to copy it  ·  double-click a card or the text to edit  ·  " + edited
                : edited + "  ·  Documents\\Personal Notes\\" + IOPath.GetFileName(current.FilePath);
        }

        private void SetStatus(string text, string colourKey)
        {
            statusText.Text = text;
            statusDot.SetResourceReference(Shape.FillProperty, colourKey);
        }

        private void UpdateStatus()
        {
            if (listening) SetStatus("Listening…  speak now", "Danger");
            else if (pasteQueue != null) SetStatus("Paste one by one is on  ·  press Ctrl+V in any program", "AccentSolid");
            else if (notesList.SelectedItems.Count > 1) SetStatus(notesList.SelectedItems.Count + " notes selected", "AccentSolid");
            else if (dirty) SetStatus("Saving…", "Warn");
            else if (current == null || current.FilePath == null) SetStatus("Not saved yet", "Faint");
            else SetStatus("Saved", "Good");
        }

        // ---------------------------------------------------------- Favourites

        private string FavouritesFile
        {
            get { return IOPath.Combine(notesDir, "favourites.list"); }
        }

        // Favourites are remembered as a list of file names kept next to the notes.
        private HashSet<string> LoadFavouriteNames()
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FavouritesFile))
                {
                    foreach (string line in File.ReadAllLines(FavouritesFile))
                    {
                        if (line.Trim().Length > 0) names.Add(line.Trim());
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return names;
        }

        private void SaveFavourites()
        {
            try
            {
                List<string> names = new List<string>();
                foreach (Note note in notes)
                {
                    if (note.Favourite && note.FilePath != null) names.Add(IOPath.GetFileName(note.FilePath));
                }
                Directory.CreateDirectory(notesDir);
                File.WriteAllLines(FavouritesFile, names.ToArray(), Utf8NoBom);
            }
            catch (Exception ex)
            {
                SetStatus("Could not save favourites: " + ex.Message, "Danger");
            }
        }

        private void ToggleFavourite()
        {
            SaveCurrent();   // a brand-new note needs its file before it can be a favourite
            List<Note> targets = TargetNotes();
            targets.RemoveAll(delegate(Note n) { return n.FilePath == null; });
            if (targets.Count == 0)
            {
                SetStatus("Give the note a name first, then mark it as a favourite", "Warn");
                return;
            }

            bool allFavourite = targets.TrueForAll(delegate(Note n) { return n.Favourite; });
            foreach (Note note in targets) note.Favourite = !allFavourite;
            SaveFavourites();
            RebuildList(true);
            OpenFirstListedIfHidden();
            UpdateStatus();
        }

        private void ShowFavouritesOnly(bool only)
        {
            if (favouritesOnly == only && visible.Count > 0) return;
            favouritesOnly = only;
            SaveSetting("FavouritesOnly", only ? 1 : 0);
            if (current == null) return;   // nothing loaded yet (still starting up)
            RebuildList(false);
            OpenFirstListedIfHidden();
            UpdateStatus();
        }

        private void UpdateFavouriteButton()
        {
            List<Note> targets = TargetNotes();
            bool on = targets.Count > 0 && targets.TrueForAll(delegate(Note n) { return n.Favourite; });
            favButton.Content = on ? "" : "";
            favButton.ToolTip = on ? "Remove from favourites (Ctrl+D)" : "Add to favourites (Ctrl+D)";
            if (on) favButton.SetResourceReference(ForegroundProperty, "Gold");
            else favButton.ClearValue(ForegroundProperty);
        }

        // --------------------------------------------------------------- Cards
        //
        // A part of the details wrapped in [[ ]] is a card. While the note is not
        // being typed in, the details are drawn with every card as a coloured box
        // inside the text: one click copies it, a double-click edits it.
        // Double-clicking the text itself brings back the plain editing box.

        private bool ViewingCards
        {
            get { return bodyView.Visibility == Visibility.Visible; }
        }

        // Shows the details with their cards, or the plain editing box when there are none.
        private void ShowCards()
        {
            if (!Cards.Pattern.IsMatch(bodyBox.Text))
            {
                ShowEditor(false, -1);
                return;
            }
            RenderCards();
            bodyBox.Visibility = Visibility.Collapsed;
            bodyView.Visibility = Visibility.Visible;
            UpdateModeButton();
            UpdateMeta();
        }

        private void ShowEditor(bool focus, int caret)
        {
            bodyView.Visibility = Visibility.Collapsed;
            bodyBox.Visibility = Visibility.Visible;
            if (focus)
            {
                bodyBox.Focus();
                Keyboard.Focus(bodyBox);
                bodyBox.CaretIndex = caret < 0 ? bodyBox.Text.Length : Math.Min(caret, bodyBox.Text.Length);
            }
            UpdateModeButton();
            UpdateMeta();
        }

        // The pencil / tick button next to the note name.
        private void UpdateModeButton()
        {
            bool viewing = ViewingCards;
            modeButton.Visibility = viewing || Cards.Pattern.IsMatch(bodyBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            modeButton.Content = viewing ? "" : "";
            modeButton.ToolTip = viewing ? "Edit the text (you can also double-click it)" : "Done: show the cards";
        }

        private void ToggleBodyMode()
        {
            if (ViewingCards) ShowEditor(true, -1);
            else ShowCards();
        }

        // Builds the read view: plain text with a coloured box for every [[card]].
        private void RenderCards()
        {
            string raw = bodyBox.Text;
            bodyText.Inlines.Clear();
            int at = 0;
            foreach (Match card in Cards.Pattern.Matches(raw))
            {
                if (card.Index > at) AddPlainText(raw.Substring(at, card.Index - at), at);
                bodyText.Inlines.Add(MakeCard(card.Groups[1].Value, card.Index, card.Length));
                at = card.Index + card.Length;
            }
            if (at < raw.Length) AddPlainText(raw.Substring(at), at);
        }

        private void AddPlainText(string text, int startInNote)
        {
            Run run = new Run(text);
            run.Tag = startInNote;   // lets a double-click find the matching place in the editing box
            bodyText.Inlines.Add(run);
        }

        private InlineUIContainer MakeCard(string value, int startInNote, int lengthInNote)
        {
            TextBlock label = new TextBlock();
            label.Text = value;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.LineHeight = double.NaN;   // not the roomy line height of the surrounding text, so the box stays compact
            label.LineStackingStrategy = LineStackingStrategy.MaxHeight;

            Button card = new Button();
            card.Style = (Style)FindResource("InlineCard");
            card.Content = label;
            card.ToolTip = "Click to copy  ·  double-click to edit";

            InlineUIContainer holder = new InlineUIContainer(card);
            holder.BaselineAlignment = BaselineAlignment.Center;
            holder.Tag = startInNote;

            card.Click += delegate { CopyCard(card, value); };
            card.MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                EditCard(holder, value, startInNote, lengthInNote);
            };
            return holder;
        }

        private void CopyCard(Button card, string value)
        {
            if (!PutOnClipboard(value))
            {
                SetStatus("The clipboard is busy in another program. Try again", "Warn");
                return;
            }

            // The card turns green and the status line confirms, for a moment.
            card.Tag = "copied";
            SetStatus("Copied  ·  " + (value.Length > 40 ? value.Substring(0, 40) + "…" : value), "Good");
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1300);
            timer.Tick += delegate
            {
                timer.Stop();
                card.Tag = null;
                UpdateStatus();
            };
            timer.Start();
        }

        // Double-click on a card: it becomes a small text box. Enter keeps the change, Esc drops it.
        private void EditCard(InlineUIContainer holder, string value, int startInNote, int lengthInNote)
        {
            TextBox editor = new TextBox();
            editor.Style = (Style)FindResource("CardEditor");
            editor.Text = value;
            TextBlock.SetLineHeight(editor, double.NaN);
            TextBlock.SetLineStackingStrategy(editor, LineStackingStrategy.MaxHeight);
            holder.Child = editor;

            bool finished = false;
            Action<bool> finish = delegate(bool keep)
            {
                if (finished) return;
                finished = true;
                if (keep) ReplaceCard(startInNote, lengthInNote, editor.Text);
                ShowCards();
            };
            editor.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { e.Handled = true; finish(true); }
                else if (e.Key == Key.Escape) { e.Handled = true; finish(false); }
            };
            editor.LostKeyboardFocus += delegate { finish(true); };

            Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)delegate
            {
                editor.Focus();
                Keyboard.Focus(editor);
                editor.SelectAll();
            });
        }

        // Writes an edited card back into the note's text. Emptying a card removes it.
        private void ReplaceCard(int startInNote, int lengthInNote, string newValue)
        {
            string raw = bodyBox.Text;
            if (startInNote + lengthInNote > raw.Length) return;
            Match existing = Cards.Pattern.Match(raw, startInNote);
            if (!existing.Success || existing.Index != startInNote || existing.Length != lengthInNote) return;   // the text changed meanwhile

            newValue = newValue.Replace("[[", "").Replace("]]", "").Replace("\r", "").Replace("\n", " ").Trim();
            if (newValue == existing.Groups[1].Value) return;
            string replacement = newValue.Length > 0 ? "[[" + newValue + "]]" : "";
            bodyBox.Text = raw.Substring(0, startInNote) + replacement + raw.Substring(startInNote + lengthInNote);   // saved by the usual auto-save
        }

        // Double-click on the text (not on a card): switch to the editing box, caret where you clicked.
        private void BodyViewMouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;   // the read view is for clicking cards, not for dragging the window
            if (e.ClickCount != 2) return;

            int caret = -1;
            try
            {
                TextPointer pointer = bodyText.GetPositionFromPoint(e.GetPosition(bodyText), true);
                Run run = pointer != null ? pointer.Parent as Run : null;
                if (run != null && run.Tag is int) caret = (int)run.Tag + new TextRange(run.ContentStart, pointer).Text.Length;
            }
            catch (Exception) { }
            ShowEditor(true, caret);
        }

        // "Make card" button / Ctrl+Q: wraps the selected text in [[ ]], or unwraps the card the caret is in.
        private void MakeCardFromSelection()
        {
            if (ViewingCards)
            {
                SetStatus("Double-click the text to edit it, select a part, then press Make card", "Warn");
                return;
            }

            string text = bodyBox.Text;
            int start = bodyBox.SelectionStart;
            int length = bodyBox.SelectionLength;

            if (length == 0)
            {
                // Caret inside a card: turn that card back into ordinary text.
                foreach (Match card in Cards.Pattern.Matches(text))
                {
                    if (start < card.Index || start > card.Index + card.Length) continue;
                    bodyBox.Select(card.Index, card.Length);
                    bodyBox.SelectedText = card.Groups[1].Value;
                    bodyBox.Select(card.Index + card.Groups[1].Length, 0);
                    SetStatus("Card removed  ·  the text stays", "Good");
                    return;
                }
                SetStatus("Select the part of the text that should become a card", "Warn");
                return;
            }

            // Ignore spaces picked up at either end of the selection.
            string selected = text.Substring(start, length);
            int leading = selected.Length - selected.TrimStart().Length;
            int trailing = selected.Length - selected.TrimEnd().Length;
            start += leading;
            length -= leading + trailing;
            if (length <= 0)
            {
                SetStatus("Select the part of the text that should become a card", "Warn");
                return;
            }
            string value = text.Substring(start, length);

            if (value.IndexOfAny(new char[] { '\r', '\n' }) >= 0)
            {
                SetStatus("A card has to be on one line. Select less text", "Warn");
                return;
            }

            // Selecting an existing card (with or without its brackets) unwraps it.
            Match whole = Cards.Pattern.Match(value);
            bool isWholeCard = whole.Success && whole.Length == value.Length;
            bool isInsideCard = start >= 2 && start + length + 2 <= text.Length
                && text.Substring(start - 2, 2) == "[[" && text.Substring(start + length, 2) == "]]";
            if (isWholeCard || isInsideCard)
            {
                string inner = isWholeCard ? whole.Groups[1].Value : value;
                if (isInsideCard) bodyBox.Select(start - 2, length + 4);
                else bodyBox.Select(start, length);
                bodyBox.SelectedText = inner;
                bodyBox.Select(bodyBox.SelectionStart + inner.Length, 0);
                SetStatus("Card removed  ·  the text stays", "Good");
                return;
            }
            if (value.Contains("[[") || value.Contains("]]"))
            {
                SetStatus("That part already contains a card. Select only plain text", "Warn");
                return;
            }

            bodyBox.Select(start, length);
            bodyBox.SelectedText = "[[" + value + "]]";
            bodyBox.Select(start + length + 4, 0);
            SetStatus("Card made  ·  press the tick (or click outside the text) to see it", "Good");
        }

        // The earlier version kept these pieces in a separate "Quick copy" list
        // (quickcopy.list). They are turned into cards inside the notes, once.
        private void MoveQuickItemsIntoText()
        {
            string listFile = IOPath.Combine(notesDir, "quickcopy.list");
            if (!File.Exists(listFile)) return;
            try
            {
                Dictionary<string, List<string>> byNote = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(listFile))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    List<string> values;
                    if (!byNote.TryGetValue(line.Substring(0, tab), out values)) byNote[line.Substring(0, tab)] = values = new List<string>();
                    values.Add(line.Substring(tab + 1).Replace("\\t", "\t").Replace("\\\\", "\\"));
                }

                foreach (KeyValuePair<string, List<string>> entry in byNote)
                {
                    string file = IOPath.Combine(notesDir, entry.Key);
                    if (!File.Exists(file)) continue;
                    string original = File.ReadAllText(file);
                    string text = original;
                    foreach (string value in entry.Value)
                    {
                        // Multi-line pieces (stored with \n) cannot be cards; their text is still in the note.
                        if (value.Trim().Length == 0 || value.Contains("\\n") || value.Contains("[[") || value.Contains("]]")) continue;
                        if (text.Contains("[[" + value + "]]")) continue;

                        int at = FindOutsideCards(text, value);
                        if (at >= 0) text = text.Substring(0, at) + "[[" + value + "]]" + text.Substring(at + value.Length);
                        else text = text.TrimEnd() + Environment.NewLine + "[[" + value + "]]";
                    }
                    if (text != original)
                    {
                        DateTime stamp = File.GetLastWriteTime(file);
                        File.WriteAllText(file, text, Utf8NoBom);
                        File.SetLastWriteTime(file, stamp);
                    }
                }
                File.Delete(listFile);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        // First place the value appears that is not already inside a card, or -1.
        private static int FindOutsideCards(string text, string value)
        {
            MatchCollection cards = Cards.Pattern.Matches(text);
            int at = text.IndexOf(value, StringComparison.Ordinal);
            while (at >= 0)
            {
                bool inside = false;
                foreach (Match card in cards)
                {
                    if (at < card.Index + card.Length && at + value.Length > card.Index) { inside = true; break; }
                }
                if (!inside) return at;
                at = text.IndexOf(value, at + 1, StringComparison.Ordinal);
            }
            return -1;
        }

        // ---------------------------------------------------- Paste one by one
        //
        // Puts the first chosen text on the clipboard. Every time you then paste
        // (Ctrl+V) in any program, the next text is loaded, so a series of notes
        // or lines can be pasted one after another.

        private void ShowPasteMenu()
        {
            if ((DateTime.UtcNow - pastePopupClosed).TotalMilliseconds < 300) return;   // the click that closed it
            SaveCurrent();
            pasteItems.Children.Clear();
            pasteItems.Children.Add(MenuCaption("EACH CTRL+V PASTES THE NEXT ONE OF", new Thickness(12, 8, 12, 6)));
            AddPasteChoice("", "Selected notes", DetailsOf(TargetNotes()));
            AddPasteChoice("", "Favourite notes", DetailsOf(notes.Where(delegate(Note n) { return n.Favourite; }).ToList()));
            AddPasteChoice("", "Lines of this note", LinesOf(Cards.Plain(bodyBox.Text)));
            AddPasteChoice("", "Cards of this note", Cards.Values(bodyBox.Text));
            if (notesList.SelectedItems.Count < 2)
                pasteItems.Children.Add(MenuCaption("Tip: Ctrl+click notes in the list to select several", new Thickness(12, 8, 12, 8)));
            pastePopup.PlacementTarget = pasteButton;
            pastePopup.IsOpen = true;
        }

        private TextBlock MenuCaption(string text, Thickness margin)
        {
            TextBlock caption = new TextBlock();
            caption.Text = text;
            caption.FontSize = 10.5;
            caption.Margin = margin;
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            return caption;
        }

        private void AddPasteChoice(string glyph, string label, List<string> items)
        {
            Grid content = new Grid();
            ColumnDefinition iconColumn = new ColumnDefinition();
            iconColumn.Width = new GridLength(26);
            ColumnDefinition countColumn = new ColumnDefinition();
            countColumn.Width = GridLength.Auto;
            content.ColumnDefinitions.Add(iconColumn);
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(countColumn);

            TextBlock icon = new TextBlock();
            icon.Text = glyph;
            icon.FontFamily = (FontFamily)FindResource("IconFont");
            icon.FontSize = 14;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentSolid");

            TextBlock text = new TextBlock();
            text.Text = label;
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);

            TextBlock count = new TextBlock();
            count.Text = items.Count.ToString();
            count.FontSize = 11;
            count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Border badge = new Border();
            badge.CornerRadius = new CornerRadius(8);
            badge.Padding = new Thickness(8, 2, 8, 2);
            badge.Margin = new Thickness(16, 0, 0, 0);
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.SetResourceReference(Border.BackgroundProperty, "Hover");
            badge.Child = count;
            Grid.SetColumn(badge, 2);

            content.Children.Add(icon);
            content.Children.Add(text);
            content.Children.Add(badge);

            Button choice = new Button();
            choice.Style = (Style)FindResource("MenuRow");
            choice.Content = content;
            choice.IsEnabled = items.Count > 0;
            choice.Click += delegate
            {
                pastePopup.IsOpen = false;
                StartPasteQueue(items);
            };
            pasteItems.Children.Add(choice);
        }

        // What gets pasted for a note: its details, or its name when it has no details.
        private static List<string> DetailsOf(List<Note> source)
        {
            List<string> texts = new List<string>();
            foreach (Note note in source)
            {
                string text = note.Body.Trim().Length > 0 ? Cards.Plain(note.Body).TrimEnd() : note.Name.Trim();
                if (text.Length > 0) texts.Add(text);
            }
            return texts;
        }

        private static List<string> LinesOf(string text)
        {
            List<string> lines = new List<string>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0) lines.Add(line);
            }
            return lines;
        }

        private void StartPasteQueue(List<string> items)
        {
            StopPasteQueue(null);
            if (items.Count == 0) return;
            if (!PutOnClipboard(items[0]))
            {
                SetStatus("The clipboard is busy in another program. Try again", "Warn");
                return;
            }

            keyboardProc = KeyboardHookProc;   // kept in a field so it is not garbage collected
            keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, Native.GetModuleHandle(null), 0);
            if (keyboardHook == IntPtr.Zero)
            {
                SetStatus("Could not start paste one by one (Windows refused the keyboard hook)", "Danger");
                return;
            }

            pasteQueue = items;
            pasteIndex = 0;
            pasteKeyDown = false;

            pasteBar = new PasteBar();
            pasteBar.BackClicked += delegate { MovePaste(-1); };
            pasteBar.SkipClicked += delegate { MovePaste(1); };
            pasteBar.StopClicked += delegate { StopPasteQueue(null); };
            pasteBar.SetProgress(pasteIndex, pasteQueue.Count, pasteQueue[pasteIndex]);
            pasteBar.Show();

            // The notes window stays pinned on top; click the other program and paste there.
            UpdateStatus();
        }

        private void StopPasteQueue(string farewell)
        {
            if (pasteQueue == null) return;
            pasteQueue = null;
            pasteTimer.Stop();
            if (keyboardHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(keyboardHook);
                keyboardHook = IntPtr.Zero;
            }
            if (pasteBar != null)
            {
                if (farewell != null) pasteBar.Finish(farewell);   // shows the message briefly, then closes itself
                else pasteBar.Close();
                pasteBar = null;
            }
            if (!exiting) UpdateStatus();
        }

        private static bool PutOnClipboard(string text)
        {
            try
            {
                WinForms.Clipboard.SetDataObject(text, true, 10, 50);
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        // Watches for the paste shortcut (Ctrl+V, Ctrl+Shift+V or Shift+Insert). The key
        // itself is passed on untouched; a moment later the next text is loaded.
        private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && pasteQueue != null)
            {
                int message = wParam.ToInt32();
                int key = Marshal.ReadInt32(lParam);
                bool down = message == Native.WM_KEYDOWN || message == Native.WM_SYSKEYDOWN;
                bool up = message == Native.WM_KEYUP || message == Native.WM_SYSKEYUP;

                if (down && !pasteKeyDown && IsPasteShortcut(key))
                {
                    pasteKeyDown = true;
                    pasteKey = key;
                    RestartPasteTimer(500);   // fallback in case the key is held down for long
                }
                else if (up && pasteKeyDown && key == pasteKey)
                {
                    pasteKeyDown = false;
                    if (pasteTimer.IsEnabled) RestartPasteTimer(150);   // give the program time to read the clipboard
                }
            }
            return Native.CallNextHookEx(keyboardHook, code, wParam, lParam);
        }

        private static bool IsPasteShortcut(int key)
        {
            if (Native.IsKeyDown(Native.VK_MENU) || Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN)) return false;
            bool control = Native.IsKeyDown(Native.VK_CONTROL);
            bool shift = Native.IsKeyDown(Native.VK_SHIFT);
            return (key == Native.VK_V && control) || (key == Native.VK_INSERT && shift && !control);
        }

        private void RestartPasteTimer(int milliseconds)
        {
            pasteTimer.Stop();
            pasteTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            pasteTimer.Start();
        }

        private void AfterPaste()
        {
            pasteTimer.Stop();
            if (pasteQueue == null) return;

            // If you copied something else in the meantime, stop instead of overwriting it.
            bool stillOurs = true;
            try
            {
                stillOurs = WinForms.Clipboard.ContainsText() && WinForms.Clipboard.GetText() == pasteQueue[pasteIndex];
            }
            catch (ExternalException) { }
            if (!stillOurs)
            {
                StopPasteQueue("Stopped: something else was copied");
                return;
            }
            MovePaste(1);
        }

        private void MovePaste(int step)
        {
            if (pasteQueue == null) return;
            int next = Math.Max(0, pasteIndex + step);
            if (next >= pasteQueue.Count)
            {
                StopPasteQueue("Done  ·  all " + pasteQueue.Count + " pasted");
                return;
            }
            if (!PutOnClipboard(pasteQueue[next]))
            {
                StopPasteQueue("Stopped: the clipboard is busy");
                return;
            }
            pasteIndex = next;
            pasteBar.SetProgress(pasteIndex, pasteQueue.Count, pasteQueue[pasteIndex]);
        }

        // ----------------------------------------------------------- Dictation

        // The text box dictation goes into: the note name if you are in it, otherwise the details.
        private TextBox DictationTarget()
        {
            if (titleBox.IsKeyboardFocused) return titleBox;
            if (ViewingCards) ShowEditor(true, -1);   // dictation types into the editing box
            return bodyBox;
        }

        // By default the mic uses Windows voice typing (accurate, follows the keyboard
        // language, needs internet). The tray menu can switch to the offline engine below.
        private void ToggleDictation()
        {
            if (listening)
            {
                StopDictation(false);
            }
            else if (offlineDictation)
            {
                StartDictation();
            }
            else
            {
                TextBox target = DictationTarget();
                target.Focus();
                Keyboard.Focus(target);
                Native.PressVoiceTypingShortcut();
                SetStatus("Voice typing: speak when the Windows microphone appears", "AccentSolid");
            }
        }

        private void StartDictation()
        {
            try
            {
                if (speech == null)
                {
                    RecognizerInfo recognizer = PickRecognizer();
                    if (recognizer == null)
                    {
                        ShowDialogCard("No speech language installed",
                            "Windows has no offline speech recognition language. Add one in Settings > Time & language > Language & region, "
                            + "or turn off \"Use offline dictation\" in the tray menu to use Windows voice typing instead.",
                            "OK", false, false, null);
                        return;
                    }
                    SpeechRecognitionEngine engine = new SpeechRecognitionEngine(recognizer);
                    engine.LoadGrammar(new DictationGrammar());
                    engine.SpeechRecognized += delegate(object s, SpeechRecognizedEventArgs e)
                    {
                        string spoken = e.Result.Text;
                        Dispatcher.BeginInvoke((Action)delegate { InsertDictated(spoken); });
                    };
                    engine.SpeechHypothesized += delegate(object s, SpeechHypothesizedEventArgs e)
                    {
                        string partial = e.Result.Text;
                        Dispatcher.BeginInvoke((Action)delegate { if (listening) SetStatus("Listening…  " + partial, "Danger"); });
                    };
                    speech = engine;
                }

                speech.SetInputToDefaultAudioDevice();
                speech.RecognizeAsync(RecognizeMode.Multiple);
                listening = true;
                DictationTarget();   // make sure the editing box is showing
            }
            catch (Exception ex)
            {
                listening = false;
                if (speech != null) { speech.Dispose(); speech = null; }
                ShowDialogCard("Could not start the microphone",
                    ex.Message + "\n\nCheck that a microphone is connected and that Settings > Privacy & security > Microphone allows desktop apps to use it.",
                    "OK", false, false, null);
            }
            UpdateMicButton();
            UpdateStatus();
        }

        private void StopDictation(bool discardPending)
        {
            if (!listening) return;
            listening = false;
            try
            {
                if (discardPending) speech.RecognizeAsyncCancel();
                else speech.RecognizeAsyncStop();   // lets the sentence being spoken finish
            }
            catch (Exception) { }
            UpdateMicButton();
            UpdateStatus();
        }

        private static RecognizerInfo PickRecognizer()
        {
            RecognizerInfo first = null;
            string preferred = CultureInfo.CurrentUICulture.Name;
            foreach (RecognizerInfo info in SpeechRecognitionEngine.InstalledRecognizers())
            {
                if (first == null) first = info;
                if (string.Equals(info.Culture.Name, preferred, StringComparison.OrdinalIgnoreCase)) return info;
            }
            return first;
        }

        private void InsertDictated(string spoken)
        {
            if (string.IsNullOrEmpty(spoken) || !IsVisible) return;

            TextBox box = DictationTarget();
            string text = box.Text;
            int caret = box.SelectionStart;
            bool atStart = caret == 0;
            char previous = atStart ? '\n' : text[caret - 1];

            int back = caret;
            while (back > 0 && (text[back - 1] == ' ' || text[back - 1] == '\t')) back--;
            bool newSentence = back == 0 || "\n.!?".IndexOf(text[back - 1]) >= 0;

            if (newSentence && char.IsLower(spoken[0])) spoken = char.ToUpper(spoken[0]) + spoken.Substring(1);
            if (!atStart && !char.IsWhiteSpace(previous)) spoken = " " + spoken;

            box.SelectedText = spoken;
            box.CaretIndex = box.SelectionStart + box.SelectionLength;   // continue after the inserted words
            if (listening) UpdateStatus();
        }

        private void UpdateMicButton()
        {
            if (listening)
            {
                micLabel.Text = "Stop";
                micIcon.Text = "";
                micButton.SetResourceReference(BackgroundProperty, "Danger");
                micButton.Foreground = Brushes.White;
            }
            else
            {
                micLabel.Text = "Dictate";
                micIcon.Text = "";
                micButton.ClearValue(BackgroundProperty);
                micButton.ClearValue(ForegroundProperty);
            }
        }
    }

    // Small floating panel shown while "paste one by one" is running. It never takes
    // keyboard focus, so your pastes keep going to the program you are working in.
    internal sealed class PasteBar : Window
    {
        private const double TrackWidth = 366;   // inner width of the panel in PasteBar.xaml

        private readonly TextBlock header;
        private readonly TextBlock counter;
        private readonly TextBlock preview;
        private readonly Border progress;
        private readonly FrameworkElement details;

        public event Action BackClicked;
        public event Action SkipClicked;
        public event Action StopClicked;

        public PasteBar()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;

            FrameworkElement view = (FrameworkElement)Program.LoadXaml("PasteBar.xaml");
            Content = view;
            header = (TextBlock)view.FindName("Header");
            counter = (TextBlock)view.FindName("Counter");
            preview = (TextBlock)view.FindName("Preview");
            progress = (Border)view.FindName("Progress");
            details = (FrameworkElement)view.FindName("Details");
            ((Button)view.FindName("BackButton")).Click += delegate { if (BackClicked != null) BackClicked(); };
            ((Button)view.FindName("SkipButton")).Click += delegate { if (SkipClicked != null) SkipClicked(); };
            ((Button)view.FindName("StopButton")).Click += delegate { if (StopClicked != null) StopClicked(); };

            // Sits in the bottom-right corner of the screen, also after it changes size.
            SizeChanged += delegate
            {
                Rect area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth;
                Top = area.Bottom - ActualHeight;
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Native.AddExtendedStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        }

        public void SetProgress(int index, int count, string text)
        {
            string firstLine = text;
            int lineEnd = text.IndexOfAny(new char[] { '\r', '\n' });
            if (lineEnd >= 0) firstLine = text.Substring(0, lineEnd) + " …";
            header.Text = "Next paste";
            counter.Text = (index + 1) + " of " + count;
            preview.Text = firstLine;

            DoubleAnimation grow = new DoubleAnimation(TrackWidth * index / count, TimeSpan.FromMilliseconds(220));
            progress.BeginAnimation(WidthProperty, grow);
        }

        // Shows a closing message for a moment, then closes.
        public void Finish(string message)
        {
            header.Text = message;
            counter.Text = "";
            details.Visibility = Visibility.Collapsed;
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(2500);
            timer.Tick += delegate { timer.Stop(); Close(); };
            timer.Start();
        }
    }
}
