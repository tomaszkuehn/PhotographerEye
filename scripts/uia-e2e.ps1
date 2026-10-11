param([int]$Pid0)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$conProc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid0)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $conProc)
if (-not $win) { Write-Output "WINDOW NOT FOUND"; exit 1 }

function Find-Named([string]$name)
{
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Named([string]$name)
{
    $b = Find-Named $name
    if (-not $b) { return $false }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}

function Get-Title
{
    $p = Get-Process -Id $Pid0
    $p.Refresh()
    return $p.MainWindowTitle
}

function Get-Sliders
{
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)
    return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-SlideValues
{
    $vals = @()
    foreach ($s in (Get-Sliders))
    {
        $rv = $s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        $vals += [math]::Round($rv.Current.Value, 4)
    }
    return ($vals -join "|")
}

function Set-Sliders([string]$csv)
{
    $sliders = Get-Sliders
    $parts = $csv.Split(';')
    for ($i = 0; $i -lt $sliders.Count; $i++)
    {
        $rv = $sliders[$i].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        $rv.SetValue([double]$parts[$i])
    }
}

Write-Output "INIT: zoom=$((Get-SlideValues).Split('|')[0]) title=$(Get-Title)"

$ok100 = Invoke-Named '100 %'
Start-Sleep -Milliseconds 700
Write-Output "100%: invoked=$ok100 zoom=$((Get-SlideValues).Split('|')[0])"

Set-Sliders '4;0;1'
Start-Sleep -Milliseconds 900
Write-Output "ZOOM400: zoom=$((Get-SlideValues).Split('|')[0])"

Set-Sliders '4;55;1'
Start-Sleep -Milliseconds 900
Write-Output "SHADOW55 set"
$p = Get-Process -Id $Pid0
Write-Output "MEM=$([math]::Round($p.WorkingSet64 / 1MB))MB"

$prevOk = Invoke-Named ([string][char]0x2039)
Start-Sleep -Milliseconds 2200
Write-Output "PREV: ok=$prevOk title=$(Get-Title)"

$fitOk = Invoke-Named 'Dopasuj'
Start-Sleep -Milliseconds 700
Write-Output "FIT: zoom=$((Get-SlideValues).Split('|')[0]) title=$(Get-Title)"

$rst = Invoke-Named 'Reset'
Start-Sleep -Milliseconds 700
Write-Output "RESET: ok=$rst zoom=$((Get-SlideValues).Split('|')[0])"