using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Limona.Services;

namespace Limona.Menu;

/// <summary>
/// Translates the static text of a freshly built window in one pass. Any text
/// that has no entry in the translation table is left exactly as it is, so
/// file names, numbers, hex codes and user content are never altered.
/// Dynamic text set later (status bars, hub hints) is wrapped with Loc.T at
/// its assignment site instead.
/// </summary>
internal static class LocalizationUi
{
    private sealed record LocalizedValue(string Source);

    private sealed class TextState
    {
        public TextState(string source, object?[]? args = null)
        {
            Source = source;
            Args = args;
        }

        public string Source { get; }
        public object?[]? Args { get; }
        public string LastApplied { get; set; } = "";

        public string Translate()
        {
            if (Args is null) return Loc.T(Source);
            var args = Args.Select(arg => arg is LocalizedValue value
                ? Loc.T(value.Source)
                : arg).ToArray();
            return Loc.F(Source, args);
        }
    }

    private sealed class NodeState
    {
        public TextState? Text;
        public TextState? Placeholder;
        public TextState? Content;
        public TextState? ToolTip;
        public TextState? AutomationName;
        public Dictionary<int, TextState?> ComboItems { get; } = new();
    }

    private static readonly ConditionalWeakTable<DependencyObject, NodeState> s_states = new();

    public static void ApplyTo(DependencyObject root)
    {
        if (root is null) return;
        if (root is FrameworkElement element)
            element.FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ApplyToChildren(root);
    }

    private static void ApplyToChildren(DependencyObject root)
    {
        Translate(root);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            ApplyToChildren(VisualTreeHelper.GetChild(root, i));
    }

    public static void Watch(FrameworkElement root)
    {
        void ApplyLanguage() => ApplyTo(root);
        void OnLoaded(object sender, RoutedEventArgs args)
        {
            ApplyTo(root);
            Loc.Changed += ApplyLanguage;
        }
        void OnUnloaded(object sender, RoutedEventArgs args) => Loc.Changed -= ApplyLanguage;

        root.Loaded += OnLoaded;
        root.Unloaded += OnUnloaded;
        if (root.IsLoaded) OnLoaded(root, new RoutedEventArgs());
    }

    public static void SetText(TextBlock text, string source)
    {
        var state = s_states.GetValue(text, _ => new NodeState());
        state.Text = new TextState(source);
        text.Text = state.Text.Translate();
        state.Text.LastApplied = text.Text;
    }

    public static void SetFormattedText(TextBlock text, string format, params object?[] args)
    {
        var state = s_states.GetValue(text, _ => new NodeState());
        state.Text = new TextState(format, args.ToArray());
        text.Text = state.Text.Translate();
        state.Text.LastApplied = text.Text;
    }

    public static object Localized(string source) => new LocalizedValue(source);

    public static void SetComboItem(ComboBox combo, int index, string format, params object?[] args)
    {
        if (combo.Items[index] is not string)
            throw new InvalidOperationException("The selected ComboBox item is not text.");

        var state = s_states.GetValue(combo, _ => new NodeState());
        var text = new TextState(format, args.ToArray());
        var translated = text.Translate();
        text.LastApplied = translated;
        state.ComboItems[index] = text;
        combo.Items[index] = translated;
    }

    public static void SetContent(ContentControl control, string source, params object?[] args)
    {
        var state = s_states.GetValue(control, _ => new NodeState());
        state.Content = new TextState(source, args.Length == 0 ? null : args.ToArray());
        var translated = state.Content.Translate();
        control.Content = translated;
        state.Content.LastApplied = translated;
    }

    public static void SetAutomationName(DependencyObject element, string source, params object?[] args)
    {
        var state = s_states.GetValue(element, _ => new NodeState());
        state.AutomationName = new TextState(source, args.Length == 0 ? null : args.ToArray());
        var translated = state.AutomationName.Translate();
        AutomationProperties.SetName(element, translated);
        state.AutomationName.LastApplied = translated;
    }

    public static void SetToolTip(DependencyObject element, string source, params object?[] args)
    {
        var state = s_states.GetValue(element, _ => new NodeState());
        state.ToolTip = new TextState(source, args.Length == 0 ? null : args.ToArray());
        var translated = state.ToolTip.Translate();
        ToolTipService.SetToolTip(element, translated);
        state.ToolTip.LastApplied = translated;
    }

    private static void Translate(DependencyObject node)
    {
        var state = s_states.GetValue(node, _ => new NodeState());

        switch (node)
        {
            case TextBlock text:
                text.Text = TranslateValue(text.Text, ref state.Text);
                break;

            case TextBox box:
                box.PlaceholderText = TranslateValue(box.PlaceholderText, ref state.Placeholder);
                break;

            case ComboBox combo:
                for (var i = 0; i < combo.Items.Count; i++)
                {
                    if (combo.Items[i] is string item)
                    {
                        state.ComboItems.TryGetValue(i, out var itemState);
                        var translated = TranslateValue(item, ref itemState);
                        state.ComboItems[i] = itemState;
                        if (!string.Equals(item, translated, StringComparison.Ordinal))
                            combo.Items[i] = translated;
                    }
                    else if (combo.Items[i] is DependencyObject itemNode)
                        Translate(itemNode);
                }
                break;

            case ContentControl content when content.Content is string text:
                content.Content = TranslateValue(text, ref state.Content);
                break;
        }

        if (ToolTipService.GetToolTip(node) is string tip)
            ToolTipService.SetToolTip(node, TranslateValue(tip, ref state.ToolTip));

        var automationName = AutomationProperties.GetName(node);
        AutomationProperties.SetName(node, TranslateValue(automationName, ref state.AutomationName) ?? "");
    }

    private static string? TranslateValue(string? current, ref TextState? state)
    {
        if (state is null || !string.Equals(current, state.LastApplied, StringComparison.Ordinal))
        {
            if (!Loc.HasTranslation(current ?? ""))
            {
                state = null;
                return current;
            }
            state = new TextState(current!);
        }

        var translated = state.Translate();
        state.LastApplied = translated;
        return translated;
    }
}
