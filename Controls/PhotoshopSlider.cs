using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ImgViewer.Controls;

public static class PhotoshopSlider
{
    public const double TrackPixels = 250.0;

    public static double Factor(bool shift, bool alt, bool ctrl) =>
        shift ? 10.0 : alt || ctrl ? 0.1 : 1.0;

    public static double Quantum(double smallChange) =>
        smallChange > 0 && smallChange < 1 ? 0.01 : 1.0;

    public static double Snap(double value, double min, double max, double smallChange)
    {
        if (value < min) value = min;
        else if (value > max) value = max;
        double q = Quantum(smallChange);
        value = Math.Round(value / q) * q;
        return value < min ? min : value > max ? max : value;
    }

    public static double Step(double smallChange, double largeChange, bool shift, bool ctrl)
    {
        double small = smallChange > 0 ? smallChange : Quantum(smallChange);
        if (shift)
            return largeChange > 0 ? largeChange : small * 10;
        if (ctrl && small < 1)
            return 0.01;
        return small;
    }

    public static double Scrub(double start, double dx, double min, double max, double smallChange, bool shift, bool alt, bool ctrl)
    {
        double upp = (max - min) / TrackPixels;
        return Snap(start + dx * upp * Factor(shift, alt, ctrl), min, max, smallChange);
    }

    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached("Enable", typeof(bool), typeof(PhotoshopSlider),
            new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);

    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);

    private static readonly Dictionary<Slider, Controller> Registered = new();

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider) return;
        if ((bool)e.NewValue)
        {
            if (!Registered.ContainsKey(slider))
                Registered[slider] = new Controller(slider);
        }
        else if (Registered.Remove(slider, out var controller))
        {
            controller.Detach();
        }
    }

    private sealed class Controller
    {
        private readonly Slider _slider;
        private readonly List<FrameworkElement> _targets = new();
        private bool _scrubbing;
        private bool _moved;
        private double _startX;
        private double _startValue;
        private Popup? _popup;

        public Controller(Slider slider)
        {
            _slider = slider;
            _slider.IsMoveToPointEnabled = true;
            _slider.Focusable = true;
            if (_slider.ToolTip is null)
                _slider.ToolTip = "Click the track to set the value; drag the label/value to fine-tune (Shift x10, Ctrl/Alt x0.1); double-click the value to type a number.";
            _slider.PreviewKeyDown += OnKeyDown;
            DiscoverTargets();
        }

        public void Detach()
        {
            _popup?.SetCurrentValue(Popup.IsOpenProperty, false);
            _slider.PreviewKeyDown -= OnKeyDown;
            foreach (var t in _targets)
            {
                t.PreviewMouseLeftButtonDown -= OnDown;
                t.PreviewMouseMove -= OnMove;
                t.PreviewMouseLeftButtonUp -= OnUp;
                t.LostMouseCapture -= OnLostCapture;
            }
            _targets.Clear();
        }

        private void DiscoverTargets()
        {
            if (_slider.Parent is not Panel parent) return;
            int idx = parent.Children.IndexOf(_slider);
            if (idx < 0) return;
            if (idx - 1 >= 0)
            {
                var prev = parent.Children[idx - 1];
                if (prev is not Slider and not ButtonBase and not Border and not ScrollBar)
                    AddTarget(prev);
            }
            if (idx + 1 < parent.Children.Count)
            {
                var next = parent.Children[idx + 1];
                if (next is not Slider and not ButtonBase and not Border and not ScrollBar)
                    AddTarget(next);
            }
        }

        private void AddTarget(UIElement element)
        {
            if (element is not FrameworkElement fe) return;
            _targets.Add(fe);
            fe.PreviewMouseLeftButtonDown += OnDown;
            fe.PreviewMouseMove += OnMove;
            fe.PreviewMouseLeftButtonUp += OnUp;
            fe.LostMouseCapture += OnLostCapture;
            fe.Cursor = Cursors.SizeWE;
        }

        private static bool IsInteractive(object? source)
        {
            var d = source as DependencyObject;
            while (d is not null)
            {
                if (d is ButtonBase or TextBoxBase or ScrollBar) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (IsInteractive(e.OriginalSource)) return;
            var fe = (FrameworkElement)sender;
            if (e.ClickCount == 2)
            {
                BeginEdit(fe);
                e.Handled = true;
                return;
            }
            _scrubbing = true;
            _moved = false;
            _startX = e.GetPosition(fe).X;
            _startValue = _slider.Value;
            fe.CaptureMouse();
            Mouse.OverrideCursor = Cursors.SizeWE;
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_scrubbing) return;
            var fe = (FrameworkElement)sender;
            double dx = e.GetPosition(fe).X - _startX;
            if (Math.Abs(dx) >= 1.0) _moved = true;
            var mods = Keyboard.Modifiers;
            _slider.Value = Scrub(_startValue, dx, _slider.Minimum, _slider.Maximum, _slider.SmallChange,
                mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Control));
            e.Handled = true;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_scrubbing) return;
            EndScrub((FrameworkElement)sender);
            e.Handled = _moved;
        }

        private void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (!_scrubbing) return;
            _scrubbing = false;
            Mouse.OverrideCursor = null;
        }

        private void EndScrub(FrameworkElement fe)
        {
            _scrubbing = false;
            if (fe.IsMouseCaptured) fe.ReleaseMouseCapture();
            Mouse.OverrideCursor = null;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            double dir;
            switch (e.Key)
            {
                case Key.Left:
                case Key.Down:
                    dir = -1;
                    break;
                case Key.Right:
                case Key.Up:
                    dir = 1;
                    break;
                default:
                    return;
            }
            _slider.Value = Snap(_slider.Value + dir * Step(_slider.SmallChange, _slider.LargeChange,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Control)),
                _slider.Minimum, _slider.Maximum, _slider.SmallChange);
            e.Handled = true;
        }

        private void BeginEdit(FrameworkElement target)
        {
            EndScrub(target);
            _popup?.SetCurrentValue(Popup.IsOpenProperty, false);

            var background = (Brush?)Application.Current?.TryFindResource("PanelBrush")
                ?? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            var foreground = (Brush?)Application.Current?.TryFindResource("ForegroundBrush") ?? Brushes.White;
            var accent = (Brush?)Application.Current?.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;

            bool fine = _slider.SmallChange > 0 && _slider.SmallChange < 1;
            var box = new TextBox
            {
                Width = 78,
                Text = _slider.Value.ToString(fine ? "0.###" : "0.####", CultureInfo.InvariantCulture),
                Foreground = foreground,
                Background = background,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3, 1, 3, 1),
                FontSize = 12,
                TextAlignment = TextAlignment.Right
            };
            var border = new Border
            {
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                Background = background,
                Child = box
            };
            var popup = new Popup
            {
                PlacementTarget = target,
                Placement = PlacementMode.Bottom,
                StaysOpen = true,
                AllowsTransparency = true,
                Child = border
            };
            _popup = popup;

            bool done = false;
            void Close()
            {
                done = true;
                popup.SetCurrentValue(Popup.IsOpenProperty, false);
            }
            void Commit()
            {
                if (done) return;
                if (double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    _slider.Value = Snap(v, _slider.Minimum, _slider.Maximum, _slider.SmallChange);
                Close();
            }

            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Commit();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };
            box.LostKeyboardFocus += (_, _) => Commit();
            box.Loaded += (_, _) =>
            {
                box.Focus();
                box.SelectAll();
            };
            popup.Closed += (_, _) =>
            {
                if (ReferenceEquals(_popup, popup)) _popup = null;
            };
            popup.SetCurrentValue(Popup.IsOpenProperty, true);
        }
    }
}
