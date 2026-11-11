<#
  UI test of the AI assistant against a fake Ollama server (no real model, nothing leaves this computer).

    powershell -ExecutionPolicy Bypass -File tools\UiAiTest\run.ps1 [-Exe <PhotographerEye.exe>] [-Port <n>]

  The script
    * builds a small test photo and an isolated settings folder (PHOTOGRAPHEREYE_APPDATA) whose assistant points at
      a fake Ollama server started inside this script (http://localhost:<port>, model "fake-vl": tools + vision);
    * starts its own PhotographerEye process and drives ONLY that process through UI Automation (no mouse, no
      keyboard, never another running PhotographerEye window);
    * checks: the chat window opens and lists the model; a request runs (set_core), the transcript shows the
      steps, the answer and the photo-preview note, the usage line shows tokens; the conversation survives closing
      and reopening the chat window; a stage 2-4 request (list_plugins, set_plugin, straighten, set_crop,
      create_mask, set_mask_values, link_tool_to_mask) runs with no tool error and lands in the .eye file together
      with the conversation; one Undo removes the whole request; "New conversation" drops it from the .eye file.
  Exit code 0 = all checks passed.
#>
param(
  [string]$Exe = (Join-Path $PSScriptRoot '..\..\bin\Debug\net8.0-windows\PhotographerEye.exe'),
  [int]$Port = 0
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$Exe = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $Exe)) { Write-Host "[FATAL] $Exe not found - build the app first (dotnet build ImgViewer.csproj -c Debug)"; exit 2 }

# ------------------------------------------------------------------ fake Ollama (C# 5: Windows PowerShell's compiler)
$fake = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

public static class FakeOllama
{
    static HttpListener _l;
    public static readonly List<string> ToolResults = new List<string>();   // "name|content" of every tool message
    public static readonly List<string> Problems = new List<string>();
    public static int Shows, Chats, Finals;
    public static bool SawImage;
    public static string PluginId = "", PluginParam = "";
    public static bool PluginMaskable;
    static readonly JavaScriptSerializer J = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };

    public static void Start(int port)
    {
        _l = new HttpListener();
        _l.Prefixes.Add("http://localhost:" + port + "/");
        _l.Start();
        var t = new Thread(Loop) { IsBackground = true };
        t.Start();
    }

    public static void Stop() { try { _l.Stop(); _l.Close(); } catch { } }

    static void Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = _l.GetContext(); } catch { return; }
            try { Handle(c); }
            catch (Exception ex)
            {
                lock (Problems) Problems.Add("fake server: " + ex.Message);
                try { c.Response.StatusCode = 500; c.Response.Close(); } catch { }
            }
        }
    }

    static Dictionary<string, object> D(params object[] kv)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }

    static object Call(string name, Dictionary<string, object> args) { return D("function", D("name", name, "arguments", args)); }

    static void Send(HttpListenerContext c, object o)
    {
        var b = Encoding.UTF8.GetBytes(J.Serialize(o));
        c.Response.ContentType = "application/json";
        c.Response.ContentLength64 = b.Length;
        c.Response.OutputStream.Write(b, 0, b.Length);
        c.Response.Close();
    }

    static string Role(Dictionary<string, object> m) { object r; return m.TryGetValue("role", out r) ? Convert.ToString(r) : ""; }
    static string Str(Dictionary<string, object> m, string k) { object r; return m.TryGetValue(k, out r) && r != null ? Convert.ToString(r, CultureInfo.InvariantCulture) : ""; }
    static double Num(object o) { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }

    static void Handle(HttpListenerContext c)
    {
        string path = c.Request.Url.AbsolutePath;
        string body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
        if (path == "/api/tags") { Send(c, D("models", new object[] { D("name", "fake-vl", "model", "fake-vl", "size", 1000000) })); return; }
        if (path == "/api/show")
        {
            Interlocked.Increment(ref Shows);
            Send(c, D("capabilities", new object[] { "completion", "tools", "vision" }, "model_info", D("fake.context_length", 32768)));
            return;
        }
        if (path != "/api/chat") { c.Response.StatusCode = 404; c.Response.Close(); return; }
        Interlocked.Increment(ref Chats);

        var req = (Dictionary<string, object>)J.DeserializeObject(body);
        var msgs = ((object[])req["messages"]).Cast<Dictionary<string, object>>().ToList();
        int lastUser = msgs.FindLastIndex(m => Role(m) == "user");
        string text = lastUser >= 0 ? Str(msgs[lastUser], "content") : "";
        foreach (var m in msgs)
        {
            object im;
            if (m.TryGetValue("images", out im) && im is object[] && ((object[])im).Length > 0) SawImage = true;
        }
        var turnTools = msgs.Skip(lastUser + 1).Where(m => Role(m) == "tool").ToList();
        int lastAssistant = msgs.FindLastIndex(m => Role(m) == "assistant");
        lock (ToolResults)
            foreach (var t in msgs.Skip(Math.Max(lastAssistant, lastUser) + 1).Where(m => Role(m) == "tool"))
                ToolResults.Add(Str(t, "tool_name") + "|" + Str(t, "content"));
        Func<string, string> result = name =>
        {
            var t = turnTools.LastOrDefault(m => Str(m, "tool_name") == name);
            return t == null ? null : Str(t, "content");
        };

        var calls = new List<object>();
        string final = null;
        if (text.Contains("TEST-BASIC"))
        {
            if (turnTools.Count == 0) calls.Add(Call("set_core", D("Exposure", 0.6)));
            else final = "Done basic.";
        }
        else if (text.Contains("TEST-STAGES"))
        {
            if (result("straighten") == null)
            {
                calls.Add(Call("list_plugins", D()));
                calls.Add(Call("straighten", D("angle", 2.5)));
                calls.Add(Call("set_crop", D("aspect", "1:1", "size", 0.9)));
            }
            else if (result("create_mask") == null)
            {
                PickPlugin(result("list_plugins"));
                if (PluginId.Length > 0) calls.Add(Call("set_plugin", D("filter", PluginId, "values", D(PluginParam, PluginValue))));
                calls.Add(Call("create_mask", D("kind", "radial", "center_x", 0.5, "center_y", 0.45, "radius_x", 0.25, "radius_y", 0.3)));
            }
            else if (result("set_mask_values") == null)
            {
                string key = "";
                try { key = Str((Dictionary<string, object>)J.DeserializeObject(result("create_mask")), "mask"); } catch { }
                calls.Add(Call("set_mask_values", D("mask", key, "values", D("Exposure", 0.8))));
                if (PluginId.Length > 0 && PluginMaskable) calls.Add(Call("link_tool_to_mask", D("filter", PluginId, "mask", key)));
            }
            else final = "Done stages.";
        }
        else final = "OK.";

        var msg = D("role", "assistant", "content", final ?? "");
        if (final == null) msg["tool_calls"] = calls.ToArray();
        else Interlocked.Increment(ref Finals);
        Send(c, D("model", "fake-vl", "message", msg, "done", true, "prompt_eval_count", 1500 + 150 * turnTools.Count, "eval_count", 40));
    }

    static double PluginValue;

    // the first panel filter with a slider: set it 60% of the way from its default towards an end
    static void PickPlugin(string json)
    {
        PluginId = ""; PluginParam = "";
        if (json == null) return;
        var o = (Dictionary<string, object>)J.DeserializeObject(json);
        object fs;
        if (!o.TryGetValue("filters", out fs) || !(fs is object[])) return;
        foreach (Dictionary<string, object> f in (object[])fs)
        {
            object tb;
            if (f.TryGetValue("toolbar", out tb) && tb is bool && (bool)tb) continue;
            object ps;
            if (!f.TryGetValue("params", out ps) || !(ps is Dictionary<string, object>)) continue;
            foreach (var kv in (Dictionary<string, object>)ps)
            {
                var p = kv.Value as Dictionary<string, object>;
                if (p == null || Str(p, "kind") != "slider") continue;
                double min = Num(p["min"]), max = Num(p["max"]), def = Num(p["default"]);
                if (max <= min) continue;
                PluginValue = max > def ? def + (max - def) * 0.6 : def - (def - min) * 0.6;
                PluginId = Str(f, "id");
                PluginParam = kv.Key;
                object nm;
                PluginMaskable = !(f.TryGetValue("no_mask", out nm) && nm is bool && (bool)nm);
                return;
            }
        }
    }
}
'@
Add-Type -TypeDefinition $fake -ReferencedAssemblies System.Web.Extensions, System.Net, System.Core -Language CSharp

if ($Port -le 0) {
  $tl = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
  $tl.Start(); $Port = $tl.LocalEndpoint.Port; $tl.Stop()
}
[FakeOllama]::Start($Port)

# ------------------------------------------------------------------ results
$Script:fails = 0
$Script:proc = $null
function Info($m) { Write-Host "[INFO] $m" }
function Check([bool]$ok, [string]$what, [string]$detail = '') {
  if ($ok) { Write-Host "PASS  $what" }
  else { $Script:fails++; Write-Host ("FAIL  $what" + $(if ($detail) { "  ($detail)" } else { '' })) }
}
function Finish([int]$code) {
  if ($Script:proc -and -not $Script:proc.HasExited) {
    try { [void]$Script:proc.CloseMainWindow() } catch { }
    if (-not $Script:proc.WaitForExit(6000)) { try { $Script:proc.Kill() } catch { } }   # only the process this script started
  }
  [FakeOllama]::Stop()
  exit $code
}
trap {
  Write-Host ('[FATAL] ' + $_.Exception.Message + ' @ line ' + $_.InvocationInfo.ScriptLineNumber)
  Finish 1
}

# ------------------------------------------------------------------ isolated settings + test photo
$td = Join-Path $env:TEMP 'photographereye-aitest'
$iso = Join-Path $td 'appdata'
New-Item -ItemType Directory -Force -Path $iso | Out-Null
Remove-Item (Join-Path $iso '*') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $td '_eye_') -Recurse -Force -ErrorAction SilentlyContinue
$settings = @{
  AutoSaveEdits = $true
  Ai = @{ Mode = 'local'; LocalUrl = "http://localhost:$Port"; LocalModel = 'fake-vl'; MaxSteps = 8; TokenBudget = 200000 }
}
($settings | ConvertTo-Json -Depth 4) | Set-Content (Join-Path $iso 'settings.json') -Encoding UTF8

$photo = Join-Path $td 'ai.png'
$bmp = New-Object System.Drawing.Bitmap(900, 600)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$br = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle(0, 0, 900, 600)), [System.Drawing.Color]::FromArgb(40, 60, 90), [System.Drawing.Color]::FromArgb(220, 190, 150), 30.0)
$g.FillRectangle($br, 0, 0, 900, 600)
$g.FillEllipse([System.Drawing.Brushes]::Peru, 330, 160, 240, 280)
$g.Dispose(); $bmp.Save($photo, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
$eyePath = Join-Path $td '_eye_\ai.png.eye'

# ------------------------------------------------------------------ launch (own process only)
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = '"' + $photo + '"'
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['PHOTOGRAPHEREYE_APPDATA'] = $iso
$Script:proc = [System.Diagnostics.Process]::Start($psi)
Info ("pid $($Script:proc.Id), fake Ollama on port $Port")

$AE = [System.Windows.Automation.AutomationElement]
$root = $AE::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $Script:proc.Id)

function Wait-Until([scriptblock]$cond, [int]$ms = 15000) {
  $end = (Get-Date).AddMilliseconds($ms)
  while ((Get-Date) -lt $end) {
    if ($Script:proc.HasExited) { throw 'the app exited' }
    $r = & $cond
    if ($r) { return $r }
    Start-Sleep -Milliseconds 200
  }
  return $null
}
# every top-level window of OUR process (the chat window is owned by the main window)
function Our-Windows { return @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) }
function Find-Id([string]$id) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
  foreach ($w in Our-Windows) {
    $el = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if ($el) { return $el }
  }
  return $null
}
function Wait-Id([string]$id, [int]$ms = 15000) { return Wait-Until { Find-Id $id } $ms }
function Invoke-Id([string]$id) {
  $el = Wait-Id $id
  if (-not $el) { throw "element $id not found" }
  $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Chat-Window {
  foreach ($w in Our-Windows) { if ($w.Current.Name -like '*AI assistant*') { return $w } }
  $wc = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
  foreach ($w in Our-Windows) {
    foreach ($x in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $wc)) { if ($x.Current.Name -like '*AI assistant*') { return $x } }
  }
  return $null
}
function Transcript {
  $t = Find-Id 'AiTranscript'
  if (-not $t) { return '' }
  $ec = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
  $lines = foreach ($e in $t.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ec)) {
    $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
  }
  return ($lines -join "`n")
}
function Ask([string]$text) {
  $finals = [FakeOllama]::Finals
  $in = Wait-Id 'AiInput'
  $in.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
  Invoke-Id 'AiSend'
  $done = Wait-Until { ([FakeOllama]::Finals -gt $finals) -and (Find-Id 'AiSend').Current.IsEnabled } 60000
  if (-not $done) { throw "the request '$text' did not finish" }
  Start-Sleep -Milliseconds 600
}
function Read-Eye { try { return (Get-Content $eyePath -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null } }
# ($eyeTest, not $cond: the block runs inside Wait-Until, whose own parameter is $cond)
function Wait-Eye([scriptblock]$eyeTest, [int]$ms = 12000) { return Wait-Until { $x = Read-Eye; if ($x -and (& $eyeTest $x)) { $x } } $ms }
function Tool-Errors { return @([FakeOllama]::ToolResults | Where-Object { $_ -match '"error"' }) }

if (-not (Wait-Until { Find-Id 'BtnAssistant' } 30000)) { throw 'main window not found' }
Start-Sleep -Milliseconds 1500

# ------------------------------------------------------------------ 1. the chat window and a basic request
Invoke-Id 'BtnAssistant'
Check ([bool](Wait-Until { Chat-Window } 10000)) 'chat window opens'
Check ([bool](Wait-Until { [FakeOllama]::Shows -gt 0 } 10000)) 'the model list is read from the server (/api/show)'
Start-Sleep -Milliseconds 800

Ask 'TEST-BASIC brighten it'
$tr = Transcript
$camera = [char]::ConvertFromUtf32(0x1F4F7)
Check ($tr -match 'TEST-BASIC') 'transcript: the request'
Check ($tr -match 'Exposure') 'transcript: the set_core step' $tr
Check ($tr -match 'Done basic') 'transcript: the answer'
Check ($tr.Contains($camera)) 'transcript: the photo-preview note (vision model)'
Check ([FakeOllama]::SawImage) 'a vision model gets the preview image'
$usage = (Find-Id 'AiUsage').Current.Name
Check (($usage -match 'Context:') -and ($usage -match 'call\(s\)') -and ($usage -match 'preview')) 'usage line shows context, calls and previews' $usage
$e = Wait-Eye { param($x) [math]::Abs($x.Exposure - 0.6) -lt 1e-6 -and $x.Assistant }
Check ([bool]$e) '.eye holds the change and the conversation'

# ------------------------------------------------------------------ 2. close and reopen: the conversation stays
$cw = Chat-Window
$cw.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
[void](Wait-Until { -not (Chat-Window) } 5000)
Invoke-Id 'BtnAssistant'
[void](Wait-Until { Chat-Window } 10000)
Start-Sleep -Milliseconds 600
Check ((Transcript) -match 'Done basic') 'the conversation survives closing the chat window'

# ------------------------------------------------------------------ 3. stages 2-4 in one request
Ask 'TEST-STAGES straighten, square crop, brighten the centre'
$errs = Tool-Errors
Check ($errs.Count -eq 0) 'stage 2-4 tools ran without errors' ($errs -join ' || ')
$names = @([FakeOllama]::ToolResults | ForEach-Object { ($_ -split '\|', 2)[0] })
foreach ($n in 'list_plugins', 'straighten', 'set_crop', 'create_mask', 'set_mask_values') { Check ($names -contains $n) "tool $n was called" }
if ([FakeOllama]::PluginId) {
  Check ($names -contains 'set_plugin') ("set_plugin on " + [FakeOllama]::PluginId + ':' + [FakeOllama]::PluginParam)
} else { Info 'no plugin filter with a slider is loaded - set_plugin skipped' }
$tr = Transcript
Check ($tr -match 'Done stages') 'transcript: the stage answer'
$e = Wait-Eye { param($x) [math]::Abs($x.Angle - 2.5) -lt 1e-6 -and $x.CropW -lt 0.99 }
Check ([bool]$e) '.eye: straightened and cropped' $(if ($e) { '' } else { (Get-Content $eyePath -Raw -ErrorAction SilentlyContinue) })
if ($e) {
  $masks = @($e.PluginMasks.PSObject.Properties | Where-Object { $_.Value.Kind -eq 'radial' })
  Check ($masks.Count -eq 1) '.eye: one radial mask'
  if ($masks.Count -eq 1) { Check ($masks[0].Value.Adjust.Exposure -eq 0.8) '.eye: the mask brightens (Exposure +0.8)' }
  if ([FakeOllama]::PluginId) {
    $pv = @($e.PluginValues.PSObject.Properties | Where-Object { $_.Name -eq ([FakeOllama]::PluginId + '/' + [FakeOllama]::PluginParam) })
    Check ($pv.Count -eq 1) '.eye: the plugin value is stored'
    if ([FakeOllama]::PluginMaskable -and $masks.Count -eq 1) {
      $link = $e.PluginMasks.PSObject.Properties | Where-Object { $_.Name -eq [FakeOllama]::PluginId }
      Check ($link -and $link.Value.Kind -eq 'link' -and $link.Value.Ref -eq $masks[0].Name) '.eye: the filter follows the mask'
    }
  }
  Check (@($e.Assistant.Messages).Count -ge 4) '.eye: both requests are in the stored conversation'
}

# ------------------------------------------------------------------ 4. one Undo removes the whole request
Invoke-Id 'BtnUndo'
$e = Wait-Eye { param($x) [math]::Abs($x.Angle) -lt 1e-9 -and $x.CropW -gt 0.999 }
Check ([bool]$e) 'Undo: geometry restored'
if ($e) {
  Check (@($e.PluginMasks.PSObject.Properties | Where-Object { $_.Value.Kind -eq 'radial' }).Count -eq 0) 'Undo: the mask is gone'
  Check ([math]::Abs($e.Exposure - 0.6) -lt 1e-6) 'Undo: the earlier request stays'
}

# ------------------------------------------------------------------ 5. new conversation
Invoke-Id 'AiNew'
$e = Wait-Eye { param($x) -not $x.Assistant }
Check ([bool]$e) 'New conversation: the .eye file drops it'
Check (-not ((Transcript) -match 'Done basic')) 'New conversation: the transcript is cleared'

# ------------------------------------------------------------------ 6. Up / Down recall earlier requests
# Keys go only to the test's own input: they are sent only when that element has the keyboard focus.
Add-Type -AssemblyName System.Windows.Forms
function Input-Value { (Find-Id 'AiInput').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Send-Key([string]$k) {
  $in = Find-Id 'AiInput'
  $in.SetFocus()
  Start-Sleep -Milliseconds 200
  $f = [System.Windows.Automation.AutomationElement]::FocusedElement
  if (-not $f -or $f.Current.ProcessId -ne $Script:proc.Id -or $f.Current.AutomationId -ne 'AiInput') { throw 'the chat input has no keyboard focus - keys not sent' }
  [System.Windows.Forms.SendKeys]::SendWait($k)
  Start-Sleep -Milliseconds 250
}
(Find-Id 'AiInput').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('draft')
Send-Key '{UP}'
Check ((Input-Value) -match '^TEST-STAGES') 'Up: the newest request' (Input-Value)
Send-Key '{UP}'
Check ((Input-Value) -match '^TEST-BASIC') 'Up again: the older request' (Input-Value)
Send-Key '{DOWN}'
Check ((Input-Value) -match '^TEST-STAGES') 'Down: back to the newer request' (Input-Value)
Send-Key '{DOWN}'
Check ((Input-Value) -eq 'draft') 'Down past the newest: the unfinished text returns' (Input-Value)

$fp = @([FakeOllama]::Problems)
Check ($fp.Count -eq 0) 'fake server had no problems' ($fp -join '; ')
Write-Host ''
if ($Script:fails -eq 0) { Write-Host 'ALL OK' } else { Write-Host "FAILURES: $Script:fails" }
Finish ([int]($Script:fails -gt 0))
