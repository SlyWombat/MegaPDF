# Launch the installed MegaPDF package for a file, the way Explorer's "Open with" does
# (IApplicationActivationManager.ActivateForFile) — the machine's default PDF app is not
# MegaPDF, so opening through the association would start another program.
if (-not ('MegaActivate2' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IApplicationActivationManager {
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
    int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
}
[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] class ApplicationActivationManager { }
public static class MegaActivate2 {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [DllImport("shell32.dll")] static extern int SHCreateShellItemArrayFromShellItem(IntPtr psi, ref Guid riid, out IntPtr ppv);
    public static uint ForFile(string aumid, string path) {
        Guid iidItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        Guid iidArray = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");
        IntPtr item, array;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out item));
        Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromShellItem(item, ref iidArray, out array));
        var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid;
        Marshal.ThrowExceptionForHR(mgr.ActivateForFile(aumid, array, "open", out pid));
        return pid;
    }
    // A full-trust packaged app gets a launched file as its first command-line argument.
    public static uint WithArgument(string aumid, string argument) {
        var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid;
        Marshal.ThrowExceptionForHR(mgr.ActivateApplication(aumid, argument, 0, out pid));
        return pid;
    }
}
'@
}
function Activate-File($path) {
    $aumid = 'ElectricRV.MegaPDF_' + (Get-AppxPackage ElectricRV.MegaPDF).PublisherId + '!App'
    return [MegaActivate2]::WithArgument($aumid, '"' + $path + '"')
}
