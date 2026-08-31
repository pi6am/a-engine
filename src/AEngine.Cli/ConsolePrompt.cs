using System.Text;

namespace AEngine.Cli;

/// <summary>A reaction choice menu for the F2 popup (quick-time events).</summary>
public sealed record ReactionMenu(
    string Title, IReadOnlyList<string> Options, int DefaultIndex, int SecondsLeft);

/// <summary>
/// Minimal MUD-style console prompt. The main thread reads input one key
/// at a time, keeping the in-progress line in a buffer; background output
/// (observed signals in real-time mode) erases the input line, prints
/// above it, then redraws the prompt and the partial text — typing is
/// never visually interrupted. Line editing: left/right move the cursor,
/// printable characters insert at the cursor, backspace deletes before
/// it; up/down cycle through the command history (down past the most
/// recent entry returns to the line you were typing). While the line is a
/// slash-command word (starts with '/', no space yet) a completion popup
/// lists matching commands with their help summaries; up/down then cycle
/// the popup's selection instead of the history, tab completes the
/// selected command, and ESC dismisses the popup until the next edit.
/// A transient status line (SetStatus) renders between the log and the
/// input line — used to announce pending quick-time reactions; F2 then
/// opens a modal popup over the input line to pick a reaction (up/down,
/// Enter to confirm, ESC to close). Falls back to plain Console.ReadLine
/// when input is redirected (pipes, tests).
/// </summary>
public sealed class ConsolePrompt
{
    private readonly object _writeLock = new();
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _history = new();
    private string _prompt = "> ";
    private bool _reading;
    private int _cursor; // insertion point, 0.._buffer.Length
    private int _historyIndex; // _history.Count == the fresh, not-yet-submitted line
    private string _savedLine = ""; // the fresh line's content while browsing history
    private int _selected; // the highlighted completion
    private bool _completionsEnabled = true;
    private bool _popupDismissed; // ESC closes the popup until the next edit
    private string? _status; // the status line above the input line, when set
    private ReactionMenu? _modalMenu; // the open F2 reaction popup
    private int _modalSel;
    private bool _wakePending; // Wake() asked the blocked ReadLine to return

    /// <summary>Slash commands (with leading '/') and their summaries, for tab completion.</summary>
    public IReadOnlyList<(string Name, string Description)>? Completions { get; set; }

    /// <summary>The pending reaction to show when F2 is pressed, if any (quick-time events).</summary>
    public Func<ReactionMenu?>? ReactionMenuProvider { get; set; }

    /// <summary>Called with the chosen option index when the F2 popup is confirmed.</summary>
    public Action<int>? ReactionChosen { get; set; }

    /// <summary>
    /// True when the last ReadLine returned because of <see cref="Wake"/>
    /// rather than input or EOF — the caller re-checks world state (e.g.
    /// the game ended while the player was idle) instead of exiting.
    /// </summary>
    public bool WasWoken { get; private set; }

    /// <summary>
    /// True when the last ReadLine returned because the user pressed ESC
    /// while in auto mode (autoStatus was set) — the caller turns auto
    /// mode off instead of exiting.
    /// </summary>
    public bool WasAutoCancel { get; private set; }

    /// <summary>
    /// Interrupt a blocked ReadLine (from another thread — the real-time
    /// world clock): the call returns null promptly with WasWoken set.
    /// </summary>
    public void Wake()
    {
        lock (_writeLock)
            _wakePending = true;
    }

    /// <summary>
    /// Prompt and read one line of input. Null on EOF (redirected input
    /// only), on <see cref="Wake"/>, or on ESC in auto mode. When
    /// <paramref name="autoStatus"/> is set, the prompt is input-disabled:
    /// the status line shows it (e.g. "Auto mode: press ESC to cancel"),
    /// all keys except ESC are ignored, and <paramref name="onIdle"/> runs
    /// on every poll iteration (outside the write lock) so the caller can
    /// keep the world moving while waiting.
    /// </summary>
    public string? ReadLine(
        string prompt = "> ", bool completions = true,
        string? autoStatus = null, Action? onIdle = null)
    {
        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }

        lock (_writeLock)
        {
            _prompt = prompt;
            _buffer.Clear();
            _cursor = 0;
            _historyIndex = _history.Count;
            _savedLine = "";
            _selected = 0;
            _popupDismissed = false;
            _completionsEnabled = completions;
            WasWoken = false;
            WasAutoCancel = false;
            _reading = true;
            if (autoStatus is not null)
            {
                // the status row sits directly above the input row (same
                // layout SetStatus maintains)
                _status = autoStatus;
                Console.Write(autoStatus + "\n" + prompt);
            }
            else
            {
                Console.Write(prompt);
            }
        }
        try
        {
            while (true)
            {
                // poll instead of a blocking ReadKey so Wake() (the world
                // clock ending the game) can interrupt the wait; do NOT
                // hold the lock while sleeping
                lock (_writeLock)
                {
                    if (_wakePending)
                    {
                        _wakePending = false;
                        WasWoken = true;
                        // settle at the end of the line and erase anything
                        // below it, like Enter does, so the log continues
                        // on a clean line
                        if (_status is not null)
                        {
                            _status = null;
                            Console.Write("\x1b[1A\r\x1b[2K\x1b[M");
                        }
                        Console.Write('\r');
                        var endCol = _prompt.Length + _buffer.Length;
                        if (endCol > 0)
                            Console.Write($"\x1b[{endCol}C");
                        Console.Write("\x1b[J");
                        Console.Write("\n");
                        return null;
                    }
                }
                if (!Console.KeyAvailable)
                {
                    // auto mode's world stepping hook (turn-based auto-play);
                    // runs outside the write lock (it touches the engine)
                    onIdle?.Invoke();
                    Thread.Sleep(15);
                    continue;
                }
                var key = Console.ReadKey(intercept: true);
                // auto mode: input is disabled; only ESC (cancel) applies
                if (autoStatus is not null)
                {
                    if (key.Key == ConsoleKey.Escape)
                    {
                        lock (_writeLock)
                        {
                            WasAutoCancel = true;
                            _status = null;
                            Console.Write("\x1b[1A\r\x1b[2K\x1b[M"); // delete the status row
                            Console.Write("\r\x1b[2K"); // clear the input row
                        }
                        return null;
                    }
                    continue;
                }
                // menu provider/callback invocations happen outside the lock
                // (they take engine locks; the timer thread calls SetStatus
                // holding them — avoid a lock-ordering deadlock)
                var openMenu = false;
                int? reactionChoice = null;
                lock (_writeLock)
                {
                    if (_modalMenu is { } menu)
                    {
                        // modal reaction popup: only navigation keys apply
                        if (key.Key == ConsoleKey.UpArrow && menu.Options.Count > 0)
                        {
                            _modalSel = (_modalSel - 1 + menu.Options.Count) % menu.Options.Count;
                            Redraw();
                        }
                        else if (key.Key == ConsoleKey.DownArrow && menu.Options.Count > 0)
                        {
                            _modalSel = (_modalSel + 1) % menu.Options.Count;
                            Redraw();
                        }
                        else if (key.Key == ConsoleKey.Enter)
                        {
                            reactionChoice = _modalSel;
                            _modalMenu = null;
                            Redraw();
                        }
                        else if (key.Key == ConsoleKey.Escape)
                        {
                            _modalMenu = null;
                            Redraw();
                        }
                    }
                    else if (key.Key == ConsoleKey.F2)
                    {
                        openMenu = true; // provider invoked below, outside the lock
                    }
                    else if (key.Key == ConsoleKey.Enter)
                    {
                        // the status line (if any) detaches into the log —
                        // the caller re-sets it if the reaction is still open
                        if (_status is not null)
                        {
                            _status = null;
                            Console.Write("\x1b[1A\r\x1b[2K\x1b[M");
                        }
                        // settle at the end of the line and erase the popup
                        // below it before moving on
                        Console.Write('\r');
                        var endCol = _prompt.Length + _buffer.Length;
                        if (endCol > 0)
                            Console.Write($"\x1b[{endCol}C");
                        Console.Write("\x1b[J");
                        Console.Write("\n");
                        var line = _buffer.ToString();
                        if (line.Length > 0 && (_history.Count == 0 || _history[^1] != line))
                            _history.Add(line);
                        return line;
                    }
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (_cursor > 0)
                        {
                            var hadPopup = CurrentMatches().Count > 0;
                            _buffer.Remove(_cursor - 1, 1);
                            _cursor--;
                            _popupDismissed = false;
                            SaveFreshLine();
                            if (InCommandWord)
                            {
                                _selected = 0;
                                Redraw();
                            }
                            else if (hadPopup)
                                Redraw(); // erase the popup the edit left behind
                            else if (_cursor == _buffer.Length)
                                Console.Write("\b \b");
                            else
                                Redraw();
                        }
                        continue;
                    }
                    if (key.Key == ConsoleKey.Escape)
                    {
                        // dismiss the popup, keep the typed text; the next
                        // edit reopens it
                        if (CurrentMatches().Count > 0)
                        {
                            _popupDismissed = true;
                            Redraw();
                        }
                        continue;
                    }
                    if (key.Key == ConsoleKey.Tab)
                    {
                        var matches = CurrentMatches();
                        if (matches.Count > 0)
                        {
                            _buffer.Clear();
                            _buffer.Append(matches[_selected].Name);
                            _buffer.Append(' ');
                            _cursor = _buffer.Length;
                            SaveFreshLine();
                            Redraw(); // the space closes the popup
                        }
                        continue;
                    }
                    if (key.Key == ConsoleKey.UpArrow)
                    {
                        var matches = CurrentMatches();
                        if (matches.Count > 0)
                        {
                            _selected = (_selected - 1 + matches.Count) % matches.Count;
                            Redraw();
                        }
                        else
                            Navigate(-1);
                        continue;
                    }
                    if (key.Key == ConsoleKey.DownArrow)
                    {
                        var matches = CurrentMatches();
                        if (matches.Count > 0)
                        {
                            _selected = (_selected + 1) % matches.Count;
                            Redraw();
                        }
                        else
                            Navigate(+1);
                        continue;
                    }
                    if (key.Key == ConsoleKey.LeftArrow)
                    {
                        if (_cursor > 0)
                        {
                            _cursor--;
                            Console.Write("\x1b[D");
                        }
                        continue;
                    }
                    if (key.Key == ConsoleKey.RightArrow)
                    {
                        if (_cursor < _buffer.Length)
                        {
                            _cursor++;
                            Console.Write("\x1b[C");
                        }
                        continue;
                    }
                    if (!char.IsControl(key.KeyChar))
                    {
                        var hadPopup = CurrentMatches().Count > 0;
                        _buffer.Insert(_cursor, key.KeyChar);
                        _cursor++;
                        _popupDismissed = false;
                        SaveFreshLine();
                        if (InCommandWord)
                        {
                            _selected = 0;
                            Redraw();
                        }
                        else if (hadPopup)
                            Redraw(); // erase the popup the edit left behind
                        else if (_cursor == _buffer.Length)
                            Console.Write(key.KeyChar);
                        else
                            Redraw();
                    }
                    // other control keys are ignored
                }
                if (openMenu && ReactionMenuProvider?.Invoke() is { } m && m.Options.Count > 0)
                {
                    lock (_writeLock)
                    {
                        _modalMenu = m;
                        _modalSel = m.DefaultIndex;
                        Redraw();
                    }
                }
                if (reactionChoice is { } choice)
                    ReactionChosen?.Invoke(choice);
            }
        }
        finally
        {
            lock (_writeLock)
                _reading = false;
        }
    }

    /// <summary>
    /// Print background output. While the user is typing, the input line
    /// is erased first and redrawn (with its partial content) afterwards.
    /// </summary>
    public void WriteAbove(string text)
    {
        lock (_writeLock)
        {
            if (_reading && !Console.IsInputRedirected)
            {
                if (_status is not null)
                    Console.Write("\x1b[1A"); // the status line is the topmost managed row
                Console.Write("\r\x1b[2K"); // carriage return + erase line
                Console.Write("\x1b[J"); // and everything below (input line, popups)
                Console.WriteLine(text);
                if (_status is not null)
                    Console.Write(_status + "\n");
                Redraw();
            }
            else
            {
                Console.WriteLine(text);
            }
        }
    }

    /// <summary>
    /// Set or clear the transient status line between the log and the
    /// input line (e.g. "the duelist swings at you! — F2 to react").
    /// Appearing/disappearing inserts/deletes a terminal row so the log
    /// is never repainted; text updates redraw in place.
    /// </summary>
    public void SetStatus(string? text)
    {
        lock (_writeLock)
        {
            var had = _status is not null;
            _status = text;
            if (!_reading || Console.IsInputRedirected)
                return;
            if (!had && text is not null)
            {
                Console.Write("\x1b[L"); // open a row at the input line; the input moves down
                Console.Write("\r\x1b[2K" + text + "\n");
                Redraw();
            }
            else if (had && text is null)
            {
                Console.Write("\x1b[1A\r\x1b[2K\x1b[M"); // delete the status row
                Redraw();
            }
            else if (had && text is not null)
            {
                Console.Write("\x1b[1A\r\x1b[2K" + text + "\n"); // update in place
                Redraw();
            }
        }
    }

    // the completion popup is only offered while the line is a single
    // slash-command word: "/q" completes, "1/2" and "/timescale 2" don't
    private bool InCommandWord =>
        _completionsEnabled && Completions is not null &&
        _buffer.Length > 0 && _buffer[0] == '/' &&
        !_buffer.ToString().Contains(' ');

    private List<(string Name, string Description)> CurrentMatches()
    {
        if (!InCommandWord || _popupDismissed)
            return [];
        var text = _buffer.ToString();
        var matches = Completions!
            .Where(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (_selected >= matches.Count)
            _selected = Math.Max(0, matches.Count - 1);
        return matches;
    }

    // up/down through the history; the slot past the most recent entry is
    // the fresh line, restored to whatever had been typed there
    private void Navigate(int delta)
    {
        var fresh = _history.Count;
        var next = _historyIndex + delta;
        if (next < 0 || next > fresh)
            return;
        _historyIndex = next;
        _popupDismissed = false;
        _buffer.Clear();
        _buffer.Append(_historyIndex == fresh ? _savedLine : _history[_historyIndex]);
        _cursor = _buffer.Length;
        Redraw();
    }

    // edits to the fresh line are remembered while browsing away from it
    private void SaveFreshLine()
    {
        if (_historyIndex == _history.Count)
            _savedLine = _buffer.ToString();
    }

    // rewrite the input line (and the popup below it — the F2 reaction
    // menu when open, else slash-command completions), leaving the cursor
    // at its position on the input line. The status line above is managed
    // separately (SetStatus/WriteAbove/Enter).
    private void Redraw()
    {
        Console.Write("\r\x1b[2K");
        Console.Write(_prompt);
        Console.Write(_buffer.ToString());
        Console.Write("\x1b[J"); // erase any stale popup below
        List<string>? popupLines = null;
        var popupSel = 0;
        if (_modalMenu is { } menu)
        {
            popupLines = menu.Options
                .Select((o, i) => $"  {i + 1}. {o}{(i == menu.DefaultIndex ? " (default)" : "")}")
                .ToList();
            popupSel = _modalSel;
        }
        else
        {
            var matches = CurrentMatches();
            if (matches.Count > 0)
            {
                var width = SafeWindowWidth();
                popupLines = matches
                    .Select(m => Truncate($"  {m.Name} — {m.Description}", width))
                    .ToList();
                popupSel = _selected;
            }
        }
        if (popupLines is not null)
        {
            var popup = new StringBuilder();
            for (var i = 0; i < popupLines.Count; i++)
            {
                popup.Append('\n');
                popup.Append(i == popupSel ? $"\x1b[7m{popupLines[i]}\x1b[0m" : popupLines[i]);
            }
            popup.Append($"\x1b[{popupLines.Count}A"); // back up to the input line
            Console.Write(popup.ToString());
        }
        Console.Write('\r');
        var col = _prompt.Length + _cursor;
        if (col > 0)
            Console.Write($"\x1b[{col}C");
    }

    private static int SafeWindowWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? 80 : Console.WindowWidth;
        }
        catch
        {
            return 80;
        }
    }

    private static string Truncate(string s, int width) =>
        width > 4 && s.Length >= width ? s[..(width - 4)] + "..." : s;
}
