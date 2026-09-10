namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class MauiInstallationDialogService : IInstallationDialogService
{
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string accept,
        string cancel)
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page
            ?? throw new InvalidOperationException("Es ist keine sichtbare Installationsseite verfügbar.");

        return MainThread.InvokeOnMainThreadAsync(() => page.DisplayAlertAsync(
            title,
            message,
            accept,
            cancel));
    }
}