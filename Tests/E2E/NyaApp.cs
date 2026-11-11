using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Xunit;

namespace VideoDownloader.Tests.E2E;

sealed class NyaApp : IDisposable
{
    public static string Exe => Environment.GetEnvironmentVariable("NYA_EXE") is { Length: > 0 } e ? e
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\publish\NyaDownloader.exe"));
    public static string LogPath(string name) => Path.Combine(Path.GetDirectoryName(Exe)!, name);

    public readonly TempDir Data = new(), Out = new();
    public Process Process { get; }
    public IntPtr Handle { get; }
    public AutomationElement Root { get; }
    readonly IntPtr _userWindow = GetForegroundWindow();
    readonly System.Threading.Timer _watchdog;
    public bool EverForeground { get; private set; }
    public bool StartedMinimized { get; }
    int _msSinceUserInput = int.MaxValue;

    static NyaApp() => SetProcessDpiAwarenessContext(-4);

    public const string NoUpdates = "http://127.0.0.1:9/no-updates";

    public NyaApp(Action<Settings>? seed = null, bool waitForStartup = true, (string Name, string Value)[]? env = null, string? exe = null)
    {
        exe ??= Exe;
        Assert.True(File.Exists(exe), $"Publish the app first (dotnet publish -c Release -o publish): {exe}");
        var settings = new Settings { OutputDir = Out.Path, MaxHeight = 360 };
        seed?.Invoke(settings);
        File.WriteAllText(Path.Combine(Data.Path, "settings.json"), JsonSerializer.Serialize(settings));

        LockSetForegroundWindow(1);
        Process = StartMinimizedNoActivate(exe, [("NYADOWNLOADER_DATA", Data.Path), ("NYADOWNLOADER_UPDATES_URL", NoUpdates), ("NYADOWNLOADER_STILL", "1"), ("NYADOWNLOADER_QUIET", "1"), .. env ?? []]);
        _watchdog = new System.Threading.Timer(_ => GuardFocus(), null, 0, 50);
        try
        {
            for (int i = 0; i < 100 && Process.MainWindowHandle == IntPtr.Zero; i++) { Thread.Sleep(100); Process.Refresh(); }
            Handle = Process.MainWindowHandle;
            Assert.NotEqual(IntPtr.Zero, Handle);
            StartedMinimized = IsIconic(Handle);
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            var work = System.Windows.Forms.SystemInformation.WorkingArea;
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(Handle, ref wp);
            int w = wp.normal.Right - wp.normal.Left, h = wp.normal.Bottom - wp.normal.Top;
            int x = screen.Left + 1 - w - work.Left;
            wp.normal = new RECT { Left = x, Top = 100, Right = x + w, Bottom = 100 + h };
            wp.showCmd = 4;
            SetWindowPlacement(Handle, ref wp);
            SetWindowPos(Handle, 0, screen.Left - w - 200, screen.Top + 100, 0, 0, 0x0001 | 0x0004 | 0x0010);
            Root = AutomationElement.FromHandle(Handle);
            if (waitForStartup) WaitFor(() => Text("engine").Length > 0, 30, "the yt-dlp check at startup");
        }
        catch
        {
            try { Process.Kill(entireProcessTree: true); } catch { }
            _watchdog.Dispose();
            Data.Dispose();
            Out.Dispose();
            throw;
        }
    }

    public AutomationElement? Find(string id) =>
        Root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    public AutomationElement El(string id) => Find(id) ?? throw new InvalidOperationException($"no control '{id}'");
    public AutomationElement ByName(string name) =>
        Root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))
        ?? throw new InvalidOperationException($"nothing named '{name}'");

    public string Text(string id) => El(id).Current.Name;
    public bool Shown(string id) => Find(id) is { } e && e.Current.BoundingRectangle.Width > 0;
    public bool Enabled(string id) => El(id).Current.IsEnabled;
    public string Value(string id) => ((ValuePattern)El(id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    public void SetValue(string id, string value)
    {
        SendMessage(Hwnd(El(id)), 0x000C, 0, value);
        Thread.Sleep(300);
    }
    public void Press(string id)
    {
        var h = Hwnd(El(id));
        PostMessage(h, 0x0100, 0x20, 0x00390001);
        PostMessage(h, 0x0101, 0x20, unchecked((nint)0xC0390001));
        Thread.Sleep(400);
    }
    public IntPtr WaitForWindow(string title, double seconds)
    {
        var h = IntPtr.Zero;
        WaitFor(() => (h = FindWindow(null, title)) != IntPtr.Zero && IsWindowVisible(h), seconds, $"the \"{title}\" window");
        return h;
    }

    public static AutomationElement In(IntPtr window, string id) =>
        AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
        ?? throw new InvalidOperationException($"no control '{id}'");

    public static void Key(IntPtr hwnd, int vk)
    {
        uint scan = MapVirtualKey((uint)vk, 0);
        nint extended = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 ? 1 << 24 : 0;
        PostMessage(hwnd, 0x0100, vk, 1 | (nint)(scan << 16) | extended);
        PostMessage(hwnd, 0x0101, vk, unchecked(1 | (nint)(scan << 16) | extended | (nint)0xC0000000));
        Thread.Sleep(150);
    }

    public static void PressIn(IntPtr window, string id) => Key(Hwnd(In(window, id)), 0x20);

    public bool IsSelected(string id) => ((SelectionItemPattern)El(id).GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;
    public bool IsChecked(string id) => ((TogglePattern)El(id).GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
    public string Quality => Value("quality");

    void GuardFocus()
    {
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out var pid);
        if (pid != Process.Id) return;
        EverForeground = true;
        var input = new LASTINPUTINFO { cbSize = Marshal.SizeOf<LASTINPUTINFO>() };
        if (GetLastInputInfo(ref input)) _msSinceUserInput = Math.Min(_msSinceUserInput, Environment.TickCount - (int)input.dwTime);
        SetForegroundWindow(_userWindow);
    }

    public void WaitFor(Func<bool> done, double seconds, string what)
    {
        var until = DateTime.Now.AddSeconds(seconds);
        while (true)
        {
            try { if (done()) return; }
            catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { }
            if (DateTime.Now > until) throw new TimeoutException($"waited {seconds}s for {what}; status: {SafeStatus()}");
            Thread.Sleep(250);
        }
    }

    string SafeStatus() { try { return Text("status"); } catch { return "?"; } }

    public string Download(string url, double seconds = 300)
    {
        SetValue("url", url);
        Press("download");
        WaitFor(() => Enabled("cancel"), 10, "the download to start");
        WaitFor(() => !Enabled("cancel") && Enabled("download"), seconds, "the download to finish");
        Thread.Sleep(300);
        return Text("status");
    }

    public Bitmap Screenshot(IntPtr window = default)
    {
        if (window == IntPtr.Zero) window = Handle;
        GetWindowRect(window, out var r);
        var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        PrintWindow(window, hdc, 0);
        g.ReleaseHdc(hdc);
        return bmp;
    }

    public Rectangle RectInWindow(AutomationElement e)
    {
        GetWindowRect(Handle, out var w);
        var b = e.Current.BoundingRectangle;
        return new Rectangle((int)b.X - w.Left, (int)b.Y - w.Top, (int)b.Width, (int)b.Height);
    }

    public static IntPtr Hwnd(AutomationElement e) => new(e.Current.NativeWindowHandle);

    public static void Click(IntPtr hwnd)
    {
        PostMessage(hwnd, 0x0201, 1, 10 | (10 << 16));
        PostMessage(hwnd, 0x0202, 0, 10 | (10 << 16));
        Thread.Sleep(500);
    }

    public bool CloseAndWait(double seconds = 10)
    {
        PostMessage(Handle, 0x0010, 0, 0);
        return Process.WaitForExit(TimeSpan.FromSeconds(seconds));
    }

    public void Dispose()
    {
        try { if (!Process.HasExited && !CloseAndWait()) Process.Kill(entireProcessTree: true); } catch { }
        _watchdog.Dispose();
        Data.Dispose();
        Out.Dispose();
        Assert.False(EverForeground, $"the app took the keyboard focus from the user ({_msSinceUserInput} ms after the last keyboard/mouse input)");
    }

    static Process StartMinimizedNoActivate(string exe, (string Name, string Value)[] env)
    {
        var vars = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in env) vars[name] = value;
        var block = string.Concat(vars.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}\0")) + "\0";
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = 1, wShowWindow = 7 };
        if (!CreateProcess(exe, new StringBuilder($"\"{exe}\""), 0, 0, false, 0x400, block,
                           Path.GetDirectoryName(exe), ref si, out var pi))
            throw new System.ComponentModel.Win32Exception();
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return Process.GetProcessById(pi.dwProcessId);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, StringBuilder cmd, nint pa, nint ta, bool inherit, int flags, string env, string? dir,
                                     ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern nint SendMessage(IntPtr h, int msg, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint SendMessage(IntPtr h, int msg, nint w, string l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] static extern bool LockSetForegroundWindow(uint code);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT normal; }
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public int cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
}
