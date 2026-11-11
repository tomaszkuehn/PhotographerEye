$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$dll = @'
using System;
using System.Runtime.InteropServices;
public static class U {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint fl, UIntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(60);
    mouse_event(2, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(80);
    mouse_event(4, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
  }
  public static void Down(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(70); mouse_event(2, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(90); }
  public static void Move(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(35); }
  public static void Up() { mouse_event(4, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(80); }
  public static void Wheel(int x, int y, int delta) { SetCursorPos(x, y); System.Threading.Thread.Sleep(40); mouse_event(0x0800, 0, 0, delta, UIntPtr.Zero); System.Threading.Thread.Sleep(90); }
  public static void Key(byte vk, int count) {
    for (int i = 0; i < count; i++) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); System.Threading.Thread.Sleep(120); }
  }
}
'@
Add-Type -TypeDefinition $dll -Language CSharp

$repo = 'D:\kody\imgViewer'
$exe = Join-Path $repo 'bin\Release\net8.0-windows\PhotographerEye.exe'
$td = Join-Path $env:TEMP 'imgviewertest'
$iso = Join-Path $td 'appdata'
$debugLog = Join-Path $env:TEMP 'photographereye-debug.log'
$report = Join-Path $repo 'UI-TEST.md'

# Any unhandled error: report it and close the app this script started (never another PhotographerEye,
# e.g. the user's own) so a failed run never leaves a frozen-looking window.
trap {
  Write-Host ('[FATAL] ' + $_.Exception.Message + ' @ line ' + $_.InvocationInfo.ScriptLineNumber)
  if ($proc -and -not $proc.HasExited) { try { $proc.Kill() } catch {} }
  exit 1
}

$Script:results = New-Object System.Collections.Generic.List[object]
$Script:debugShots = New-Object System.Collections.Generic.List[string]
[void]([U]::SetThreadDpiAwarenessContext([IntPtr](-4)))

function Info($m) { Write-Host "[INFO] $m" }

function Add-Result($phase, $target, $inD, $outD, $verdict, $note) {
  $o = [ordered]@{ Phase = $phase; Target = $target; In = [math]::Round($inD, 1); Out = [math]::Round($outD, 1); Verdict = $verdict; Note = $note }
  $Script:results.Add([pscustomobject]$o)
  Write-Host ("[{0}] {1}: IN={2:0.0} OUT={3:0.0} {4} {5}" -f $phase, $target, $inD, $outD, $verdict, $note)
}

# --------------------------------------------------------------- process + appdata
# Other PhotographerEye windows (the user's) are left alone: the test drives only the process it starts below.
New-Item -ItemType Directory -Force -Path $iso, $td, $repo | Out-Null
Remove-Item (Join-Path $iso '*') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $td '*') -Filter '*.png' -Force -ErrorAction SilentlyContinue
# the app keeps each photo's edit (masks, links, values) in _eye_\<photo>.eye next to the photo;
# a leftover edit from an earlier run would start the test with its masks and tool links
Remove-Item (Join-Path $td '_eye_') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $debugLog -Force -ErrorAction SilentlyContinue
# the privacy policy counts as accepted (its window is tested by tools\UiAiTest), so the main window opens at once
$privacyVersion = [regex]::Match((Get-Content (Join-Path $repo 'Services\PrivacyPolicy.cs') -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
(@{ ShadowLift = 0.0; Gamma = 1.0; ShowMaskMarkers = $false; PrivacyAcceptedVersion = $privacyVersion } | ConvertTo-Json) | Set-Content (Join-Path $iso 'settings.json') -Encoding UTF8

# --------------------------------------------------------------- test photo
$photo = Join-Path $td 'photo.png'
function New-TestPhoto {
  $W = 1400; $H = 1000
  $bmp = New-Object System.Drawing.Bitmap($W, $H)
  $bmd = $bmp.LockBits((New-Object System.Drawing.Rectangle(0, 0, $W, $H)), [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
  $bytes = New-Object byte[] ($bmd.Stride * $H)
  $rng = New-Object System.Random(1234)
  $sites = @(@(0.5, 0.5), @(0.8, 0.5), @(0.25, 0.28))
  foreach ($s in $sites) {
    $cx = [int]($s[0] * $W); $cy = [int]($s[1] * $H)
    $half = 45
    for ($y = -$half; $y -le $half; $y++) {
      for ($x = -$half; $x -le $half; $x++) {
        $sx = $cx + $x; $sy = $cy + $y
        if ($sx -lt 13 -or $sy -lt 13 -or $sx -ge ($W - 13) -or $sy -ge ($H - 13)) { continue }
        $col = [math]::Floor((($x + $half) / 90.0) * 10)
        $tone = 50 + 200 * ($col / 9.0)
        $r = $tone; $g = $tone; $b = $tone
        if ($col -eq 2) { $g = $tone * 0.45; $b = $tone * 0.45 }
        if ($col -eq 4) { $r = $tone * 0.35; $b = $tone * 0.35 }
        if ($col -eq 6) { $r = $tone * 0.4 }
        if ($col -eq 8) { $r = $tone * 0.7; $g = $tone * 0.45 }
        $nz = $rng.Next(-10, 11)
        $i = $sy * $bmd.Stride + $sx * 3
        $bytes[$i] = [byte][math]::Max(0, [math]::Min(255, $b + $nz))
        $bytes[$i + 1] = [byte][math]::Max(0, [math]::Min(255, $g + $nz))
        $bytes[$i + 2] = [byte][math]::Max(0, [math]::Min(255, $r + $nz))
      }
    }
  }
  for ($y = 0; $y -lt $H; $y++) {
    $v = 77 + 91 * ($y / [double]($H - 1))
    for ($x = 0; $x -lt $W; $x++) {
      $i = $y * $bmd.Stride + $x * 3
      $already = ($bytes[$i] -ne 0 -or $bytes[$i + 1] -ne 0 -or $bytes[$i + 2] -ne 0)
      if ($already -and $x -gt 12 -and $x -lt ($W - 12) -and $y -gt 12 -and $y -lt ($H - 12)) { continue }
      $nz = $rng.Next(-4, 5)
      $r = $v + $nz; $g = $v + $nz; $b = $v + $nz
      if ($x -lt 12 -or $x -ge ($W - 12) -or $y -lt 12 -or $y -ge ($H - 12)) { $r = 235; $g = 10; $b = 235 }
      $bytes[$i] = [byte][math]::Max(0, [math]::Min(255, $b))
      $bytes[$i + 1] = [byte][math]::Max(0, [math]::Min(255, $g))
      $bytes[$i + 2] = [byte][math]::Max(0, [math]::Min(255, $r))
    }
  }
  [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $bmd.Scan0, $bytes.Length)
  $bmp.UnlockBits($bmd)
  $bmp.Save($photo, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}
$userPhoto = Join-Path $repo 'tools\test.jpg'
$Script:framed = $true
if (Test-Path $userPhoto) {
  $photo = Join-Path $td 'test.jpg'
  Copy-Item $userPhoto $photo -Force
  $Script:framed = $false
  $pimg = [System.Drawing.Image]::FromFile($photo)
  $Script:photoAspect = $pimg.Height / [double]$pimg.Width
  $Script:photoSize = ('' + $pimg.Width + 'x' + $pimg.Height)
  $pimg.Dispose()
} else {
  New-TestPhoto
  $Script:photoAspect = 1000 / 1400.0
  $Script:photoSize = '1400x1000'
}
Info ('photo: ' + $photo)

# --------------------------------------------------------------- launch
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = ('"' + $photo + '"')
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['PHOTOGRAPHEREYE_APPDATA'] = $iso
$psi.EnvironmentVariables['PHOTOGRAPHEREYE_DEBUG'] = '1'
$proc = [System.Diagnostics.Process]::Start($psi)
Info ('launched pid=' + $proc.Id)

$deadline = (Get-Date).AddSeconds(30)
$hwnd = [IntPtr]::Zero
while ($hwnd -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) {
  $proc.Refresh()
  if ($proc.HasExited) { throw 'app exited early' }
  if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle }
  else { Start-Sleep -Milliseconds 300 }
}
if ($hwnd -eq [IntPtr]::Zero) { throw 'no main window' }
[void][U]::ShowWindow($hwnd, 9)
[void][U]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 2500
$rect = New-Object 'U+RECT'
[void][U]::GetWindowRect($hwnd, [ref]$rect)
$Script:winRect = @{ L = $rect.L; T = $rect.T; R = $rect.R; B = $rect.B }
Info ('window ' + ($rect.R - $rect.L) + 'x' + ($rect.B - $rect.T))

$rootEl = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$Script:win = $null
for ($i = 0; $i -lt 40 -and -not $Script:win; $i++) {
  $Script:win = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $Script:win) { Start-Sleep -Milliseconds 250 }
}
if (-not $Script:win) { throw 'UIA window not found' }
Info 'UIA window ok'

function Find-ById([string]$id, [int]$timeoutMs = 6000) {
  $end = (Get-Date).AddMilliseconds($timeoutMs)
  $condId = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  while ($true) {
    $el = $Script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condId)
    if ($el) { return $el }
    if ((Get-Date) -gt $end) { return $null }
    Start-Sleep -Milliseconds 220
  }
}

# Render-aware wait. The app logs 'APPLY:' when a render finishes and 'CLAMP:'/'FIT:' when the
# new frame is laid out on screen. $Script:logMark is the log length at the end of the previous
# wait, so whatever appears after it was caused by the action in between.
#  - quietSec >= 1.0 ('after an action'): wait until a render-done line shows up after the mark,
#    then 150 ms of log silence; when no render line arrives within 2.5 s of the last log
#    growth (the action did not re-render), return.
#  - quietSec < 1.0 ('settle'): return after 250 ms of log silence.
$Script:logMark = 0
function Log-Len { if (Test-Path $debugLog) { return (Get-Item $debugLog).Length } else { return 0 } }
function Log-Since([long]$from) {
  try {
    $fs = [System.IO.File]::Open($debugLog, 'Open', 'Read', 'ReadWrite')
    try {
      if ($from -gt $fs.Length) { $from = 0 }
      [void]$fs.Seek($from, 'Begin')
      $sr = New-Object System.IO.StreamReader($fs)
      return $sr.ReadToEnd()
    } finally { $fs.Dispose() }
  } catch { return '' }
}
function Wait-Idle([double]$quietSec = 1.0, [int]$maxMs = 12000) {
  $start = Get-Date
  $end = $start.AddMilliseconds($maxMs)
  $render = ($quietSec -ge 1.0)
  $lastLen = Log-Len
  $lastChange = $start
  $seen = $false
  while ((Get-Date) -lt $end) {
    Start-Sleep -Milliseconds 50
    $len = Log-Len
    if ($len -ne $lastLen) { $lastLen = $len; $lastChange = Get-Date }
    $quietMs = ((Get-Date) - $lastChange).TotalMilliseconds
    if (-not $render) { if ($quietMs -ge 250) { break } else { continue } }
    if (-not $seen -and $len -gt $Script:logMark) {
      if ((Log-Since $Script:logMark) -match '(APPLY|FIT|CLAMP):') { $seen = $true }
    }
    if ($seen -and $quietMs -ge 150) { break }
    if (-not $seen -and $quietMs -ge 2500) { break }
  }
  $Script:logMark = Log-Len
}

function ScreenShot([string]$file) {
  $w = $Script:winRect.R - $Script:winRect.L
  $h = $Script:winRect.B - $Script:winRect.T
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($Script:winRect.L, $Script:winRect.T, 0, 0, (New-Object System.Drawing.Size($w, $h)))
  $g.Dispose()
  if ($file) { $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png) }
  return $bmp
}

function Get-ImageRectPlain($shot) {
  # Real photo (no magenta frame): the viewer background is a flat dark colour. Measure the
  # photo's horizontal extent along the viewer's middle row and its bottom edge along the
  # middle column; the height then follows from the photo's known aspect ratio (the top of a
  # photo can be as dark as the background, the bottom/sides are found reliably).
  $data = Buf-Of $shot
  $px = { param($x, $y) $i = $y * $data.stride + $x * 4; @($data.buf[$i + 2], $data.buf[$i + 1], $data.buf[$i]) }
  $bg = & $px 6 ([int]($data.h / 2))
  $diff = { param($x, $y) $c = & $px $x $y; [math]::Max([math]::Max([math]::Abs($c[0] - $bg[0]), [math]::Abs($c[1] - $bg[1])), [math]::Abs($c[2] - $bg[2])) }
  # viewer spans from x=0 to the right panel; find the panel edge as the first long run of non-bg
  # pixels on the far right is unreliable, so scan the middle row from both ends of the left 82%
  $yMid = [int]($data.h / 2)
  $xLimit = [int]($data.w * 0.80)
  $xCentre = [int]($xLimit * 0.5)
  $x0 = -1; $x1 = -1
  for ($x = 90; $x -lt $xLimit; $x++) { if ((& $diff $x $yMid) -gt 30) { $x0 = $x; break } }
  for ($x = $xLimit; $x -gt 90; $x--) { if ((& $diff $x $yMid) -gt 30) { $x1 = $x; break } }
  if ($x0 -lt 0 -or $x1 -le $x0 + 50) { throw 'photo not detected' }
  $xc = [int](($x0 + $x1) / 2)
  $y1 = -1
  for ($y = $data.h - 40; $y -gt $yMid; $y--) { if ((& $diff $xc $y) -gt 30) { $y1 = $y; break } }
  if ($y1 -lt 0) { throw 'photo bottom not detected' }
  $w = $x1 - $x0
  $h = [int][math]::Round($w * $Script:photoAspect)
  return @{ L = $x0; T = ($y1 - $h); R = $x1; B = $y1; W = $w; H = $h }
}

function Get-ImageRect($shot) {
  if (-not $Script:framed) { return (Get-ImageRectPlain $shot) }
  $bmp = $shot
  $r = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
  $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $stride = $d.Stride
  $buf = New-Object byte[] ($stride * $bmp.Height)
  [System.Runtime.InteropServices.Marshal]::Copy($d.Scan0, $buf, 0, $buf.Length)
  $bmp.UnlockBits($d)
  $w = $bmp.Width; $h = $bmp.Height
  $xs = New-Object int[] $w
  $ys = New-Object int[] $h
  for ($y = 0; $y -lt $h; $y++) {
    $row = $y * $stride
    for ($x = 0; $x -lt $w; $x++) {
      $i = $row + $x * 4
      if ($buf[$i + 2] -ge 140 -and $buf[$i] -ge 140 -and $buf[$i + 1] -le 110) { $xs[$x]++; $ys[$y]++ }
    }
  }
  $minCount = 24
  $x0 = -1; $x1 = -1; $y0 = -1; $y1 = -1
  for ($x = 0; $x -lt $w; $x++) { if ($xs[$x] -ge $minCount) { if ($x0 -lt 0) { $x0 = $x }; $x1 = $x } }
  for ($y = 0; $y -lt $h; $y++) { if ($ys[$y] -ge $minCount) { if ($y0 -lt 0) { $y0 = $y }; $y1 = $y } }
  if ($x0 -lt 0 -or $y0 -lt 0) { throw 'frame not detected' }
  return @{ L = $x0; T = $y0; R = $x1; B = $y1; W = ($x1 - $x0); H = ($y1 - $y0) }
}

function Buf-Of($shot) {
  $r = New-Object System.Drawing.Rectangle(0, 0, $shot.Width, $shot.Height)
  $d = $shot.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $stride = $d.Stride
  $buf = New-Object byte[] ($stride * $shot.Height)
  [System.Runtime.InteropServices.Marshal]::Copy($d.Scan0, $buf, 0, $buf.Length)
  $shot.UnlockBits($d)
  return @{ buf = $buf; stride = $stride; w = $shot.Width; h = $shot.Height }
}

function Mean-At-Buf($data, [double]$u, [double]$v, [int]$r = 6) {
  $x = [int]($Script:ir.L + $u * $Script:ir.W); $y = [int]($Script:ir.T + $v * $Script:ir.H)
  $sr = 0.0; $sg = 0.0; $sb = 0.0; $n = 0
  for ($yy = -$r; $yy -le $r; $yy += 2) {
    $py = $y + $yy
    if ($py -lt 0 -or $py -ge $data.h) { continue }
    for ($xx = -$r; $xx -le $r; $xx += 2) {
      $px = $x + $xx
      if ($px -lt 0 -or $px -ge $data.w) { continue }
      $i = $py * $data.stride + $px * 4
      $sb += $data.buf[$i]; $sg += $data.buf[$i + 1]; $sr += $data.buf[$i + 2]; $n++
    }
  }
  if ($n -eq 0) { return @(0.0, 0.0, 0.0) }
  return @(($sr / $n), ($sg / $n), ($sb / $n))
}

function Patch-Delta($a, $b) {
  $d = 0.0
  for ($i = 0; $i -lt 3; $i++) { $dd = [math]::Abs($a[$i] - $b[$i]); if ($dd -gt $d) { $d = $dd } }
  return $d
}
function Ring-Points([double]$cu, [double]$cv, [double]$du, [double]$dv) {
  # NB: in PowerShell ',' binds tighter than '-'/'+', so each coordinate needs its own parentheses
  $a = $cu - $du; $b = $cv - $dv; $c = $cu + $du; $d = $cv + $dv
  return @(@($a, $b), @($c, $b), @($a, $d), @($c, $d))
}

function Verify($phase, $target, $shot, $base, $ins, $outs, [string]$expect) {
  $sd = Buf-Of $shot
  $bd = Buf-Of $base
  $shot.Dispose(); $base.Dispose()
  $inD = 0.0
  foreach ($p in $ins) { $d = Patch-Delta (Mean-At-Buf $sd $p[0] $p[1]) (Mean-At-Buf $bd $p[0] $p[1]); if ($d -gt $inD) { $inD = $d } }
  $outD = 0.0
  foreach ($p in $outs) { $d = Patch-Delta (Mean-At-Buf $sd $p[0] $p[1]) (Mean-At-Buf $bd $p[0] $p[1]); if ($d -gt $outD) { $outD = $d } }
  $v = 'FAIL-UNKNOWN'
  if ($expect -eq 'mask') {
    if ($inD -ge 8 -and $outD -le 4) { $v = 'PASS' }
    elseif ($inD -lt 8) {
      Start-Sleep -Seconds 1.2
      $shot2 = ScreenShot $null
      $sd2 = Buf-Of $shot2
      $shot2.Dispose()
      $inD2 = 0.0
      foreach ($p in $ins) { $d = Patch-Delta (Mean-At-Buf $sd2 $p[0] $p[1]) (Mean-At-Buf $bd $p[0] $p[1]); if ($d -gt $inD2) { $inD2 = $d } }
      if ($inD2 -ge 8) {
        $sd = $sd2; $inD = $inD2
        if ($outD -le 4) { $v = 'PASS' } else { $v = 'FAIL-LEAK' }
      } else { $v = 'FAIL-NOEFFECT' }
    }
    else { $v = 'FAIL-LEAK' }
  } elseif ($expect -eq 'inverted') {
    if ($outD -ge 8 -and $inD -le 4) { $v = 'PASS' }
    elseif ($inD -ge 8) { $v = 'FAIL-LEAK-INVERTED' }
    else { $v = 'FAIL-NOEFFECT' }
  } else {
    if ($outD -ge 8) { $v = 'PASS-GLOBAL-BY-DESIGN' }
    elseif ($inD -ge 8) { $v = 'FAIL-LEAK?' }
    else { $v = 'FAIL-NOEFFECT' }
  }
  if ($v -ne 'PASS' -and $v -ne 'PASS-GLOBAL-BY-DESIGN') {
    $shotFile = Join-Path $td ((('dbg_' + $phase + '_' + $target) -replace '[^\w\-]', '_') + '.png')
    try {
      $save = New-Object System.Drawing.Bitmap $sd.w, $sd.h
      $sdv = $sd.buf
      $r2 = New-Object System.Drawing.Rectangle(0, 0, $sd.w, $sd.h)
      $d2 = $save.LockBits($r2, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
      [System.Runtime.InteropServices.Marshal]::Copy($sdv, 0, $d2.Scan0, $sdv.Length)
      $save.UnlockBits($d2)
      $save.Save($shotFile, [System.Drawing.Imaging.ImageFormat]::Png)
      $save.Dispose()
      $Script:debugShots.Add($shotFile)
    } catch { }
  }
  Add-Result $phase $target $inD $outD $v ''
  return ($v -like 'PASS*')
}

# Elements of the right panel can sit outside its ScrollViewer's viewport; their bounding rect
# then points outside the panel and a click lands on something else. Wheel the panel until the
# element is inside the panel's visible band.
function Scroll-IntoView($el) {
  if ($null -eq $Script:panelTitle) { $Script:panelTitle = Find-TitleText 'Adjustments' -Exact }
  $pt = $Script:panelTitle
  if (-not $pt) { return }
  $pr = $pt.Current.BoundingRectangle
  $top = $pr.Y + $pr.Height + 8
  $bottom = $Script:winRect.B - 40
  $wx = [int]($pr.X + 120); $wy = [int](($top + $bottom) / 2)
  for ($i = 0; $i -lt 160; $i++) {
    $r = $el.Current.BoundingRectangle
    if ($r.X -lt ($pr.X - 30)) { return }          # not a panel element
    $off = $false; try { $off = $el.Current.IsOffscreen } catch { }
    if (-not $off -and $r.Y -ge $top -and ($r.Y + $r.Height) -le $bottom) { return }
    $far = [math]::Abs($r.Y - $wy) -gt 600
    $step = 120; if ($far) { $step = 360 }
    if ($r.Y -lt $top) { [U]::Wheel($wx, $wy, $step) } else { [U]::Wheel($wx, $wy, -$step) }
    Start-Sleep -Milliseconds 90
  }
}

function Click-El($el) {
  Scroll-IntoView $el
  $rr = $el.Current.BoundingRectangle
  $cx = $rr.X + $rr.Width / 2; $cy = $rr.Y + $rr.Height / 2
  # never click outside the app window (a stray click on the taskbar/desktop opens other apps)
  if ($cx -lt ($Script:winRect.L + 4) -or $cx -gt ($Script:winRect.R - 4) -or $cy -lt ($Script:winRect.T + 30) -or $cy -gt ($Script:winRect.B - 28)) {
    Write-Host ('[WARN] click skipped, element outside the window: ' + $el.Current.AutomationId + ' "' + $el.Current.Name + '" at ' + [int]$cx + ',' + [int]$cy)
    return
  }
  [U]::Click([int]($rr.X + $rr.Width / 2), [int]($rr.Y + $rr.Height / 2))
  Start-Sleep -Milliseconds 150
}
function Abs-Pt([double]$u, [double]$v) {
  return @([int]([math]::Round($Script:winRect.L + $Script:ir.L + $u * $Script:ir.W)), [int]([math]::Round($Script:winRect.T + $Script:ir.T + $v * $Script:ir.H)))
}

function On-State($el) {
  try { $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); return ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) } catch { return $false }
}
function RangeValue($el) { return $el.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern) }
function Set-Range([string]$id, [double]$v) {
  $el = Find-ById $id
  if (-not $el) { return $null }
  $rv = RangeValue $el
  $cur = $rv.Current.Value
  $v = [math]::Max($rv.Current.Minimum, [math]::Min($rv.Current.Maximum, $v))
  try { $rv.SetValue($v) } catch { Write-Host ('[WARN] SetValue ' + $id + '=' + $v + ': ' + $_.Exception.Message); return $null }
  Start-Sleep -Milliseconds 60
  return $cur
}
function Get-Range($el) { $rv = RangeValue $el; return $rv.Current.Value }

function Open-Choice([string]$id, [int]$targetIndex) {
  $el = Find-ById $id
  if (-not $el) { return }
  $collapsed = $true
  try {
    $ec = $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec.Current.ExpandCollapseState -eq 'Expanded') { $collapsed = $false } else { $ec.Expand(); $collapsed = $false }
  } catch { }
  if ($collapsed) { Click-El $el }
  Start-Sleep -Milliseconds 420
  $items = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $names = @()
  foreach ($it in $items) {
    if ($it.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem) { $names += $it.Current.Name }
  }
  if ($names.Count -eq 0) {
    # fallback: keyboard on the dropdown
    [U]::Key(0x28, [Math]::Max(1, $targetIndex - 1))
    [U]::Key(0x0D, 1)
    Start-Sleep -Milliseconds 320
    return
  }
  $pick = -1
  $defName = $names[0]
  for ($i = 1; $i -lt $names.Count; $i++) {
    if ($names[$i] -ne $defName -and $names[$i] -notmatch 'Custom') { $pick = $i; break }
  }
  if ($targetIndex -lt 0 -and $pick -ge 0) { $targetIndex = $pick }
  if ($targetIndex -lt 0) { $targetIndex = 1 }
  $condId = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  $comboEl = $Script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condId)
  $listItems = $comboEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $idx = 0
  foreach ($it in $listItems) {
    if ($it.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { continue }
    if ($idx -eq $targetIndex) {
      $sel = $false
      try {
        $sp = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sp.Select(); $sel = $true
      } catch { }
      if (-not $sel) {
        $rr = $it.Current.BoundingRectangle
        [U]::Click([int]($rr.X + $rr.Width / 2), [int]($rr.Y + $rr.Height / 2))
      }
      break
    }
    $idx++
  }
  Start-Sleep -Milliseconds 320
  try {
    $ec2 = $comboEl.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec2.Current.ExpandCollapseState -eq 'Expanded') { $ec2.Collapse() }
  } catch { }
  Start-Sleep -Milliseconds 260
}
function Close-ChoiceDefault([string]$id) { Open-Choice $id 0 }
function Get-ChoiceSel([string]$id) {
  $el = Find-ById $id 1500
  if (-not $el) { return '' }
  try { return (($el.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() | ForEach-Object { $_.Current.Name }) -join ',') } catch { return '' }
}

# --------------------------------------------------------------- open panel + expand sections
# Section headers are plain WPF Borders (no automation peer), so their AutomationId is not in the
# UIA tree; the header's title TextBlock is. Find it by its visible text instead.
function Find-TitleText([string]$title, [switch]$Exact, [switch]$InPanel) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  $texts = $Script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  $minX = -100000
  if ($InPanel) {
    if ($null -eq $Script:panelTitle) { $Script:panelTitle = Find-TitleText 'Adjustments' -Exact }
    if ($Script:panelTitle) { $minX = $Script:panelTitle.Current.BoundingRectangle.X - 30 } else { return $null }
  }
  foreach ($t in $texts) {
    $nm = $t.Current.Name
    if ($nm -and (($Exact -and $nm -ceq $title) -or (-not $Exact -and $nm.StartsWith($title)))) {
      $r = $t.Current.BoundingRectangle
      if ($r.X -lt $minX) { continue }
      if ($r.Width -gt 2 -and $r.Height -gt 2) { return $t }
    }
  }
  return $null
}

$btnPanel = Find-ById 'BtnPanel'
if (-not $btnPanel) { throw 'BtnPanel not found' }
# BtnPanel is a toggle and the app may still be settling: click, then confirm the panel is
# really open (its MASK header has a visible rect); retry if a click was lost or closed it.
$panelOk = $false
for ($t = 0; $t -lt 4 -and -not $panelOk; $t++) {
  [void][U]::SetForegroundWindow($hwnd)
  Click-El $btnPanel
  Wait-Idle 1.0 9000
  if (Find-TitleText 'MASK') { $panelOk = $true }
  else { Info ('panel not open yet (try ' + ($t + 1) + ')') }
}
if (-not $panelOk) { throw 'adjustments panel did not open' }

function Click-HeaderByTitle([string]$title) {
  $t = Find-TitleText $title -InPanel
  if (-not $t) { return $false }
  Click-El $t
  return $true
}
function Count-PluginBodies {
  $n = 0
  foreach ($a in $Script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    $id = $a.Current.AutomationId
    if ($id -and ($id.StartsWith('PluginBody_') -or $id.StartsWith('PluginList_'))) { $n++ }
  }
  return $n
}
# Expand a plugin section without toggling an open one shut: a click that lowers the number of
# plugin bodies in the UIA tree closed the section, so click once more to reopen it.
function Expand-Section([string]$title) {
  $before = Count-PluginBodies
  if (-not (Click-HeaderByTitle $title)) { return }
  Start-Sleep -Milliseconds 250
  if ((Count-PluginBodies) -lt $before) { [void](Click-HeaderByTitle $title); Start-Sleep -Milliseconds 250 }
}

# Expand MASK by its header's state (HelpText 'expanded'/'collapsed'), never a blind toggle:
# a blind click collapses the section when the app already opened it.
$maskOk = $false
for ($t = 0; $t -lt 4 -and -not $maskOk; $t++) {
  # collapsed body is not in the UIA tree, so BtnMaskNew present == MASK expanded
  if (Find-ById 'BtnMaskNew' 1200) { $maskOk = $true; break }
  $mh = Find-TitleText 'MASK'
  if (-not $mh) { throw 'MASK section header not found' }
  Click-El $mh; Start-Sleep -Milliseconds 450
  if (Find-ById 'BtnMaskNew' 2500) { $maskOk = $true }
}
if (-not $maskOk) { throw 'BtnMaskNew not found after expanding MASK' }
Info 'mask section open'
# Sections of the excluded tools (Auto enhance, B&W classic, B&W profiles, Heal, Post-crop vignette)
# stay collapsed: they are not tested for masks, and a shorter panel scrolls less.
foreach ($nm in @('Built-in', 'Color balance', 'Split-tone grade', 'B&W red mix', 'Sky mask', 'Experimental trans-noise', 'Negative', 'Web export', 'History', 'Event log', 'Exposure info', 'Netpbm import')) {
  Expand-Section $nm
}
Wait-Idle 1.2
Info ('all sections expanded; plugin bodies in tree: ' + (Count-PluginBodies))

# --------------------------------------------------------------- calibration (image rect; retried until painted)
$Script:ir = $null
for ($try = 0; $try -lt 25 -and -not $Script:ir; $try++) {
  Start-Sleep -Milliseconds 700
  $cal = ScreenShot (Join-Path $td 'calibration.png')
  try { $Script:ir = Get-ImageRect $cal } catch { $Script:ir = $null }
  $cal.Dispose()
}
if (-not $Script:ir) { throw 'image frame never detected' }
Info ('image ' + $Script:ir.W + 'x' + $Script:ir.H + ' at ' + $Script:ir.L + ',' + $Script:ir.T)
$du = 10.0 / $Script:ir.W
$dv = 10.0 / $Script:ir.H
# Every mask sits in its own place on the photo (a new radial appears at the centre and is
# dragged there; the brush paints a band near the bottom-left):
#   Mask 1 (radial) upper-left, Mask 2 (radial) lower-right, Mask 3 (brush) bottom-left band.
$M1 = @(0.30, 0.25)
$M2 = @(0.70, 0.62)
$BR = @(0.25, 0.81)          # middle of the brush band
# outside probes, away from all three masks
$OL = @(0.88, 0.08)          # top-right corner
$BL = @(0.08, 0.55)          # left edge, middle
$MB = @(0.88, 0.92)          # bottom-right corner
$MC = @(0.50, 0.44)          # centre (no mask lives there)

function In-Set($phase) {
  if ($phase -eq 'mask2') { return (Ring-Points $M2[0] $M2[1] $du $dv) }
  if ($phase -eq 'brush') { return @(@(0.22, 0.79), @(0.25, 0.81), @(0.28, 0.83)) }
  return (Ring-Points $M1[0] $M1[1] $du $dv)
}
function Out-Set($phase) {
  if ($phase -eq 'mask1') { return @($OL, $BL, $MB, $MC, $M2, $BR) }
  if ($phase -eq 'mask2') { return @($OL, $BL, $MB, $MC, $M1, $BR) }
  if ($phase -eq 'pin') { return @($OL, $BL, $MC, $M2) }
  if ($phase -eq 'invert') { return @($OL, $BL, $MB, $MC, $M2) }
  if ($phase -eq 'brush') { return @($OL, $MB, $MC, $M1, $M2) }
  return @($OL, $BL, $MB)
}

# Drag the just-created radial (always born at the photo centre) to (u, v).
function Move-NewRadial([double]$u, [double]$v) {
  $p1 = Abs-Pt 0.5 0.5
  $p2 = Abs-Pt $u $v
  [U]::Down($p1[0], $p1[1])
  for ($i = 1; $i -le 14; $i++) { [U]::Move($p1[0] + [int](($p2[0] - $p1[0]) * $i / 14), $p1[1] + [int](($p2[1] - $p1[1]) * $i / 14)) }
  [U]::Up()
  Wait-Idle 1.0
}

# --------------------------------------------------------------- mask helpers
function Ensure-MaskSection {
  # BtnMaskNew present == panel open and MASK expanded (collapsed content leaves the UIA tree).
  if (Find-ById 'BtnMaskNew' 1200) { return }
  # (1) panel closed (no MASK title visible) - open it (BtnPanel toggles, so only when closed)
  if (-not (Find-TitleText 'MASK')) {
    $btnP = Find-ById 'BtnPanel' 2000
    if ($btnP) { Click-El $btnP; Wait-Idle 1.0 9000 }
  }
  # (2) MASK collapsed - expand it via its title text
  if (-not (Find-ById 'BtnMaskNew' 1200)) {
    $hdr = Find-TitleText 'MASK'
    if ($hdr) { Click-El $hdr; Start-Sleep -Milliseconds 420 }
  }
}

function Create-Mask([string]$kind) {
  Ensure-MaskSection
  $new = Find-ById 'BtnMaskNew'
  if (-not $new) { throw 'BtnMaskNew missing' }
  # Ensure app has focus so keyboard events land in the popup
  [void][U]::SetForegroundWindow($hwnd)
  Start-Sleep -Milliseconds 120
  Click-El $new
  Start-Sleep -Milliseconds 650
  # WPF ContextMenu popups are not reliably found via UIA AutomationId search from the
  # desktop root; navigate by keyboard: first Down focuses item 0 (Radial), subsequent
  # Downs advance through the list, then Enter invokes.
  $kinds = @('radial', 'linear', 'brush', 'luma')
  $idx = [array]::IndexOf($kinds, $kind)
  if ($idx -lt 0) { throw ('unknown mask kind: ' + $kind) }
  for ($d = 0; $d -le $idx; $d++) { [U]::Key(0x28, 1) }  # VK_DOWN
  [U]::Key(0x0D, 1)  # VK_RETURN
  Wait-Idle 1.1 9000
}
function Tint-Off {
  $t = Find-ById 'BtnMaskTint' 2500
  if ($t -and (On-State $t)) { Click-El $t }
}
function Mask-Done {
  $d = Find-ById 'BtnMaskDone' 2500
  if ($d) { Click-El $d }
  Wait-Idle 1.1
}

# --------------------------------------------------------------- cases
$CORE = @(
  @{ id = 'SliderExposure'; v = 1.2 }
  @{ id = 'SliderContrast'; v = -85 }
  @{ id = 'SliderHighlights'; v = -90 }
  @{ id = 'SliderShadows'; v = -90 }
  @{ id = 'SliderWhites'; v = -90 }
  @{ id = 'SliderBlacks'; v = 90 }
  @{ id = 'SliderTemp'; v = -80 }
  @{ id = 'SliderTint'; v = -70 }
  @{ id = 'SliderClarity'; v = -85 }
  @{ id = 'SliderSaturation'; v = -100 }
  @{ id = 'SliderSharpen'; v = 150 }
  @{ id = 'SliderDehaze'; v = -90 }
  @{ id = 'SliderDenoise'; v = 200 }
  @{ id = 'SliderColorNoise'; v = 200 }
)

function Run-CoreSliders($phase) {
  $ins = In-Set $phase
  $outs = Out-Set $phase
  foreach ($c in $CORE) {
    Wait-Idle 0.7
    $base = ScreenShot $null
    $before = Set-Range $c.id $c.v
    if ($null -eq $before) { Add-Result $phase ('core/' + $c.id) 0 0 'FAIL-NOTFOUND' ''; continue }
    Wait-Idle 1.0 14000
    $shot = ScreenShot $null
    Verify $phase ('core/' + $c.id) $shot $base $ins $outs 'mask' > $null
    Set-Range $c.id 0.0
    Wait-Idle 1.0
  }
}

# Plugins NOT tested for masks (by the user's decision): Auto enhance, B&W classic, B&W profiles,
# Heal, Post-crop vignette. Matched against the tool's full id (panel params and toolbar buttons).
$Script:excludeTools = 'autoenhance|bwclassic|bwprofiles|heal|vignette'

function Get-FilterList($onlyIds) {
  # PluginBody_/PluginMaskBox_ are StackPanels (no automation peer) and never reach the UIA tree;
  # the param controls (PluginParam_<fullId>_<param>) and the scope button (PluginMaskScope_<fullId>)
  # do. Maskable = has a Mask scope button. Params are grouped by the longest matching fullId.
  $maskable = @{}
  $params = New-Object System.Collections.Generic.List[object]
  $walk = $Script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($a in $walk) {
    $aid = $a.Current.AutomationId
    if (-not $aid) { continue }
    if ($aid.StartsWith('PluginMaskScope_') -and -not $aid.StartsWith('PluginMaskScope_core:adjustments')) { $maskable[$aid.Substring(16)] = $true }
    if ($aid.StartsWith('PluginParam_')) { $params.Add($a) }
  }
  $groups = [ordered]@{}
  foreach ($p in $params) {
    $rest = $p.Current.AutomationId.Substring(12)
    $fid = $null
    foreach ($k in $maskable.Keys) { if ($rest.StartsWith($k + '_') -and (-not $fid -or $k.Length -gt $fid.Length)) { $fid = $k } }
    if (-not $fid) {
      $colon = $rest.IndexOf(':')
      $us = $rest.IndexOf('_', [math]::Max(0, $colon))
      if ($us -lt 0) { continue }
      $fid = $rest.Substring(0, $us)
    }
    if (-not $groups.Contains($fid)) { $groups[$fid] = New-Object System.Collections.Generic.List[object] }
    $groups[$fid].Add($p)
  }
  $list = New-Object System.Collections.Generic.List[object]
  foreach ($fullId in $groups.Keys) {
    if ($onlyIds -and -not ($onlyIds -contains $fullId)) { continue }
    if ($fullId -match $Script:excludeTools) { continue }
    if ($fullId -like '*heal*') {
      $list.Add([pscustomobject]@{ FullId = $fullId; Maskable = $false; Skips = 'point-based tool (spots added by clicking the photo)'; Sliders = @(); Toggles = @(); Choices = @() })
      continue
    }
    $sliders = @(); $toggles = @(); $choices = @(); $skips = @()
    foreach ($ch in $groups[$fullId]) {
      $chid = $ch.Current.AutomationId
      $ct = $ch.Current.ControlType
      $prmId = $chid.Substring(('PluginParam_' + $fullId + '_').Length)
      if ($ct -eq [System.Windows.Automation.ControlType]::Slider) { $sliders += @($prmId, $chid) }
      elseif ($ct -eq [System.Windows.Automation.ControlType]::CheckBox -or $ct -eq [System.Windows.Automation.ControlType]::ToggleButton) { $toggles += @($prmId, $chid) }
      elseif ($ct -eq [System.Windows.Automation.ControlType]::ComboBox) { $choices += @($prmId, $chid) }
      else { $skips += $prmId }
    }
    $noteSkips = ''
    if ($skips.Count -gt 0) { $noteSkips = ($skips -join ',') + ' (not editable params)' }
    $list.Add([pscustomobject]@{ FullId = $fullId; Maskable = $maskable.ContainsKey($fullId); Skips = $noteSkips; Sliders = $sliders; Toggles = $toggles; Choices = $choices })
  }
  return $list
}

# A tool works on the whole photo until it is pointed at a mask. While a mask is edited, a tool
# without a mask shows 'Work inside Mask N' (PluginMaskInsideEdited_<key>) - that links it.
function Link-ToEditedMask([string]$key) {
  $b = Find-ById ('PluginMaskInsideEdited_' + $key) 1500
  if (-not $b) { return $false }
  Click-El $b
  Wait-Idle 1.0 9000
  return $true
}
# Back to the whole photo: the scope menu's first item is 'Whole photo' (keyboard; WPF popups
# are not reliably reachable through UIA).
function Unlink-Tool([string]$key) {
  $b = Find-ById ('PluginMaskScope_' + $key) 1500
  if (-not $b) { return }
  if ($b.Current.Name -like '*Whole photo*') { return }
  [void][U]::SetForegroundWindow($hwnd)
  Click-El $b
  Start-Sleep -Milliseconds 550
  [U]::Key(0x28, 1); [U]::Key(0x0D, 1)
  Wait-Idle 1.0 9000
}

# After a tool: the photo must look as before it (every in/out probe within 4/255). A leftover is
# reported as FAIL-STATE (with a debug screenshot) so the following results can be read with it in mind.
function Check-Restored($phase, [string]$fullId, $refBuf, $points) {
  Wait-Idle 1.0
  $now = ScreenShot $null
  $nb = Buf-Of $now
  $worst = 0.0
  foreach ($p in $points) { $d = Patch-Delta (Mean-At-Buf $nb $p[0] $p[1]) (Mean-At-Buf $refBuf $p[0] $p[1]); if ($d -gt $worst) { $worst = $d } }
  if ($worst -gt 4) {
    $file = Join-Path $td ((('dbg_' + $phase + '_restore_' + $fullId) -replace '[^\w\-]', '_') + '.png')
    try { $now.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $Script:debugShots.Add($file) } catch { }
    Add-Result $phase ('tool/' + $fullId + '/restore') $worst 0 'FAIL-STATE' 'photo did not return to its look before the tool - later results of this phase may be affected'
  }
  $now.Dispose()
}

function Run-Filters($phase, $onlyIds = $null, [switch]$KeepLink) {
  $ins = In-Set $phase
  $outs = Out-Set $phase
  $filters = Get-FilterList $onlyIds
  Info ('[' + $phase + '] tools found: ' + $filters.Count)
  foreach ($f in $filters) {
    $expect = 'mask'
    $note = ''
    if (-not $f.Maskable) {
      if (-not ($f.FullId -like '*heal*')) {
        $expect = 'global'
        $note = 'tool has no mask row: whole photo by design'
      } else { $expect = 'none' }
    }
    $drivable = ($f.Sliders.Count -gt 0 -or $f.Toggles.Count -gt 0 -or $f.Choices.Count -gt 0)
    if (-not $drivable) {
      Add-Result $phase ('tool/' + $f.FullId) 0 0 'SKIPPED' $f.Skips
      continue
    }
    # reference of the photo before the tool is touched: after the tool every probe must be back here,
    # otherwise a leftover (e.g. a choice that did not reset) would spoil all the following tests
    Wait-Idle 0.7
    $refShot = ScreenShot $null
    $refBuf = Buf-Of $refShot
    $refShot.Dispose()
    $linked = $false
    if ($f.Maskable) {
      $linked = Link-ToEditedMask $f.FullId
      if (-not $linked) {
        $sb = Find-ById ('PluginMaskScope_' + $f.FullId) 800
        if (-not ($sb -and $sb.Current.Name -like '*Inside*')) {
          Add-Result $phase ('tool/' + $f.FullId) 0 0 'FAIL-NOLINK' 'Work inside button missing - tool could not be pointed at the edited mask'
          continue
        }
      }
    }
    for ($k = 0; $k -lt $f.Sliders.Count; $k += 2) {
      $prmId = $f.Sliders[$k]; $chid = $f.Sliders[$k + 1]
      Wait-Idle 0.7
      $base = ScreenShot $null
      $before = Set-Range $chid 0 2>$null
      if ($null -eq $before) { Add-Result $phase ('tool/' + $f.FullId + '/' + $prmId) 0 0 'FAIL-NOTFOUND' ''; continue }
      $el = Find-ById $chid 1500
      if (-not $el) { continue }
      $rv = RangeValue $el
      $lo = $rv.Current.Minimum; $hi = $rv.Current.Maximum
      $cur = Get-Range $el
      $strong = 0.0
      if ($lo -lt -0.5) { $strong = $lo + 0.1 * ($hi - $lo) } else { $strong = $lo + 0.9 * ($hi - $lo) }
      $rv.SetValue($strong)
      Wait-Idle 1.0 14000
      $shot = ScreenShot $null
      $t = $expect
      if ($t -eq 'none') { Add-Result $phase ('tool/' + $f.FullId + '/' + $prmId) 0 0 'SKIPPED' 'no mask row: point-based'; continue }
      Verify $phase ('tool/' + $f.FullId + '/' + $prmId) $shot $base $ins $outs $t > $null
      Set-Range $chid $cur
      Wait-Idle 1.0
    }
    for ($k = 0; $k -lt $f.Toggles.Count; $k += 2) {
      $prmId = $f.Toggles[$k]; $chid = $f.Toggles[$k + 1]
      $el = Find-ById $chid 1500
      if (-not $el) { continue }
      if (On-State $el) { Add-Result $phase ('tool/' + $f.FullId + '/' + $prmId) 0 0 'SKIPPED' 'default ON: switching off has no visual effect'; continue }
      Wait-Idle 0.7
      $base = ScreenShot $null
      Click-El $el
      Wait-Idle 1.0 14000
      $shot = ScreenShot $null
      $t = $expect
      if ($t -ne 'none') { Verify $phase ('tool/' + $f.FullId + '/' + $prmId + '=on') $shot $base $ins $outs $t > $null } else { $shot.Dispose(); $base.Dispose(); Add-Result $phase ('tool/' + $f.FullId + '/' + $prmId) 0 0 'SKIPPED' 'point-based' }
      $el2 = Find-ById $chid 1500
      if ($el2 -and (On-State $el2)) { Click-El $el2 }
      Wait-Idle 1.0
    }
    for ($k = 0; $k -lt $f.Choices.Count; $k += 2) {
      $prmId = $f.Choices[$k]; $chid = $f.Choices[$k + 1]
      Wait-Idle 0.7
      $origSel = Get-ChoiceSel $chid
      $base = ScreenShot $null
      Open-Choice $chid -1
      Wait-Idle 1.0 14000
      $shot = ScreenShot $null
      $t = $expect
      if ($t -ne 'none') { Verify $phase ('tool/' + $f.FullId + '/' + $prmId + '#alt') $shot $base $ins $outs $t > $null } else { $shot.Dispose(); $base.Dispose(); Add-Result $phase ('tool/' + $f.FullId + '/' + $prmId) 0 0 'SKIPPED' 'point-based' }
      Open-Choice $chid 0
      Wait-Idle 1.0
      # the reset must really land on the original item (a missed reset left the photo B&W before)
      for ($r = 0; $r -lt 2 -and (Get-ChoiceSel $chid) -ne $origSel; $r++) {
        Write-Host ('[WARN] ' + $chid + ' still "' + (Get-ChoiceSel $chid) + '", resetting to "' + $origSel + '" again')
        Open-Choice $chid 0
        Wait-Idle 1.0
      }
    }
    if ($f.Skips -and $f.Skips -ne '') {
      Add-Result $phase ('tool/' + $f.FullId) 0 0 'SKIPPED-PART' ('not driven: ' + $f.Skips)
    }
    if ($f.Maskable -and -not $KeepLink) { Unlink-Tool $f.FullId }
    Check-Restored $phase $f.FullId $refBuf ($ins + $outs)
  }
  if ($phase -ne 'pin' -and $phase -ne 'invert') {
    $allEls = $Script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $tbIds = @()
    foreach ($t in $allEls) {
      $tid = $t.Current.AutomationId
      if ($tid -and $tid.StartsWith('PluginBtn_') -and -not ($tid -match $Script:excludeTools)) { $tbIds += $tid }
    }
    foreach ($tid in $tbIds) {
      $fullId = $tid.Substring(10)
      $el = Find-ById $tid
      if (-not $el) { continue }
      if (On-State $el) { continue }
      Wait-Idle 0.7
      $base = ScreenShot $null
      Click-El $el
      Wait-Idle 1.1 14000
      $shot = ScreenShot $null
      Verify $phase ('toolbar/' + $fullId + '=on') $shot $base $ins $outs 'mask' > $null
      $el2 = Find-ById $tid
      if ($el2 -and (On-State $el2)) { Click-El $el2 }
      Wait-Idle 1.0
    }
  }
}

# --------------------------------------------------------------- mask 1
Create-Mask 'radial'
Move-NewRadial $M1[0] $M1[1]
Tint-Off
Wait-Idle 0.5
Run-CoreSliders 'mask1'
Run-Filters 'mask1'
Mask-Done

# --------------------------------------------------------------- mask 2 (lower-right)
Create-Mask 'radial'
Move-NewRadial $M2[0] $M2[1]
Tint-Off
Wait-Idle 0.5
Run-CoreSliders 'mask2'
Run-Filters 'mask2'
Mask-Done

# --------------------------------------------------------------- pin click (mask 1)
$mk = Find-ById 'BtnMaskMarkers' 3000
if ($mk -and -not (On-State $mk)) { Click-El $mk }
Wait-Idle 1.0
$pp = Abs-Pt $M1[0] $M1[1]
[U]::Click($pp[0], $pp[1])
Wait-Idle 1.0
if (-not (Find-ById 'BtnMaskDone' 2500)) { Add-Result 'pin' 'pin-click' 0 0 'FAIL-NOTFOUND' 'clicking the Mask 1 pin did not enter mask editing' }
foreach ($sid in @('SliderExposure', 'SliderContrast', 'SliderSaturation')) {
  $v = 0.0
  foreach ($c in $CORE) { if ($c.id -eq $sid) { $v = $c.v } }
  Wait-Idle 0.7
  $base = ScreenShot $null
  Set-Range $sid $v
  Wait-Idle 1.0 14000
  $shot = ScreenShot $null
  Verify 'pin' ('core/' + $sid) $shot $base (In-Set 'mask1') (Out-Set 'pin') 'mask' > $null
  Set-Range $sid 0.0
  Wait-Idle 1.0
}
Run-Filters 'pin' @('photographereye.colorbalance:midtones') -KeepLink
Wait-Idle 1.0

# --------------------------------------------------------------- invert
function Get-InvertCheckbox([string]$fullId) {
  # the mask box is a StackPanel (not in the UIA tree): take the 'Invert' checkbox that sits
  # closest below the tool's Mask scope button
  $sb = Find-ById ('PluginMaskScope_' + $fullId)
  if (-not $sb) { throw ('mask scope button not found: ' + $fullId) }
  Scroll-IntoView $sb
  $sy = $sb.Current.BoundingRectangle.Y
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
  $best = $null; $bestDy = 1e9
  foreach ($cb in $Script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    if ($cb.Current.Name -ne 'Invert') { continue }
    $dy = $cb.Current.BoundingRectangle.Y - $sy
    if ($dy -ge 0 -and $dy -lt $bestDy) { $best = $cb; $bestDy = $dy }
  }
  if (-not $best -or $bestDy -gt 200) { throw ('Invert checkbox not found under ' + $fullId) }
  return $best
}
$invTool = 'photographereye.colorbalance:midtones'
$invEl = Get-InvertCheckbox $invTool
if (-not (On-State $invEl)) { Click-El $invEl }
Wait-Idle 1.2
Add-Result 'invert' ('link-invert/' + $invTool) 0 0 'STEP' 'Invert ON: the tool now works outside Mask 1'
Wait-Idle 1.0
$base = ScreenShot $null
$inv = Set-Range 'PluginParam_photographereye.colorbalance:midtones_cr' -90
if ($null -eq $inv) { Add-Result 'invert' 'missing param' 0 0 'FAIL-NOTFOUND' '' }
else {
  Wait-Idle 1.0 14000
  $shot = ScreenShot $null
  Verify 'invert' ('tool/' + $invTool + '/cr-outside') $shot $base (In-Set 'mask1') (Out-Set 'invert') 'inverted' > $null
  Set-Range 'PluginParam_photographereye.colorbalance:midtones_cr' 0.0
}
Wait-Idle 1.0
$invEl2 = Get-InvertCheckbox $invTool
if (On-State $invEl2) { Click-El $invEl2 }
Wait-Idle 1.0
$base = ScreenShot $null
Set-Range 'SliderExposure' 1.0
Wait-Idle 1.0 14000
$shot = ScreenShot $null
Verify 'invert' 'core/SliderExposure-inside' $shot $base (In-Set 'mask1') (Out-Set 'invert') 'mask' > $null
Set-Range 'SliderExposure' 0.0
Wait-Idle 1.0
Unlink-Tool $invTool    # back to the whole photo, so the brush phase links it afresh
Mask-Done
Wait-Idle 1.0

# --------------------------------------------------------------- brush mask
Create-Mask 'brush'
$bp = Abs-Pt 0.10 0.75
$bx = $bp[0]
$bw = [int](0.30 * $Script:ir.W)
$brpx = [int](0.04 * $Script:ir.W)
$byBase = $bp[1]
for ($k = 0; $k -lt 4; $k++) {
  $y0 = $byBase + [int]($brpx * 0.85 * $k) + [int]($brpx * 0.6)
  [U]::Down($bx, $y0)
  for ($i = 1; $i -le 10; $i++) { [U]::Move($bx + [int]($bw * $i / 10), $y0) }
  [U]::Up()
  Start-Sleep -Milliseconds 160
}
Wait-Idle 1.8
Tint-Off
Start-Sleep -Milliseconds 350
Run-CoreSliders 'brush'
Run-Filters 'brush'
Mask-Done
Wait-Idle 1.0

$mk2 = Find-ById 'BtnMaskMarkers' 3000
if ($mk2 -and (On-State $mk2)) { Click-El $mk2 }

Info 'done; writing report'
$nl = [string][char]10
$md = New-Object System.Collections.Generic.List[string]
$md.Add('# UI-TEST - Maski w PhotographerEye' + $nl)
$md.Add('Data: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + $nl)
$md.Add('Exe: ' + $exe + $nl)
$md.Add('APPDATA (izolowany): ' + $iso + $nl)
if ($Script:framed) { $md.Add('Zdjecie: ' + $photo + ' (1400x1000; gradient + rampy tonalno-kolorowe + noise + ramka magenta)' + $nl) }
else { $md.Add('Zdjecie: ' + $userPhoto + ' (' + $Script:photoSize + '; prawdziwe zdjecie, ramka wykrywana z tla)' + $nl) }
$md.Add('Metoda: UIA (klik/drag/suwaki RangeValue); weryfikacja = srednia RGB z patcha 6px przed/po czynnosci (kropka = 5x5 probek co 2px). IN = ring 10px wokol srodka maski (4 punkty) lub wnetrze polecia szczotki; OUT = punkty kontrolne (rog, dole, srodki pozostalych masek). Progi: PASS gdy IN >= 8 i OUT <= 4 (0-255). FAIL-STATE = po narzedziu zdjecie nie wrocilo do wygladu sprzed niego.' + $nl)
$md.Add('Pominiete (nie testowane dla masek): Auto enhance, B&W classic, B&W profiles, Heal, Post-crop vignette.' + $nl)
$labels = @{ mask1 = '1) Maska radial Mask 1 (lewa gora, 30%/25%) - wszystkie regulacje i efekty'; mask2 = '2) Druga maska radial Mask 2 (prawy dol, 70%/62%) - wszystkie regulacje i efekty'; pin = '3) Klik w pin Mask 1 na zdjeciu - przykladowe operacje'; invert = '4) Invert - czynnosc narzedzia na zewnatrz Mask 1, srodek bez zmian'; brush = '5) Maska brush (pas u dolu po lewej) - ustawienia tylko w wymalowanym obszarze' }
foreach ($pn in @('mask1', 'mask2', 'pin', 'invert', 'brush')) {
  $rows = $Script:results | Where-Object { $_.Phase -eq $pn }
  if ($rows.Count -eq 0) { continue }
  $md.Add('## ' + $labels[$pn] + $nl)
  $md.Add('| Cel | IN d | OUT d | Werdykt |' + $nl)
  $md.Add('|---|---|---|---|' + $nl)
  foreach ($r in $rows) { $md.Add(('| {0} | {1} | {2} | {3} |' -f $r.Target, $r.In, $r.Out, $r.Verdict) + $nl) }
  $md.Add($nl)
}
$pass = ($Script:results | Where-Object { $_.Verdict -like 'PASS*' -or $_.Verdict -eq 'STEP' -or $_.Verdict -like 'SKIPPED*' }).Count
$fail = $Script:results.Count - $pass
$md.Add('**Wynik: PASS=' + $pass + '; nieliczace-do-PASS wiersze=' + $fail + ' z ' + $Script:results.Count + ' cznie**' + $nl)
if ($Script:debugShots.Count -gt 0) {
  $md.Add('Screenshoty przypadkw: ' + $nl)
  foreach ($s in $Script:debugShots) { $md.Add('- ' + $s + $nl) }
}
[System.IO.File]::WriteAllLines($report, $md)
Write-Host ('REPORT: ' + $report)
Write-Host ('PASS=' + $pass + ' FAIL=' + $fail)
Info 'app left running for inspection'
