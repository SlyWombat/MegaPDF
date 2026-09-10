# Read or set the primary display's scale (100, 125, 150, ...), no sign-out.
#
#     .\Set-Scale.ps1            # prints the current scale
#     .\Set-Scale.ps1 150        # sets 150 %
#
# The Store shots need 150 %: ApplyToolbarLayout works in effective pixels, and
# this 2560-wide display at 200 % is only 1280 effective — below the toolbar's
# Full breakpoint in every language, so the labels never show (README, "Why
# this frame"). Uses the same DISPLAYCONFIG device-info calls the Settings app
# uses (types -3 / -4, undocumented but stable since Windows 10 1703).
param([int]$Percent = 0)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class DpiScale {
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(IntPtr packet);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(IntPtr packet);
    const uint QDC_ONLY_ACTIVE_PATHS = 2;
    const int PATH_SIZE = 72;
    public static readonly int[] Steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    // adapterId (8 bytes) + sourceId (4 bytes) of the first active path.
    static byte[] PrimarySource() {
        uint np, nm;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) throw new Exception("GetDisplayConfigBufferSizes");
        IntPtr paths = Marshal.AllocHGlobal((int)np * PATH_SIZE);
        IntPtr modes = Marshal.AllocHGlobal((int)nm * 64);
        try {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) throw new Exception("QueryDisplayConfig");
            var id = new byte[12];
            Marshal.Copy(paths, id, 0, 12);
            return id;
        } finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
    }

    // DISPLAYCONFIG_SOURCE_DPI_SCALE_GET: header(type, size, adapterId, id) + min, cur, max (relative to recommended).
    static int[] Get(byte[] src) {
        var buf = new byte[20 + 12];   // header is 20 bytes: type, size, LUID, id
        BitConverter.GetBytes(-3).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)buf.Length).CopyTo(buf, 4);
        Array.Copy(src, 0, buf, 8, 12);
        IntPtr p = Marshal.AllocHGlobal(buf.Length);
        try {
            Marshal.Copy(buf, 0, p, buf.Length);
            int rc = DisplayConfigGetDeviceInfo(p);
            if (rc != 0) throw new Exception("DisplayConfigGetDeviceInfo rc=" + rc);
            Marshal.Copy(p, buf, 0, buf.Length);
        } finally { Marshal.FreeHGlobal(p); }
        return new[] { BitConverter.ToInt32(buf, 20), BitConverter.ToInt32(buf, 24), BitConverter.ToInt32(buf, 28) };
    }

    public static int Current() {
        var g = Get(PrimarySource());
        // cur is relative to the recommended step; recommended = index where rel==0.
        // Windows reports min/cur/max relative offsets; the absolute index is
        // recommendedIndex + cur, and recommendedIndex = -min.
        int recommended = -g[0];
        return Steps[recommended + g[1]];
    }

    public static void Set(int percent) {
        var src = PrimarySource();
        var g = Get(src);
        int recommended = -g[0];
        int target = Array.IndexOf(Steps, percent);
        if (target < 0) throw new Exception("unsupported scale " + percent);
        int rel = target - recommended;
        if (rel < g[0] || rel > g[2]) throw new Exception("scale " + percent + " is outside this display's range");
        var buf = new byte[20 + 4];
        BitConverter.GetBytes(-4).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)buf.Length).CopyTo(buf, 4);
        Array.Copy(src, 0, buf, 8, 12);
        BitConverter.GetBytes(rel).CopyTo(buf, 20);
        IntPtr p = Marshal.AllocHGlobal(buf.Length);
        try {
            Marshal.Copy(buf, 0, p, buf.Length);
            int rc = DisplayConfigSetDeviceInfo(p);
            if (rc != 0) throw new Exception("DisplayConfigSetDeviceInfo rc=" + rc);
        } finally { Marshal.FreeHGlobal(p); }
    }
}
"@

if ($Percent -gt 0) {
    [DpiScale]::Set($Percent)
    Start-Sleep -Seconds 2
}
Write-Host ("scale: {0}%" -f [DpiScale]::Current())
