using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace HexagonLlamaCppSharp.Maui
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        private const int StorageSettingsRequestCode = 4101;
        private TaskCompletionSource<bool>? _storageSettingsCompletion;

        internal Task<bool> RequestStorageSettingsAsync(Intent intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_storageSettingsCompletion is not null)
            {
                return _storageSettingsCompletion.Task.WaitAsync(cancellationToken);
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _storageSettingsCompletion = completion;
            try
            {
                StartActivityForResult(intent, StorageSettingsRequestCode);
                return completion.Task.WaitAsync(cancellationToken);
            }
            catch
            {
                _storageSettingsCompletion = null;
                throw;
            }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == StorageSettingsRequestCode)
            {
                var completion = _storageSettingsCompletion;
                _storageSettingsCompletion = null;
                completion?.TrySetResult(global::Android.OS.Environment.IsExternalStorageManager);
            }
        }

        protected override void OnDestroy()
        {
            _storageSettingsCompletion?.TrySetCanceled();
            _storageSettingsCompletion = null;
            base.OnDestroy();
        }
    }
}
