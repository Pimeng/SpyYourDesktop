using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Desktop.Views;

public sealed partial class StrategyPage : Page
{
    public StrategyPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        DataContext = args.Parameter;
        base.OnNavigatedTo(args);
    }
}
