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
/// recent entry returns to the line you were typing). Falls back to plain
/// Console.ReadLine when input is redirected (pipes, tests).
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

    /// <summary>Prompt and read one line of input. Null on EOF (redirected input only).</summary>
    public string? ReadLine(string prompt = "> ")
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
                            _buffer.Remove(_cursor - 1, 1);
                            _cursor--;
                            SaveFreshLine();
                            if (_cursor == _buffer.Length)
                                Console.Write("\b \b");
                            else
                                Redraw();
                        }
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
                    if (key.Key == ConsoleKey.UpArrow)
                    {
                        Navigate(-1);
                        continue;
                    }
                    if (key.Key == ConsoleKey.DownArrow)
                    {
                        Navigate(+1);
                        continue;
                    }
                    if (!char.IsControl(key.KeyChar))
                    {
                        _buffer.Insert(_cursor, key.KeyChar);
                        _cursor++;
                        SaveFreshLine();
                        if (_cursor == _buffer.Length)
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
                Console.WriteLine(text);
                Redraw();
            }
            else
            {
                Console.WriteLine(text);
            }
        }
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

    // rewrite the whole input line and restore the cursor position
    private void Redraw()
    {
        Console.Write("\r\x1b[2K");
        Console.Write(_prompt);
        Console.Write(_buffer.ToString());
        var back = _buffer.Length - _cursor;
        if (back > 0)
            Console.Write($"\x1b[{back}D");
    }
}
