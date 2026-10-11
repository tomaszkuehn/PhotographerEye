param([int]$Pid0)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid0)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

$sliders = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)))
$zm = $sliders[0].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
$sm = $sliders[1].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)

$zm.SetValue(2)
Start-Sleep -Milliseconds 1500
$zm.SetValue(4)
Start-Sleep -Milliseconds 1500
$sm.SetValue(40)
Start-Sleep -Milliseconds 2500
Write-Output "DONE zoom=$([math]::Round($zm.Current.Value,2)) shadow=$([math]::Round($sm.Current.Value,0))"