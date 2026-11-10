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
    private bool _wide;
    private string? _pageUrl;
    private string? _webRoot;
    private string? _embedCss;
    private string? _embedJs;
    private readonly bool _offscreen = Environment.GetEnvironmentVariable("CW_OFFSCREEN") == "1";

    private const int CompactWidth = 380;
    private const string VirtualHost = "colorwitch.local";

    public MainWindow()
    {
        InitializeComponent();
        _bridge.LogSink = Log;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        LoadSettings();
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

    // ---------- settings ----------

    private static string SettingsPath
    {
        get
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColorWitch.Sai2");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.json");
            }
            catch
            {
                return Path.Combine(AppContext.BaseDirectory, "settings.json");
            }
        }
    }

    private sealed class HostSettings
    {
        public int Width { get; set; } = CompactWidth;
        public bool Wide { get; set; }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(SettingsPath));
                if (s is not null)
                {
                    _wide = s.Wide;
                    _panelWidth = Math.Clamp(s.Width <= 0 ? CompactWidth : s.Width, CompactWidth, 1440);
                }
            }
        }
        catch { }
        LayoutButton.Content = _wide ? "紧凑" : "完整";
        // 自动化/排障用的尺寸覆盖：不写配置，仅本次运行生效
        if (int.TryParse(Environment.GetEnvironmentVariable("CW_PANEL_WIDTH"), out var forcedWidth))
            _panelWidth = Math.Clamp(forcedWidth, CompactWidth, 1440);
        if (Environment.GetEnvironmentVariable("CW_PANEL_WIDE") == "1")
        {
            _wide = true;
            _panelWidth = Math.Max(_panelWidth, 1180);
            LayoutButton.Content = "紧凑";
        }
        if (_wide && _panelWidth < 900) _panelWidth = WideWidth();
        Width = _panelWidth;
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new HostSettings { Width = _panelWidth, Wide = _wide }));
        }
        catch { }
    }

    private int WideWidth()
    {
        var target = _attached && _sai2Hwnd != IntPtr.Zero ? Sai2ClientWidthDip() : SystemParameters.WorkArea.Width;
        var w = (int)Math.Round(target) - 40;
        return Math.Clamp(w, 900, 1440);
    }

    private double Sai2ClientWidthDip()
    {
        if (_sai2Hwnd == IntPtr.Zero || !Native.IsWindow(_sai2Hwnd)) return SystemParameters.WorkArea.Width;
        Native.GetClientRect(_sai2Hwnd, out var client);
        var dpi = VisualTreeHelper.GetDpi(this);
        return client.Right / dpi.DpiScaleX;
    }

    private void LayoutButton_Click(object sender, RoutedEventArgs e)
    {
        _wide = !_wide;
        _panelWidth = _wide ? WideWidth() : CompactWidth;
        LayoutButton.Content = _wide ? "紧凑" : "完整";
        if (_attached && _sai2Hwnd != IntPtr.Zero)
        {
            FollowSai2();
        }
        else
        {
            Width = _panelWidth;
            Height = Math.Min(900, SystemParameters.WorkArea.Height - 80);
        }
        SaveSettings();
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
        _webRoot = Path.GetDirectoryName(index)!;
        // 用 https 虚拟主机加载（而不是 file://）：拿到安全上下文与稳定来源，
        // 剪贴板 / EyeDropper / localStorage 的行为和普通网页一致。
        _pageUrl = $"https://{VirtualHost}/index.html";
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
            Log("core ready");
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Web.CoreWebView2.DownloadStarting += OnDownloadStarting;
            if (_webRoot is not null)
            {
                Web.CoreWebView2.SetVirtualHostNameToFolderMapping(VirtualHost, _webRoot, CoreWebView2HostResourceAccessKind.Allow);
                Log("virtual host mapped: " + VirtualHost + " -> " + _webRoot);
            }
            Web.CoreWebView2.Navigate(_pageUrl + "?embed=1");
            Log("navigate " + _pageUrl);
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
                "(function(){" +
                "var ids=['spHsv','spHvs','spOk','cmdHex','pickBtn','copyBtn','linkBtn','helpBtn'," +
                "'rail','cvV','cvL','capV','capL','stage','gl','path','hud','viewDefault','viewTop','viewSide','gridBtn','shotBtn'," +
                "'panel','swatch','swHex','swGray','swGrayHex','chips','palHint','gradCurBtn','saveBtn','histRow'," +
                "'hRange','hNum','sRange','sNum','vRange','vNum','yNum','gNum','isoLum','isoGray'," +
                "'rOut','gOut','bOut','hOut','sOut','vOut','kOut','mOut','yOut','kkOut','okLOut','okCOut','okHOut'," +
                "'hexIn','cOnWhite','cOnBlack','gradCard','gradTop','gradFold','gradBody','gradOn','gradSwap','gradA','gradB'," +
                "'gradMode','hueDir','lightMode','gradBar','gradPick','gradInfo','deadzone','deadOn'," +
                "'dzH0','dzH1','dzS0','dzS1','dzV0','dzV1','copyCss','copySvg','cardMod','cardTop','cardFold','cardBody','cardList','cardSave'," +
                "'expCss','expJson','expGpl','expAse','helpDlg','cwBar','cwApply','cwRead','cwLive','cwAttach','cwStatus'];" +
                "var missing=[],hidden=[];" +
                "for(var i=0;i<ids.length;i++){var el=document.getElementById(ids[i]);" +
                "if(!el){missing.push(ids[i]);continue;}" +
                "if(el.offsetParent===null&&el.getClientRects().length===0)hidden.push(ids[i]);}" +
                "var ls=false;try{localStorage.setItem('cw-probe','1');ls=localStorage.getItem('cw-probe')==='1';localStorage.removeItem('cw-probe');}catch(e){}" +
                "var deck=document.getElementById('deck'),panel=document.getElementById('panel');" +
                "return JSON.stringify({url:location.href,cls:document.documentElement.className,bar:!!document.getElementById('cwBar')," +
                "width:document.documentElement.clientWidth,height:document.documentElement.clientHeight," +
                "missing:missing,hidden:hidden,missingCount:missing.length,hiddenCount:hidden.length," +
                "canvas:document.querySelectorAll('canvas').length," +
                "localStorage:ls,eyeDropper:typeof EyeDropper,clipboard:!!(navigator.clipboard&&navigator.clipboard.writeText)," +
                "webgl:!!document.createElement('canvas').getContext('webgl')," +
                "deckH:deck?deck.clientHeight:-1,deckScroll:deck?deck.scrollHeight:-1," +
                "panelScroll:panel?panel.scrollHeight:-1," +
                "hScroll:document.documentElement.scrollWidth>document.documentElement.clientWidth});" +
                "})()");
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

            string r4;
            try
            {
                // 用 CDP 发一次真实鼠标点击（保存图片按钮走页面自己的 Blob 下载），
                // 普通 ExecuteScript 没有用户手势，浏览器会拦下载。
                // 记录点击与 anchor 下载，确认页面自己走到了下载分支
                await Web.CoreWebView2.ExecuteScriptAsync(
                    "(function(){try{" +
                    "var oc=HTMLAnchorElement.prototype.click;" +
                    "HTMLAnchorElement.prototype.click=function(){window.__cwLog('anchor-click download='+(this.download||'')+' href='+String(this.href).slice(0,48));return oc.apply(this,arguments);};" +
                    "document.addEventListener('click',function(e){window.__cwLog('click id='+(e.target&&e.target.id)+' trusted='+e.isTrusted);},true);" +
                    "}catch(e){window.__cwLog('hook fail '+e.message)}})();");
                var posJson = await Web.CoreWebView2.ExecuteScriptAsync(
                    "(function(){var el=document.getElementById('shotBtn');if(!el)return '[]';" +
                    "var r=el.getBoundingClientRect();return JSON.stringify([r.left+r.width/2,r.top+r.height/2]);})()");
                var posText = JsonSerializer.Deserialize<string>(posJson) ?? "[]";
                var pos = JsonSerializer.Deserialize<double[]>(posText) ?? Array.Empty<double>();
                if (pos.Length == 2)
                {
                    var click = JsonSerializer.Serialize(new { type = "mousePressed", x = pos[0], y = pos[1], button = "left", clickCount = 1 });
                    await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", click);
                    var up = JsonSerializer.Serialize(new { type = "mouseReleased", x = pos[0], y = pos[1], button = "left", clickCount = 1 });
                    r4 = await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", up);
                }
                else
                {
                    r4 = "shotBtn not found";
                }
            }
            catch (Exception ex)
            {
                r4 = "cdp failed: " + ex.Message;
            }
            Log("selftest download " + r4);
            await Task.Delay(1800);
            var auto = Path.Combine(AppContext.BaseDirectory, "exports");
            var files = Directory.Exists(auto) ? string.Join(",", Directory.GetFiles(auto).Select(Path.GetFileName)) : "";
            Log("selftest downloads=" + files);

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

    /// <summary>页面导出由 embed.js 拦截后发来 app:save，这里负责选路径并落盘。</summary>
    private void SaveExport(string? name, string? mime, string? dataB64)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dataB64))
            {
                Log("export: empty payload");
                return;
            }
            var bytes = Convert.FromBase64String(dataB64);
            var fileName = string.IsNullOrWhiteSpace(name) ? "colorwitch-export" : Path.GetFileName(name);
            string path;
            if (_offscreen || Environment.GetEnvironmentVariable("CW_AUTOSAVE") == "1")
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "exports");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, fileName);
            }
            else
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "保存导出文件",
                    FileName = fileName,
                    Filter = "所有文件|*.*",
                    AddExtension = false,
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }
            File.WriteAllBytes(path, bytes);
            Log($"export saved {path} ({bytes.Length} bytes, {mime})");
            PostToJs(new { type = "save:ack", ok = true });
        }
        catch (Exception ex)
        {
            Log("export failed: " + ex.Message);
            PostToJs(new { type = "save:ack", ok = false });
        }
    }

    /// <summary>页面里的导出（CSS/GPL/ASE/JSON、保存图片）走 Blob 下载，这里接管保存位置。</summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            Log("DownloadStarting path=" + e.ResultFilePath + " uri=" + (e.DownloadOperation?.Uri ?? ""));
            var name = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(name)) name = "colorwitch-export";
            if (_offscreen || Environment.GetEnvironmentVariable("CW_AUTOSAVE") == "1")
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "exports");
                Directory.CreateDirectory(dir);
                e.ResultFilePath = Path.Combine(dir, name);
                e.Handled = true;
                Log("download -> " + e.ResultFilePath);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存导出文件",
                FileName = name,
                Filter = "所有文件|*.*",
                AddExtension = false,
            };
            if (dlg.ShowDialog(this) == true)
            {
                e.ResultFilePath = dlg.FileName;
                e.Handled = true;
            }
            else
            {
                e.Cancel = true;
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            Log("download dialog failed: " + ex.Message);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SaveSettings();
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
        var dpi = VisualTreeHelper.GetDpi(this);
        if (Environment.GetEnvironmentVariable("CW_DEBUG") == "1")
            Log($"follow: panelWidth={_panelWidth} dpi={dpi.DpiScaleX:0.00} wide={_wide}");
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
            case "app:save":
            {
                var name = msg.TryGetProperty("name", out var n) ? n.GetString() : null;
                var dataB64 = msg.TryGetProperty("data", out var d) ? d.GetString() : null;
                var mime = msg.TryGetProperty("mime", out var m) ? m.GetString() : null;
                SaveExport(name, mime, dataB64);
                break;
            }
            case "app:toggleAttach":
                SetAttached(!_attached);
                break;
            case "app:close":
                Close();
                break;
            case "app:resize":
                if (msg.TryGetProperty("width", out var w) && w.TryGetInt32(out var width)
                    && Environment.GetEnvironmentVariable("CW_DEBUG") == "1")
                    Log("page width " + width);
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
