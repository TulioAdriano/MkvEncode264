enum SourceKind { Mkv, DvdImage, DvdFolder }

/// <summary>One thing to process: an MKV file, a DVD ISO image, or a folder containing VIDEO_TS.</summary>
record Source(string Path, SourceKind Kind)
{
    public bool   IsDvd     => Kind != SourceKind.Mkv;
    public string Name      => System.IO.Path.GetFileName(Path);
    public string Stem      => Kind == SourceKind.DvdFolder ? Name : System.IO.Path.GetFileNameWithoutExtension(Path);
    public string OutputDir => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public string KindText  => Kind switch { SourceKind.DvdImage => "DVD image", SourceKind.DvdFolder => "DVD folder", _ => "MKV" };
}

static class SourceDiscovery
{
    /// <summary>
    /// Expands what the user gave: files stand for themselves, a DVD folder is one source, and any
    /// other folder is a batch of the ISO images and DVD folders inside it (in natural name order).
    /// </summary>
    public static bool TryDiscover(IEnumerable<string> inputs, out List<Source> sources, out string? error)
    {
        sources = [];
        error   = null;

        foreach (string input in inputs)
        {
            if (Directory.Exists(input))
            {
                if (MakeMkv.IsDvdFolder(input))
                {
                    sources.Add(new Source(MakeMkv.DvdRoot(input), SourceKind.DvdFolder));
                    continue;
                }
                var found = FindInFolder(input);
                if (found.Count == 0)
                {
                    error = $"No ISO images or DVD folders found in: {input}";
                    return false;
                }
                sources.AddRange(found);
            }
            else if (File.Exists(input))
            {
                string full = Path.GetFullPath(input);
                bool   iso  = Path.GetExtension(full).Equals(".iso", StringComparison.OrdinalIgnoreCase);
                sources.Add(new Source(full, iso ? SourceKind.DvdImage : SourceKind.Mkv));
            }
            else
            {
                error = $"File not found: {input}";
                return false;
            }
        }
        return true;
    }

    public static List<Source> FindInFolder(string folder)
    {
        var list = new List<Source>();
        foreach (string f in Directory.GetFiles(folder))
            if (Path.GetExtension(f).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                list.Add(new Source(Path.GetFullPath(f), SourceKind.DvdImage));
        foreach (string d in Directory.GetDirectories(folder))
            if (MakeMkv.IsDvdFolder(d))
                list.Add(new Source(MakeMkv.DvdRoot(d), SourceKind.DvdFolder));
        list.Sort((a, b) => NaturalCompare(a.Name, b.Name));
        return list;
    }

    /// <summary>Orders "Disc 2" before "Disc 10": digit runs compare by value, the rest case-insensitively.</summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                int cmp = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
