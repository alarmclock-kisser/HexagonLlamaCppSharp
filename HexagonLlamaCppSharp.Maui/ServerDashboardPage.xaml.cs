using HexagonLlamaCppSharp.Maui.ViewModels;

namespace HexagonLlamaCppSharp.Maui;

public partial class ServerDashboardPage : ContentPage
{
    public ServerDashboardPage(ServerDashboardViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}