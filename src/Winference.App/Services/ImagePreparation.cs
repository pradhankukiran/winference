using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Winference.Core.Runtime;

namespace Winference.App.Services;

/// <summary>Shrinks a picture for a vision model and re-encodes it as JPEG. Metadata such as location is dropped.</summary>
public static class ImagePreparation
{
    public const uint MaxSide = 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic" };

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    public static async Task<byte[]> PrepareAsync(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            using var input = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(input);
            var longSide = Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            var scale = longSide > MaxSide ? (double)MaxSide / longSide : 1;
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
                ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            using var output = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.85, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            output.Seek(0);
            using var reader = new DataReader(output);
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(bytes);
            if (bytes.Length > ChatImage.MaxBytes) throw new RuntimeException("InvalidImage", $"{name} is too large to send, even after shrinking it.");
            return bytes;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
        {
            throw new RuntimeException("InvalidImage", $"{name} couldn't be opened as a picture.", error);
        }
    }
}
