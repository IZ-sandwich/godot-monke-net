using System;
using Godot;

namespace MonkeNet.Shared;

/// <summary>
/// Per-installation persistent client identity. This is the value the client
/// sends to the server on every connect (via <c>ClientHelloMessage</c>) so the
/// server can recognise a returning player across disconnect/reconnect cycles
/// without ever rotating an identifier across reconnects.
///
/// Resolution order (first match wins, evaluated lazily on first
/// <see cref="Get"/>):
/// <list type="number">
///   <item><c>--client-id=&lt;guid&gt;</c> CLI user arg (passed after the
///         Godot <c>--</c> separator). Direct value; nothing is written to
///         disk. Used by the multi-process test harness so each spawned
///         child process gets a known, distinct identity without polluting
///         the developer's <c>user://</c> directory.</item>
///   <item><c>--client-id-file=&lt;path&gt;</c> CLI user arg. Override the
///         on-disk file path. Used by manual multi-instance Godot Editor
///         runs to keep each instance's identity in its own config file
///         while still preserving the persistence-across-restart property.
///         Set per instance via Editor Settings → Run → Run Multiple
///         Instances → Additional Args, or on the <c>godot</c> command
///         line.</item>
///   <item>Default: <c>user://client_persistent_id.cfg</c>. Created on
///         first run with a fresh GUID; reused unchanged on every
///         subsequent launch.</item>
/// </list>
///
/// File format is a Godot <c>ConfigFile</c> (INI-style) so the user can
/// inspect / edit it by hand:
/// <code>
///   [identity]
///   client_id="3f2504e0-4f89-11d3-9a0c-0305e82c3301"
///   created="2026-05-16T00:00:00Z"
/// </code>
///
/// The resolved identity is cached for the lifetime of the process. To
/// force a re-resolution (e.g. between tests in the same hosting process)
/// call <see cref="ResetCache"/>.
/// </summary>
public static class ClientPersistentIdentity
{
    public const string DefaultUserPath = "user://client_persistent_id.cfg";
    private const string CliArgIdValue = "--client-id=";
    private const string CliArgIdFile = "--client-id-file=";
    private const string Section = "identity";
    private const string KeyId = "client_id";
    private const string KeyCreated = "created";

    private static readonly object _lock = new();
    private static string _cached;
    private static string _cachedSourceDescription;

    /// <summary>
    /// Returns the persistent client identity for this process. Triggers
    /// resolution (CLI → file → generate) on first call, then caches.
    /// Safe to call from any thread; resolution is single-flighted via a
    /// lock so concurrent callers during startup don't race on file I/O.
    /// </summary>
    public static string Get()
    {
        if (_cached != null) return _cached;
        lock (_lock)
        {
            if (_cached != null) return _cached;
            var (id, source) = Resolve();
            _cached = id;
            _cachedSourceDescription = source;
            MonkeLogger.Info($"[ClientPersistentIdentity] resolved id ...{Tail(id)} from {source}");
        }
        return _cached;
    }

    /// <summary>Human-readable description of where <see cref="Get"/>
    /// resolved its value from (CLI flag, file path, freshly generated).
    /// Useful for diagnostic logs.</summary>
    public static string SourceDescription => _cachedSourceDescription ?? "<not yet resolved>";

    /// <summary>Clears the cache so the next <see cref="Get"/> re-resolves.
    /// Used by tests that mutate <c>OS.SetCmdlineArgs</c> or the user-dir
    /// state between test cases.</summary>
    public static void ResetCache()
    {
        lock (_lock)
        {
            _cached = null;
            _cachedSourceDescription = null;
        }
    }

    /// <summary>4-character suffix of a client id (or "----" if shorter).
    /// Mirrors <c>ServerConnectionMonitor.Tok</c> so the same identifier
    /// reads the same in client- and server-side logs.</summary>
    public static string Tail(string id) =>
        id != null && id.Length >= 4 ? id[^4..] : "----";

    private static (string id, string source) Resolve()
    {
        // 1) CLI direct override — no file I/O. Highest priority so the
        //    test harness can guarantee per-process identities regardless
        //    of what's in user://.
        string[] userArgs;
        try { userArgs = OS.GetCmdlineUserArgs(); }
        catch { userArgs = Array.Empty<string>(); }

        foreach (string a in userArgs)
        {
            if (a != null && a.StartsWith(CliArgIdValue))
            {
                string id = a.Substring(CliArgIdValue.Length);
                if (!string.IsNullOrWhiteSpace(id))
                    return (id, $"CLI {CliArgIdValue}…");
            }
        }

        // 2) CLI file override — read/write from a non-default path. Used
        //    by manual multi-instance Editor runs so each instance has its
        //    own persisted identity without colliding on the default path.
        string filePath = DefaultUserPath;
        string sourcePathSuffix = " (default user:// path)";
        foreach (string a in userArgs)
        {
            if (a != null && a.StartsWith(CliArgIdFile))
            {
                filePath = a.Substring(CliArgIdFile.Length);
                sourcePathSuffix = $" (CLI {CliArgIdFile}{filePath})";
                break;
            }
        }

        // 3) Read existing file, if any.
        var cfg = new ConfigFile();
        Error loadErr = cfg.Load(filePath);
        if (loadErr == Error.Ok && cfg.HasSectionKey(Section, KeyId))
        {
            string existing = cfg.GetValue(Section, KeyId).AsString();
            if (!string.IsNullOrWhiteSpace(existing))
                return (existing, $"file {filePath}{sourcePathSuffix}");
        }

        // 4) Generate fresh + write back. A failed save isn't fatal —
        //    the in-memory value still works for THIS process; the next
        //    launch will just generate a new one. Logged so it's not
        //    silent.
        string fresh = Guid.NewGuid().ToString();
        cfg.SetValue(Section, KeyId, fresh);
        cfg.SetValue(Section, KeyCreated, DateTime.UtcNow.ToString("o"));
        Error saveErr = cfg.Save(filePath);
        if (saveErr != Error.Ok)
        {
            MonkeLogger.Warn($"[ClientPersistentIdentity] could not write {filePath}: {saveErr}. " +
                $"Using ephemeral id for this run only.");
            return (fresh, $"freshly generated (file {filePath} not writable: {saveErr})");
        }
        return (fresh, $"freshly generated and written to {filePath}");
    }
}
