param([int]$Pid0)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid0)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

$rect = $win.Current.BoundingRectangle
Write-Output "WIN-RECT: x=$([int]$rect.X) y=$([int]$rect.Y) w=$([int]$rect.Width) h=$([int]$rect.Height)"

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DpiUtil
{
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetProcessDpiAwareness(IntPtr h, out uint value);
}
'@
Write-Output "SM_CXSCREEN=$( [DpiUtil]::GetSystemMetrics(0) ) SM_CYSCREEN=$( [DpiUtil]::GetSystemMetrics(1) )"
Write-Output "SM_CXFULLSCREEN=$( [DpiUtil]::GetSystemMetrics(16) ) SM_CYFULLSCREEN=$( [DpiUtil]::GetSystemMetrics(17) )"

$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($el in $all)
{
    if ($el.Current.ControlType.ProgrammaticName -match 'Slider|Text|Image')
    {
        $r = $el.Current.BoundingRectangle
        Write-Output "EL: $( if ($el.Current.ControlType.ProgrammaticName -match 'Slider') { 'Slider' } else { $el.Current.Name.Substring(0, [Math]::Min(30, $el.Current.Name.Length)) } ) rect=($([int]$r.X),$([int]$r.Y),$([int]$r.Width),$([int]$r.Height))"
    }
}

$prevCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, [string][char]0x2039)
$nextCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, [string][char]0x203A)
$prev = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $prevCond)
$next = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nextCond)
if (-not $next -or -not $prev) { Write-Output "NAV BUTTONS NOT FOUND"; exit 0 }

$next.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 2500
$p = Get-Process -Id $Pid0
$p.Refresh()
Write-Output "T-NEXT: [$($p.MainWindowTitle)] MEM=$([math]::Round($p.WorkingSet64 / 1MB))MB"

$next.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 2500
$p.Refresh()
Write-Output "T-NEXT2: [$($p.MainWindowTitle)] MEM=$([math]::Round($p.WorkingSet64 / 1MB))MB"

$next.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 2500
$p.Refresh()
Write-Output "T-NEXT3(wrap): [$($p.MainWindowTitle)] MEM=$([math]::Round($p.WorkingSet64 / 1MB))MB"

$prev.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 2500
$p.Refresh()
Write-Output "T-PREV: [$($p.MainWindowTitle)] MEM=$([math]::Round($p.WorkingSet64 / 1MB))MB"