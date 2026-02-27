using System.IO;
using System.Text;

namespace Malie.Services;

public sealed class DebugLogService
{
    private readonly object _sync = new();
    private readonly List<string> _entries = new();
    private readonly string _logFilePath;

    public event EventHandler<string>? EntryAdded;

    public DebugLogService(string logFilePath)
    {
        _logFilePath = logFilePath;
    }

    public void Log(string message)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"{timestamp} | {message}";

        lock (_sync)
        {
            _entries.Add(line);
            if (_entries.Count > 1000)
            {
                _entries.RemoveRange(0, _entries.Count - 1000);
            }
        }

        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Logging must never break app flow.
        }

        EntryAdded?.Invoke(this, line);
    }

    public IReadOnlyList<string> GetRecentEntries(int maxEntries = 200)
    {
        lock (_sync)
        {
            if (_entries.Count <= maxEntries)
            {
                return _entries.ToArray();
            }

            return _entries.Skip(_entries.Count - maxEntries).ToArray();
        }
    }
}
