using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ColorWitch.Sai2;

public enum BridgeStatus
{
    Ok,
    Unsupported,
    NotFound,
    Error,
}

public sealed class Sai2Target
{
    public IntPtr Hwnd { get; init; }
    public int Pid { get; init; }
    public string? ExePath { get; init; }
    public string? Sha256 { get; init; }
    public bool Supported => string.Equals(Sha256, Sai2Bridge.SupportedSha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Color bridge to a running PaintTool SAI Ver.2 (Alpha.2026.10.19 64bit).
/// The SAI2 UI is a custom "sfl" toolkit (no accessibility, no real controls), so the only
/// reliable surfaces are the hidden numeric text proxies it keeps in sync and window messages.
/// Read path: the colour panel numeric proxies (R/G/B) are ordinary window texts and are updated
/// live. Write path is version-gated and committed via the mechanism documented in
/// plugin/docs/BRIDGE.md; unsupported builds fall back to clipboard only.
/// </summary>
public sealed class Sai2Bridge
{
    public const string SupportedSha256 = "FF22717250618BE0DFD6BB56B23EACE08F6EB2FBF344A66CE9DA41E9857EA15F";

    private readonly Dictionary<IntPtr, (IntPtr Label, IntPtr Field)> _colorFields = new();
    private Sai2Memory? _memory;
    private DateTime _lastOpenAttempt = DateTime.MinValue;
    private Sai2Target? _target;
    private DateTime _targetStamp = DateTime.MinValue;

    public string LastWriteDetail { get; private set; } = "";

    public Action<string>? LogSink { get; set; }

    private void Log(string message) => LogSink?.Invoke("[bridge] " + message);

    public BridgeStatus Status { get; private set; } = BridgeStatus.NotFound;
    public string Detail { get; private set; } = "";

    public Sai2Target? Target
    {
        get
        {
            if (_target is not null && Native.IsWindow(_target.Hwnd))
                return _target;
            return Find();
        }
    }

    public Sai2Target? Find(bool force = false)
    {
        if (!force && _target is not null && Native.IsWindow(_target.Hwnd))
            return _target;

        foreach (var hwnd in Native.TopWindows())
        {
            if (Native.ClassOf(hwnd) != "sflRootWindow")
                continue;
            var title = Native.TextOf(hwnd);
            if (!title.StartsWith("PaintTool SAI Ver.2", StringComparison.Ordinal))
                continue;

            Native.GetWindowThreadProcessId(hwnd, out var pid);
            var exe = Native.ExePathOf(pid);
            if (exe is not null && !Path.GetFileName(exe).Equals("sai2.exe", StringComparison.OrdinalIgnoreCase))
            {
                Log($"skip hwnd={hwnd} pid={pid} exe={exe}");
                continue;
            }
            if (exe is null)
                Log($"accept hwnd={hwnd} pid={pid} (exe path unavailable; version guard off)");

            var sha = exe is null ? null : Sha256OfFile(exe);
            _target = new Sai2Target { Hwnd = hwnd, Pid = (int)pid, ExePath = exe, Sha256 = sha };
            _targetStamp = DateTime.UtcNow;
            Status = _target.Supported ? BridgeStatus.Ok : BridgeStatus.Unsupported;
            Detail = _target.Supported ? "" : "SAI2 版本不受支持，只能复制颜色到剪贴板";
            Log($"found hwnd={hwnd} pid={pid} sha={sha} supported={_target.Supported}");
            _colorFields.Clear();
            _memory?.Dispose();
            _memory = _target.Supported ? new Sai2Memory((int)pid) : null;
            Log(_memory is null ? "memory bridge: off" : $"memory bridge: open={_memory.IsOpen} base={_memory.ModuleBase} diag={_memory.Diagnostic}");
            return _target;
        }

        _target = null;
        Status = BridgeStatus.NotFound;
        Detail = "未找到 SAI2";
        _memory?.Dispose();
        _memory = null;
        return null;
    }

    private static string? Sha256OfFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Locate the hidden R/G/B proxies that SAI2 keeps in sync with the foreground colour.</summary>
    private bool EnsureColorFields(IntPtr root)
    {
        if (_colorFields.Count >= 3 && _colorFields.Values.All(v => Native.IsWindow(v.Field)))
            return true;

        _colorFields.Clear();
        var groups = new Dictionary<IntPtr, List<IntPtr>>();
        foreach (var child in Native.Children(root))
        {
            var parent = Native.GetParent(child);
            if (parent == IntPtr.Zero) continue;
            if (!groups.TryGetValue(parent, out var list))
                groups[parent] = list = new List<IntPtr>();
            list.Add(child);
        }

        foreach (var parent in groups.Keys)
        {
            var ordered = Native.DirectChildren(parent).ToList();
            (IntPtr Label, IntPtr Field)? r = null, g = null, b = null;
            for (int i = 0; i + 2 < ordered.Count; i++)
            {
                var label = Native.TextOf(ordered[i]);
                var between = Native.TextOf(ordered[i + 1]);
                var field = Native.TextOf(ordered[i + 2]);
                if (between.Length != 0) continue;
                if (!Regex.IsMatch(field, @"^\d{1,3}$")) continue;
                switch (label)
                {
                    case "R": r ??= (ordered[i], ordered[i + 2]); break;
                    case "G": g ??= (ordered[i], ordered[i + 2]); break;
                    case "B": b ??= (ordered[i], ordered[i + 2]); break;
                }
            }
            if (r is not null && g is not null && b is not null)
            {
                _colorFields[0x52] = r.Value;
                _colorFields[0x47] = g.Value;
                _colorFields[0x42] = b.Value;
                return true;
            }
        }
        return false;
    }

    private static int ReadChannel(IntPtr field)
    {
        var txt = Native.TextOf(field);
        return int.TryParse(txt, out var v) ? Math.Clamp(v, 0, 255) : 0;
    }

    /// <summary>Open (or re-open, at most every few seconds) the memory bridge for the target.</summary>
    private Sai2Memory? EnsureMemory(Sai2Target target)
    {
        if (_memory is { IsOpen: true }) return _memory;
        if (_memory is not null && DateTime.UtcNow - _lastOpenAttempt < TimeSpan.FromSeconds(5))
            return _memory;
        _lastOpenAttempt = DateTime.UtcNow;
        _memory?.Dispose();
        _memory = new Sai2Memory(target.Pid);
        Log($"memory bridge: open={_memory.IsOpen} diag={_memory.Diagnostic}");
        return _memory;
    }

    /// <summary>Read SAI2 foreground colour as #RRGGBB, or null when unavailable.</summary>
    public string? ReadColor()
    {
        var target = Find();
        if (target is null) return null;

        var memory = target.Supported ? EnsureMemory(target) : null;
        if (memory is { IsOpen: true })
        {
            var argb = memory.ReadArgb();
            if (argb != 0)
                return $"#{((argb >> 16) & 0xFF):X2}{((argb >> 8) & 0xFF):X2}{(argb & 0xFF):X2}";
        }

        if (!EnsureColorFields(target.Hwnd))
            return null;
        var r = ReadChannel(_colorFields[0x52].Field);
        var g = ReadChannel(_colorFields[0x47].Field);
        var b = ReadChannel(_colorFields[0x42].Field);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>Write SAI2 foreground colour. Returns true when SAI2 accepted the value.</summary>
    public bool WriteColor(string hex, bool copyOnFailure = true)
    {
        var target = Find();
        if (target is null)
        {
            LastWriteDetail = "SAI2 未运行";
            return false;
        }
        if (!TryParse(hex, out var r, out var g, out var b))
        {
            LastWriteDetail = "颜色格式无效";
            return false;
        }
        if (!target.Supported)
        {
            if (copyOnFailure) ClipboardSet(hex);
            LastWriteDetail = "版本不受支持，已复制 HEX 到剪贴板";
            return false;
        }

        var memory = EnsureMemory(target);
        if (memory is { IsOpen: true })
        {
            var argb = memory.WriteRgb(r, g, b);
            var expected = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            if (argb != 0 && (argb & 0xFFFFFF) == (expected & 0xFFFFFF))
            {
                LastWriteDetail = $"已写入 #{r:X2}{g:X2}{b:X2}";
                return true;
            }
            LastWriteDetail = argb == 0
                ? (memory.Diagnostic.Length > 0
                    ? "写入失败：" + memory.Diagnostic
                    : "写入失败（进程内存不可写，可能权限不足）")
                : $"写入校验不一致：读回 #{((argb >> 16) & 0xFF):X2}{((argb >> 8) & 0xFF):X2}{(argb & 0xFF):X2}";
        }
        else
        {
            LastWriteDetail = "内存桥未打开" + (memory is null ? "" : "（" + memory.Diagnostic + "）");
        }

        if (copyOnFailure) ClipboardSet(hex);
        return false;
    }

    public static bool TryParse(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        r = (v >> 16) & 0xFF;
        g = (v >> 8) & 0xFF;
        b = v & 0xFF;
        return true;
    }

    public static void ClipboardSet(string text)
    {
        try
        {
            var t = new Thread(() =>
            {
                try { System.Windows.Clipboard.SetText(text); } catch { }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(500);
        }
        catch { }
    }
}
