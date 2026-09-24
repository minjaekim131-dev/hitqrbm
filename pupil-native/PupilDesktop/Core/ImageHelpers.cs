using System.IO;
using System.Windows.Media.Imaging;

namespace PupilDesktop.Core;

public static class ImageHelpers
{
    public static BitmapImage ToBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static string SafeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "gallery" : value.Trim();
    }
}
