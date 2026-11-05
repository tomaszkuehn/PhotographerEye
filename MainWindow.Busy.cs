using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ImgViewer.Imaging;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>
/// The waiting symbol for actions that cannot be made fast: an analog clock with a sweeping hand over the photo. It
/// shows at once when the expected wait is over 2 s (full-resolution save and export, predicted from the measured
/// time of the last full render per megapixel), and any other action that is still running after 2 s shows it too.
/// Also throttles the plugin-panel renders (40 ms, like the STANDARD / DETAIL sliders) so a slider drag does not
/// start a render for every tick.
/// </summary>
public partial class MainWindow
{
    private const double BusyThresholdMs = 2000;

    private int _busyDepth;
    private DispatcherTimer? _busyDelay;
    private RotateTransform? _busyHand;
    private DoubleAnimation? _busySpin;
    private long _lastPluginRenderTicks;
    private DispatcherTimer? _pluginRenderTimer;

    /// <summary>Starts a long action. <paramref name="expectedMs"/> above the threshold shows the clock at once; otherwise
    /// it appears when the action is still running after <see cref="BusyThresholdMs"/>.</summary>
    private void BeginBusy(double? expectedMs)
    {
        _busyDepth++;
        if (expectedMs > BusyThresholdMs)
        {
            _busyDelay?.Stop();
            ShowBusyClock();
            return;
        }
        if (_busyDepth > 1) return; // another action is already pending: its decision stands
        if (_busyDelay is null)
        {
            _busyDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BusyThresholdMs) };
            _busyDelay.Tick += OnBusyDelayTick;
        }
        _busyDelay.Stop();
        _busyDelay.Start();
    }

    private void OnBusyDelayTick(object? sender, EventArgs e)
    {
        _busyDelay?.Stop();
        if (_busyDepth > 0) ShowBusyClock();
    }

    /// <summary>Ends a long action; the clock hides when none is left.</summary>
    private void EndBusy()
    {
        if (_busyDepth > 0) _busyDepth--;
        if (_busyDepth > 0) return;
        _busyDelay?.Stop();
        HideBusyClock();
    }

    private void ShowBusyClock()
    {
        if (BusyClock is null) return;
        if (BusyClock.Visibility == Visibility.Visible) return;
        if (_busyHand is null)
        {
            _busyHand = new RotateTransform(0);
            BusyClockHand.RenderTransform = _busyHand;
            BusyClockHand.RenderTransformOrigin = new Point(0.5, 0.5);
            _busySpin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        }
        BusyClock.Visibility = Visibility.Visible;
        _busyHand.BeginAnimation(RotateTransform.AngleProperty, _busySpin);
    }

    private void HideBusyClock()
    {
        if (BusyClock is null || BusyClock.Visibility != Visibility.Visible) return;
        _busyHand?.BeginAnimation(RotateTransform.AngleProperty, null);
        BusyClock.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    //  Expected duration of a full-resolution render (Save, Export)
    // ------------------------------------------------------------------

    /// <summary>Measured milliseconds per megapixel of the last full renders; 0 until the first one finished.</summary>
    private double _fullRenderMsPerMp;

    private int ActivePluginCount()
    {
        int n = 0;
        foreach (var f in _pluginReg.Filters)
            if (_pluginState.IsFilterActive(f)) n++;
        return n;
    }

    /// <summary>
    /// Rough wait for a full-resolution render of the open photo: megapixels × the measured time per megapixel (a
    /// conservative default before the first measurement), a RAW costs its extra decode, each active plugin its pass.
    /// </summary>
    private double? ExpectedFullRenderMs()
    {
        if (_baseInfo is not ImageInfo bi || bi.Width <= 0 || bi.Height <= 0) return null;
        double mp = bi.Width * bi.Height / 1e6;
        double perMp = _fullRenderMsPerMp > 0 ? _fullRenderMsPerMp : 120.0;
        bool isRaw = _currentPath is not null && RawDecoder.IsRaw(_currentPath);
        return mp * (perMp + (isRaw ? 60.0 : 0.0) + 30.0 * ActivePluginCount());
    }

    /// <summary>Remembers how long a full-resolution render took, so the next one can be predicted.</summary>
    private void RecordFullRenderMs(double ms, double megapixels)
    {
        if (ms <= 0 || megapixels <= 0) return;
        double perMp = ms / megapixels;
        _fullRenderMsPerMp = _fullRenderMsPerMp <= 0 ? perMp : 0.7 * _fullRenderMsPerMp + 0.3 * perMp;
    }

    // ------------------------------------------------------------------
    //  Plugin-panel render throttle
    // ------------------------------------------------------------------

    /// <summary>A plugin control changed: render at most once per 40 ms while a slider is dragged (the core panel sliders do the same).</summary>
    private void RequestPluginRenderThrottled()
    {
        long now = Environment.TickCount64;
        if (now - _lastPluginRenderTicks >= 40)
        {
            _lastPluginRenderTicks = now;
            _pluginRenderTimer?.Stop();
            RequestRender();
        }
        else
        {
            _pluginRenderTimer ??= NewPluginRenderTimer();
            _pluginRenderTimer.Stop();
            _pluginRenderTimer.Start();
        }
    }

    private DispatcherTimer NewPluginRenderTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _lastPluginRenderTicks = Environment.TickCount64;
            RequestRender();
        };
        return t;
    }
}
