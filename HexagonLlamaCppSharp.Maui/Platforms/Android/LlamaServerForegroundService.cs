using Android.App;
using Android.Content;
using Android.OS;

namespace HexagonLlamaCppSharp.Maui.Platforms.Android;

[Service(
    Name = "com.companyname.hexagonllamacppsharp.maui.LlamaServerForegroundService",
    Exported = false)]
public sealed class LlamaServerForegroundService : Service
{
    internal const string NotificationChannelId = "llama-server-runtime";
    private const int NotificationId = 4102;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(
        Intent? intent,
        StartCommandFlags flags,
        int startId)
    {
        EnsureNotificationChannel();
        var notification = new Notification.Builder(this, NotificationChannelId)
            .SetContentTitle("Hexagon llama-server")
            .SetContentText("llama-server läuft im Hintergrund")
            .SetSmallIcon(ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .Build();

        StartForeground(NotificationId, notification);
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        var manager = GetSystemService(global::Android.Content.Context.NotificationService) as NotificationManager;
        manager?.CreateNotificationChannel(new NotificationChannel(
            NotificationChannelId,
            "llama-server",
            NotificationImportance.Low)
        {
            Description = "Zeigt an, dass der lokale llama-server weiterläuft."
        });
    }
}
