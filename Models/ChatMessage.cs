using CommunityToolkit.Mvvm.ComponentModel;

namespace LectureSmith.Models;

/// <summary>
/// Represents a single message in the follow-up Q&A chat session.
/// </summary>
public partial class ChatMessage : ObservableObject
{
    public string Content { get; }
    public bool IsUser { get; }
    public DateTime Timestamp { get; }

    public ChatMessage(string content, bool isUser)
    {
        Content = content;
        IsUser = isUser;
        Timestamp = DateTime.Now;
    }
}
