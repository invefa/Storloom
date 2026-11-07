namespace Storloom.App;

internal static class ProductInfo {
    public const string Developer = "invefa";
    public const string Repository = "https://github.com/invefa/Storloom";
    public static string Version => typeof(ProductInfo).Assembly.GetName().Version?.ToString(3) ?? "0.5.2";
    public static System.Windows.Media.ImageSource? LoadIcon(int pixels = 32) {
        using var stream = typeof(ProductInfo).Assembly.GetManifestResourceStream("Storloom.App.Assets.storloom.ico");
        if (stream == null) return null;
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderBy(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth >= pixels) ?? decoder.Frames[^1];
    }
}
