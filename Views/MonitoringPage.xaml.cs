using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

}
