param([int]$Pid0, [string]$OutFile)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$root = [System.Windows.Automation.AutomationElement]::RootElement
$conProc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid0)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $conProc)
if (-not $win) { Write-Output "WINDOW NOT FOUND"; exit 1 }

function Click-Button([string]$name)
{
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $b = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
    return $false
}

[void](Click-Button "Dopasuj")
Start-Sleep -Milliseconds 900
[void](Click-Button "Reset")
Start-Sleep -Milliseconds 900

$current = $win.Current.BoundingRectangle
"RECT: X=$([int]$current.X) Y=$([int]$current.Y) W=$([int]$current.Width) H=$([int]$current.Height)"

Start-Sleep -Milliseconds 600
$bmp = New-Object System.Drawing.Bitmap([int]$current.Width, [int]$current.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$current.X, [int]$current.Y, 0, 0, $bmp.Size)
$g.Dispose()
if ($OutFile) { $bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png) }

$w = $bmp.Width; $h = $bmp.Height
$minX = $w; $minY = $h; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $h; $y++)
{
    for ($x = 0; $x -lt $w; $x++)
    {
        $c = $bmp.GetPixel($x, $y)
        $rg = [Math]::Abs([int]$c.R - [int]$c.G)
        $gb = [Math]::Abs([int]$c.G - [int]$c.B)
        if (($rg -gt 18 -or $gb -gt 18) -and $c.A -gt 200)
        {
            if ($x -lt $minX) { $minX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
$bmp.Dispose()
if ($maxX -lt 0) { Write-Output "IMAGE-PIXELS NOT FOUND"; exit 2 }

$left = $minX; $right = $w - 1 - $maxX; $top = $minY; $bottom = $h - 1 - $maxY
"IMG-BBOX: [$minX,$minY..$maxX,$maxY]"
"MARGINS: left=$left right=$right top=$top bottom=$bottom"
$symH = [Math]::Abs($left - $right)
$symV = [Math]::Abs($top - $bottom)
if ($symH -le 8 -and $symV -le 8) { Write-Output "CENTERED OK (symH=$symH symV=$symV)" }
else { Write-Output "NOT-CENTERED? symH=$symH symV=$symV" }