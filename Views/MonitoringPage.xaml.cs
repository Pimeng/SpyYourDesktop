using System.ComponentModel;
using Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Desktop.Views;

public sealed partial class MonitoringPage : Page
{
    private MainViewModel? _viewModel;

    public MonitoringPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is null)
        {
            return;
        }

        UploadKeyBox.Password = _viewModel.UploadKey;
        SetPasswordRevealMode(_viewModel.ShowKey);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void UploadKeyBox_PasswordChanged(object sender, RoutedEventArgs args)
    {
        if (_viewModel is not null && _viewModel.UploadKey != UploadKeyBox.Password)
        {
            _viewModel.UploadKey = UploadKeyBox.Password;
        }
    }

    private void ShowKeyCheckBox_Checked(object sender, RoutedEventArgs args) => SetPasswordRevealMode(true);

    private void ShowKeyCheckBox_Unchecked(object sender, RoutedEventArgs args) => SetPasswordRevealMode(false);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.UploadKey) && UploadKeyBox.Password != _viewModel?.UploadKey)
        {
            UploadKeyBox.Password = _viewModel?.UploadKey ?? string.Empty;
        }

        if (args.PropertyName == nameof(MainViewModel.ShowKey) && _viewModel is not null)
        {
            SetPasswordRevealMode(_viewModel.ShowKey);
        }
    }

    private void SetPasswordRevealMode(bool visible)
    {
        UploadKeyBox.PasswordRevealMode = visible
            ? PasswordRevealMode.Visible
            : PasswordRevealMode.Hidden;
    }
}
