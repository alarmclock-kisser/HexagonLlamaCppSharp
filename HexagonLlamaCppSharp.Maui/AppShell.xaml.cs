namespace HexagonLlamaCppSharp.Maui
{
    public partial class AppShell : Shell
    {
        public AppShell(InstallationPage installationPage, ServerDashboardPage serverDashboardPage)
        {
            InitializeComponent();
            Items.Add(new ShellContent
            {
                Title = "Installation",
                Route = "InstallationPage",
                Content = installationPage
            });
            Items.Add(new ShellContent
            {
                Title = "llama-server",
                Route = "ServerDashboardPage",
                Content = serverDashboardPage
            });
        }
    }
}
