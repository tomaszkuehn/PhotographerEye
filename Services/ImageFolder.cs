using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ImgViewer.Services;

public sealed class NaturalComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x is null) return y is null ? 0 : -1;
        if (y is null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            bool da = char.IsDigit(x[ix]);
            bool db = char.IsDigit(y[iy]);
            if (da && db)
            {
                int sx = ix, sy = iy;
                while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                while (iy < y.Length && char.IsDigit(y[iy])) iy++;
                string ra = x[sx..ix].TrimStart('0');
                string rb = y[sy..iy].TrimStart('0');
                int c = ra.Length != rb.Length
                    ? ra.Length.CompareTo(rb.Length)
                    : string.CompareOrdinal(ra, rb);
                if (c != 0) return c;
                continue;
            }

            int cf = string.Compare(x, ix, y, iy, 1, StringComparison.OrdinalIgnoreCase);
            if (cf != 0) return cf;
            ix++;
            iy++;
        }

        if (ix < x.Length) return 1;
        if (iy < y.Length) return -1;
        return string.CompareOrdinal(x, y);
    }
}

public static class ImageFolder
{
    private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png" };

    public static List<string> List(string dir)
    {
        var files = new List<string>();
        try
        {
            files.AddRange(Directory
                .EnumerateFiles(dir)
                .Where(p => Extensions.Contains(Path.GetExtension(p).ToLowerInvariant())));
        }
        catch (Exception)
        {
        }

        files.Sort(new NaturalComparer());
        return files;
    }
}