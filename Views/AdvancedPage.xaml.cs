using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Desktop.Views;

public sealed partial class AdvancedPage : Page
{
    public AdvancedPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        DataContext = args.Parameter;
        base.OnNavigatedTo(args);
    }
}
