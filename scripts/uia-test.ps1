param([int]$Pid0)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid0)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Output "WINDOW NOT FOUND"; exit 1 }

function Get-Button([System.Windows.Automation.AutomationElement]$win, [string]$name)
{
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Button([System.Windows.Automation.AutomationElement]$win, [string]$name)
{
    $b = Get-Button $win $name
    if (-not $b) { Write-Output "NOBTN:$name"; return }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Get-SliderValues([System.Windows.Automation.AutomationElement]$win)
{
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)
    $sliders = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $vals = @()
    foreach ($s in $sliders)
    {
        $rv = $s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        $vals += [math]::Round($rv.Current.Value, 4)
    }
    return ($vals -join " | ")
}

function Wait-Title([int]$ms)
{
    Start-Sleep -Milliseconds $ms
    $p = Get-Process -Id $Pid0
    $p.Refresh()
    return $p.MainWindowTitle
}

Write-Output "SLIDERS-INIT: $(Get-SliderValues $win)"
Write-Output "NEXT: $(Wait-Title 100) -> $( { Invoke-Button $win '>' } )"
Invoke-Button $win '>'
Write-Output "T-NEXT: $(Wait-Title 2500)"
Invoke-Button $win '>'
Write-Output "T-NEXT2: $(Wait-Title 2500)"
Invoke-Button $win '<'
Write-Output "T-PREV: $(Wait-Title 2500)"

Invoke-Button $win '+'
Write-Output "Z-IN: zoom-slider=$(Get-SliderValues $win) title=$(Wait-Title 600)"
Invoke-Button $win '-'
Write-Output "Z-OUT: zoom-slider=$(Get-SliderValues $win) title=$(Wait-Title 600)"

$fit = Get-Button $win 'Dopasuj'
if ($fit)
{
    $fit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "Z-FIT: zoom-slider=$(Get-SliderValues $win) title=$(Wait-Title 600)"
}

$p = Get-Process -Id $Pid0
Write-Output "MEM=$([math]::Round($p.WorkingSet64 / 1MB))MB"