using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Desktop.ViewModels;

namespace Desktop.Views;

public sealed partial class MonitoringPage : Page
{
    public MonitoringPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (RootNavigation.SelectedItem is null)
        {
            RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        }
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "Connection" => typeof(ConnectionPage),
            "Advanced" => typeof(AdvancedPage),
            "Updates" => typeof(UpdatesPage),
            _ => typeof(OverviewPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, DataContext);
        }
    }

    private void UpdateActions_DismissRequested(object sender, EventArgs args)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.DismissNoticeCommand.Execute(null);
        }
    }

    private void UpdateActions_SkipRequested(object sender, EventArgs args)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SkipUpdateCommand.Execute(null);
        }
    }
}
