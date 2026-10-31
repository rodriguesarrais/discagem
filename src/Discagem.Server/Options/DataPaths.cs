namespace Discagem.Server.Options;

public static class DataPaths
{
    public static string Root(DiscagemOptions options)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        return root;
    }

    public static string Uploads(DiscagemOptions options)
    {
        var path = Path.Combine(Root(options), "uploads");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Avatars(DiscagemOptions options)
    {
        var path = Path.Combine(Root(options), "avatars");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Database(DiscagemOptions options) => Path.Combine(Root(options), "discagem.db");
}
