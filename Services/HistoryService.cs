using LectureSmith.Models;
using System.Text.Json;

namespace LectureSmith.Services;

/// <summary>
/// Persists and manages lecture generation sessions and follow-up chat histories in %APPDATA%\LectureSmith\history.json.
/// Maintains an in-memory snapshot to avoid re-reading large JSON payloads on every chat message.
/// </summary>
public class HistoryService : IHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private List<HistorySession>? _cache;

    public List<HistorySession> LoadHistory()
    {
        lock (_lock)
        {
            EnsureLoadedUnsafe();
            return [.. _cache!];
        }
    }

    public void SaveSession(HistorySession session)
    {
        lock (_lock)
        {
            try
            {
                EnsureLoadedUnsafe();
                var index = _cache!.FindIndex(s => s.Id == session.Id);
                if (index >= 0)
                    _cache[index] = session;
                else
                    _cache.Insert(0, session);

                _cache = _cache.OrderByDescending(s => s.Timestamp).ToList();
                PersistUnsafe();
            }
            catch
            {
                // Avoid crashing active chat/generation flows on transient disk errors
            }
        }
    }

    public void DeleteSession(string sessionId)
    {
        lock (_lock)
        {
            try
            {
                EnsureLoadedUnsafe();
                var removed = _cache!.RemoveAll(s => s.Id == sessionId);
                if (removed > 0)
                    PersistUnsafe();
            }
            catch
            {
                // Non-fatal
            }
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            _cache = [];
            try
            {
                if (File.Exists(AppPaths.HistoryFile))
                    File.Delete(AppPaths.HistoryFile);
            }
            catch
            {
                // Non-fatal
            }
        }
    }

    private void EnsureLoadedUnsafe()
    {
        if (_cache != null) return;

        try
        {
            if (!File.Exists(AppPaths.HistoryFile))
            {
                _cache = [];
                return;
            }

            var json = File.ReadAllText(AppPaths.HistoryFile);
            var sessions = JsonSerializer.Deserialize<List<HistorySession>>(json) ?? [];
            foreach (var s in sessions)
                s.ChatHistory ??= [];

            _cache = sessions.OrderByDescending(s => s.Timestamp).ToList();
        }
        catch
        {
            AppPaths.BackupCorruptFile(AppPaths.HistoryFile);
            _cache = [];
        }
    }

    private void PersistUnsafe()
    {
        var json = JsonSerializer.Serialize(_cache ?? [], JsonOptions);
        AppPaths.WriteAllTextAtomic(AppPaths.HistoryFile, json);
    }
}
