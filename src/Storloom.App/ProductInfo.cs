namespace Storloom.App;

internal static class ProductInfo {
    public const string Developer = "invefa";
    public const string Repository = "https://github.com/invefa/Storloom";
    public static string Version => typeof(ProductInfo).Assembly.GetName().Version?.ToString(3) ?? "0.4.0";
    public static Icon? LoadIcon(int size = 32) {
        using var stream = typeof(ProductInfo).Assembly.GetManifestResourceStream("Storloom.App.Assets.storloom.ico");
        return stream == null ? null : new Icon(stream, size, size);
    }
}
