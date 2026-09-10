using System.ComponentModel;
using HexagonLlamaCppSharp.Maui.ViewModels;

namespace HexagonLlamaCppSharp.Maui;

public partial class InstallationPage : ContentPage
{
    private const double TerminalBottomThreshold = 24;
    private readonly InstallationViewModel _viewModel;
    private bool _started;
    private bool _followTerminal = true;
    private bool _terminalScrollPending;

    public InstallationPage(InstallationViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (!_started)
        {
            _started = true;
            _ = _viewModel.RunPreflightAsync();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstallationViewModel.TerminalText))
        {
            if (string.IsNullOrEmpty(_viewModel.TerminalText))
            {
                _followTerminal = true;
            }

            QueueTerminalScroll();
        }
    }

    private void OnTerminalScrolled(object? sender, ScrolledEventArgs e)
    {
        if (sender is not ScrollView scrollView)
        {
            return;
        }

        var remainingScroll = scrollView.ContentSize.Height - scrollView.Height - e.ScrollY;
        _followTerminal = remainingScroll <= TerminalBottomThreshold;
    }

    private void QueueTerminalScroll()
    {
        if (!_followTerminal || _terminalScrollPending)
        {
            return;
        }

        _terminalScrollPending = true;
        Dispatcher.DispatchDelayed(
            TimeSpan.FromMilliseconds(50),
            () =>
            {
                _terminalScrollPending = false;
                if (_followTerminal)
                {
                    _ = ScrollTerminalToEndAsync();
                }
            });
    }

    private async Task ScrollTerminalToEndAsync()
    {
        await TerminalScrollView.ScrollToAsync(TerminalLogLabel, ScrollToPosition.End, false);
    }
}
