namespace pxdImageViewer;

/// <summary>
/// One-time move of what versions from before the pxd rename left behind (their settings folder). Safe to repeat:
/// does nothing once the new folder exists or the old one is gone, and never throws. This is the only place that
/// names the former prefix.
/// </summary>
public static class ViewerMigration
{
    /// <summary>The prefix the app had before the pxd rename; used only to find the old settings folder.</summary>
    public const string FormerPrefix = "Pax";

    public const string AppId = "pxdImageViewer";

    /// <summary>"pxdImageViewer" → its former name.</summary>
    public static string FormerName(string pxdName) =>
        pxdName.StartsWith("pxd", StringComparison.Ordinal) ? FormerPrefix + pxdName[3..] : pxdName;

    /// <summary>%APPDATA%\pxdImageViewer, moved over from the former folder the first time.</summary>
    public static string DataFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, AppId);
        Folder(Path.Combine(appData, FormerName(AppId)), dir);
        return dir;
    }

    /// <summary>Moves <paramref name="oldDir"/> to <paramref name="newDir"/> when only the old one exists. True if it moved.</summary>
    public static bool Folder(string oldDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
            try
            {
                Directory.Move(oldDir, newDir);
            }
            catch (IOException)
            {
                // Another drive, or a file in use: copy what can be copied and leave the old folder.
                CopyTree(oldDir, newDir);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false); } catch { /* skip */ }
        foreach (var d in Directory.GetDirectories(from))
            CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
