namespace HexagonLlamaCppSharp.Maui.Services;

public interface IInstallationDialogService
{
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string accept,
        string cancel);
}