namespace Bridge.Server;

/// <summary>Plain-text log file per server process; the server has no console Pro Tools would show.</summary>
public static class Log
{
    const int KeepFiles = 30;
    static readonly object Lock = new();
    static StreamWriter? _writer;

    public static bool Debug { get; set; }
    public static string? FilePath { get; private set; }

    public static void Open(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var old in new DirectoryInfo(dir).GetFiles("server-*.log").OrderByDescending(f => f.CreationTimeUtc).Skip(KeepFiles - 1))
                old.Delete();
            FilePath = Path.Combine(dir, $"server-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            _writer = new StreamWriter(FilePath, append: false) { AutoFlush = true };
        }
        catch (Exception)
        {
            _writer = null; // logging must never take the server down
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Verbose(string message)
    {
        if (Debug)
            Write("DEBUG", message);
    }

    static void Write(string level, string message)
    {
        lock (Lock)
            _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} {message}");
    }
}
