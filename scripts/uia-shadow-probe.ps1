param([int]$Pid0, [string]$Dir="C:\Users\Asus\AppData\Local\Temp\opencode", [switch]$Darken)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid0)))
if (-not $win) { Write-Output "WINDOW NOT FOUND"; exit 1 }

$sliderCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)
$sliders = @($win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCond))
if ($sliders.Count -lt 3) { Write-Output "SLIDERS NOT FOUND: $($sliders.Count)"; exit 1 }
$shadow = $sliders[1].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
$gamma = $sliders[2].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)

function Shot($name)
{
    $r = $win.Current.BoundingRectangle
    $b = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $b.Size)
    $g.Dispose()
    $b.Save($name, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}

$log = "$env:TEMP\photographereye-debug.log"

# --- baseline: cienie 0
$gamma.SetValue(1.0)
$shadow.SetValue(0.0)
Start-Sleep -Milliseconds 1600
$t0 = Get-Date
if ($Darken) { $shadow.SetValue(-75.0) } else { $shadow.SetValue(75.0) }
Start-Sleep -Milliseconds 60
Shot "$Dir\sh_early.png"
Start-Sleep -Milliseconds 2200
$t1 = Get-Date
Shot "$Dir\sh_full.png"
Write-Output ("SET->FINAL: {0:0} ms" -f (($t1-$t0).TotalMilliseconds))

# --- porownanie: srednia jasnosc ciemnych pikseli (lum < 128) w obu shotach (wycinek viewport, bez UI)
function DarkStats($path)
{
    $b = New-Object System.Drawing.Bitmap($path)
    $sum = 0.0; $n = 0
    for ($y = 260; $y -lt 1090; $y += 3)
    {
        for ($x = 220; $x -lt 1560; $x += 3)
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
"a-dark-avg=$(DarkStats "$Dir\sh_early.png")"   # po 60ms od ustawienia (czyli w trakcie przeliczania)
"b-dark-avg=$(DarkStats "$Dir\sh_full.png")"    # po zakonczeniu

# --- wracamy do 0 i znow shot (kontrola)
$shadow.SetValue(0.0)
Start-Sleep -Milliseconds 1600
Shot "$Dir\sh_back.png"
"c-dark-avg=$(DarkStats "$Dir\sh_back.png")"
