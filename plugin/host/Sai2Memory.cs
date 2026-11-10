using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Atlas.Sai2;

/// <summary>
/// Direct state access to the SAI2 foreground colour.
/// The colour lives in sai2.exe .data as float V/S/H (+A) followed by the packed ARGB value,
/// and SAI2 exposes an internal setter that recomputes RGB and repaints every colour surface.
/// Writing the three floats and calling that setter through a remote thread is the same code
/// path the UI uses, so markers, swatches and the brush preview stay in sync.
/// Offsets are pinned to the supported build (checked by exe SHA-256 before use).
/// </summary>
internal sealed class Sai2Memory : IDisposable
{
    // floats V, S, H, A at base+RvaVSH; packed 0xAARRGGBB at base+RvaArgb
    public const int RvaVsh = 0x47a7f8;
    public const int RvaArgb = 0x47a818;
    public const int RvaColorMode = 0x47b4a7;   // 0 = V-HSV (default), 1 = HSL, 2 = HSV
    public const int RvaSetter = 0x194fd0;

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;

    private IntPtr _process;
    private IntPtr _moduleBase;
    private readonly int _pid;

    public bool IsOpen => _process != IntPtr.Zero && _moduleBase != IntPtr.Zero;
    public IntPtr ModuleBase => _moduleBase;
    public string Diagnostic { get; private set; } = "";

    public Sai2Memory(int pid)
    {
        _pid = pid;
        _process = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION |
                               PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION, false, pid);
        if (_process == IntPtr.Zero)
        {
            Diagnostic = $"OpenProcess failed: {Marshal.GetLastWin32Error()}";
            return;
        }

        _moduleBase = FindModuleBaseToolhelp(pid, "sai2.exe");
        if (_moduleBase == IntPtr.Zero)
            _moduleBase = FindModuleBasePsapi(_process, "sai2.exe");
        if (_moduleBase == IntPtr.Zero)
        {
            Diagnostic = "module base not found";
        }
    }

    public void Dispose()
    {
        if (_process != IntPtr.Zero)
        {
            CloseHandle(_process);
            _process = IntPtr.Zero;
            _moduleBase = IntPtr.Zero;
        }
    }

    private byte ReadByte(int rva)
    {
        var buf = new byte[1];
        if (!ReadProcessMemory(_process, _moduleBase + rva, buf, 1, out var read) || read.ToInt64() != 1)
            return 0xFF;
        return buf[0];
    }

    /// <summary>Read the packed 0xAARRGGBB foreground colour; 0 when unavailable.</summary>
    public uint ReadArgb()
    {
        if (!IsOpen) return 0;
        var buf = new byte[4];
        if (!ReadProcessMemory(_process, _moduleBase + RvaArgb, buf, 4, out var read) || read.ToInt64() != 4)
            return 0;
        return BitConverter.ToUInt32(buf, 0);
    }

    /// <summary>Push RGB into SAI2 through its own colour setter. Returns the resulting ARGB, 0 on failure.</summary>
    public uint WriteRgb(int r, int g, int b)
    {
        if (!IsOpen) return 0;

        // The VHSV state is only what the setter expects while the colour panel is in its
        // default V-HSV space; HSL/HSV panels store different floats at the same address.
        var mode = ReadByte(RvaColorMode);
        if (mode != 0)
        {
            Diagnostic = $"颜色面板当前不是 V-HSV 模式（mode={mode}），请切回默认颜色面板再写色";
            return 0;
        }

        ToVsh(r, g, b, out var v, out var s, out var h);

        var data = new byte[12];
        BitConverter.GetBytes(v).CopyTo(data, 0);
        BitConverter.GetBytes(s).CopyTo(data, 4);
        BitConverter.GetBytes(h).CopyTo(data, 8);
        if (!WriteProcessMemory(_process, _moduleBase + RvaVsh, data, 12, out var written) || written.ToInt64() != 12)
            return 0;

        var thread = CreateRemoteThread(_process, IntPtr.Zero, IntPtr.Zero,
            _moduleBase + RvaSetter, _moduleBase + RvaVsh, 0, out _);
        if (thread == IntPtr.Zero)
            return 0;
        try
        {
            WaitForSingleObject(thread, 1000);
        }
        finally
        {
            CloseHandle(thread);
        }

        return ReadArgb();
    }

    /// <summary>RGB (0-255) → SAI2's own VHSV model (V/S/H as used by 0x47a7f8).</summary>
    public static void ToVsh(int r8, int g8, int b8, out float v, out float s, out float h)
    {
        float r = r8 / 255f, g = g8 / 255f, b = b8 / 255f;
        v = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float chroma = v - min;

        if (chroma <= 1e-6f)
        {
            s = 0f;
            h = 0f;
            return;
        }

        float d = (v - 1f) * 0.5f;
        s = Math.Abs(d) < 1e-9f ? chroma : (float)((Math.Sqrt(0.25 + d * chroma) - 0.5) / d);

        float hh;
        if (Math.Abs(v - r) < 1e-9f) hh = (g - b) / chroma;
        else if (Math.Abs(v - g) < 1e-9f) hh = 2f + (b - r) / chroma;
        else hh = 4f + (r - g) / chroma;
        if (hh < 0) hh += 6f;
        h = hh % 6f;
    }

    private static IntPtr FindModuleBaseToolhelp(int pid, string moduleName)
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return IntPtr.Zero;
        try
        {
            var entry = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            if (!Module32FirstW(snapshot, ref entry)) return IntPtr.Zero;
            do
            {
                if (string.Equals(entry.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                    return entry.modBaseAddr;
            } while (Module32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindModuleBasePsapi(IntPtr process, string moduleName)
    {
        try
        {
            var handles = new IntPtr[512];
            if (!EnumProcessModulesEx(process, handles, handles.Length * IntPtr.Size, out var needed, 0x03))
                return IntPtr.Zero;
            int count = Math.Min(handles.Length, (int)(needed / IntPtr.Size));
            var sb = new StringBuilder(260);
            var info = new MODULEINFO();
            for (int i = 0; i < count; i++)
            {
                if (!GetModuleBaseNameW(process, handles[i], sb, sb.Capacity)) continue;
                if (!string.Equals(sb.ToString(), moduleName, StringComparison.OrdinalIgnoreCase)) continue;
                if (GetModuleInformation(process, handles[i], ref info, (uint)Marshal.SizeOf<MODULEINFO>()))
                    return info.lpBaseOfDll;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int size, out int needed, uint filter);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetModuleBaseNameW(IntPtr process, IntPtr module, StringBuilder baseName, int size);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetModuleInformation(IntPtr process, IntPtr module, ref MODULEINFO info, uint size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, IntPtr stackSize,
        IntPtr startAddress, IntPtr parameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32FirstW(IntPtr snapshot, ref MODULEENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32NextW(IntPtr snapshot, ref MODULEENTRY32W entry);
}
