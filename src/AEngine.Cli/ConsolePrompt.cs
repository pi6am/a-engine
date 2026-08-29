using System.Text;

namespace AEngine.Cli;

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
/// Falls back to plain Console.ReadLine when input is
/// redirected (pipes, tests).
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

    /// <summary>Slash commands (with leading '/') and their summaries, for tab completion.</summary>
    public IReadOnlyList<(string Name, string Description)>? Completions { get; set; }

    /// <summary>Prompt and read one line of input. Null on EOF (redirected input only).</summary>
    public string? ReadLine(string prompt = "> ", bool completions = true)
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
            _reading = true;
            Console.Write(prompt);
        }
        try
        {
            while (true)
            {
                // ReadKey blocks; do NOT hold the lock while waiting
                var key = Console.ReadKey(intercept: true);
                lock (_writeLock)
                {
                    if (key.Key == ConsoleKey.Enter)
                    {
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
                Console.Write("\r\x1b[2K"); // carriage return + erase line
                Console.Write("\x1b[J"); // and any completion popup below
                Console.WriteLine(text);
                Redraw();
            }
            else
            {
                Console.WriteLine(text);
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

    // rewrite the input line (and the completion popup below it), leaving
    // the cursor at its position on the input line
    private void Redraw()
    {
        Console.Write("\r\x1b[2K");
        Console.Write(_prompt);
        Console.Write(_buffer.ToString());
        Console.Write("\x1b[J"); // erase any stale popup below
        var matches = CurrentMatches();
        if (matches.Count > 0)
        {
            var width = SafeWindowWidth();
            var popup = new StringBuilder();
            for (var i = 0; i < matches.Count; i++)
            {
                var line = Truncate($"  {matches[i].Name} — {matches[i].Description}", width);
                popup.Append('\n');
                popup.Append(i == _selected ? $"\x1b[7m{line}\x1b[0m" : line);
            }
            popup.Append($"\x1b[{matches.Count}A"); // back up to the input line
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
