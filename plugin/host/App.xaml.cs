using System.IO;
using System.Threading;
using System.Windows;

namespace ColorWitch.Sai2;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 命令行工具模式（供自动化验证 / 排查用）：
        //   ColorWitch.Sai2.exe --get-color [--out file]
        //   ColorWitch.Sai2.exe --set-color #RRGGBB [--out file]
        var args = e.Args ?? Array.Empty<string>();
        if (args.Length > 0 && args[0] == "--selftest-memory")
        {
            var report = MemorySelfTest.Run();
            Console.WriteLine(report);
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "memory-selftest.json"), report); } catch { }
            Shutdown(0);
            return;
        }

        if (args.Length > 0 && args[0] == "--stress")
        {
            var count = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 200;
            var bridge = new Sai2Bridge();
            var target = bridge.Find();
            var report = new Dictionary<string, object?>
            {
                ["found"] = target is not null,
                ["count"] = count,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int ok = 0;
            double max = 0;
            var rnd = new Random(1);
            for (int i = 0; i < count; i++)
            {
                var r = rnd.Next(256); var g = rnd.Next(256); var b = rnd.Next(256);
                var hex = $"#{r:X2}{g:X2}{b:X2}";
                var t0 = sw.Elapsed.TotalMilliseconds;
                if (bridge.WriteColor(hex, copyOnFailure: false)) ok++;
                var dt = sw.Elapsed.TotalMilliseconds - t0;
                if (dt > max) max = dt;
            }
            sw.Stop();
            report["ok"] = ok;
            report["totalMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            report["avgMs"] = Math.Round(sw.Elapsed.TotalMilliseconds / count, 2);
            report["maxMs"] = Math.Round(max, 2);
            report["colorAfter"] = bridge.ReadColor();
            report["detail"] = bridge.LastWriteDetail;
            var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            try { Console.WriteLine(json); } catch { }
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "stress-result.json"), json); } catch { }
            Shutdown(0);
            return;
        }

        if (args.Length > 0 && args[0] == "--sleep")
        {
            Thread.Sleep(args.Length > 1 && int.TryParse(args[1], out var ms) ? ms : 3000);
            Shutdown(0);
            return;
        }

        if (args.Length > 0 && (args[0] == "--get-color" || args[0] == "--set-color"))
        {
            RunCommandLine(args);
            Shutdown(0);
            return;
        }

        _mutex = new Mutex(true, @"Local\ColorWitch.Sai2.Singleton", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ColorWitch 面板已在运行（按 Ctrl+Alt+C 显示/隐藏）。", "ColorWitch for SAI2");
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void RunCommandLine(string[] args)
    {
        var mode = args[0];
        string? hex = null;
        string? outPath = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
            else if (!args[i].StartsWith("--")) hex = args[i];
        }

        var bridge = new Sai2Bridge();
        var result = new Dictionary<string, object?>();
        var target = bridge.Find();
        result["found"] = target is not null;
        result["supported"] = target?.Supported ?? false;
        result["sha256"] = target?.Sha256;
        result["status"] = bridge.Status.ToString();

        if (mode == "--get-color")
        {
            result["color"] = target is null ? null : bridge.ReadColor();
        }
        else
        {
            result["requested"] = hex;
            result["ok"] = hex is not null && bridge.WriteColor(hex);
            result["detail"] = bridge.LastWriteDetail;
            result["colorAfter"] = target is null ? null : bridge.ReadColor();
        }

        var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        try { Console.WriteLine(json); } catch { }
        if (outPath is not null)
        {
            try { File.WriteAllText(outPath, json); } catch { }
        }
        else
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "cli-result.json"), json); } catch { }
        }
    }
}
