param([int]$Pid0, [string]$OutFile, [double]$Shadow, [double]$Gamma, [double]$Zoom)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$root = [System.Windows.Automation.AutomationElement]::RootElement
$conProc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid0)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $conProc)

$sliders = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)))
$parts = @($Zoom, $Shadow, $Gamma)
for ($i = 0; $i -lt 3; $i++)
{
    $rv = $sliders[$i].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $rv.SetValue($parts[$i])
    Start-Sleep -Milliseconds 300
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32Move
{
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);
}
'@

$h = $win.Current.NativeWindowHandle
[void][Win32Move]::SetWindowPos($h, [IntPtr]::Zero, 200, 120, 0, 0, 0x0001 -bor 0x0004 -bor 0x0040)
Start-Sleep -Milliseconds 300
[void][Win32Move]::SetWindowPos($h, [IntPtr]::Zero, 200, 120, [int]$r.Width, [int]$r.Height, 0x0004 -bor 0x0040)
Start-Sleep -Milliseconds 900

Start-Sleep -Milliseconds 800
$r = $win.Current.BoundingRectangle
$bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "SAVED $OutFile"