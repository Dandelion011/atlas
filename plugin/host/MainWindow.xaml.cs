using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace ColorWitch.Sai2;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xC0DE;
    private readonly Sai2Bridge _bridge = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private IntPtr _hwnd;
    private IntPtr _sai2Hwnd;
    private Native.WinEventDelegate? _winEvent;
    private IntPtr _hook;
    private string? _lastColor;
    private string? _bridgeError;
    private bool _webReady;
    private bool _attached = true;
    private int _panelWidth = 380;
    private string? _pageUrl;
    private string? _embedCss;
    private string? _embedJs;
    private readonly bool _offscreen = Environment.GetEnvironmentVariable("CW_OFFSCREEN") == "1";

    public MainWindow()
    {
        InitializeComponent();
        _bridge.LogSink = Log;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        if (_offscreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -9000;
            Top = -9000;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left + 16, area.Right - Width - 16);
            Top = area.Top + 48;
        }
    }

    // ---------- assets ----------

    private static string? FindUpwards(string fileName, int maxLevels = 6)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < maxLevels && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private bool LoadAssets()
    {
        var index = FindUpwards("index.html");
        var css = FindUpwards("embed.css");
        var js = FindUpwards("embed.js");
        if (index is null || css is null || js is null)
        {
            MessageBox.Show(
                "找不到 ColorWitch 页面资源（index.html / embed.css / embed.js）。\n" +
                "请把插件目录完整安装（index.html 与 embed.* 需和 ColorWitch.Sai2.exe 同级，或在仓库里直接运行）。",
                "ColorWitch for SAI2", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _pageUrl = new Uri(index).AbsoluteUri;
        _embedCss = File.ReadAllText(css);
        _embedJs = File.ReadAllText(js);
        return true;
    }

    // ---------- window plumbing ----------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.ExStyleOf(_hwnd);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex | Native.WS_EX_TOOLWINDOW));

        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);
        Native.RegisterHotKey(_hwnd, HotkeyId, 0x0002 | 0x0001, 0x43); // Ctrl+Alt+C

        _winEvent = OnWinEvent;
        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEvent, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleVisible();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_attached && hwnd == _sai2Hwnd)
            FollowSai2();
    }

    private static IEnumerable<string> CandidateUserDataFolders()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ColorWitch.Sai2", "WebView2");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ColorWitch.Sai2", "WebView2");
        yield return Path.Combine(AppContext.BaseDirectory, "webview-data");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log("start baseDir=" + AppContext.BaseDirectory);
        if (Environment.GetEnvironmentVariable("CW_DEBUG") == "1")
        {
            if (!LoadAssets())
            {
                Application.Current.Shutdown(1);
                return;
            }
            Log("index=" + _pageUrl);
        }
        else if (!LoadAssets())
        {
            Application.Current.Shutdown(1);
            return;
        }

        try
        {
            CoreWebView2Environment? env = null;
            Exception? lastError = null;
            foreach (var userData in CandidateUserDataFolders())
            {
                try
                {
                    Directory.CreateDirectory(userData);
                    env = await CoreWebView2Environment.CreateAsync(null, userData);
                    Log("webview2 user data: " + userData);
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Log("webview2 init failed at " + userData + ": " + ex.Message);
                }
            }
            if (env is null)
                throw lastError ?? new InvalidOperationException("no user data folder");

            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Web.CoreWebView2.Navigate(_pageUrl + "?embed=1");
        }
        catch (Exception ex)
        {
            MessageBox.Show("WebView2 初始化失败：" + ex.Message, "ColorWitch for SAI2");
            Application.Current.Shutdown(1);
            return;
        }

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "ColorWitch.Sai2.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Log($"NavigationCompleted success={e.IsSuccess} status={e.HttpStatusCode} webError={e.WebErrorStatus}");
        if (!e.IsSuccess || _embedCss is null || _embedJs is null)
        {
            SetStatus("页面加载失败", null);
            return;
        }

        var css = JsonSerializer.Serialize(_embedCss);
        var prelude =
            "(function(){try{" +
            "window.__cwLog=function(m){try{window.chrome.webview.postMessage({type:'cw:log',text:String(m)})}catch(e){}};" +
            "window.addEventListener('error',function(e){window.__cwLog('error: '+e.message+' @'+e.filename+':'+e.lineno)});" +
            "var oe=console.error,ow=console.warn;" +
            "console.error=function(){window.__cwLog('console.error: '+Array.prototype.join.call(arguments,' '));oe.apply(console,arguments)};" +
            "console.warn=function(){window.__cwLog('console.warn: '+Array.prototype.join.call(arguments,' '));ow.apply(console,arguments)};" +
            "}catch(e){}})();";
        await Web.CoreWebView2.ExecuteScriptAsync(prelude);
        var script =
            "(function(){try{" +
            "var s=document.createElement('style');s.id='cw-embed-style';s.textContent=" + css + ";" +
            "(document.head||document.documentElement).appendChild(s);" +
            "}catch(e){window.__cwLog('embed css: '+e.message);}})();";
        await Web.CoreWebView2.ExecuteScriptAsync(script);
        await Web.CoreWebView2.ExecuteScriptAsync(_embedJs);
        _webReady = true;
        PushState();
        PushColor(force: true);

        try
        {
            var probe = await Web.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({" +
                "cls:document.documentElement.className," +
                "style:!!document.getElementById('cw-embed-style')," +
                "bar:!!document.getElementById('cwBar')," +
                "ids:['deck','panel','cmdHex','hexIn','swatch','histRow'].map(function(i){return i+':'+!!document.getElementById(i)}).join(',')," +
                "title:document.title," +
                "text:(document.body?document.body.innerText.length:-1)," +
                "canvas:document.querySelectorAll('canvas').length," +
                "url:location.href})");
            Log("probe " + probe);
        }
        catch (Exception ex)
        {
            Log("probe failed " + ex.Message);
        }

        _ = SavePreviewAsync();
        if (Environment.GetEnvironmentVariable("CW_SELFTEST") == "1")
            _ = RunSelfTestAsync();
    }

    /// <summary>CW_SELFTEST=1: exercise the host ↔ panel bridge without touching SAI2.</summary>
    private async Task RunSelfTestAsync()
    {
        try
        {
            await Task.Delay(800);
            var r1 = await Web.CoreWebView2.ExecuteScriptAsync(
                "window.__cwFromHost({type:'sai2:state',attached:true,visible:true,bridge:'ok'});" +
                "window.__cwFromHost({type:'sai2:color',hex:'#3366CC'});" +
                "JSON.stringify({cmdHex:document.getElementById('cmdHex').textContent," +
                "hexIn:document.getElementById('hexIn').value," +
                "attach:document.getElementById('cwAttach').textContent})");
            Log("selftest push " + r1);

            var r2 = await Web.CoreWebView2.ExecuteScriptAsync(
                "document.getElementById('cwApply').click(); 'clicked'");
            Log("selftest apply " + r2);

            await Task.Delay(700);
            var r3 = await Web.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({status:document.getElementById('cwStatus').title," +
                "cmdHex:document.getElementById('cmdHex').textContent})");
            Log("selftest after apply " + r3);

            await Task.Delay(300);
            PayloadCapture();
        }
        catch (Exception ex)
        {
            Log("selftest failed " + ex.Message);
        }
    }

    private void PayloadCapture()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "panel-capture-selftest.png");
            using var fs = File.Create(path);
            var task = Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            task.ContinueWith(_ => Log("selftest preview " + path));
        }
        catch (Exception ex)
        {
            Log("selftest preview failed " + ex.Message);
        }
    }

    private async Task SavePreviewAsync()
    {
        if (Environment.GetEnvironmentVariable("CW_DEBUG") != "1") return;
        try
        {
            await Task.Delay(1500);
            var path = Path.Combine(AppContext.BaseDirectory, "panel-capture.png");
            using var fs = File.Create(path);
            await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            Log("preview saved " + path);
        }
        catch (Exception ex)
        {
            Log("preview failed " + ex.Message);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
    }

    // ---------- SAI2 attach / follow ----------

    private void Tick()
    {
        var target = _bridge.Find();
        if (target is null)
        {
            _sai2Hwnd = IntPtr.Zero;
            if (_attached) Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, IntPtr.Zero);
            SetStatus("未运行", null);
            return;
        }

        if (_sai2Hwnd != target.Hwnd)
        {
            _sai2Hwnd = target.Hwnd;
            if (_attached && !_offscreen)
                Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, _sai2Hwnd);
        }

        SetStatus(_bridgeError ?? (_bridge.Status == BridgeStatus.Ok ? "已连接" : _bridge.Detail),
            _bridgeError is not null ? "#B3261E" : _bridge.Status == BridgeStatus.Ok ? "#1F51FF" : "#C24A32");

        if (_offscreen)
        {
            PushColor();
            return;
        }

        FollowSai2();
        PushColor();
    }

    private void FollowSai2()
    {
        if (_sai2Hwnd == IntPtr.Zero || !Native.IsWindow(_sai2Hwnd)) return;

        if (!Native.IsWindowVisible(_sai2Hwnd) || Native.IsIconic(_sai2Hwnd))
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();

        if (!_attached) return;

        Native.GetClientRect(_sai2Hwnd, out var client);
        var origin = new Native.POINT { X = 0, Y = 0 };
        Native.ClientToScreen(_sai2Hwnd, ref origin);

        var dpi = VisualTreeHelper.GetDpi(this);
        int widthPx = (int)Math.Round(_panelWidth * dpi.DpiScaleX);
        int heightPx = (int)Math.Round(Math.Max(480, client.Bottom / dpi.DpiScaleY - 16) * dpi.DpiScaleY);
        int x = origin.X + client.Right - widthPx - 8;
        int y = origin.Y + 8;
        if (heightPx > client.Bottom - 16) heightPx = client.Bottom - 16;

        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, widthPx, heightPx,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_SHOWWINDOW);
    }

    private void SetAttached(bool attached)
    {
        if (_offscreen) return;
        _attached = attached;
        AttachButton.Content = attached ? "浮动" : "贴靠";
        Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT,
            attached ? _sai2Hwnd : IntPtr.Zero);
        if (attached)
        {
            FollowSai2();
        }
        else if (_sai2Hwnd != IntPtr.Zero)
        {
            Native.GetWindowRect(_sai2Hwnd, out var r);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Right - 400, r.Top + 40, _panelWidth, 820,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }
        PushState();
    }

    private void ToggleVisible()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            if (_attached) FollowSai2();
        }
        PushState();
    }

    // ---------- bridge protocol ----------

    private void SetStatus(string text, string? dot)
    {
        StatusText.Text = "SAI2 · " + text;
        StatusText.ToolTip = text.Length > 12 ? text : null;
        if (dot is not null)
            StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(dot)!;
        else
            StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString("#D9D9D6")!;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement msg;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            msg = doc.RootElement.Clone();
        }
        catch
        {
            return;
        }

        var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "cw:log")
        {
            Log("js: " + (msg.TryGetProperty("text", out var lt) ? lt.GetString() : ""));
            return;
        }
        if (type is not null && type != "app:resize")
        {
            var extra = msg.TryGetProperty("hex", out var hx) ? " " + hx.GetString() : "";
            var live = msg.TryGetProperty("live", out var lv) ? " live=" + lv.GetBoolean() : "";
            Log($"js->host {type}{extra}{live}");
        }
        switch (type)
        {
            case "app:apply":
            {
                var hex = msg.TryGetProperty("hex", out var h) ? h.GetString() : null;
                if (hex is null) return;
                var live = msg.TryGetProperty("live", out var lv) && lv.GetBoolean();
                var ok = _bridge.WriteColor(hex, copyOnFailure: !live);
                PostAck(ok, hex);
                if (ok) PushColor(force: true);
                break;
            }
            case "app:pick":
                PushColor(force: true);
                break;
            case "app:toggleAttach":
                SetAttached(!_attached);
                break;
            case "app:close":
                Close();
                break;
            case "app:resize":
                if (msg.TryGetProperty("width", out var w) && w.TryGetInt32(out var width))
                {
                    var clamped = Math.Clamp(width, 340, 520);
                    if (Math.Abs(clamped - _panelWidth) > 6)
                    {
                        _panelWidth = clamped;
                        if (_attached) FollowSai2();
                    }
                }
                break;
        }
    }

    private void PostToJs(object payload)
    {
        if (!_webReady) return;
        var json = JsonSerializer.Serialize(payload);
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            "window.__cwFromHost&&window.__cwFromHost(" + json + ")");
    }

    private void PushState() => PostToJs(new
    {
        type = "sai2:state",
        attached = _attached,
        visible = IsVisible,
        bridge = _bridgeError is not null
            ? "error"
            : _bridge.Status switch
            {
                BridgeStatus.Ok => "ok",
                BridgeStatus.Unsupported => "unsupported",
                BridgeStatus.NotFound => "error",
                _ => "error",
            },
    });

    private void PushColor(bool force = false)
    {
        if (!_webReady) return;
        var hex = _bridge.ReadColor();
        if (hex is null) return;
        if (!force && string.Equals(hex, _lastColor, StringComparison.OrdinalIgnoreCase)) return;
        _lastColor = hex;
        Log("color " + hex + " status=" + _bridge.Status);
        PostToJs(new { type = "sai2:color", hex });
    }

    private void PostAck(bool ok, string hex)
    {
        _bridgeError = ok ? null : _bridge.LastWriteDetail;
        PostToJs(new { type = "apply:ack", ok, hex });
        PushState();
    }

    // ---------- chrome ----------

    private void Chrome_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_attached && e.ClickCount == 1) DragMove();
    }

    private void AttachButton_Click(object sender, RoutedEventArgs e) => SetAttached(!_attached);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
