using System.Text;

namespace AEngine.Cli;

/// <summary>
/// Minimal MUD-style console prompt. The main thread reads input one key
/// at a time, keeping the in-progress line in a buffer; background output
/// (observed signals in real-time mode) erases the input line, prints
/// above it, then redraws the prompt and the partial text — typing is
/// never visually interrupted. Falls back to plain Console.ReadLine when
/// input is redirected (pipes, tests).
/// </summary>
public sealed class ConsolePrompt
{
    private readonly object _writeLock = new();
    private readonly StringBuilder _buffer = new();
    private string _prompt = "> ";
    private bool _reading;

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
                        return _buffer.ToString();
                    }
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (_buffer.Length > 0)
                        {
                            _buffer.Remove(_buffer.Length - 1, 1);
                            Console.Write("\b \b");
                        }
                        continue;
                    }
                    if (key.KeyChar >= ' ')
                    {
                        _buffer.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                    // arrows and other control keys are ignored (no line editing)
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
                Console.Write(_prompt);
                Console.Write(_buffer.ToString());
            }
            else
            {
                Console.WriteLine(text);
            }
        }
    }
}
