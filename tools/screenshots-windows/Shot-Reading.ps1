# Slot 1 — reading mode (#504, #510), the hardest picture in the set (#613).
#
# Hardest because the feature is the absence of the app: Ctrl+H collapses the busy
# strip, the toolbar and the tab strip, and what is left is a page of a document.
# Four decisions make it a capture of MegaPDF rather than a capture of a PDF, and
# all four are deliberate:
#
#  * **Windowed, never F11.** Full screen is reachable from inside reading mode and
#    is wrong here twice over: it takes the title bar, which is the only thing left
#    on screen with the app's name and icon on it, and it changes the frame, so the
#    image would not be the 2482x1541 the other six slots share.
#  * **The bar shown, not faded.** It fades two seconds after the last pointer move
#    (DocumentView.ReadingMode.cs, BarIdle), so the shot is taken immediately after
#    a nudge. The nudge is in the *gutter* beside the page, not on it: the gutter
#    raises the bar (DocumentAreaRoot's PointerMoved) and has nothing to hover.
#  * **Page colours left normal.** Sepia and night are settings, and a store image
#    that opens on an inverted page reads as a dark-mode screenshot rather than as
#    a feature.
#  * **A page in the middle of a long document.** -Pages steps forward with the
#    bar's own Next button, so the pill reads "4 / 12" and the arrows either side of
#    it have somewhere to go. On the one-page agreement the bar reads "1 / 1" and
#    the whole picture argues against itself.
#
# It asserts it arrived, because the failure mode is silent: a Ctrl+H that did not
# land leaves an ordinary viewer shot under the file name of the reading slot, and
# the gate cannot tell you that an image is of the wrong screen.
param([int]$Pages = 3, [string]$Name = "01-reading")
. (Join-Path $PSScriptRoot "lib.ps1")

$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
# Park focus off the toolbar first: Ctrl+H is on RootGrid, so it is heard wherever
# focus is, but a ring left on a toolbar button is still showing when the toolbar
# collapses and WinUI paints it on the way out.
Blur $h
[System.Windows.Forms.SendKeys]::SendWait("^h")
Start-Sleep -Seconds 2

$r = New-Object Win+RECT
if ([Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($h, [ref]$r) | Out-Null }

# Arrived, or not. Two halves, and they are asserted in this order for a reason the
# first dry run paid for: the floating bar fades two seconds after the last pointer
# move and its buttons go *Collapsed* with it, so a check for ReadingExitButton run
# after a two-second wait finds nothing and reports that reading mode did not engage
# while the window is plainly in it. Raise the bar first, then look for it.
[Win]::SetCursorPos([int]($r.Left + 20), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
Waggle
Start-Sleep -Milliseconds 300
$root = $AE::FromHandle($h)
$exit = BtnById $root "ReadingExitButton"
$open = BtnById $root "OpenButton"
if (-not $exit) { Write-Host "!! reading mode did not engage: no ReadingExitButton"; exit 1 }
if ($open) { Write-Host "!! the toolbar is still there: OpenButton is in the tree"; exit 1 }
Write-Host "  reading mode: on (exit button present, toolbar gone)"

for ($i = 0; $i -lt $Pages; $i++) {
    $next = BtnById $root "ReadingNextButton"
    if (-not $next) { Write-Host "!! no ReadingNextButton"; exit 1 }
    $next.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
}

# Invoking a bar button through UIA leaves keyboard focus on it, and WinUI paints the
# button's tooltip ("Next page") above the bar for a focused control -- the first dry
# run of this step put that word in the middle of the listing image, with a focus box
# round the page control beside it. A click in the gutter takes focus off the bar; the
# wait is long enough for the tooltip to go with it (the bar fades too, and the nudge
# below brings it back).
Click-At ([int]($r.Left + 35)) ([int](($r.Top + $r.Bottom) / 2))
Start-Sleep -Seconds 4

# The bar is up for two seconds after a pointer move, so waggle and shoot in one go.
# The gutter, a few tens of pixels in from the left edge, is where Blur already
# clicks: inside DocumentAreaRoot (so the move counts) and off the page (so nothing
# hovers). Waggle, not SetCursorPos -- see lib.ps1.
[Win]::SetCursorPos([int]($r.Left + 20), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
Waggle
Shot $h $Name

# Leave the window the way the next step expects it: Escape steps back out (the
# ladder), and the toolbar is back before anything else runs.
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 2
if (-not (BtnById ($AE::FromHandle($h)) "OpenButton")) {
    Write-Host "!! still in reading mode after Escape"
    exit 1
}
