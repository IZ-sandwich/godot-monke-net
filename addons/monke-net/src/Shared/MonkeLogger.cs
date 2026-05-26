using System;
using System.IO;
using System.Runtime.CompilerServices;
using Godot;

namespace MonkeNet.Shared;

[GlobalClass]
public partial class MonkeLogger : Node
{
    public static MonkeLogger Instance { get; private set; }

    /// <summary>
    /// Gates <see cref="Debug"/> output. Toggle in the inspector on the autoload to
    /// enable/disable verbose logs without recompiling. Default off so production runs
    /// stay quiet — Info/Warn/Error always print.
    /// </summary>
    [Export] public bool DebugEnabled { get; set; } = false;

    // Direct file sink. We don't rely on Godot's stdout-based file logger because the
    // editor's output panel drops chunks ("[output overflow, print less text!]") when a
    // single frame prints too much, and depending on the build the dropped lines may
    // not survive in user://logs/godot.log either. Writing here ourselves with AutoFlush
    // guarantees every line lands on disk regardless of console pressure.
    private static StreamWriter _file;
    private static readonly object _fileLock = new();
    private static string _filePath;
    /// <summary>Absolute path of the log file this process is writing to, or null
    /// if file logging is disabled / failed to open. Surfaced so a test harness
    /// can copy the live log out at run end without having to rediscover the path.</summary>
    public static string FilePath => _filePath;

    public override void _EnterTree()
    {
        Instance = this;
        OpenLogFile();
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
        CloseLogFile();
    }

    private static void OpenLogFile()
    {
        try
        {
            string logDir = ProjectSettings.GlobalizePath("user://logs");
            Directory.CreateDirectory(logDir);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _filePath = Path.Combine(logDir, $"monke-net_{stamp}_pid{System.Environment.ProcessId}.log");
            _file = new StreamWriter(new FileStream(_filePath, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            GD.Print($"[MonkeLogger] writing full log to {_filePath}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MonkeLogger] failed to open log file: {ex.Message}");
            _file = null;
        }
    }

    private static void CloseLogFile()
    {
        lock (_fileLock)
        {
            try { _file?.Dispose(); } catch { }
            _file = null;
        }
    }

    // Set by the client when it receives a session token; cleared on disconnect.
    // Server leaves this null — server-side logs embed the token per-message instead.
    public static string CurrentToken { get; set; }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);

    /// <summary>Fast static-property check that mirrors the autoload's
    /// <see cref="DebugEnabled"/> toggle. Safe when the autoload isn't in the
    /// tree (returns false). Used both by the interpolated-string handler to
    /// short-circuit placeholder evaluation and by callers that want to gate
    /// expensive log message construction by hand.</summary>
    public static bool IsDebugEnabled => Instance != null && Instance.DebugEnabled;

    /// <summary>
    /// Emits a debug-level log line. The interpolated-string overload below is
    /// the one the compiler picks for any <c>Debug($"...{x}...")</c> call site;
    /// this string-only overload exists for the rare callers that pass a
    /// pre-built or constant string. Both bail out when
    /// <see cref="DebugEnabled"/> is false on the autoload.
    /// </summary>
    public static void Debug(string message)
    {
        if (!IsDebugEnabled) return;
        Log("DEBUG", message);
    }

    /// <summary>
    /// Interpolated-string overload of <see cref="Debug(string)"/>. The C# 10+
    /// custom-handler pattern — <see cref="MonkeLoggerDebugHandler"/>'s
    /// constructor checks <see cref="IsDebugEnabled"/> FIRST and reports the
    /// answer via the <c>out bool isEnabled</c>; when it's false the compiler
    /// skips every <c>AppendLiteral</c>/<c>AppendFormatted</c> call entirely,
    /// so the placeholder expressions in <c>$"...{x}..."</c> are never
    /// evaluated and no string is allocated. Cost on the disabled path: ~3 ns
    /// (one bool comparison), zero allocation. See
    /// https://learn.microsoft.com/dotnet/csharp/advanced-topics/performance/interpolated-string-handler
    /// </summary>
    public static void Debug([InterpolatedStringHandlerArgument] MonkeLoggerDebugHandler handler)
    {
        // Handler short-circuits placeholder evaluation when disabled, but the
        // method body still runs — guard so we don't call Log("DEBUG", "") on
        // the disabled path.
        if (!IsDebugEnabled) return;
        Log("DEBUG", handler.ToStringAndClear());
    }

    private static int _markCounter = 0;

    /// <summary>
    /// Emits a distinctive [MARK] line so a user can drop a needle into the log and
    /// later jump back to that point when reconstructing a session. Always prints
    /// regardless of <see cref="DebugEnabled"/> — the whole point is being findable.
    /// Each call gets an incrementing id so multiple marks in one session stay
    /// distinguishable.
    /// </summary>
    public static void Mark(int tick, string label = null)
    {
        int id = System.Threading.Interlocked.Increment(ref _markCounter);
        string suffix = string.IsNullOrEmpty(label) ? "" : $" label='{label}'";
        Log("MARK", $"#{id} tick={tick}{suffix}");
    }

    /// <summary>Quote a delegate so the cost of constructing a long debug
    /// string is paid only when the level is enabled, while still letting the
    /// call site use ordinary <c>$"…"</c> syntax. The compiler rewrites
    /// <c>MonkeLogger.Debug($"x={x}")</c> into a sequence of calls on this
    /// type; if <see cref="MonkeLogger.IsDebugEnabled"/> is false at the
    /// moment the call site runs, the <c>isEnabled</c> out parameter reports
    /// false and the compiler-generated wrapper skips every
    /// <c>AppendLiteral</c>/<c>AppendFormatted</c> call, leaving the inner
    /// <see cref="DefaultInterpolatedStringHandler"/> as <c>default</c> (no
    /// buffer rented, no allocation).
    /// <para>
    /// <c>ref struct</c> is fine here: log call sites never <c>await</c> or
    /// store the handler, and stack-only allocation keeps the disabled-path
    /// cost to a single bool comparison + a few field writes.
    /// </para>
    /// </summary>
    [InterpolatedStringHandler]
    public ref struct MonkeLoggerDebugHandler
    {
        private DefaultInterpolatedStringHandler _inner;

        public MonkeLoggerDebugHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            isEnabled = MonkeLogger.IsDebugEnabled;
            _inner = isEnabled
                ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
                : default;
        }

        // Forward every overload the C# compiler may emit to the BCL's
        // DefaultInterpolatedStringHandler. The compiler picks the most-
        // specific overload at the call site; missing overloads cause CS1502
        // ("no overload matches the format specifier") so list them all.
        public void AppendLiteral(string value)                                  => _inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value)                                  => _inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string format)                   => _inner.AppendFormatted(value, format);
        public void AppendFormatted<T>(T value, int alignment)                   => _inner.AppendFormatted(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string format)    => _inner.AppendFormatted(value, alignment, format);
        public void AppendFormatted(ReadOnlySpan<char> value)                    => _inner.AppendFormatted(value);
        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string format = null)
            => _inner.AppendFormatted(value, alignment, format);
        public void AppendFormatted(string value)                                => _inner.AppendFormatted(value);
        public void AppendFormatted(string value, int alignment = 0, string format = null)
            => _inner.AppendFormatted(value, alignment, format);

        public string ToStringAndClear() => _inner.ToStringAndClear();
    }

    private static void Log(string level, string message)
    {
        string dt = Time.GetDatetimeStringFromSystem(false, false);
        string ms = (Time.GetTicksMsec() % 1000).ToString("D3");
        int peerId = 0;
        try
        {
            var peer = Instance?.Multiplayer?.MultiplayerPeer;
            if (peer != null && peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected)
                peerId = Instance.Multiplayer.GetUniqueId();
        }
        catch { }

        string tok = CurrentToken?.Length >= 4 ? CurrentToken[^4..] : "----";
        string line = $"[{dt}.{ms}] [{peerId}]\t\t[tok:{tok}] [{level}]\t{message}";

        // File sink first — this is the source of truth and must never be skipped, even
        // when the editor's output panel is overflowing.
        if (_file != null)
        {
            lock (_fileLock)
            {
                try { _file?.WriteLine(line); } catch { }
            }
        }

        // Editor/console sink. DEBUG is intentionally suppressed here: it's the highest-
        // volume level and the main cause of "[output overflow, print less text!]" in
        // the editor. Debug remains in the file log above.
        if (level == "DEBUG") return;
        if (level == "ERROR")
            GD.PrintErr(line);
        else
            GD.Print(line);
    }
}
