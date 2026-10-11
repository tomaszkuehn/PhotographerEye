param([int]$Pid0, [string]$Dir="C:\Users\Asus\AppData\Local\Temp\opencode")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid0)))
if (-not $win) { Write-Output "WINDOW NOT FOUND"; exit 1 }

function Find-Id([string]$id)
{
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}

function Get-SliderVals
{
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)
    $out = @()
    foreach ($s in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
    {
        $out += [Math]::Round($s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value, 1)
    }
    return ($out -join ';')
}

function DarkStats($path)
{
    $b = New-Object System.Drawing.Bitmap($path)
    $sum = 0.0; $n = 0
    for ($y = 260; $y -lt 1090; $y += 3)
    {
        for ($x = 220; $x -lt 1400; $x += 3)
        {
            $c = $b.GetPixel($x, $y)
            $lum = 0.2126*$c.R + 0.7152*$c.G + 0.0722*$c.B
            if ($lum -lt 200) { $sum += $lum; $n++ }
        }
    }
    $b.Dispose()
    if ($n -eq 0) { return "no-dark-pixels" }
    return [Math]::Round($sum / $n, 1)
}

# histogram po ruszeniu Cienia
$sliders = @($win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider))))
$sliders[1].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(55.0)
Start-Sleep -Milliseconds 1500

$r = $win.Current.BoundingRectangle
function HistCount($path, [int]$top, [int]$bottom)
{
    $b = New-Object System.Drawing.Bitmap($path)
    $n = 0
    $x0 = [int]($b.Width - 290); $x1 = [int]($b.Width - 40)
    for ($y = $top; $y -lt $bottom; $y += 4)
    {
        for ($x = $x0; $x -lt $x1; $x += 4)
        {
            $c = $b.GetPixel($x, $y)
            if ([Math]::Abs([int]$c.R - 0x14) -le 10 -and [Math]::Abs([int]$c.G - 0x14) -le 10 -and [Math]::Abs([int]$c.B - 0x14) -le 10) { $n++ }
        }
    }
    $b.Dispose()
    return $n
}
$bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
$g.Dispose()
$name1 = "$Dir\hp_hist.png"
$bmp.Save($name1, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"HIST-PIXELS after slider move: $(HistCount $name1 60 320)"

# klik X zamyka
$btnX = Find-Id "BtnHistClose"
"X found: $($btnX -ne $null)"
if ($btnX) { $btnX.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 500 }
$bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
$g.Dispose()
$name2 = "$Dir\hp_closed.png"
$bmp.Save($name2, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"HIST-PIXELS after X: $(HistCount $name2 60 320)"

# compare: przytrzymanie = oryginal, puszczenie = przetworzony
$cmp = Find-Id "BtnCompare"
if ($cmp)
{
    $cr = $cmp.Current.BoundingRectangle
    $cx = [int]($cr.X + $cr.Width / 2); $cy = [int]($cr.Y + $cr.Height / 2)
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MouseNative
{
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, UIntPtr dwExtraInfo);
}
"@
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($cx, $cy)
    Start-Sleep -Milliseconds 200
    [MouseNative]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1000
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save("$Dir\hp_hold.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [MouseNative]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1800
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save("$Dir\hp_release.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "HOLD-dark-avg=$(DarkStats "$Dir\hp_hold.png")"
    "RELEASE-dark-avg=$(DarkStats "$Dir\hp_release.png")"
}

# 3) czy przełączenie obrazu resetuje ustawienia
"SLIDERS before nav: $(Get-SliderVals)"
$next = Find-Id "BtnNext"
$next.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 2600
"SLIDERS after NEXT: $(Get-SliderVals)"
$prev = Find-Id "BtnPrev"
$prev.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 2600
"SLIDERS after PREV: $(Get-SliderVals)"