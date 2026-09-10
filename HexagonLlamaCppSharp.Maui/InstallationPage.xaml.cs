using HexagonLlamaCppSharp.Maui.ViewModels;

namespace HexagonLlamaCppSharp.Maui;

public partial class InstallationPage : ContentPage
{
    private readonly InstallationViewModel _viewModel;
    private bool _started;

    public InstallationPage(InstallationViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
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
}
