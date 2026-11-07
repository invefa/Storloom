namespace Storloom.App;

internal static class ProductInfo {
    public const string Developer = "invefa";
    public const string Repository = "https://github.com/invefa/Storloom";
    public static string Version => typeof(ProductInfo).Assembly.GetName().Version?.ToString(3) ?? "0.5.0";
    public static System.Windows.Media.ImageSource? LoadIcon() {
        using var stream = typeof(ProductInfo).Assembly.GetManifestResourceStream("Storloom.App.Assets.storloom.ico");
        if (stream == null) return null;
        return System.Windows.Media.Imaging.BitmapFrame.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
    }
}
