using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhilipsControl;

/// <summary>Small themed modal used for pairing PINs, renames and confirmations, instead of the stock MessageBox.</summary>
internal sealed class TerminalDialog : Window
{
    private readonly TextBox? _input;
    private string? _result;

    private TerminalDialog(Window? owner, string title, string message, string? inputValue, string? placeholder, string acceptLabel, string? cancelLabel, bool danger, int maxLength, bool requireInput = false)
    {
        if (owner is { IsVisible: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ShowInTaskbar = owner is not { IsVisible: true };
        Title = title;
        Background = (Brush)Application.Current.Resources["Bg"];
        Foreground = (Brush)Application.Current.Resources["Text"];
        FontFamily = (FontFamily)Application.Current.Resources["Mono"];
        FontSize = 13;

        var shell = new Border { BorderBrush = (Brush)Application.Current.Resources["AccentLine"], BorderThickness = new Thickness(1), Padding = new Thickness(24, 20, 24, 20) };
        shell.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var stack = new StackPanel();
        var heading = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 15, FontWeight = FontWeights.Bold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Danger" : "Accent");
        stack.Children.Add(heading);
        stack.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 10, 0, 16), Foreground = (Brush)Application.Current.Resources["Dim"], TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
        if (inputValue is not null)
        {
            _input = new TextBox { Text = inputValue, Tag = placeholder, MaxLength = maxLength, FontSize = 18, Padding = new Thickness(12, 10, 12, 10), Style = (Style)Application.Current.Resources["TextField"] };
            stack.Children.Add(_input);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        if (cancelLabel is not null)
        {
            var cancel = new Button { Content = $"[ {cancelLabel} ]", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            cancel.Click += (_, _) => { _result = null; DialogResult = false; };
            buttons.Children.Add(cancel);
        }
        var accept = new Button { Content = $"[ {acceptLabel} ]", IsDefault = true, Style = (Style)Application.Current.Resources[danger ? "DangerButton" : "PrimaryButton"] };
        accept.Click += (_, _) =>
        {
            if (requireInput && string.IsNullOrWhiteSpace(_input?.Text)) { _input?.Focus(); return; }
            _result = _input?.Text.Trim() ?? "";
            DialogResult = true;
        };
        buttons.Children.Add(accept);
        stack.Children.Add(buttons);
        shell.Child = stack;
        Content = shell;
        Loaded += (_, _) =>
        {
            if (_input is not null) { _input.Focus(); _input.SelectAll(); }
            else accept.Focus();
        };
    }

    public static string? AskPin(Window owner, string tvName) =>
        Show(new TerminalDialog(owner, "Pair Philips TV", $"Enter the PIN now shown on {tvName}. The PIN is used once to create a secure, encrypted pairing with this PC.", "", "PIN", "PAIR", "CANCEL", false, 12, requireInput: true));

    public static string? AskText(Window owner, string title, string message, string value, string placeholder) =>
        Show(new TerminalDialog(owner, title, message, value, placeholder, "SAVE", "CANCEL", false, 60));

    public static bool Confirm(Window owner, string title, string message, string acceptLabel, bool danger = false) =>
        Show(new TerminalDialog(owner, title, message, null, null, acceptLabel, "CANCEL", danger, 0)) is not null;

    public static void Inform(Window owner, string title, string message) =>
        Show(new TerminalDialog(owner, title, message, null, null, "OK", null, false, 0));

    private static string? Show(TerminalDialog dialog)
    {
        dialog.ShowDialog();
        return dialog._result;
    }
}
