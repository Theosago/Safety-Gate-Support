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
    // The largest preview is around 500 DIPs; allow ample detail on high-DPI displays.
    public const int MaxPixelDimension = 1280;

    public static Task<IdentificationPhotoResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(path, cancellationToken), cancellationToken);

    private static IdentificationPhotoResult Load(string path, CancellationToken cancellationToken)
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
            if (width > MaxPixelDimension || height > MaxPixelDimension)
            {
                if (width >= height) bitmap.DecodePixelWidth = MaxPixelDimension;
                else bitmap.DecodePixelHeight = MaxPixelDimension;
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
