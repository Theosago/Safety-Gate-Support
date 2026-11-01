using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;

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

    private static Transform ExifMirrorRotation(int angle)
    {
        var transforms = new TransformGroup(); transforms.Children.Add(new ScaleTransform(-1, 1)); transforms.Children.Add(new RotateTransform(angle)); return transforms;
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
            int orientation = 1;
            if (header.Metadata is BitmapMetadata metadata)
                foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                    try { if (metadata.GetQuery(query) is { } value) { orientation = Convert.ToInt32(value); break; } } catch { /* Metadata is optional; decode the image even if this query is unavailable. */ }
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
            Transform? transform = orientation switch
            {
                2 => new ScaleTransform(-1, 1), 3 => new RotateTransform(180), 4 => new ScaleTransform(1, -1),
                5 => ExifMirrorRotation(270), 6 => new RotateTransform(90), 7 => ExifMirrorRotation(90), 8 => new RotateTransform(270), _ => null
            };
            if (transform is null) return new(bitmap, IdentificationPhotoStatus.Loaded);
            var oriented = new TransformedBitmap(bitmap, transform); oriented.Freeze();
            return new(oriented, IdentificationPhotoStatus.Loaded);
        }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return new(null, IdentificationPhotoStatus.Missing); }
        catch (DirectoryNotFoundException) { return new(null, IdentificationPhotoStatus.Missing); }
        catch { return new(null, IdentificationPhotoStatus.Unreadable); }
    }
}
