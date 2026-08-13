using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Desktop.Views;

public sealed partial class OverviewPage : Page
{
    public OverviewPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        DataContext = args.Parameter;
        base.OnNavigatedTo(args);
    }
}
