using System.Text;
using Android.Content;
using Android.Provider;

namespace HexagonLlamaCppSharp.Maui.Services;

public sealed class AndroidLogExportService : ILogExportService
{
    private const string DownloadRelativePath = "Download/HexagonLlamaCppSharp/";

    public async Task<string> SaveAsync(
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var fileName = $"hexagon-installation-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt";
        var content = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        if (global::Android.OS.Environment.IsExternalStorageManager)
        {
            var externalRoot = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath
                ?? throw new IOException("Android hat keinen öffentlichen Speicherpfad bereitgestellt.");
            var directory = Path.Combine(externalRoot, "Download", "HexagonLlamaCppSharp");
            Directory.CreateDirectory(directory);
            var destinationPath = Path.Combine(directory, fileName);
            var temporaryPath = destinationPath + ".partial";
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: false);
            return destinationPath;
        }

        try
        {
            var context = global::Android.App.Application.Context;
            var resolver = context.ContentResolver
                ?? throw new IOException("Android konnte den Downloads-Speicheranbieter nicht öffnen.");
            var values = new ContentValues();
            values.Put("display_name", fileName);
            values.Put("mime_type", "text/plain");
            values.Put("relative_path", DownloadRelativePath);
            values.Put("is_pending", 1);

            var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values)
                ?? throw new IOException("Android konnte die Logdatei in Downloads nicht anlegen.");
            try
            {
                await using var stream = resolver.OpenOutputStream(uri)
                    ?? throw new IOException("Android konnte die Logdatei in Downloads nicht öffnen.");
                var bytes = Encoding.UTF8.GetBytes(content);
                await stream.WriteAsync(bytes.AsMemory(), cancellationToken);
                await stream.FlushAsync(cancellationToken);

                var completedValues = new ContentValues();
                completedValues.Put("is_pending", 0);
                resolver.Update(uri, completedValues, null, null);
                return $"Download/HexagonLlamaCppSharp/{fileName}";
            }
            catch
            {
                resolver.Delete(uri, null, null);
                throw;
            }
        }
        catch (global::Java.Lang.Exception)
        {
            return await SavePrivateCopyAsync(fileName, content, cancellationToken);
        }
        catch (IOException)
        {
            return await SavePrivateCopyAsync(fileName, content, cancellationToken);
        }
    }

    private static async Task<string> SavePrivateCopyAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken)
    {
        var destinationPath = Path.Combine(FileSystem.AppDataDirectory, fileName);
        await File.WriteAllTextAsync(
            destinationPath,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        return $"AppData/{fileName}";
    }
}