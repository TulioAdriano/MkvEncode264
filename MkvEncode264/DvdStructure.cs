using System.Buffers.Binary;
using System.Text;

/// <summary>
/// Reads the DVD-Video structure straight from an ISO image or a VIDEO_TS folder, without MakeMKV:
/// the title table (VIDEO_TS.IFO), per-title chapter counts and durations (VTS_xx_0.IFO), and the
/// targets of the menu buttons (navigation packs inside VIDEO_TS.VOB and VTS_xx_0.VOB).
/// <see cref="EpisodePlanner"/> uses this to recognise how episodes are laid out on the disc.
/// </summary>
sealed class DvdStructure
{
    const int SectorSize = 2048;

    public string          VolumeLabel { get; private set; } = "";
    public List<IfoTitle>  Titles      { get; } = [];
    public List<MenuScreen> Screens    { get; } = [];

    public int ButtonCount => Screens.Sum(s => s.Targets.Count);

    bool verbose;

    // Menu program chains per domain (0 = VMGM, otherwise the VTS number): PGC number -> its
    // pre, post and cell commands. Buttons often just link to a small routing PGC whose
    // pre-command performs the real jump into a title, so those are followed.
    readonly Dictionary<int, Dictionary<int, List<byte[]>>> menuPgcs = [];

    /// <summary>Returns null (with a reason) when the source cannot be read as DVD-Video.</summary>
    public static DvdStructure? TryRead(string path, bool verbose, out string? error)
    {
        error = null;
        try
        {
            using IDvdFiles files = Directory.Exists(path) ? new FolderFiles(path) : new IsoFiles(path);
            if (verbose) Console.WriteLine($"> VIDEO_TS: {string.Join(" ", files.Names.Order())}");
            var dvd = new DvdStructure { VolumeLabel = files.VolumeLabel, verbose = verbose };
            dvd.ReadTitles(files);
            dvd.ScanMenu(files, "VIDEO_TS.VOB", vts: 0);
            foreach (int vts in dvd.Titles.Select(t => t.Vts).Distinct().Order())
                dvd.ScanMenu(files, $"VTS_{vts:D2}_0.VOB", vts);
            return dvd;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // ── IFO tables ───────────────────────────────────────────────────────────

    void ReadTitles(IDvdFiles files)
    {
        byte[] vmg = files.ReadAll("VIDEO_TS.IFO");
        if (vmg.Length < 0x100 || Ascii(vmg, 0, 12) != "DVDVIDEO-VMG")
            throw new InvalidDataException("VIDEO_TS.IFO is not a DVD-Video manager file.");

        // TT_SRPT: one 12-byte entry per title (type, angles, chapters, parental, VTS, VTS_TTN, sector)
        int ttSrpt     = checked((int)BE32(vmg, 0xC4) * SectorSize);
        int titleCount = BE16(vmg, ttSrpt);
        var vtsCache   = new Dictionary<int, VtsInfo?>();

        menuPgcs[0] = ReadMenuPgcs(vmg, 0xC8);   // VMGM_PGCI_UT: main menu program chains

        for (int i = 0; i < titleCount; i++)
        {
            int e        = ttSrpt + 8 + i * 12;
            int angles   = vmg[e + 1];
            int chapters = BE16(vmg, e + 2);
            int vts      = vmg[e + 6];
            int ttn      = vmg[e + 7];

            if (!vtsCache.TryGetValue(vts, out VtsInfo? info))
            {
                vtsCache[vts] = info = ReadVts(files, vts);
                if (info is not null) menuPgcs[vts] = info.MenuPgcs;
            }

            TimeSpan duration       = TimeSpan.Zero;
            int      cells          = 0;
            var      chapterLengths = new List<TimeSpan>();
            if (info is not null && ttn >= 1 && ttn <= info.TitlePtts.Count)
            {
                var ptts = info.TitlePtts[ttn - 1];
                chapters = ptts.Count;
                foreach (int pgcn in ptts.Select(p => p.Pgcn).Distinct())
                    if (info.Pgcs.TryGetValue(pgcn, out PgcInfo? pgc))
                    {
                        duration += pgc.Duration;
                        cells    += pgc.Cells;
                    }

                // A chapter runs from its program up to the program before the next chapter (or the PGC end).
                for (int k = 0; k < ptts.Count; k++)
                {
                    var (pgcn, pgn) = ptts[k];
                    if (!info.Pgcs.TryGetValue(pgcn, out PgcInfo? pgc) || pgc.ProgramLengths.Count == 0)
                    { chapterLengths.Clear(); break; }

                    int lastPgn = k + 1 < ptts.Count && ptts[k + 1].Pgcn == pgcn ? ptts[k + 1].Pgn - 1 : pgc.ProgramLengths.Count;
                    TimeSpan length = TimeSpan.Zero;
                    for (int pg = Math.Max(pgn, 1); pg <= Math.Min(lastPgn, pgc.ProgramLengths.Count); pg++)
                        length += pgc.ProgramLengths[pg - 1];
                    chapterLengths.Add(length);
                }
            }

            Titles.Add(new IfoTitle(i + 1, vts, ttn, chapters, duration, cells, angles, chapterLengths));
        }
    }

    sealed record VtsInfo(List<List<(int Pgcn, int Pgn)>> TitlePtts, Dictionary<int, PgcInfo> Pgcs, Dictionary<int, List<byte[]>> MenuPgcs);
    sealed record PgcInfo(int Programs, int Cells, TimeSpan Duration, List<TimeSpan> ProgramLengths);

    /// <summary>
    /// Reads a menu PGCI unit table (VMGM_PGCI_UT or VTSM_PGCI_UT, one PGC table per menu
    /// language) and returns each PGC's navigation commands, keyed by PGC number.
    /// </summary>
    static Dictionary<int, List<byte[]>> ReadMenuPgcs(byte[] ifo, int pointerOffset)
    {
        var result = new Dictionary<int, List<byte[]>>();
        try
        {
            uint sector = BE32(ifo, pointerOffset);
            if (sector == 0) return result;

            int table = checked((int)sector * SectorSize);
            if (table + 8 > ifo.Length) return result;
            int units = BE16(ifo, table);
            for (int u = 0; u < units; u++)
            {
                int pgcit = table + checked((int)BE32(ifo, table + 8 + u * 8 + 4));
                if (pgcit + 8 > ifo.Length) continue;
                int nPgcs = BE16(ifo, pgcit);
                for (int i = 0; i < nPgcs; i++)
                {
                    int pgc = pgcit + checked((int)BE32(ifo, pgcit + 8 + i * 8 + 4));
                    if (pgc + 236 > ifo.Length) continue;
                    int cmdTable = BE16(ifo, pgc + 228);            // command table offset in the PGC header
                    if (cmdTable == 0) continue;

                    int t = pgc + cmdTable;
                    if (t + 8 > ifo.Length) continue;
                    int total = BE16(ifo, t) + BE16(ifo, t + 2) + BE16(ifo, t + 4);   // pre + post + cell commands
                    var commands = result.TryGetValue(i + 1, out var existing) ? existing : result[i + 1] = [];
                    for (int c = 0; c < total && t + 8 + (c + 1) * 8 <= ifo.Length; c++)
                        commands.Add(ifo.AsSpan(t + 8 + c * 8, 8).ToArray());
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            // damaged menu tables: keep whatever was read, buttons will simply resolve to fewer targets
        }
        return result;
    }

    static VtsInfo? ReadVts(IDvdFiles files, int vts)
    {
        string name = $"VTS_{vts:D2}_0.IFO";
        if (!files.Exists(name)) return null;

        byte[] ifo = files.ReadAll(name);
        if (ifo.Length < 0x100 || Ascii(ifo, 0, 12) != "DVDVIDEO-VTS") return null;

        // VTS_PTT_SRPT: per title, the list of (PGC number, program number) for each chapter
        int ptt      = checked((int)BE32(ifo, 0xC8) * SectorSize);
        int nTitles  = BE16(ifo, ptt);
        int lastByte = checked((int)BE32(ifo, ptt + 4));
        var titlePtts = new List<List<(int, int)>>();
        for (int i = 0; i < nTitles; i++)
        {
            int start = checked((int)BE32(ifo, ptt + 8 + i * 4));
            int end   = i + 1 < nTitles ? checked((int)BE32(ifo, ptt + 8 + (i + 1) * 4)) : lastByte + 1;
            var list  = new List<(int, int)>();
            for (int o = start; o + 4 <= end; o += 4)
                list.Add((BE16(ifo, ptt + o), BE16(ifo, ptt + o + 2)));
            titlePtts.Add(list);
        }

        // VTS_PGCIT: program chains with their playback time and cell count
        int pgcit = checked((int)BE32(ifo, 0xCC) * SectorSize);
        int nPgcs = BE16(ifo, pgcit);
        var pgcs  = new Dictionary<int, PgcInfo>();
        for (int i = 0; i < nPgcs; i++)
        {
            // PGC header (236 bytes): programs @2, cells @3, playback time @4, program map offset @230,
            // cell playback table offset @232 (24 bytes per cell, playback time at +4)
            int pgc      = pgcit + checked((int)BE32(ifo, pgcit + 8 + i * 8 + 4));
            int programs = ifo[pgc + 2];
            int cells    = ifo[pgc + 3];
            int mapOff   = BE16(ifo, pgc + 230);
            int cellOff  = BE16(ifo, pgc + 232);

            var programLengths = new List<TimeSpan>();
            try
            {
                if (mapOff > 0 && cellOff > 0)
                {
                    var cellLengths = new List<TimeSpan>();
                    for (int c = 0; c < cells; c++)
                        cellLengths.Add(DvdTime(ifo, pgc + cellOff + c * 24 + 4));

                    for (int p = 0; p < programs; p++)
                    {
                        int firstCell = ifo[pgc + mapOff + p];
                        int lastCell  = p + 1 < programs ? ifo[pgc + mapOff + p + 1] - 1 : cells;
                        TimeSpan length = TimeSpan.Zero;
                        for (int c = Math.Max(firstCell, 1); c <= Math.Min(lastCell, cells); c++)
                            length += cellLengths[c - 1];
                        programLengths.Add(length);
                    }
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                programLengths.Clear();   // damaged cell tables: chapter lengths stay unknown
            }

            pgcs[i + 1] = new PgcInfo(programs, cells, DvdTime(ifo, pgc + 4), programLengths);
        }

        return new VtsInfo(titlePtts, pgcs, ReadMenuPgcs(ifo, 0xD0));   // VTSM_PGCI_UT: this title set's menus
    }

    // ── menu buttons ─────────────────────────────────────────────────────────

    /// <summary>
    /// Walks the navigation packs of a menu VOB. Each pack carries the button table of the menu
    /// screen it belongs to (PCI highlight information); every button has an 8-byte navigation
    /// command, and we keep the ones that jump to a title or to a chapter of a title.
    /// </summary>
    void ScanMenu(IDvdFiles files, string name, int vts)
    {
        if (!files.Exists(name))
        {
            if (verbose) Console.WriteLine($"> {name}: not present");
            return;
        }

        using Stream stream = files.OpenRead(name);
        var sector   = new byte[SectorSize];
        var seen     = new HashSet<string>();
        var commands = new Dictionary<string, int>();   // raw button commands seen (verbose diagnostics)
        int sectors  = 0, navPacks = 0, withButtons = 0;

        while (ReadSector(stream, sector))
        {
            sectors++;
            if (!(sector[0] == 0 && sector[1] == 0 && sector[2] == 1 && sector[3] == 0xBA)) continue;   // MPEG pack
            int pci = FindPci(sector);
            if (pci < 0) continue;
            navPacks++;

            // hli (highlight info) starts 96 bytes into the PCI: hli_ss @+0, btn_ns @+17, button table @+46
            int hli = pci + 96;
            if ((BE16(sector, hli) & 0x03) == 0) continue;                 // no buttons in this pack
            int buttons = sector[hli + 17];
            if (buttons == 0 || buttons > 36) continue;
            withButtons++;

            var targets = new List<MenuTarget>();
            for (int b = 0; b < buttons; b++)
            {
                int cmd = hli + 46 + b * 18 + 10;                          // 18-byte button entry, command at +10
                if (verbose)
                {
                    string hex = Convert.ToHexString(sector, cmd, 8);
                    commands[hex] = commands.GetValueOrDefault(hex) + 1;
                }
                foreach (MenuTarget t in ResolveTargets(sector.AsSpan(cmd, 8).ToArray(), vts, depth: 0))
                    if (!targets.Contains(t)) targets.Add(t);
            }
            if (targets.Count == 0) continue;

            string key = string.Join("|", targets);
            if (seen.Add(key))
                Screens.Add(new MenuScreen(vts == 0 ? "main menu" : $"VTS {vts} menu", targets));
        }

        if (verbose)
            Console.WriteLine($"> {name}: {sectors} sectors, {navPacks} nav packs, {withButtons} with buttons; commands: " +
                              string.Join(" ", commands.OrderByDescending(kv => kv.Value).Take(24).Select(kv => $"{kv.Key}x{kv.Value}")));
    }

    /// <summary>Finds the PCI payload (private stream 2, substream 0) inside a navigation pack.</summary>
    static int FindPci(byte[] sector)
    {
        for (int i = 14; i + 7 + 980 <= sector.Length; i++)
            if (sector[i] == 0 && sector[i + 1] == 0 && sector[i + 2] == 1 && sector[i + 3] == 0xBF && sector[i + 6] == 0x00)
                return i + 7;
        return -1;
    }

    /// <summary>
    /// Where a navigation command ends up: a direct jump into a title, or, when it links to
    /// another menu PGC, whatever that PGC's own commands jump to (followed a few levels deep).
    /// A routing PGC with several conditional jumps yields all of them.
    /// </summary>
    IEnumerable<MenuTarget> ResolveTargets(byte[] c, int vts, int depth)
    {
        if (DecodeJump(c, vts) is { } direct)
        {
            yield return direct;
            yield break;
        }
        if (depth >= 3 || LinkedPgc(c) is not int pgcn) yield break;
        if (!menuPgcs.TryGetValue(vts, out var pgcs) || !pgcs.TryGetValue(pgcn, out var commands)) yield break;

        foreach (byte[] command in commands)
            foreach (MenuTarget t in ResolveTargets(command, vts, depth + 1))
                yield return t;
    }

    /// <summary>The PGC number a LinkPGCN command (possibly combined with a Set instruction) links to.</summary>
    static int? LinkedPgc(byte[] c)
    {
        int type = c[0] >> 5;
        if (type < 1 || type > 6) return null;
        if (type == 1 && (c[0] & 0x10) != 0) return null;               // that is a Jump/Call, not a Link
        if ((c[1] & 0x0F) != 4) return null;                            // link sub-op 4 = LinkPGCN
        int pgcn = ((c[6] << 8) | c[7]) & 0x7FFF;
        return pgcn > 0 ? pgcn : null;
    }

    /// <summary>Decodes JumpTT / JumpVTS_TT / JumpVTS_PTT navigation commands.</summary>
    MenuTarget? DecodeJump(ReadOnlySpan<byte> c, int vts)
    {
        if ((c[0] >> 5) != 1 || (c[0] & 0x10) == 0) return null;         // not a Jump/Call instruction
        int title = c[5] & 0x7F;
        switch (c[1] & 0x0F)
        {
            case 2:                                                        // JumpTT <title>
                return title > 0 ? new MenuTarget(title, null) : null;
            case 3:                                                        // JumpVTS_TT <vts title>
                return vts > 0 ? Map(vts, title, null) : null;
            case 5:                                                        // JumpVTS_PTT <vts title>, <chapter>
                int ptt = ((c[2] & 0x03) << 8) | c[3];
                return vts > 0 && ptt > 0 ? Map(vts, title, ptt) : null;
            default:
                return null;
        }
    }

    MenuTarget? Map(int vts, int ttn, int? chapter)
    {
        IfoTitle? t = Titles.FirstOrDefault(t => t.Vts == vts && t.VtsTtn == ttn);
        return t is null ? null : new MenuTarget(t.Number, chapter);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    static bool ReadSector(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total == buffer.Length;
    }

    static string Ascii(byte[] b, int offset, int count) => Encoding.ASCII.GetString(b, offset, count);
    static int    BE16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o, 2));
    static uint   BE32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
    static uint   LE32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
    static int    Bcd(byte v) => (v >> 4) * 10 + (v & 0x0F);

    /// <summary>DVD time: BCD hours, minutes, seconds, then frame-rate bits and BCD frames.</summary>
    static TimeSpan DvdTime(byte[] b, int o)
    {
        double fps = (b[o + 3] >> 6) switch { 1 => 25.0, 3 => 29.97, _ => 30.0 };
        double seconds = Bcd(b[o]) * 3600 + Bcd(b[o + 1]) * 60 + Bcd(b[o + 2]) + Bcd((byte)(b[o + 3] & 0x3F)) / fps;
        return TimeSpan.FromSeconds(seconds);
    }

    // ── file access: VIDEO_TS folder or ISO 9660 image ───────────────────────

    interface IDvdFiles : IDisposable
    {
        string VolumeLabel { get; }
        IEnumerable<string> Names { get; }
        bool   Exists(string name);
        byte[] ReadAll(string name);
        Stream OpenRead(string name);
    }

    sealed class FolderFiles : IDvdFiles
    {
        readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);

        public FolderFiles(string root)
        {
            root = MakeMkv.DvdRoot(root);
            VolumeLabel = Path.GetFileName(root);
            foreach (string f in Directory.GetFiles(Path.Combine(root, "VIDEO_TS")))
                paths[Path.GetFileName(f)] = f;
        }

        public string VolumeLabel { get; }
        public IEnumerable<string> Names => paths.Keys;
        public bool   Exists(string name)   => paths.ContainsKey(name);
        public byte[] ReadAll(string name)  => File.ReadAllBytes(paths[name]);
        public Stream OpenRead(string name) => File.OpenRead(paths[name]);
        public void   Dispose() { }
    }

    /// <summary>Minimal ISO 9660 reader: locates the VIDEO_TS directory and exposes its files.</summary>
    sealed class IsoFiles : IDvdFiles
    {
        readonly FileStream iso;
        readonly Dictionary<string, (long Offset, long Length)> entries = new(StringComparer.OrdinalIgnoreCase);

        public IsoFiles(string path)
        {
            iso = File.OpenRead(path);

            var pvd = new byte[SectorSize];
            iso.Position = 16L * SectorSize;
            if (!ReadSector(iso, pvd) || pvd[0] != 1 || Ascii(pvd, 1, 5) != "CD001")
                throw new InvalidDataException("No ISO 9660 file system found in the image.");
            VolumeLabel = Ascii(pvd, 40, 32).Trim();

            // Root directory record sits at offset 156 of the primary volume descriptor.
            var root  = ReadDirectory(LE32(pvd, 156 + 2), LE32(pvd, 156 + 10));
            if (!root.TryGetValue("VIDEO_TS", out var videoTs) || !videoTs.IsDir)
                throw new InvalidDataException("The image has no VIDEO_TS folder.");

            foreach (var (name, e) in ReadDirectory(videoTs.Extent, videoTs.Size))
                if (!e.IsDir) entries[name] = (e.Extent * SectorSize, e.Size);
        }

        Dictionary<string, (long Extent, long Size, bool IsDir)> ReadDirectory(long extent, long size)
        {
            var data = new byte[size];
            iso.Position = extent * SectorSize;
            iso.ReadExactly(data);

            var map = new Dictionary<string, (long, long, bool)>(StringComparer.OrdinalIgnoreCase);
            int pos = 0;
            while (pos < data.Length)
            {
                int len = data[pos];
                if (len == 0) { pos = (pos / SectorSize + 1) * SectorSize; continue; }   // records never span sectors
                if (pos + len > data.Length) break;

                int    nameLen = data[pos + 32];
                string name    = Ascii(data, pos + 33, nameLen);
                if (name is not ("\0" or "\u0001"))                                          // skip "." and ".."
                {
                    int semi = name.IndexOf(';');
                    if (semi >= 0) name = name[..semi];
                    map[name] = (LE32(data, pos + 2), LE32(data, pos + 10), (data[pos + 25] & 0x02) != 0);
                }
                pos += len;
            }
            return map;
        }

        public string VolumeLabel { get; }
        public IEnumerable<string> Names => entries.Select(e => $"{e.Key} ({e.Value.Length / 1024 / 1024} MB)");
        public bool   Exists(string name) => entries.ContainsKey(name);

        public byte[] ReadAll(string name)
        {
            var (offset, length) = entries[name];
            var data = new byte[length];
            iso.Position = offset;
            iso.ReadExactly(data);
            return data;
        }

        public Stream OpenRead(string name)
        {
            var (offset, length) = entries[name];
            return new SubStream(iso, offset, length);
        }

        public void Dispose() => iso.Dispose();
    }

    /// <summary>Read-only window over a region of another stream.</summary>
    sealed class SubStream(Stream inner, long offset, long length) : Stream
    {
        long position;

        public override bool CanRead  => true;
        public override bool CanSeek  => true;
        public override bool CanWrite => false;
        public override long Length   => length;
        public override long Position { get => position; set => position = Math.Clamp(value, 0, length); }

        public override int Read(byte[] buffer, int start, int count)
        {
            int n = (int)Math.Min(count, length - position);
            if (n <= 0) return 0;
            inner.Position = offset + position;
            int read = inner.Read(buffer, start, n);
            position += read;
            return read;
        }

        public override long Seek(long value, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Begin => value, SeekOrigin.Current => position + value, _ => length + value };
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// A title as listed in VIDEO_TS.IFO. <see cref="Number"/> is the 1-based DVD title number;
/// <see cref="ChapterLengths"/> is empty when the cell tables could not be read.
/// </summary>
record IfoTitle(int Number, int Vts, int VtsTtn, int Chapters, TimeSpan Duration, int Cells, int Angles, List<TimeSpan> ChapterLengths);

/// <summary>Where a menu button jumps: a DVD title, optionally at a given 1-based chapter.</summary>
record MenuTarget(int Title, int? Chapter)
{
    public override string ToString() => Chapter is null ? $"T{Title}" : $"T{Title}:{Chapter}";
}

/// <summary>One menu screen: a distinct set of button targets found in a menu VOB.</summary>
record MenuScreen(string Source, List<MenuTarget> Targets);
