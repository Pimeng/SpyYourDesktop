using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Desktop.Controls;

public sealed class UpdateInfoBarActions : Button
{
    public UpdateInfoBarActions()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        BorderBrush = new SolidColorBrush(Colors.Transparent);
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        var dismissButton = new Button { Content = "不再提示" };
        var skipButton = new Button { Content = "不提示此版本" };
        dismissButton.Click += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
        skipButton.Click += (_, _) => SkipRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(dismissButton);
        actions.Children.Add(skipButton);
        Content = actions;
    }

    public event EventHandler? DismissRequested;
    public event EventHandler? SkipRequested;
}
