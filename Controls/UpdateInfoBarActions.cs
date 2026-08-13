using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Desktop.Controls;

public sealed class UpdateInfoBarActions : ButtonBase
{
    public UpdateInfoBarActions()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        BorderBrush = new SolidColorBrush(Colors.Transparent);
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        HorizontalContentAlignment = HorizontalAlignment.Right;
        VerticalContentAlignment = VerticalAlignment.Center;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var dismissButton = new HyperlinkButton
        {
            Content = "不再提示",
            Padding = new Thickness(6, 2, 6, 2)
        };
        var skipButton = new HyperlinkButton
        {
            Content = "不再提示此版本",
            Padding = new Thickness(6, 2, 6, 2)
        };
        dismissButton.Click += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
        skipButton.Click += (_, _) => SkipRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(dismissButton);
        actions.Children.Add(skipButton);
        Content = actions;
    }

    public event EventHandler? DismissRequested;
    public event EventHandler? SkipRequested;
}
