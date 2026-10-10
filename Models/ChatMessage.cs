using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace LectureSmith.Models;

/// <summary>
/// Represents a single message in the follow-up Q&A chat session.
/// </summary>
public partial class ChatMessage : ObservableObject
{
    public string Content { get; set; } = string.Empty;
    public bool IsUser { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public ChatMessage() { }

    [JsonConstructor]
    public ChatMessage(string content, bool isUser, DateTime timestamp = default)
    {
        Content = content;
        IsUser = isUser;
        Timestamp = timestamp == default ? DateTime.Now : timestamp;
    }
}
