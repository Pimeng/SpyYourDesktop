using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Desktop.ViewModels;

namespace Desktop.Views;

public sealed partial class UpdatesPage : Page
{
    public UpdatesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        DataContext = args.Parameter;
        if (DataContext is MainViewModel viewModel)
        {
            _ = viewModel.EnsureUpdateCheckAsync();
        }

        base.OnNavigatedTo(args);
    }
}
