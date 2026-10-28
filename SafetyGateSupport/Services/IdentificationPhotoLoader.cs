using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace SafetyGateSupport.Services;

public enum IdentificationPhotoStatus { Loaded, Missing, Unreadable }

public readonly record struct IdentificationPhotoResult(BitmapSource? Source, IdentificationPhotoStatus Status);

/// <summary>Loads a fresh, display-sized photo without holding its file open or blocking the UI.</summary>
public static class IdentificationPhotoLoader
{
    // Workspace previews use 1280 px; gallery tiles request 640 and the large viewer 4096.
    public const int MaxPixelDimension = 1280;

    public static Task<IdentificationPhotoResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, MaxPixelDimension, cancellationToken);

    public static Task<IdentificationPhotoResult> LoadAsync(string path, int maxPixelDimension, CancellationToken cancellationToken = default)
    {
        if (maxPixelDimension is < 192 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxPixelDimension));
        return Task.Run(() => Load(path, maxPixelDimension, cancellationToken), cancellationToken);
    }

    private static IdentificationPhotoResult Load(string path, int maxPixelDimension, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return new(null, IdentificationPhotoStatus.Missing);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            // Inspect dimensions without decoding the full-size pixel buffer. Limit the long edge,
            // preserving aspect ratio and avoiding upscaling photos that are already small.
            var header = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var width = header.PixelWidth;
            var height = header.PixelHeight;
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (width > maxPixelDimension || height > maxPixelDimension)
            {
                if (width >= height) bitmap.DecodePixelWidth = maxPixelDimension;
                else bitmap.DecodePixelHeight = maxPixelDimension;
            }
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            return new(bitmap, IdentificationPhotoStatus.Loaded);
        }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return new(null, IdentificationPhotoStatus.Missing); }
        catch (DirectoryNotFoundException) { return new(null, IdentificationPhotoStatus.Missing); }
        catch { return new(null, IdentificationPhotoStatus.Unreadable); }
    }
}
