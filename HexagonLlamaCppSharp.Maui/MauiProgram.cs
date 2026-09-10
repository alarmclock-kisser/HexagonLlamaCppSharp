using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HexagonLlamaCppSharp.Maui.ViewModels;

namespace HexagonLlamaCppSharp.Maui
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            builder.Services.AddSingleton<RuntimeLayout>(_ => new RuntimeLayout(FileSystem.AppDataDirectory));
            builder.Services.AddSingleton<HttpClient>(_ => new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(30)
            });
            builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
            builder.Services.AddSingleton<IFilePermissionService, UnixFilePermissionService>();
            builder.Services.AddSingleton<IEmbeddedNativeAssetStore, EmbeddedNativeAssetStore>();
            builder.Services.AddSingleton<IExecutionProbe, AndroidExecutionProbe>();
            builder.Services.AddSingleton<IToolchainManifestProvider, PackageToolchainManifestProvider>();
            builder.Services.AddSingleton<IInstallationDialogService, MauiInstallationDialogService>();
            builder.Services.AddSingleton<ISharedStorageAccessService, AndroidSharedStorageAccessService>();
            builder.Services.AddSingleton<ILogExportService, AndroidLogExportService>();
            builder.Services.AddSingleton<IToolchainArchiveTransferService, ToolchainArchiveTransferService>();
            builder.Services.AddSingleton<IInAppToolchainBootstrapper, InAppToolchainBootstrapper>();
            builder.Services.AddSingleton<ILlamaBuildInstaller, LlamaBuildInstaller>();
            builder.Services.AddSingleton<IInstallationManifestStore, InstallationManifestStore>();
            builder.Services.AddSingleton<ILlamaServerService, LlamaServerService>();
            builder.Services.AddSingleton<IModelFilePicker, ModelFilePicker>();
            builder.Services.AddSingleton<ITermuxFallbackService, TermuxFallbackService>();
            builder.Services.AddTransient<InstallationViewModel>();
            builder.Services.AddSingleton<ServerDashboardViewModel>();
            builder.Services.AddSingleton<InstallationPage>();
            builder.Services.AddSingleton<ServerDashboardPage>();
            builder.Services.AddSingleton<AppShell>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
