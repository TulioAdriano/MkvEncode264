using System.Text;

/// <summary>
/// Full-screen, keyboard-driven setup in the style of nmtui, drawn with System.Console only:
/// a form for source, show, first episode and video mode, then a plan screen with Start/Back.
/// Tab / arrows move between fields, Enter or Space activates, Esc quits.
///
/// For headless tests: MKVENCODE264_TUI_KEYS holds a key script ("tab enter down ... " plus
/// literal text; "sp" types a space) and MKVENCODE264_TUI_DUMP=1 prints every frame as text.
/// </summary>
static class Tui
{
    public sealed record Setup(Options Options, List<Source> Sources, ShowInfo? Show, List<DiscPlan> Plans, string Encoder);

    const int BoxWidth = 80, BoxHeight = 22;

    public static async Task<Setup?> RunAsync(Options o)
    {
        bool dump = Environment.GetEnvironmentVariable("MKVENCODE264_TUI_DUMP") == "1";
        scripted  = Environment.GetEnvironmentVariable("MKVENCODE264_TUI_KEYS") is { Length: > 0 } script ? ParseKeys(script) : null;

        var terminal = new Terminal(dump);
        try   { return await new Flow(o, terminal).RunAsync(); }
        finally { terminal.Restore(); }
    }

    // ── the two screens ──────────────────────────────────────────────────────

    sealed class Flow(Options o, Terminal terminal)
    {
        // state carried between screens
        TextField source = null!, show = null!, firstEp = null!, quality = null!;
        int       videoMode = o.Deinterlace ? 3 : o.Encode ? 2 : 1;
        ShowInfo? showInfo;
        string    lookedUp = "";
        List<Source>   sources = [];
        List<DiscPlan> plans   = [];
        string  encoder = "h264_nvenc";
        string? encoderNote;
        string? makemkvcon;

        enum Next { Stay, Plan, Back, Start, Quit }
        Next next = Next.Stay;

        public async Task<Setup?> RunAsync()
        {
            Form form = BuildSetupForm();
            while (true)
            {
                next = Next.Stay;
                var canvas = new Canvas(BoxWidth, BoxHeight);
                form.Draw(canvas);
                terminal.Paint(canvas, form.Cursor);

                ConsoleKeyInfo key = ReadKey();
                if (key.Key == ConsoleKey.Escape || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                    return null;

                Widget before = form.Focused;
                form.Dispatch(key);
                if (before == show && form.Focused != show && show.Value.Trim() != lookedUp)
                    await LookupShowAsync(form);

                switch (next)
                {
                    case Next.Plan:
                        if (await ScanAsync(form)) form = BuildPlanForm();
                        break;
                    case Next.Back:
                        form = BuildSetupForm();
                        break;
                    case Next.Start:
                        return new Setup(o, sources, showInfo, plans, encoder);
                    case Next.Quit:
                        return null;
                }
            }
        }

        Form BuildSetupForm()
        {
            var f = new Form("MkvEncode264", BoxWidth, BoxHeight)
            {
                Footer = "Tab/arrows move   Enter/Space select   Esc quit",
            };
            const int labelX = 2, fieldX = 17, fieldW = 58;

            source  ??= new TextField("Source",        fieldX, fieldW) { Value = o.Inputs.Count > 0 ? string.Join("; ", o.Inputs) : Directory.GetCurrentDirectory(), Note = "an ISO image, an MKV, a DVD folder, or a folder holding several ISOs" };
            show    ??= new TextField("Show",          fieldX, fieldW) { Value = o.Show ?? "", Note = "optional: names the files and adds episode titles from TVmaze" };
            firstEp ??= new TextField("First episode", fieldX, 6)      { Value = o.StartEp.ToString() };
            quality ??= new TextField("Quality",       fieldX, 6)      { Value = o.Cq.ToString(), Note = "0-51, lower is better" };

            source.Y = 2;  show.Y = 5;  firstEp.Y = 8;
            f.Add(source);
            f.Add(show);
            f.Add(firstEp);
            f.Add(new Label("Video", labelX, 10));
            string[] modes = ["Keep the original video (fast, lossless)", "Encode to H.264", "Encode to H.264 and deinterlace"];
            for (int i = 0; i < modes.Length; i++)
            {
                int mode = i + 1;
                f.Add(new Radio(modes[i], fieldX, 10 + i, () => videoMode == mode, () => videoMode = mode));
            }
            quality.Y = 14;
            f.Add(quality);
            f.Add(new Button("Scan discs", 26, 17, () => next = Next.Plan));
            f.Add(new Button("Quit",       44, 17, () => next = Next.Quit));
            return f;
        }

        Form BuildPlanForm()
        {
            var f = new Form("Plan", BoxWidth, BoxHeight) { Footer = "Enter on Start begins ripping   Back returns to the setup   Esc quits" };
            var kinds = sources.GroupBy(s => s.KindText).Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}");
            f.Add(new Label($"Sources: {string.Join(", ", kinds)}   Mode: {Runner.DescribeMode(o, encoder)}" + (showInfo is not null ? $"   Show: {showInfo.Name}" : ""), 2, 1, Style.Dim));

            var list = new ListBox(2, 3, BoxWidth - 4, BoxHeight - 8) { Items = Runner.OverviewLines(plans) };
            f.Add(list);

            bool anyRunnable = plans.Any(p => p.Error is null);
            int buttonsY = BoxHeight - 4;
            if (anyRunnable) f.Add(new Button("Start", 26, buttonsY, () => next = Next.Start));
            f.Add(new Button("Back", 36, buttonsY, () => next = Next.Back));
            f.Add(new Button("Quit", 46, buttonsY, () => next = Next.Quit));
            if (!anyRunnable)               f.Status = ("Nothing can be ripped: every source was skipped.", Style.Error);
            else if (encoderNote is not null) f.Status = (encoderNote, Style.Error);
            f.FocusFirstButton();
            return f;
        }

        async Task LookupShowAsync(Form form)
        {
            string name = show.Value.Trim();
            lookedUp = name;
            showInfo = null;
            if (name.Length == 0)
            {
                show.Note = "optional: names the files and adds episode titles from TVmaze";
                show.NoteStyle = Style.Dim;
                return;
            }

            show.Note = "looking up on TVmaze...";
            show.NoteStyle = Style.Dim;
            Repaint(form);

            string? problem = null;
            showInfo = await ShowLookup.FetchAsync(name, false, message => problem = message);
            if (showInfo is null)
            {
                show.Note = problem ?? "not found on TVmaze; files will use this name without episode titles";
                show.NoteStyle = Style.Error;
            }
            else
            {
                string year  = showInfo.Premiered is { Length: >= 4 } p ? $" ({p[..4]})" : "";
                string first = string.Join(" / ", showInfo.Episodes.Take(3));
                show.Note = $"{showInfo.Name}{year}, {showInfo.Episodes.Count} titles: {first}";
                show.NoteStyle = Style.Ok;
            }
        }

        /// <summary>Validates the form, then discovers the sources and plans every one of them.</summary>
        async Task<bool> ScanAsync(Form form)
        {
            bool ok = true;

            var inputs = source.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                     .Select(p => p.Trim('"')).ToList();
            string? sourceError = inputs.Count == 0 ? "enter a path" : null;
            if (sourceError is not null || !SourceDiscovery.TryDiscover(inputs, out sources, out sourceError))
            {
                source.Note = sourceError ?? "enter a path";
                source.NoteStyle = Style.Error;
                ok = false;
            }
            else
            {
                source.Note = $"{sources.Count} source{(sources.Count == 1 ? "" : "s")}: {string.Join(", ", sources.Select(s => s.Name))}";
                source.NoteStyle = Style.Ok;
            }

            if (!int.TryParse(firstEp.Value, out int startEp) || startEp < 1)
            {
                firstEp.Note = "must be a positive number";
                firstEp.NoteStyle = Style.Error;
                ok = false;
            }
            else
            {
                firstEp.Note = null;
            }

            if (!int.TryParse(quality.Value, out int cq) || cq is < 0 or > 51)
            {
                quality.Note = "must be between 0 and 51";
                quality.NoteStyle = Style.Error;
                ok = false;
            }
            else
            {
                quality.Note = "0-51, lower is better";
                quality.NoteStyle = Style.Dim;
            }

            if (show.Value.Trim() != lookedUp) await LookupShowAsync(form);
            if (!ok) return false;

            o.Inputs.Clear();
            o.Inputs.AddRange(inputs);
            o.Show        = show.Value.Trim().Length > 0 ? show.Value.Trim() : null;
            o.StartEp     = startEp;
            o.Encode      = videoMode >= 2;
            o.Deinterlace = videoMode == 3;
            o.Cq          = cq;

            encoderNote = null;
            if (o.Encode && o.Cpu)
            {
                encoder = "libx264";
            }
            else if (o.Encode)
            {
                form.Status = ("Testing the NVENC encoder...", Style.Dim);
                Repaint(form);
                (encoder, string? reason) = await Ffmpeg.DetectEncoderAsync();
                if (encoder != "h264_nvenc") encoderNote = $"NVENC is not usable here ({reason}); encoding on the CPU instead.";
            }

            makemkvcon = sources.Any(s => s.IsDvd) ? MakeMkv.Locate(o.MakeMkvPath) : null;
            if (sources.Any(s => s.IsDvd) && makemkvcon is null)
            {
                form.Status = ("makemkvcon not found: install MakeMKV or pass --makemkv <path>.", Style.Error);
                return false;
            }

            plans = [];
            for (int i = 0; i < sources.Count; i++)
            {
                form.Status = ($"Scanning {i + 1}/{sources.Count}: {sources[i].Name}...", Style.Dim);
                Repaint(form);
                plans.Add(await Runner.PlanAsync(sources[i], o, makemkvcon));
            }
            Runner.AssignNumbers(plans, o.StartEp);
            form.Status = null;
            return true;
        }

        void Repaint(Form form)
        {
            var canvas = new Canvas(BoxWidth, BoxHeight);
            form.Draw(canvas);
            terminal.Paint(canvas, null);
        }
    }

    // ── widgets ──────────────────────────────────────────────────────────────

    enum Style { Normal, Dim, Label, Title, Frame, Field, FieldFocus, Button, ButtonFocus, Error, Ok, Selected }

    abstract class Widget
    {
        public int X, Y;
        public virtual bool Focusable => true;
        public abstract void Draw(Canvas c, bool focused);
        /// <summary>Returns true when the key was consumed.</summary>
        public virtual bool OnKey(ConsoleKeyInfo key) => false;
        public virtual (int X, int Y)? Cursor => null;
    }

    sealed class Label(string text, int x, int y, Style style = Style.Label) : Widget
    {
        public override bool Focusable => false;
        public override void Draw(Canvas c, bool focused) { X = x; Y = y; c.Write(x, y, Canvas.Clip(text, c.Width - x - 2), style); }
    }

    sealed class TextField(string label, int x, int width) : Widget
    {
        public string  Value = "";
        public string? Note;
        public Style   NoteStyle = Style.Dim;
        int cursor = int.MaxValue;   // caret position; starts at the end
        int scroll;

        public override void Draw(Canvas c, bool focused)
        {
            X = x;
            cursor = Math.Clamp(cursor, 0, Value.Length);
            if (cursor < scroll) scroll = cursor;
            if (cursor - scroll >= width) scroll = cursor - width + 1;

            c.Write(x - label.Length - 2, Y, label, Style.Label);
            c.Fill(x, Y, width, focused ? Style.FieldFocus : Style.Field);
            string visible = Value.Length > scroll ? Value[scroll..Math.Min(Value.Length, scroll + width)] : "";
            c.Write(x, Y, visible, focused ? Style.FieldFocus : Style.Field);
            if (Note is not null) c.Write(x, Y + 1, Canvas.Clip(Note, c.Width - x - 2), NoteStyle);
        }

        public override (int X, int Y)? Cursor => (X + Math.Clamp(cursor, 0, Value.Length) - scroll, Y);

        public override bool OnKey(ConsoleKeyInfo key)
        {
            cursor = Math.Clamp(cursor, 0, Value.Length);
            switch (key.Key)
            {
                case ConsoleKey.LeftArrow:  cursor = Math.Max(0, cursor - 1); return true;
                case ConsoleKey.RightArrow: cursor = Math.Min(Value.Length, cursor + 1); return true;
                case ConsoleKey.Home:       cursor = 0; return true;
                case ConsoleKey.End:        cursor = Value.Length; return true;
                case ConsoleKey.Backspace:
                    if (cursor > 0) { Value = Value.Remove(cursor - 1, 1); cursor--; }
                    return true;
                case ConsoleKey.Delete:
                    if (cursor < Value.Length) Value = Value.Remove(cursor, 1);
                    return true;
            }
            if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                Value = Value.Insert(cursor, key.KeyChar.ToString());
                cursor++;
                return true;
            }
            return false;
        }
    }

    sealed class Radio(string text, int x, int y, Func<bool> isSelected, Action select) : Widget
    {
        public override void Draw(Canvas c, bool focused)
        {
            X = x; Y = y;
            c.Write(x, y, (isSelected() ? "(X) " : "( ) ") + text, focused ? Style.Selected : Style.Normal);
        }

        public override bool OnKey(ConsoleKeyInfo key)
        {
            if (key.Key is ConsoleKey.Spacebar or ConsoleKey.Enter) { select(); return true; }
            return false;
        }
    }

    sealed class Button(string label, int x, int y, Action press) : Widget
    {
        public override void Draw(Canvas c, bool focused)
        {
            X = x; Y = y;
            c.Write(x, y, $"<{label}>", focused ? Style.ButtonFocus : Style.Button);
        }

        public override bool OnKey(ConsoleKeyInfo key)
        {
            if (key.Key is ConsoleKey.Spacebar or ConsoleKey.Enter) { press(); return true; }
            return false;
        }
    }

    sealed class ListBox(int x, int y, int width, int height) : Widget
    {
        public List<string> Items = [];
        int top;

        public override void Draw(Canvas c, bool focused)
        {
            X = x; Y = y;
            for (int row = 0; row < height; row++)
            {
                int i = top + row;
                if (i >= Items.Count) break;
                string text = Items[i].Length > width ? Items[i][..(width - 1)] + "~" : Items[i];
                c.Write(x, y + row, text, Style.Normal);
            }
            if (Items.Count > height)
                c.Write(x + width - 12, y + height, $"[{top + 1}-{Math.Min(top + height, Items.Count)} of {Items.Count}]", Style.Dim);
        }

        public override bool OnKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.DownArrow && top + height < Items.Count) { top++; return true; }
            if (key.Key == ConsoleKey.UpArrow && top > 0)                       { top--; return true; }
            return false;
        }
    }

    sealed class Form(string title, int width, int height)
    {
        readonly List<Widget> widgets = [];
        int focus = -1;

        public string Footer = "";
        public (string Text, Style Style)? Status;

        public void Add(Widget w)
        {
            widgets.Add(w);
            if (focus < 0 && w.Focusable) focus = widgets.Count - 1;
        }

        public Widget Focused => widgets[focus];
        public (int X, int Y)? Cursor => Focused.Cursor;

        public void FocusFirstButton()
        {
            int i = widgets.FindIndex(w => w is Button);
            if (i >= 0) focus = i;
        }

        public void Draw(Canvas c)
        {
            foreach (Widget w in widgets) w.Draw(c, w == Focused);
            if (Status is { } s) c.Write(2, height - 3, Canvas.Clip(s.Text, width - 4), s.Style);
            c.Write(2, height - 2, Canvas.Clip(Footer, width - 4), Style.Dim);
            c.Box(0, 0, width, height, title);   // last, so nothing can run over the frame
        }

        public void Dispatch(ConsoleKeyInfo key)
        {
            Widget w = Focused;
            if (w.OnKey(key)) return;

            bool shiftTab = key.Key == ConsoleKey.Tab && key.Modifiers.HasFlag(ConsoleModifiers.Shift);
            if (key.Key == ConsoleKey.UpArrow || shiftTab)                                     MoveFocus(-1);
            else if (key.Key is ConsoleKey.DownArrow or ConsoleKey.Tab or ConsoleKey.Enter)     MoveFocus(+1);
        }

        void MoveFocus(int delta)
        {
            for (int i = 1; i <= widgets.Count; i++)
            {
                int candidate = (focus + delta * i + widgets.Count * i) % widgets.Count;
                if (widgets[candidate].Focusable) { focus = candidate; return; }
            }
        }
    }

    // ── drawing surface and terminal ─────────────────────────────────────────

    sealed class Canvas(int width, int height)
    {
        readonly char[,]  chars  = Filled(width, height);
        readonly Style[,] styles = new Style[width, height];

        public int Width  => width;
        public int Height => height;

        static char[,] Filled(int w, int h)
        {
            var a = new char[w, h];
            for (int x = 0; x < w; x++) for (int y = 0; y < h; y++) a[x, y] = ' ';
            return a;
        }

        public void Write(int x, int y, string text, Style style = Style.Normal)
        {
            if (y < 0 || y >= height) return;
            for (int i = 0; i < text.Length; i++)
            {
                int cx = x + i;
                if (cx < 0 || cx >= width) continue;
                chars[cx, y]  = text[i];
                styles[cx, y] = style;
            }
        }

        public void Fill(int x, int y, int w, Style style) => Write(x, y, new string(' ', w), style);

        public void Box(int x, int y, int w, int h, string title)
        {
            Write(x, y, "┌" + new string('─', w - 2) + "┐", Style.Frame);
            for (int row = 1; row < h - 1; row++)
            {
                Write(x, y + row, "│", Style.Frame);
                Write(x + w - 1, y + row, "│", Style.Frame);
            }
            Write(x, y + h - 1, "└" + new string('─', w - 2) + "┘", Style.Frame);
            string t = $" {title} ";
            Write(x + (w - t.Length) / 2, y, t, Style.Title);
        }

        public Style StyleAt(int x, int y) => styles[x, y];

        /// <summary>Shortens text to fit, marking the cut with "~".</summary>
        public static string Clip(string text, int maxLength) =>
            maxLength <= 0 ? "" : text.Length <= maxLength ? text : text[..(maxLength - 1)] + "~";

        public string Text(int x, int y, int count)
        {
            var sb = new StringBuilder(count);
            for (int i = 0; i < count; i++) sb.Append(chars[x + i, y]);
            return sb.ToString();
        }

        public string Row(int y) => Text(0, y, width);
    }

    sealed class Terminal
    {
        readonly bool dump;

        public Terminal(bool dump)
        {
            this.dump = dump;
            Console.OutputEncoding = Encoding.UTF8;
            if (dump) return;
            Console.TreatControlCAsInput = true;
            Console.CursorVisible       = false;
            Console.Clear();
        }

        public void Paint(Canvas c, (int X, int Y)? cursor)
        {
            if (dump)
            {
                Console.WriteLine(new string('=', c.Width));
                for (int y = 0; y < c.Height; y++) Console.WriteLine(c.Row(y).TrimEnd());
                return;
            }

            int windowW = Console.WindowWidth, windowH = Console.WindowHeight;
            int originX = Math.Max(0, (windowW - c.Width) / 2);
            int originY = Math.Max(0, (windowH - c.Height) / 2);
            int maxX    = Math.Min(c.Width, windowW - originX - 1);

            Console.CursorVisible = false;
            for (int y = 0; y < c.Height && originY + y < windowH - 1; y++)
            {
                Console.SetCursorPosition(originX, originY + y);
                int x = 0;
                while (x < maxX)
                {
                    Style style = c.StyleAt(x, y);
                    int   start = x;
                    while (x < maxX && c.StyleAt(x, y) == style) x++;
                    var (fg, bg) = Colors(style);
                    Console.ForegroundColor = fg;
                    Console.BackgroundColor = bg;
                    Console.Write(c.Text(start, y, x - start));
                }
            }
            Console.ResetColor();

            if (cursor is { } cur && originX + cur.X < windowW && originY + cur.Y < windowH - 1)
            {
                Console.SetCursorPosition(originX + cur.X, originY + cur.Y);
                Console.CursorVisible = true;
            }
        }

        public void Restore()
        {
            if (dump) return;
            Console.ResetColor();
            Console.CursorVisible        = true;
            Console.TreatControlCAsInput = false;
            Console.Clear();
        }

        static (ConsoleColor Fg, ConsoleColor Bg) Colors(Style s) => s switch
        {
            Style.Dim         => (ConsoleColor.DarkGray, ConsoleColor.Black),
            Style.Label       => (ConsoleColor.Cyan,     ConsoleColor.Black),
            Style.Title       => (ConsoleColor.Red,      ConsoleColor.Black),
            Style.Frame       => (ConsoleColor.Gray,     ConsoleColor.Black),
            Style.Field       => (ConsoleColor.White,    ConsoleColor.DarkBlue),
            Style.FieldFocus  => (ConsoleColor.White,    ConsoleColor.Blue),
            Style.Button      => (ConsoleColor.White,    ConsoleColor.Black),
            Style.ButtonFocus => (ConsoleColor.Black,    ConsoleColor.White),
            Style.Error       => (ConsoleColor.Red,      ConsoleColor.Black),
            Style.Ok          => (ConsoleColor.Green,    ConsoleColor.Black),
            Style.Selected    => (ConsoleColor.Black,    ConsoleColor.Cyan),
            _                 => (ConsoleColor.Gray,     ConsoleColor.Black),
        };
    }

    // ── keyboard ─────────────────────────────────────────────────────────────

    static Queue<ConsoleKeyInfo>? scripted;

    static ConsoleKeyInfo ReadKey()
    {
        if (scripted is not null)
            return scripted.Count > 0 ? scripted.Dequeue() : new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false);
        return Console.ReadKey(intercept: true);
    }

    static Queue<ConsoleKeyInfo> ParseKeys(string script)
    {
        var queue = new Queue<ConsoleKeyInfo>();
        foreach (string token in script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token)
            {
                case "tab":   queue.Enqueue(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)); break;
                case "stab":  queue.Enqueue(new ConsoleKeyInfo('\t', ConsoleKey.Tab, true, false, false)); break;
                case "enter": queue.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)); break;
                case "esc":   queue.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)); break;
                case "up":    queue.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false)); break;
                case "down":  queue.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false)); break;
                case "left":  queue.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false)); break;
                case "right": queue.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false)); break;
                case "home":  queue.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false)); break;
                case "end":   queue.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false)); break;
                case "bs":    queue.Enqueue(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false)); break;
                case "del":   queue.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false)); break;
                case "sp":    queue.Enqueue(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false)); break;
                default:
                    foreach (char ch in token)
                        queue.Enqueue(new ConsoleKeyInfo(ch, char.IsAsciiLetterUpper(ch) ? (ConsoleKey)ch : ConsoleKey.NoName, false, false, false));
                    break;
            }
        }
        return queue;
    }
}
