using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ColorWitch.Sai2;

/// <summary>
/// Dev-only: verifies the Win32 glue used by the colour bridge (OpenProcess,
/// ReadProcessMemory, WriteProcessMemory, CreateRemoteThread) against a throw-away
/// child process, so the mechanics can be tested on machines where the host is not
/// allowed to open the real SAI2 process (for example under a tool sandbox).
/// The SAI2-specific offsets/conversion are covered by plugin/docs/BRIDGE.md.
/// </summary>
internal static class MemorySelfTest
{
    private const uint PROCESS_ALL = 0x0010 | 0x0020 | 0x0008 | 0x0002 | 0x0400;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    public static string Run()
    {
        var result = new Dictionary<string, object?>();
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var child = Process.Start(new ProcessStartInfo(exe, "--sleep 20000")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("failed to spawn child");

        try
        {
            Thread.Sleep(1200);
            var handle = OpenProcess(PROCESS_ALL, false, child.Id);
            result["openProcess"] = handle != IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                result["error"] = Marshal.GetLastWin32Error();
                return Json(result);
            }

            try
            {
                var baseAddr = ToolhelpBase(child.Id);
                result["moduleBase"] = baseAddr.ToInt64().ToString("X");

                var mz = new byte[2];
                var ok = ReadProcessMemory(handle, baseAddr, mz, 2, out var read);
                result["readMz"] = ok && read.ToInt64() == 2 && mz[0] == 'M' && mz[1] == 'Z';

                var page = VirtualAllocEx(handle, IntPtr.Zero, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                result["virtualAlloc"] = page != IntPtr.Zero;
                if (page != IntPtr.Zero)
                {
                    var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
                    var wrote = WriteProcessMemory(handle, page, payload, payload.Length, out var written);
                    var back = new byte[payload.Length];
                    var readBack = ReadProcessMemory(handle, page, back, back.Length, out var read2);
                    result["writeReadBack"] = wrote && readBack && written.ToInt64() == payload.Length
                        && read2.ToInt64() == back.Length && back.AsSpan().SequenceEqual(payload);

                    var exitCode = new IntPtr(0);
                    var sleep = GetProcAddress(GetModuleHandleW("kernel32.dll"), "Sleep");
                    var thread = CreateRemoteThread(handle, IntPtr.Zero, IntPtr.Zero, sleep, new IntPtr(10), 0, out _);
                    result["createRemoteThread"] = thread != IntPtr.Zero;
                    if (thread != IntPtr.Zero)
                    {
                        result["waitMs"] = WaitForSingleObject(thread, 2000);
                        CloseHandle(thread);
                    }
                    VirtualFreeEx(handle, page, IntPtr.Zero, MEM_RELEASE);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        finally
        {
            try { child.Kill(true); } catch { }
            child.Dispose();
        }

        return Json(result);
    }

    private static string Json(Dictionary<string, object?> data) =>
        System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    private static IntPtr ToolhelpBase(int pid)
    {
        var snap = CreateToolhelp32Snapshot(0x00000008 | 0x00000010, (uint)pid);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return IntPtr.Zero;
        try
        {
            var entry = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            if (Module32FirstW(snap, ref entry))
                return entry.modBaseAddr;
        }
        finally
        {
            CloseHandle(snap);
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize, th32ModuleID, th32ProcessID, GlblcntUsage, ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, int size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, int size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, int size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, IntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr attr, IntPtr stack, IntPtr start, IntPtr param, uint flags, out uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Module32FirstW(IntPtr snap, ref MODULEENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
