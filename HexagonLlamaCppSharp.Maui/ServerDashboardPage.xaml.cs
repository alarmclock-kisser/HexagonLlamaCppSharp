using System.ComponentModel;
using HexagonLlamaCppSharp.Maui.ViewModels;
using Microsoft.Maui.Controls;

namespace HexagonLlamaCppSharp.Maui;

public partial class ServerDashboardPage : ContentPage
{
    private readonly ServerDashboardViewModel _viewModel;
    private bool _followServerLog = true;

    public ServerDashboardPage(ServerDashboardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _followServerLog = true;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Dispatcher.Dispatch(() => _ = ScrollServerLogToEndAsync());
    }

    protected override void OnDisappearing()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnDisappearing();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerDashboardViewModel.ConsoleText) && _followServerLog)
        {
            Dispatcher.Dispatch(() => _ = ScrollServerLogToEndAsync());
        }
    }

    private void OnServerLogScrolled(object? sender, ScrolledEventArgs e)
    {
        if (sender is not ScrollView scrollView)
        {
            return;
        }

        var bottom = e.ScrollY + scrollView.Height;
        _followServerLog = scrollView.ContentSize.Height <= scrollView.Height + 8 ||
                           bottom >= scrollView.ContentSize.Height - 24;
    }

    private async Task ScrollServerLogToEndAsync()
    {
        await Task.Yield();
        if (_followServerLog)
        {
            await ServerLogScrollView.ScrollToAsync(ServerLogLabel, ScrollToPosition.End, false);
        }
    }
}