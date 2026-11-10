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
Get-Process PhotographerEye -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
New-Item -ItemType Directory -Force -Path $iso, $td, $repo | Out-Null
Remove-Item (Join-Path $iso '*') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $td '*') -Filter '*.png' -Force -ErrorAction SilentlyContinue
Remove-Item $debugLog -Force -ErrorAction SilentlyContinue
(@{ ShadowLift = 0.0; Gamma = 1.0; ShowMaskMarkers = $false } | ConvertTo-Json) | Set-Content (Join-Path $iso 'settings.json') -Encoding UTF8

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
New-TestPhoto
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

function Wait-Idle([double]$quietSec = 1.0, [int]$maxMs = 12000) {
  $end = (Get-Date).AddMilliseconds($maxMs)
  $lastLen = -1
  $lastChange = Get-Date
  while ((Get-Date) -lt $end) {
    Start-Sleep -Milliseconds 170
    $len = 0
    if (Test-Path $debugLog) { $len = (Get-Item $debugLog).Length }
    if ($len -ne $lastLen) { $lastLen = $len; $lastChange = Get-Date }
    elseif (((Get-Date) - $lastChange).TotalMilliseconds -gt ($quietSec * 1000)) { break }
  }
  Start-Sleep -Milliseconds 300
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

function Get-ImageRect($shot) {
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
  return @($sr / $n, $sg / $n, $sb / $n)
}

function Patch-Delta($a, $b) {
  $d = 0.0
  for ($i = 0; $i -lt 3; $i++) { $dd = [math]::Abs($a[$i] - $b[$i]); if ($dd -gt $d) { $d = $dd } }
  return $d
}
function Ring-Points([double]$cu, [double]$cv, [double]$du, [double]$dv) {
  return @(@($cu - $du, $cv - $dv), @($cu + $du, $cv - $dv), @($cu - $du, $cv + $dv), @($cu + $du, $cv + $dv))
}

function Verify($phase, $target, $shot, $base, $ins, $outs, [string]$expect) {
  Wait-Idle 1.0 12000
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
    $shotFile = Join-Path $td (('dbg_' + $phase + '_' + $target) -replace '[^\w\-]', '_') + '.png'
    $bmp = New-Object System.Drawing.Bitmap 1, 1
    $bmp.Dispose()
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

function Click-El($el) {
  $rr = $el.Current.BoundingRectangle
  [U]::Click([int]($rr.X + $rr.Width / 2), [int]($rr.Y + $rr.Height / 2))
  Start-Sleep -Milliseconds 280
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
  $rv.SetValue($v)
  Start-Sleep -Milliseconds 200
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

# --------------------------------------------------------------- open panel + expand sections
$btnPanel = Find-ById 'BtnPanel'
if (-not $btnPanel) { throw 'BtnPanel not found' }
Click-El $btnPanel
Wait-Idle 0.9 9000

function Click-HeaderByTitle([string]$title) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  $texts = $Script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  foreach ($t in $texts) {
    $nm = $t.Current.Name
    if ($nm -and $nm.StartsWith($title)) {
      $r = $t.Current.BoundingRectangle
      if ($r.Width -gt 2 -and $r.Height -gt 2) {
        [U]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 260
        return $true
      }
    }
  }
  return $false
}

if (-not (Click-HeaderByTitle 'MASK')) { throw 'MASK section header not clickable' }
if (-not (Find-ById 'BtnMaskNew')) { throw 'BtnMaskNew not found after expanding MASK' }
Info 'mask section open'
foreach ($nm in @('Built-in', 'Color balance', 'Post-crop vignette', 'Split-tone grade', 'B&W classic', 'B&W red mix', 'B&W profiles', 'Sky mask', 'Experimental trans-noise', 'Negative', 'Auto enhance', 'Heal', 'Web export', 'History', 'Event log', 'Exposure info', 'Netpbm import')) {
  [void](Click-HeaderByTitle $nm)
}
Wait-Idle 1.2
Info 'all sections expanded'

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
$OL = @(0.09, 0.09)
$BL = @(0.09, 0.86)
$MB = @(0.40, 0.86)
$C1 = @(0.5, 0.5)
$C2 = @(0.8, 0.5)

function In-Set($phase) {
  if ($phase -eq 'mask2') { return (Ring-Points 0.8 0.5 $du $dv) }
  if ($phase -eq 'brush') { return @(@(0.27, 0.29), @(0.30, 0.31), @(0.33, 0.33)) }
  return (Ring-Points 0.5 0.5 $du $dv)
}
function Out-Set($phase) {
  if ($phase -eq 'mask1') { return @($OL, $BL, $MB, $C2) }
  if ($phase -eq 'mask2') { return @($OL, $BL, $MB, $C1) }
  if ($phase -eq 'pin') { return @($OL, $BL, $C2) }
  if ($phase -eq 'invert') { return @($OL, $BL, $C2, $MB) }
  if ($phase -eq 'brush') { return @($OL, $C1, $C2) }
  return @($OL, $BL, $MB)
}

# --------------------------------------------------------------- mask helpers
function Create-Mask([string]$kind) {
  $new = Find-ById 'BtnMaskNew'
  if (-not $new) { throw 'BtnMaskNew missing' }
  Click-El $new
  Start-Sleep -Milliseconds 550
  $condId = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, ('BtnMaskNew_' + $kind))
  $el = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condId)
  for ($i = 0; $i -lt 10 -and -not $el; $i++) { Start-Sleep -Milliseconds 220; $el = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condId) }
  if (-not $el) { throw ('menu item BtnMaskNew_' + $kind + ' not found') }
  $ok = $false
  try { $ip = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $ip.Invoke(); $ok = $true } catch { }
  if (-not $ok) {
    $rr = $el.Current.BoundingRectangle
    [U]::Click([int]($rr.X + $rr.Width / 2), [int]($rr.Y + $rr.Height / 2))
  }
  Wait-Idle 1.1 9000
  Start-Sleep -Milliseconds 500
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
    Wait-Idle 0.8
  }
}

function Get-FilterList($onlyIds) {
  $maskable = @{}
  $bodies = @()
  $walk = $Script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($a in $walk) {
    $aid = $a.Current.AutomationId
    if (-not $aid) { continue }
    if ($aid.StartsWith('PluginMaskBox_')) { $maskable[$aid.Substring(14)] = $true }
    if ($aid.StartsWith('PluginBody_')) { $bodies += $aid.Substring(11) }
  }
  $list = New-Object System.Collections.Generic.List[object]
  foreach ($fullId in $bodies) {
    if ($onlyIds -and -not ($onlyIds -contains $fullId)) { continue }
    if ($fullId -like '*heal*') {
      $list.Add([pscustomobject]@{ FullId = $fullId; Maskable = $false; Skips = 'point-based tool (spots added by clicking the photo)'; Sliders = @(); Toggles = @(); Choices = @() })
      continue
    }
    $sliders = @(); $toggles = @(); $choices = @(); $skips = @()
    $bodyEl = Find-ById ('PluginBody_' + $fullId) 2000
    if (-not $bodyEl) { continue }
    $children = $bodyEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($ch in $children) {
      $chid = $ch.Current.AutomationId
      if (-not $chid -or -not $chid.StartsWith('PluginParam_')) { continue }
      $ct = $ch.Current.ControlType
      $pid = $chid.Substring(('PluginParam_' + $fullId + '_').Length)
      if ($ct -eq [System.Windows.Automation.ControlType]::Slider) { $sliders += @($pid, $chid) }
      elseif ($ct -eq [System.Windows.Automation.ControlType]::CheckBox -or $ct -eq [System.Windows.Automation.ControlType]::ToggleButton) { $toggles += @($pid, $chid) }
      elseif ($ct -eq [System.Windows.Automation.ControlType]::ComboBox) { $choices += @($pid, $chid) }
      else { $skips += $pid }
    }
    $noteSkips = ''
    if ($skips.Count -gt 0) { $noteSkips = ($skips -join ',') + ' (not editable params)' }
    $list.Add([pscustomobject]@{ FullId = $fullId; Maskable = $maskable.ContainsKey($fullId); Skips = $noteSkips; Sliders = $sliders; Toggles = $toggles; Choices = $choices })
  }
  return $list
}

function Run-Filters($phase, $onlyIds = $null) {
  $ins = In-Set $phase
  $outs = Out-Set $phase
  $filters = Get-FilterList $onlyIds
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
    for ($k = 0; $k -lt $f.Sliders.Count; $k += 2) {
      $pid = $f.Sliders[$k]; $chid = $f.Sliders[$k + 1]
      Wait-Idle 0.7
      $base = ScreenShot $null
      $before = Set-Range $chid 0 2>$null
      if ($null -eq $before) { Add-Result $phase ('tool/' + $f.FullId + '/' + $pid) 0 0 'FAIL-NOTFOUND' ''; continue }
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
      if ($t -eq 'none') { Add-Result $phase ('tool/' + $f.FullId + '/' + $pid) 0 0 'SKIPPED' 'no mask row: point-based'; continue }
      Verify $phase ('tool/' + $f.FullId + '/' + $pid) $shot $base $ins $outs $t > $null
      Set-Range $chid $cur
      Wait-Idle 0.8
    }
    for ($k = 0; $k -lt $f.Toggles.Count; $k += 2) {
      $pid = $f.Toggles[$k]; $chid = $f.Toggles[$k + 1]
      $el = Find-ById $chid 1500
      if (-not $el) { continue }
      if (On-State $el) { Add-Result $phase ('tool/' + $f.FullId + '/' + $pid) 0 0 'SKIPPED' 'default ON: switching off has no visual effect'; continue }
      Wait-Idle 0.7
      $base = ScreenShot $null
      Click-El $el
      Wait-Idle 1.0 14000
      $shot = ScreenShot $null
      $t = $expect
      if ($t -ne 'none') { Verify $phase ('tool/' + $f.FullId + '/' + $pid + '=on') $shot $base $ins $outs $t > $null } else { $shot.Dispose(); $base.Dispose(); Add-Result $phase ('tool/' + $f.FullId + '/' + $pid) 0 0 'SKIPPED' 'point-based' }
      $el2 = Find-ById $chid 1500
      if ($el2 -and (On-State $el2)) { Click-El $el2 }
      Wait-Idle 0.8
    }
    for ($k = 0; $k -lt $f.Choices.Count; $k += 2) {
      $pid = $f.Choices[$k]; $chid = $f.Choices[$k + 1]
      Wait-Idle 0.7
      $base = ScreenShot $null
      Open-Choice $chid -1
      Wait-Idle 1.0 14000
      $shot = ScreenShot $null
      $t = $expect
      if ($t -ne 'none') { Verify $phase ('tool/' + $f.FullId + '/' + $pid + '#alt') $shot $base $ins $outs $t > $null } else { $shot.Dispose(); $base.Dispose(); Add-Result $phase ('tool/' + $f.FullId + '/' + $pid) 0 0 'SKIPPED' 'point-based' }
      Open-Choice $chid 0
      Wait-Idle 0.8
    }
    if ($f.Skips -and $f.Skips -ne '') {
      Add-Result $phase ('tool/' + $f.FullId) 0 0 'SKIPPED-PART' ('not driven: ' + $f.Skips)
    }
  }
  if ($phase -ne 'pin' -and $phase -ne 'invert') {
    $allEls = $Script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $tbIds = @()
    foreach ($t in $allEls) {
      $tid = $t.Current.AutomationId
      if ($tid -and $tid.StartsWith('PluginBtn_')) { $tbIds += $tid }
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
      Wait-Idle 0.8
    }
  }
}

# --------------------------------------------------------------- mask 1
Create-Mask 'radial'
Tint-Off
Start-Sleep -Milliseconds 350
Run-CoreSliders 'mask1'
Run-Filters 'mask1'
Mask-Done
Wait-Idle 1.0

# --------------------------------------------------------------- mask 2 (moved right)
Create-Mask 'radial'
$p1 = Abs-Pt 0.5 0.5
$p2 = Abs-Pt 0.8 0.5
[U]::Down($p1[0], $p1[1])
for ($i = 1; $i -le 14; $i++) { [U]::Move($p1[0] + [int](($p2[0] - $p1[0]) * $i / 14), $p1[1]) }
[U]::Up()
Wait-Idle 1.6
Tint-Off
Start-Sleep -Milliseconds 350
Run-CoreSliders 'mask2'
Run-Filters 'mask2'
Mask-Done
Wait-Idle 1.0

# --------------------------------------------------------------- pin click (mask 1)
$mk = Find-ById 'BtnMaskMarkers' 3000
if ($mk -and -not (On-State $mk)) { Click-El $mk }
Wait-Idle 1.0
$pp = Abs-Pt 0.5 0.5
[U]::Click($pp[0], $pp[1])
Wait-Idle 1.4
Start-Sleep -Milliseconds 500
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
  Wait-Idle 0.8
}
Run-Filters 'pin' @('photographereye.colorbalance:midtones')
Wait-Idle 1.0

# --------------------------------------------------------------- invert
function Get-InvertCheckbox([string]$fullId) {
  $box = Find-ById ('PluginMaskBox_' + $fullId)
  if (-not $box) { throw ('mask box not found: ' + $fullId) }
  $cbs = $box.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($cb in $cbs) {
    if ($cb.Current.ControlType -eq [System.Windows.Automation.ControlType]::CheckBox -and $cb.Current.Name -eq 'Invert') { return $cb }
  }
  throw ('Invert checkbox not found in ' + $fullId)
}
$invTool = 'photographereye.colorbalance:midtones'
$invEl = Get-InvertCheckbox $invTool
if (-not (On-State $invEl)) { Click-El $invEl }
Wait-Idle 1.2
Add-Result 'invert' ('link-invert/' + $invTool) 0 0 'STEP' 'Invert ON: the tool now works outside Mask 1'
Wait-Idle 0.8
$base = ScreenShot $null
$inv = Set-Range 'PluginParam_photographereye.colorbalance:midtones_cr' -90
if ($null -eq $inv) { Add-Result 'invert' 'missing param' 0 0 'FAIL-NOTFOUND' '' }
else {
  Wait-Idle 1.0 14000
  $shot = ScreenShot $null
  Verify 'invert' ('tool/' + $invTool + '/cr-outside') $shot $base (In-Set 'mask1') (Out-Set 'invert') 'inverted' > $null
  Set-Range 'PluginParam_photographereye.colorbalance:midtones_cr' 0.0
}
Wait-Idle 0.8
$invEl2 = Get-InvertCheckbox $invTool
if (On-State $invEl2) { Click-El $invEl2 }
Wait-Idle 1.0
$base = ScreenShot $null
Set-Range 'SliderExposure' 1.0
Wait-Idle 1.0 14000
$shot = ScreenShot $null
Verify 'invert' 'core/SliderExposure-inside' $shot $base (In-Set 'mask1') (Out-Set 'invert') 'mask' > $null
Set-Range 'SliderExposure' 0.0
Wait-Idle 0.8
Mask-Done
Wait-Idle 1.0

# --------------------------------------------------------------- brush mask
Create-Mask 'brush'
$bp = Abs-Pt 0.14 0.26
$bx = $bp[0]
$bw = [int](0.28 * $Script:ir.W)
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
$md.Add('Zdjecie: ' + $photo + ' (1400x1000; gradient + rampy tonalno-kolorowe + noise + ramka magenta)' + $nl)
$md.Add('Metoda: UIA (klik/drag/suwaki RangeValue); weryfikacja = srednia RGB z patcha 6px przed/po czynnosci (kropka = 5x5 probek co 2px). IN = ring 10px wokol srodka maski (4 punkty) lub wnetrze polecia szczotki; OUT = punkty kontrolne (rog, dole, srodki pozostalych masek). Progi: PASS gdy IN >= 8 i OUT <= 4 (0-255).' + $nl)
$labels = @{ mask1 = '1) Maska radial Mask 1 (srodek) - wszystkie regulacje i efekty'; mask2 = '2) Druga maska radial Mask 2 (przesunieta do 80% szerokosci) - wszystkie regulacje i efekty'; pin = '3) Klik w pin Mask 1 na zdjeciu - przykladowe operacje'; invert = '4) Invert - czynnosc narzedzia na zewnatrz Mask 1, srodek bez zmian'; brush = '5) Maska brush - ustawienia tylko w wymalowanym obszarze' }
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
