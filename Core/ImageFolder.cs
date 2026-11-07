using System.Runtime.InteropServices;

namespace PaxImageViewer;

/// <summary>The images in a folder, in Explorer's order ("img2" before "img10").</summary>
public static partial class ImageFolder
{
    public static List<string> List(string folder)
    {
        var files = new List<string>();
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (var f in Directory.EnumerateFiles(folder, "*", options))
                if (ImageLoader.IsImage(f)) files.Add(f);
        }
        catch
        {
            // An unreadable folder just means no neighbours to step through.
        }
        files.Sort(NaturalOrder.Instance);
        return files;
    }

    /// <summary>The folder's images with <paramref name="file"/> in it (even if the folder hides or skips it).</summary>
    public static (List<string> Files, int Index) Around(string file)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(file));
        var files = folder is null ? [] : List(folder);
        var i = files.FindIndex(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            files.Add(file);
            files.Sort(NaturalOrder.Instance);
            i = files.FindIndex(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
        }
        return (files, i);
    }
}

/// <summary>File-name order like Explorer's: StrCmpLogicalW on Windows, digit runs compared as numbers elsewhere.</summary>
public sealed partial class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);

    public int Compare(string? a, string? b)
    {
        a = Path.GetFileName(a ?? "");
        b = Path.GetFileName(b ?? "");
        if (OperatingSystem.IsWindows()) return StrCmpLogicalW(a, b);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
