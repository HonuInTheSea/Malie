using System.IO;

namespace Malie.Infrastructure;

internal static class AppBranding
{
    public const string DisplayName = "Mâlie";
    public const string SafeName = "Malie";
    public const string LegacySafeName = "Malie";

    public static string GetLocalAppDataRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localAppData, SafeName);
        if (Directory.Exists(root))
        {
            return root;
        }

        var legacyRoot = Path.Combine(localAppData, LegacySafeName);
        if (!Directory.Exists(legacyRoot))
        {
            return root;
        }

        try
        {
            Directory.Move(legacyRoot, root);
            return root;
        }
        catch
        {
            return legacyRoot;
        }
    }
}
