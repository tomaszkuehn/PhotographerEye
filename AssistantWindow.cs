using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImgViewer.Services;
using ImgViewer.Services.Ai;

namespace ImgViewer;

/// <summary>
/// The AI assistant window (non-modal): the model choice, a conversation per photo, quick prompts. The model edits the
/// photo through <see cref="MainWindow.AiEditorFor"/>; each turn is one undo step ("AI: …").
/// </summary>
public sealed class AssistantWindow : Window
{
    private readonly MainWindow _main;
    private Dictionary<string, AiSession> _sessions => _main.AiSessions;
    private AiSettings _s;
    private List<AiModelChoice> _choices = new();
    private CancellationTokenSource? _turnCts;
    private CancellationTokenSource? _listCts;
    private bool _busy;
    private bool _loadingUi;

    private readonly Brush _muted;
    private readonly Brush _fg;
    private readonly Brush _panel;
    private readonly Brush _border;

    private readonly ComboBox _mode = new() { Width = 150, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _model = new() { IsEditable = true, MinWidth = 220, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _refresh = new() { Content = "↻", Width = 28, ToolTip = "Reload the model list" };
    private readonly Button _gear = new() { Content = "⚙", Width = 28, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Connection settings" };
    private readonly TextBlock _modelNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private readonly StackPanel _settingsPanel = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBox _url = new() { Margin = new Thickness(0, 2, 0, 6) };
    private readonly PasswordBox _key = new() { Margin = new Thickness(0, 2, 6, 0), MinWidth = 220 };
    private readonly TextBlock _keyState = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _steps = new() { Width = 50, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _stepsWarn = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBox _ctxTokens = new() { Width = 70, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBox _budget = new() { Width = 80, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _usage = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
    private readonly CheckBox _meta = new() { Content = "Send camera data (camera, ISO, aperture, shutter, focal length, date; never GPS or the file name)", Margin = new Thickness(0, 6, 0, 0) };

    private readonly StackPanel _transcript = new() { Margin = new Thickness(0, 0, 4, 0) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _send = new() { Content = "Send", MinWidth = 72, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _stop = new() { Content = "Stop", MinWidth = 72, Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
    private readonly Button _new = new() { Content = "New conversation", Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
    private readonly WrapPanel _quick = new() { Margin = new Thickness(0, 6, 0, 6) };

    private static readonly (string Label, string Prompt)[] QuickPrompts =
    {
        ("Family album", "Prepare this photo for a family album."),
        ("Reportage", "Edit this photo in a reportage / documentary style."),
        ("Artistic B&W", "Make this an artistic black and white photo."),
        ("Fix exposure", "Fix the exposure and the white balance, keep it natural."),
    };

    public AssistantWindow(MainWindow main)
    {
        _main = main;
        _s = SettingsStore.Load().Ai ?? new AiSettings();
        _muted = (Brush)Application.Current.Resources["MutedBrush"];
        _fg = (Brush)Application.Current.Resources["ForegroundBrush"];
        _panel = (Brush)Application.Current.Resources["PanelBrush"];
        _border = (Brush)Application.Current.Resources["BorderBrushCol"];

        Title = "PhotographerEye — AI assistant";
        Width = 520;
        Height = 680;
        MinWidth = 400;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = (Brush)Application.Current.Resources["WindowBrush"];
        Foreground = _fg;
        FontSize = 12;
        Icon = main.Icon;
        Left = Math.Max(0, main.Left + main.ActualWidth - Width - 40);
        Top = main.Top + 80;

        _modelNote.Foreground = _muted;
        _keyState.Foreground = _muted;
        _status.Foreground = _muted;
        _usage.Foreground = _muted;
        Id(_usage, "AiUsage");
        _usage.ToolTip = "Context: what the model reads in one call (the conversation so far, the function list and at most one preview).\n" +
                         "Used: all tokens of this photo's conversation, read + written, against the budget set in ⚙.\n" +
                         "After each request its working steps are folded into one short line, so follow-ups stay small.\n" +
                         "~ marks an estimate (about 4 characters per token, ~1k per preview); it is used whenever the server reports\n" +
                         "nothing or less (a local Ollama may count only the part it did not have cached).\n" +
                         "Only models that see images get the photo, as a reduced preview; each one sent is noted in the conversation.";
        _meta.Foreground = _fg;

        Id(_mode, "AiMode");
        Id(_model, "AiModel");
        Id(_refresh, "AiRefresh");
        Id(_gear, "AiSettings");
        Id(_input, "AiInput");
        Id(_send, "AiSend");
        Id(_stop, "AiStop");
        Id(_new, "AiNew");
        Id(_status, "AiStatus");

        // header: mode, model, refresh, settings
        _mode.Items.Add(new ComboBoxItem { Content = "Ollama (this computer)", Tag = "local" });
        _mode.Items.Add(new ComboBoxItem { Content = "Ollama Cloud (ollama.com)", Tag = "cloud" });
        var head = new DockPanel();
        DockPanel.SetDock(_mode, Dock.Left);
        DockPanel.SetDock(_gear, Dock.Right);
        DockPanel.SetDock(_refresh, Dock.Right);
        head.Children.Add(_mode);
        head.Children.Add(_gear);
        head.Children.Add(_refresh);
        head.Children.Add(_model);

        BuildSettingsPanel();

        var top = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
        top.Children.Add(head);
        top.Children.Add(_modelNote);
        top.Children.Add(_settingsPanel);

        _scroll = new ScrollViewer
        {
            Content = _transcript,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(8)
        };
        Id(_scroll, "AiTranscript");
        var transcriptBorder = new Border
        {
            Child = _scroll,
            Background = _panel,
            BorderBrush = _border,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12, 0, 12, 0)
        };

        foreach (var (label, prompt) in QuickPrompts)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 2, 8, 2), ToolTip = prompt };
            Id(b, "AiQuick_" + label.Replace(" ", "").Replace("&", ""));
            b.Click += (_, _) => _ = SendAsync(prompt);
            _quick.Children.Add(b);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(_new);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_send);

        var bottom = new StackPanel { Margin = new Thickness(12, 0, 12, 10) };
        bottom.Children.Add(_quick);
        bottom.Children.Add(_usage);
        bottom.Children.Add(_input);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(transcriptBorder);
        Content = root;

        _mode.SelectionChanged += (_, _) => OnModeChanged();
        _model.SelectionChanged += (_, _) => OnModelPicked();
        _model.LostKeyboardFocus += (_, _) => OnModelTyped();
        _refresh.Click += (_, _) => _ = LoadModelsAsync();
        _gear.Click += (_, _) => _settingsPanel.Visibility = _settingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        _send.Click += (_, _) => _ = SendAsync(_input.Text);
        _stop.Click += (_, _) => CancelTurn();
        _new.Click += (_, _) => NewConversation();
        _input.PreviewKeyDown += OnInputKey;
        Closed += (_, _) =>
        {
            CancelTurn();
            _listCts?.Cancel();
        };

        _loadingUi = true;
        _mode.SelectedIndex = _s.IsCloud ? 1 : 0;
        _loadingUi = false;
        LoadSettingsUi();
        ShowSession();
        Loaded += (_, _) =>
        {
            _input.Focus();
            _ = LoadModelsAsync();
        };
    }

    private static void Id(UIElement e, string id) => AutomationProperties.SetAutomationId(e, id);

    private void BuildSettingsPanel()
    {
        _settingsPanel.Children.Add(Muted("Server address"));
        _settingsPanel.Children.Add(_url);
        _url.LostKeyboardFocus += (_, _) => SaveUrl();

        _settingsPanel.Children.Add(Muted("API key (Ollama Cloud only; stored encrypted for this Windows user, or set OLLAMA_API_KEY)"));
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "Save key", Padding = new Thickness(8, 1, 8, 1) };
        var clear = new Button { Content = "Remove", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 6, 0) };
        Id(_key, "AiKey");
        Id(save, "AiKeySave");
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_key.Password)) return;
            Save(_s with { ApiKeyProtected = AiKeyProtector.Protect(_key.Password.Trim()) });
            _key.Clear();
            LoadSettingsUi();
            _ = LoadModelsAsync();
        };
        clear.Click += (_, _) =>
        {
            Save(_s with { ApiKeyProtected = null });
            LoadSettingsUi();
        };
        keyRow.Children.Add(_key);
        keyRow.Children.Add(save);
        keyRow.Children.Add(clear);
        keyRow.Children.Add(_keyState);
        _settingsPanel.Children.Add(keyRow);

        NumberRow(_steps, "Most model calls per request:",
            $"1-30, default {AiSettings.DefaultMaxSteps}. Each call is one model answer (it may change several settings at once); the request stops at the limit and the changes made so far stay.",
            n => Save(_s with { MaxSteps = (int)Math.Clamp(n, 1, AiSettings.MaxMaxSteps) }), () => _s.MaxSteps);
        _stepsWarn.Foreground = Brushes.IndianRed;
        _settingsPanel.Children.Add(_stepsWarn);
        NumberRow(_ctxTokens, "Context window (num_ctx, tokens):", "0 = server default (often 4k: too small with a preview); local models use more memory for larger values",
            n => Save(_s with { ContextTokens = (int)(n <= 0 ? 0 : Math.Clamp(n, 2048, 262144)) }), () => _s.ContextTokens);
        NumberRow(_budget, "Token budget per photo:", "all calls of one photo's conversation, read + written; 0 = no limit",
            n => Save(_s with { TokenBudget = Math.Max(0, n) }), () => _s.TokenBudget);

        _meta.Checked += (_, _) => { if (!_loadingUi) Save(_s with { SendMetadata = true }); };
        _meta.Unchecked += (_, _) => { if (!_loadingUi) Save(_s with { SendMetadata = false }); };
        _settingsPanel.Children.Add(_meta);
    }

    /// <summary>A labelled number box saved when it loses focus (accepts "200k" / "1.5M"); invalid input reverts.</summary>
    private void NumberRow(TextBox box, string label, string hint, Action<long> save, Func<long> current)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(box);
        if (hint.Length > 0) box.ToolTip = hint;
        box.LostKeyboardFocus += (_, _) =>
        {
            if (!_loadingUi && ParseTokens(box.Text) is { } n)
            {
                save(n);
                UpdateUsage();
            }
            box.Text = current().ToString(CultureInfo.InvariantCulture);
            UpdateStepsWarning();
        };
        _settingsPanel.Children.Add(row);
    }

    public static long? ParseTokens(string text)
    {
        var t = text.Trim().Replace(" ", "").Replace(',', '.').ToLowerInvariant();
        double mul = 1;
        if (t.EndsWith('k')) { mul = 1e3; t = t[..^1]; }
        else if (t.EndsWith('m')) { mul = 1e6; t = t[..^1]; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? (long)Math.Round(v * mul)
            : null;
    }

    /// <summary>Shown while the step limit is above the default: what a longer request costs and risks.</summary>
    private void UpdateStepsWarning()
    {
        int n = _s.MaxSteps;
        if (n <= AiSettings.DefaultMaxSteps)
        {
            _stepsWarn.Visibility = Visibility.Collapsed;
            return;
        }
        int ctx = _s.ContextTokens > 0 ? _s.ContextTokens : 16384;
        _stepsWarn.Text =
            $"⚠ {n} calls per request: every call re-reads the whole conversation (plus a preview for models that see the photo), " +
            $"so one request can use up to ~{AiSession.Fmt((long)n * ctx)} tokens (calls × context) and run much longer. " +
            "A confused model may loop, changing the same settings back and forth; on a cloud model this costs real money. " +
            (_s.TokenBudget > 0 ? $"The token budget per photo ({AiSession.Fmt(_s.TokenBudget)}) still stops it." : "There is no token budget set to stop it.");
        _stepsWarn.Visibility = Visibility.Visible;
    }

    private TextBlock Muted(string text) => new() { Text = text, Foreground = _muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private void LoadSettingsUi()
    {
        _loadingUi = true;
        _url.Text = _s.IsCloud ? _s.CloudUrl : _s.LocalUrl;
        _steps.Text = _s.MaxSteps.ToString(CultureInfo.InvariantCulture);
        _ctxTokens.Text = _s.ContextTokens.ToString(CultureInfo.InvariantCulture);
        _budget.Text = _s.TokenBudget.ToString(CultureInfo.InvariantCulture);
        _meta.IsChecked = _s.SendMetadata;
        UpdateStepsWarning();
        bool stored = !string.IsNullOrEmpty(AiKeyProtector.Unprotect(_s.ApiKeyProtected));
        bool env = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OLLAMA_API_KEY"));
        _keyState.Text = stored ? "key saved" : env ? "using OLLAMA_API_KEY" : "no key";
        _loadingUi = false;
    }

    private void Save(AiSettings s)
    {
        _s = s;
        try
        {
            SettingsStore.Save(SettingsStore.Load() with { Ai = s });
        }
        catch (Exception ex)
        {
            SetStatus("Could not save the settings: " + ex.Message);
        }
    }

    private void SaveUrl()
    {
        if (_loadingUi) return;
        var u = _url.Text.Trim();
        if (u.Length == 0) u = _s.IsCloud ? AiSettings.CloudDefault : AiSettings.LocalDefault;
        var cur = _s.IsCloud ? _s.CloudUrl : _s.LocalUrl;
        if (string.Equals(u, cur, StringComparison.Ordinal)) return;
        Save(_s.IsCloud ? _s with { CloudUrl = u } : _s with { LocalUrl = u });
        _ = LoadModelsAsync();
    }

    private void OnModeChanged()
    {
        if (_loadingUi || _mode.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        if (string.Equals(mode, _s.Mode, StringComparison.OrdinalIgnoreCase)) return;
        Save(_s with { Mode = mode });
        LoadSettingsUi();
        _ = LoadModelsAsync();
    }

    // ---------- models ----------

    private async Task LoadModelsAsync()
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _refresh.IsEnabled = false;
        SetStatus("Loading models from " + _s.BaseUrl + " …");
        try
        {
            if (_s.IsCloud && _s.ApiKey() is null)
            {
                _choices = new();
                FillModels();
                SetStatus("Ollama Cloud needs an API key (ollama.com → Settings → Keys). Open ⚙ to enter it.");
                _settingsPanel.Visibility = Visibility.Visible;
                return;
            }
            using var client = OllamaClient.For(_s);
            var models = await client.ListModelsAsync(cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(cts, _listCts)) return;
            _choices = AiModels.Rank(models);
            FillModels();
            int good = _choices.Count(c => c.Tier == AiModelTier.Recommended);
            SetStatus(_choices.Count == 0
                ? (_s.IsCloud ? "The server lists no models." : "No models installed. Example: ollama pull qwen2.5vl  (or a cloud one: ollama pull qwen3-vl:235b-cloud)")
                : $"{_choices.Count} models, {good} recommended (★).");
        }
        catch (Exception ex) when (ReferenceEquals(cts, _listCts))
        {
            _choices = new();
            FillModels();
            SetStatus(FriendlyError(ex));
        }
        catch (Exception)
        {
        }
        finally
        {
            if (ReferenceEquals(cts, _listCts)) _refresh.IsEnabled = true;
        }
    }

    private void FillModels()
    {
        _loadingUi = true;
        _model.Items.Clear();
        foreach (var c in _choices)
            _model.Items.Add(new ComboBoxItem { Content = c.Display, Tag = c.Name, Foreground = c.Tier == AiModelTier.Unsuitable ? _muted : _fg });
        string? want = _s.Model;
        if (string.IsNullOrEmpty(want) || _choices.All(c => !string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase)))
        {
            // nothing chosen yet: the best one; a typed name not on the list stays as it is
            if (string.IsNullOrEmpty(want) && _choices.FirstOrDefault(c => c.Tier != AiModelTier.Unsuitable) is { } best)
            {
                want = best.Name;
                Save(_s.WithModel(want));
            }
        }
        int idx = _choices.FindIndex(c => string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _model.SelectedIndex = idx;
        else
        {
            _model.SelectedIndex = -1;
            _model.Text = want ?? "";
        }
        _loadingUi = false;
        UpdateModelNote();
    }

    private void OnModelPicked()
    {
        if (_loadingUi || _model.SelectedItem is not ComboBoxItem { Tag: string name }) return;
        Save(_s.WithModel(name));
        // the combo shows the decorated label; keep it, the name is in the settings
        UpdateModelNote();
    }

    private void OnModelTyped()
    {
        if (_loadingUi || _model.SelectedItem is ComboBoxItem) return;
        var t = _model.Text.Trim();
        if (t.Length == 0 || string.Equals(t, _s.Model, StringComparison.Ordinal)) return;
        Save(_s.WithModel(t));
        UpdateModelNote();
    }

    private AiModelChoice? CurrentChoice()
    {
        var name = _s.Model;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _choices.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? AiModels.Classify(new AiModelInfo(name, 0, Array.Empty<string>(), CapabilitiesKnown: false));
    }

    private void UpdateModelNote()
    {
        var c = CurrentChoice();
        _modelNote.Text = c is null
            ? "Choose a model. ★ = recommended: it sees the photo and changes the settings."
            : c.Name + ": " + c.Note + (c.ContextLength > 0 ? $" · context up to {AiSession.Fmt(c.ContextLength)}" : "");
        UpdateUsage();
    }

    /// <summary>Context window in effect: num_ctx from the settings, capped by what the model supports (0 = unknown).</summary>
    private int EffectiveContext()
    {
        int model = CurrentChoice()?.ContextLength ?? 0;
        int asked = _s.ContextTokens;
        if (asked > 0 && model > 0) return Math.Min(asked, model);
        return asked > 0 ? asked : model;
    }

    private void UpdateUsage()
    {
        var s = CurrentSession(create: false);
        var c = CurrentChoice();
        int ctxMax = EffectiveContext();
        int ctxNow = s is { LastPromptTokens: > 0 } ? s.LastPromptTokens : (s ?? new AiSession()).EstimateNextContext(c?.Vision ?? false) + (c?.Vision == true ? AiSession.ImageTokens : 0);
        bool measured = s is { LastPromptTokens: > 0 } && !s.LastPromptEstimated;
        string ctx = $"Context: {(measured ? "" : "~")}{AiSession.Fmt(ctxNow)}" + (ctxMax > 0 ? $" / {AiSession.Fmt(ctxMax)}" : "");
        long used = s?.TotalTokens ?? 0;
        string budget = _s.TokenBudget > 0 ? $" / {AiSession.Fmt(_s.TokenBudget)}" : " (no limit)";
        string calls = s is { TotalCalls: > 0 } ? $" in {s.TotalCalls} call(s)" : "";
        string sight = c is null ? ""
            : c.Vision ? " · model sees the photo" + (s is { ImagesSent: > 0 } ? $" ({s.ImagesSent} preview(s) sent)" : "")
            : " · text only: the photo is not sent, only its statistics";
        string text = $"{ctx} tokens · used for this photo: {(s?.Estimated == true ? "~" : "")}{AiSession.Fmt(used)}{budget}{calls}{sight}";
        bool warn = false;
        if (ctxMax > 0 && ctxNow >= ctxMax * 0.9)
        {
            text += " — context nearly full: the server cuts the oldest part; raise num_ctx or start a new conversation";
            warn = true;
        }
        else if (s is not null && _s.TokenBudget > 0 && used >= _s.TokenBudget * 0.8)
        {
            text += s.OverBudget(_s.TokenBudget) ? " — budget used up" : " — budget nearly used";
            warn = true;
        }
        _usage.Text = text;
        _usage.Foreground = warn ? Brushes.IndianRed : _muted;
    }

    // ---------- conversation ----------

    private AiSession? CurrentSession(bool create)
    {
        var path = _main.AiPhotoPath;
        if (path is null) return null;
        if (!_sessions.TryGetValue(path, out var s) && create) _sessions[path] = s = new AiSession();
        return s;
    }

    /// <summary>Another photo is shown: its own conversation (or an empty one) appears.</summary>
    public void OnPhotoChanged(string? path) => ShowSession();

    private void ShowSession()
    {
        _transcript.Children.Clear();
        var s = CurrentSession(create: false);
        if (_main.AiPhotoPath is null)
            AddLine(new AiEvent(AiEventKind.Info, "Open a photo, then describe the look you want."));
        else if (s is null || s.Transcript.Count == 0)
            AddLine(new AiEvent(AiEventKind.Info,
                "Describe the result you want, e.g. \"prepare this photo for a family album\", \"reportage style\", " +
                "\"artistic black and white\", \"a bit warmer\". The assistant changes the settings itself; " +
                "one Undo (Ctrl+Z) removes a whole request."));
        else
            foreach (var e in s.Transcript)
                AddLine(e);
        UpdateUsage();
    }

    private void NewConversation()
    {
        if (_busy) return;
        var path = _main.AiPhotoPath;
        if (path is not null) _sessions.Remove(path);
        _main.AiConversationChanged(); // the .eye file drops the old conversation too
        ShowSession();
        SetStatus("New conversation (the photo keeps its settings).");
    }

    private void AddLine(AiEvent e)
    {
        var tb = new TextBox
        {
            Text = e.Kind switch
            {
                AiEventKind.User => e.Text,
                AiEventKind.Step => "• " + e.Text,
                AiEventKind.Error => "⚠ " + e.Text,
                _ => e.Text
            },
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = e.Kind is AiEventKind.Step or AiEventKind.Info ? _muted : e.Kind == AiEventKind.Error ? Brushes.IndianRed : _fg,
            FontSize = e.Kind == AiEventKind.Step ? 11 : 12,
            FontWeight = e.Kind == AiEventKind.User ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = e.Kind switch
            {
                AiEventKind.User => new Thickness(0, 10, 0, 2),
                AiEventKind.Step => new Thickness(10, 0, 0, 0),
                _ => new Thickness(0, 2, 0, 2)
            }
        };
        _transcript.Children.Add(tb);
        _scroll.ScrollToEnd();
        UpdateUsage();
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendAsync(_input.Text);
        }
        else if (e.Key == Key.Escape && _busy)
        {
            e.Handled = true;
            CancelTurn();
        }
    }

    /// <summary>Stops the running turn (changes made so far stay; Undo removes them).</summary>
    public void CancelTurn() => _turnCts?.Cancel();

    private async Task SendAsync(string text)
    {
        text = text.Trim();
        if (_busy || text.Length == 0) return;
        var path = _main.AiPhotoPath;
        if (path is null)
        {
            SetStatus("Open a photo first.");
            return;
        }
        if (_model.SelectedItem is not ComboBoxItem) OnModelTyped();
        var choice = CurrentChoice();
        if (choice is null)
        {
            SetStatus("Choose a model first.");
            return;
        }
        if (!choice.Tools)
        {
            SetStatus(choice.Name + " cannot call functions, so it cannot change settings — choose a model marked ★ or 'text only'.");
            return;
        }
        if (_s.IsCloud && _s.ApiKey() is null)
        {
            SetStatus("Ollama Cloud needs an API key — open ⚙ to enter it.");
            _settingsPanel.Visibility = Visibility.Visible;
            return;
        }
        if (IsCloudModel(choice.Name) && !_s.CloudConsent)
        {
            var r = MessageBox.Show(this,
                "This model runs on ollama.com: your request" + (choice.Vision ? ", a reduced preview of the photo (max 1024 px)" : "") +
                " and the image statistics will be sent to Ollama's servers." +
                (_s.SendMetadata ? " Camera data is sent too (no GPS, no file name)." : "") +
                "\n\nContinue? (asked once)",
                "AI assistant — cloud model", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            Save(_s with { CloudConsent = true });
        }

        var session = CurrentSession(create: true)!;
        if (session.OverBudget(_s.TokenBudget))
        {
            SetStatus($"This photo's conversation used its token budget ({AiSession.Fmt(session.TotalTokens)} of {AiSession.Fmt(_s.TokenBudget)}). " +
                      "Start a new conversation or raise the budget in ⚙.");
            return;
        }
        if (_transcript.Children.Count == 1 && session.Transcript.Count == 0) _transcript.Children.Clear();
        _input.Clear();
        SetBusy(true);
        var cts = _turnCts = new CancellationTokenSource();
        var settings = _s;
        _main.AiBeginTurn();
        var started = DateTime.Now;
        try
        {
            SetStatus($"{choice.Name} is working…");
            using var client = OllamaClient.For(settings);
            var res = await session.RunTurnAsync(client, _main.AiEditorFor(path, settings.SendMetadata), choice.Name, choice.Vision,
                text, new AiTurnOptions(settings.MaxSteps, settings.TokenBudget), AddLine, cts.Token);
            SetStatus($"Done in {(DateTime.Now - started).TotalSeconds:0} s, {res.ModelCalls} model call(s)" +
                      (res.Changed ? " — Ctrl+Z undoes the whole request." : "."));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            var stopped = new AiEvent(AiEventKind.Info, "Stopped. Changes made so far stay (Ctrl+Z removes them).");
            session.Note(stopped);
            if (string.Equals(_main.AiPhotoPath, path, StringComparison.OrdinalIgnoreCase)) AddLine(stopped);
            SetStatus("Stopped.");
        }
        catch (Exception ex)
        {
            var err = new AiEvent(AiEventKind.Error, FriendlyError(ex));
            session.Note(err);
            if (string.Equals(_main.AiPhotoPath, path, StringComparison.OrdinalIgnoreCase)) AddLine(err);
            SetStatus("The request failed.");
        }
        finally
        {
            _main.AiEndTurn(path, "AI: " + Short(text));
            if (ReferenceEquals(_turnCts, cts)) _turnCts = null;
            cts.Dispose();
            SetBusy(false);
            UpdateUsage();
            if (IsVisible) _input.Focus();
        }
    }

    /// <summary>The request leaves this computer: cloud mode, or a local Ollama's "-cloud" model (run on ollama.com).</summary>
    private bool IsCloudModel(string name) =>
        _s.IsCloud || name.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-cloud:", StringComparison.OrdinalIgnoreCase) || name.Contains(":cloud", StringComparison.OrdinalIgnoreCase);

    private static string Short(string text)
    {
        var t = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length <= 40 ? t : t[..39] + "…";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _send.IsEnabled = !busy;
        _stop.IsEnabled = busy;
        _new.IsEnabled = !busy;
        _mode.IsEnabled = !busy;
        _model.IsEnabled = !busy;
        foreach (var b in _quick.Children.OfType<Button>()) b.IsEnabled = !busy;
    }

    private void SetStatus(string text) => _status.Text = text;

    private string FriendlyError(Exception ex) => ex switch
    {
        HttpRequestException h when h.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            "The server refused the API key (" + (int)h.StatusCode + "). Check the key in ⚙.",
        HttpRequestException h when h.StatusCode is null =>
            _s.IsCloud
                ? "Cannot reach " + _s.BaseUrl + " — check the internet connection."
                : "Cannot reach Ollama at " + _s.BaseUrl + " — is Ollama running? (install from ollama.com, then start it)",
        TaskCanceledException => "The server did not answer in time.",
        _ => ex.Message
    };
}
