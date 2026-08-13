# Shared harness for the Microsoft Store screenshots (tools/screenshots-windows).
# Dot-source this from the step scripts; see README.md for the run order.
# Buttons all carry AutomationProperties.Name since d250120, so plain
# Name+ControlType matching works -- no TreeWalker climb needed.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public static System.Collections.Generic.List<IntPtr> Found = new System.Collections.Generic.List<IntPtr>();
    public static string Title(IntPtr h) { var sb = new System.Text.StringBuilder(512); GetWindowTextW(h, sb, 512); return sb.ToString(); }
    public static string Cls(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
    public static System.Collections.Generic.List<IntPtr> Windows() {
        Found.Clear();
        EnumWindows((h, p) => { if (IsWindowVisible(h)) Found.Add(h); return true; }, IntPtr.Zero);
        return Found;
    }
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[Win]::SetProcessDPIAware() | Out-Null
$global:AE = [System.Windows.Automation.AutomationElement]
$global:TS = [System.Windows.Automation.TreeScope]
$global:CT = [System.Windows.Automation.ControlType]
$global:AUMID = "ElectricRV.MegaPDF_spcj169vsxppp!App"
# Shots land in the repo's (gitignored) artifacts dir, never beside these scripts.
$global:REPO = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$global:SHOTDIR = if ($env:MEGAPDF_SHOTDIR) { $env:MEGAPDF_SHOTDIR } else { Join-Path $global:REPO "artifacts\store\screenshots" }
New-Item -ItemType Directory -Force $global:SHOTDIR | Out-Null

function Start-App {
    $p = Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) {
        Start-Process "shell:AppsFolder\$global:AUMID"
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 500
            $p = Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
            if ($p) { break }
        }
        Start-Sleep -Seconds 3   # let the splash finish
        $p = Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    }
    return $p
}
function Set-Size($h, $w, $t, $x = 0, $y = 0) { [Win]::MoveWindow($h, $x, $y, $w, $t, $true) | Out-Null; Start-Sleep -Milliseconds 800 }
function Front($h) {
    $r = New-Object Win+RECT
    if ([Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($h, [ref]$r) | Out-Null }
    Click-At ([int](($r.Left + $r.Right) / 2)) ([int]($r.Top + 14))    # title bar click == foreground rights, no ALT
    [Win]::SetForegroundWindow($h) | Out-Null
    [Win]::SetCursorPos($r.Right - 8, $r.Bottom - 8) | Out-Null        # park the pointer out of the shot
    Start-Sleep -Milliseconds 600
}
function Shot($h, $name) {
    $r = New-Object Win+RECT
    if ([Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($h, [ref]$r) | Out-Null }
    $bmp = New-Object System.Drawing.Bitmap ($r.Right-$r.Left), ($r.Bottom-$r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $path = Join-Path $global:SHOTDIR "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    # mean brightness over a sampled grid -- a locked session captures pure black
    $sum = 0; $n = 0
    for ($x = 5; $x -lt $bmp.Width; $x += 60) { for ($y = 5; $y -lt $bmp.Height; $y += 60) {
        $c = $bmp.GetPixel($x, $y); $sum += ($c.R + $c.G + $c.B) / 3; $n++ } }
    $mean = [math]::Round($sum / $n, 1)
    $g.Dispose(); $bmp.Dispose()
    Write-Host ("{0}.png  {1}x{2}  mean={3}" -f $name, ($r.Right-$r.Left), ($r.Bottom-$r.Top), $mean)
}
function BtnByName($root, $name) {
    $c1 = New-Object System.Windows.Automation.PropertyCondition($global:AE::NameProperty, $name)
    $c2 = New-Object System.Windows.Automation.PropertyCondition($global:AE::ControlTypeProperty, $global:CT::Button)
    $and = New-Object System.Windows.Automation.AndCondition($c1, $c2)
    return $root.FindFirst($global:TS::Descendants, $and)
}
function Click-Btn($root, $name) {
    $b = BtnByName $root $name
    if (-not $b) { Write-Host "!! no button '$name'"; return $false }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
    return $true
}
function Click-At($x, $y) {
    [Win]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 250
    [Win]::mouse_event(0x02, 0, 0, 0, [IntPtr]::Zero)
    [Win]::mouse_event(0x04, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}

# The packaged FileOpenPicker is a modern Explorer dialog that doesn't surface as
# #32770 under RootElement, so drive it by keyboard instead of by UIA: its File
# name box already has focus when it opens.
function Find-Dialog($titles) {
    foreach ($w in [Win]::Windows()) {
        $t = [Win]::Title($w)
        if ($titles -contains $t) { return $w }
    }
    return [IntPtr]::Zero
}
function Send-Path($path) {
    # Poll rather than sleep a fixed time: the first launch after a build or install
    # can take several seconds longer to put the picker up.
    $dlg = [IntPtr]::Zero
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 700
        $dlg = Find-Dialog @("Open", "Save As", "Save a smaller copy")
        if ($dlg -ne [IntPtr]::Zero) { break }
    }
    if ($dlg -ne [IntPtr]::Zero) {
        $dr = New-Object Win+RECT; [Win]::GetWindowRect($dlg, [ref]$dr) | Out-Null
        Click-At ([int](($dr.Left + $dr.Right) / 2)) ([int]($dr.Top + 20))
        [Win]::SetForegroundWindow($dlg) | Out-Null
        Start-Sleep -Milliseconds 800
        Write-Host ("  picker foreground: '{0}'" -f [Win]::Title([Win]::GetForegroundWindow()))
    } else {
        # Bailing out matters: without a picker in the foreground the SendKeys below
        # would type a file path and press Enter into whatever window happens to have
        # focus, which can open files in other apps. Seen for real.
        Write-Host "  !! no picker dialog found -- not typing (would go to the wrong window)"
        return $false
    }
    [System.Windows.Forms.SendKeys]::SendWait($path.Replace("(", "{(}").Replace(")", "{)}"))
    Start-Sleep -Milliseconds 900
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Start-Sleep -Seconds 6
    return $true
}

# WinUI keeps painting access-key badges on the toolbar until it leaves menu mode.
function Clear-Badges($h) {
    Front $h
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 500
}

# Clicking a toolbar button leaves a keyboard-focus ring on it. Park focus in the
# gutter beside the page (no page hit there, so no edit mode is triggered).
function Blur($h) {
    $r = New-Object Win+RECT
    if ([Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($h, [ref]$r) | Out-Null }
    Click-At ([int]($r.Left + 35)) ([int](($r.Top + $r.Bottom) / 2))
    [Win]::SetCursorPos($r.Right - 8, $r.Bottom - 8) | Out-Null
    Start-Sleep -Milliseconds 400
}

# Click using coordinates read off the last screenshot: the shot IS the DWM rect,
# so image (x,y) maps to screen (rect.Left + x, rect.Top + y).
function Click-InShot($h, $x, $y) {
    $r = New-Object Win+RECT
    if ([Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($h, [ref]$r) | Out-Null }
    Click-At ([int]($r.Left + $x)) ([int]($r.Top + $y))
}
function Park($h) {
    $r = New-Object Win+RECT
    if ([Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($h, [ref]$r) | Out-Null }
    [Win]::SetCursorPos($r.Right - 8, $r.Bottom - 8) | Out-Null
    Start-Sleep -Milliseconds 300
}

# Flyout buttons take their UIA name from their inner TextBlock, which ends in a
# real ellipsis (U+2026). PowerShell 5.1 reads .ps1 as ANSI, so match by prefix
# instead of embedding the character.
function BtnLike($root, $prefix) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($global:AE::ControlTypeProperty, $global:CT::Button)
    foreach ($b in $root.FindAll($global:TS::Descendants, $cond)) {
        if ($b.Current.Name -like "$prefix*") { return $b }
    }
    return $null
}

function Scroll($h, $notches) {
    $r = New-Object Win+RECT
    if ([Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($h, [ref]$r) | Out-Null }
    [Win]::SetCursorPos([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 200
    for ($i = 0; $i -lt [math]::Abs($notches); $i++) {
        # WHEEL_DELTA as an unsigned dword: scrolling down is -120 wrapped to 2^32-120
        $d = if ($notches -gt 0) { [uint32]4294967176 } else { [uint32]120 }
        [Win]::mouse_event(0x0800, 0, 0, $d, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 120
    }
    Start-Sleep -Milliseconds 900
}

# Toolbar-only capture: the empty state lists the user's own recent documents, so
# width testing crops to the command bar and never photographs the page area.
function ShotStrip($h, $name, $height = 130) {
    $r = New-Object Win+RECT
    if ([Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($h, [ref]$r) | Out-Null }
    $w = $r.Right - $r.Left
    $bmp = New-Object System.Drawing.Bitmap $w, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $height)))
    $bmp.Save((Join-Path $global:SHOTDIR "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host ("{0}.png  {1}x{2}" -f $name, $w, $height)
}
