using Android.Content;
using Android.Provider;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class AndroidSharedStorageAccessService : ISharedStorageAccessService
{
    public Task<bool> RequestAccessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (global::Android.OS.Environment.IsExternalStorageManager)
        {
            return Task.FromResult(true);
        }

        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activity = Platform.CurrentActivity as MainActivity
                ?? throw new InvalidOperationException("Die Android-Activity für die Speicherfreigabe ist nicht verfügbar.");
            using var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission);
            intent.SetData(global::Android.Net.Uri.Parse($"package:{activity.PackageName}"));
            try
            {
                return await activity.RequestStorageSettingsAsync(intent, cancellationToken);
            }
            catch (ActivityNotFoundException)
            {
                using var fallback = new Intent(Settings.ActionManageAllFilesAccessPermission);
                return await activity.RequestStorageSettingsAsync(fallback, cancellationToken);
            }
        });
    }
}
