using LectureSmith.Models;
using System.Text.Json;

namespace LectureSmith.Services;

/// <summary>
/// Persists and manages lecture generation sessions and their follow-up chat histories in %APPDATA%\LectureSmith\history.json.
/// </summary>
public class HistoryService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LectureSmith");
    private static readonly string HistoryFile = Path.Combine(SettingsDir, "history.json");

    private readonly object _lock = new();

    /// <summary>
    /// Loads all recorded generation sessions, ordered newest first.
    /// </summary>
    public List<HistorySession> LoadHistory()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(HistoryFile))
                    return [];

                var json = File.ReadAllText(HistoryFile);
                var sessions = JsonSerializer.Deserialize<List<HistorySession>>(json) ?? [];
                return sessions.OrderByDescending(s => s.Timestamp).ToList();
            }
            catch
            {
                return [];
            }
        }
    }

    /// <summary>
    /// Saves or updates a session in the history storage.
    /// </summary>
    public void SaveSession(HistorySession session)
    {
        lock (_lock)
        {
            try
            {
                var sessions = LoadHistory();
                var index = sessions.FindIndex(s => s.Id == session.Id);
                if (index >= 0)
                {
                    sessions[index] = session;
                }
                else
                {
                    sessions.Insert(0, session);
                }

                Persist(sessions);
            }
            catch
            {
                // Silently handle history write failures
            }
        }
    }

    /// <summary>
    /// Deletes a session from history. Note: This only deletes the entry in the app GUI/metadata,
    /// and never deletes generated lecture files from the user's computer.
    /// </summary>
    public void DeleteSession(string sessionId)
    {
        lock (_lock)
        {
            try
            {
                var sessions = LoadHistory();
                var removed = sessions.RemoveAll(s => s.Id == sessionId);
                if (removed > 0)
                {
                    Persist(sessions);
                }
            }
            catch
            {
                // Silently handle deletion failures
            }
        }
    }

    /// <summary>
    /// Clears all recorded history.
    /// </summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(HistoryFile))
                {
                    File.Delete(HistoryFile);
                }
            }
            catch
            {
                // Silently handle cleanup failures
            }
        }
    }

    private static void Persist(List<HistorySession> sessions)
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(sessions, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(HistoryFile, json);
    }
}
