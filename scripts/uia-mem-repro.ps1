param([int]$Phase, [string]$OutDir, [int]$AppId)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$root = [System.Windows.Automation.AutomationElement]::RootElement
$conProc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppId)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $conProc)
if (-not $win) { Write-Output "WINDOW NOT FOUND"; exit 1 }

function Get-Sliders
{
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)
    return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Set-ByIndex([int]$i, [double]$v)
{
    $s = (Get-Sliders)[$i]
    $rv = $s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $rv.SetValue($v)
}

function Get-Values
{
    $vals = @()
    foreach ($s in (Get-Sliders))
    {
        $rv = $s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        $vals += [math]::Round($rv.Current.Value, 3)
    }
    return ($vals -join "|")
}

function Shot([string]$name)
{
    $r = $win.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $g.Dispose()
    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop = $bmp.Clone([System.Drawing.Rectangle]::new([int]($r.Width/2-120), [int]($r.Height/2-90), 240, 180), $bmp.PixelFormat)
    $mean = 0.0; $n = 0
    $rect = [System.Drawing.Rectangle]::new(0, 0, 240, 180)
    $bd = $crop.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    for ($y = 0; $y -lt 180; $y++) {
        $row = [IntPtr]::Add($bd.Scan0, $y * $bd.Stride)
        for ($x = 0; $x -lt 240; $x++) {
            $b = [System.Runtime.InteropServices.Marshal]::ReadByte($row, $x * 3)
            $gg = [System.Runtime.InteropServices.Marshal]::ReadByte($row, $x * 3 + 1)
            $r8 = [System.Runtime.InteropServices.Marshal]::ReadByte($row, $x * 3 + 2)
            $mean += ($b + $gg + $r8) / 3.0; $n++
        }
    }
    $crop.UnlockBits($bd)
    $mean = [math]::Round($mean / $n, 2)
    $crop.Dispose(); $bmp.Dispose()
    Write-Output "SHOT $name $path MEAN=$mean"
}

function Invoke-Named([string]$name)
{
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $b = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $b) { return $false }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}

function Get-Title
{
    $p = Get-Process -Id $Pid
    $p.Refresh()
    return $p.MainWindowTitle
}

Write-Output "PHASE$Phase LOADED title=$(Get-Title)"
Write-Output "V0: $(Get-Values)"

if ($Phase -eq 1)
{
    Set-ByIndex 1 55; Set-ByIndex 2 1.4
    Start-Sleep -Milliseconds 1500
    Shot "s1_a.png"
    Write-Output "V1: $(Get-Values)"

    [void](Invoke-Named ([string][char]0x203A))
    Start-Sleep -Milliseconds 3000
    Write-Output "B title=$(Get-Title)"

    Set-ByIndex 1 25; Set-ByIndex 2 1.2
    Start-Sleep -Milliseconds 1500
    Shot "s2_b.png"

    [void](Invoke-Named ([string][char]0x2039))
    Start-Sleep -Milliseconds 3000
    Write-Output "BACK title=$(Get-Title)"
    Shot "s3_a.png"
}
else
{
    Start-Sleep -Milliseconds 2500
    Shot "e1_a.png"
    [void](Invoke-Named ([string][char]0x203A))
    Start-Sleep -Milliseconds 3000
    Set-ByIndex 1 25; Set-ByIndex 2 1.2
    Start-Sleep -Milliseconds 1500
    Shot "e2_b.png"
    [void](Invoke-Named ([string][char]0x2039))
    Start-Sleep -Milliseconds 3000
    Write-Output "BACK title=$(Get-Title)"
    Shot "e3_a.png"
}

Write-Output "VF: $(Get-Values)"