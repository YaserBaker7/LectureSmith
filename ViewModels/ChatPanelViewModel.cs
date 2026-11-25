using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureSmith.Models;
using LectureSmith.Services;
using System.Collections.ObjectModel;
using System.Text;

namespace LectureSmith.ViewModels;

/// <summary>
/// Reusable follow-up Q&amp;A chat ViewModel with isolated Gemini session state and live chunk streaming.
/// </summary>
public partial class ChatPanelViewModel : ViewModelBase
{
    private readonly IGeminiService _gemini;
    private IGeminiChatHandle? _chatHandle;
    private string _contextMarkdown = string.Empty;
    private IEnumerable<ChatMessage>? _historySnapshot;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string _subtitle = "AI assistant retains context from lecture notes";

    public event Action<ChatMessage, ChatMessage>? ExchangeCompleted;

    public ChatPanelViewModel(IGeminiService gemini)
    {
        _gemini = gemini;
    }

    public void InitializeDisplayOnly(string title, IEnumerable<ChatMessage>? existingMessages)
    {
        _chatHandle = null;
        _contextMarkdown = string.Empty;
        _historySnapshot = null;
        Messages.Clear();

        if (existingMessages != null)
        {
            foreach (var msg in existingMessages)
                Messages.Add(msg);
        }

        if (Messages.Count == 0)
        {
            Messages.Add(new ChatMessage(
                $"I've reviewed your lecture notes for '{title}'. Ask me anything about this material!",
                isUser: false));
        }
    }

    public void StartOrResume(string markdownContext, IEnumerable<ChatMessage>? previousMessages = null, string? greeting = null)
    {
        _contextMarkdown = markdownContext;
        _historySnapshot = previousMessages?.ToList();
        _chatHandle = null;

        Messages.Clear();
        if (_historySnapshot != null)
        {
            foreach (var msg in _historySnapshot)
                Messages.Add(msg);
        }

        if (Messages.Count == 0)
        {
            Messages.Add(new ChatMessage(
                greeting ?? "I've reviewed your lecture notes. Ask me anything about the material!",
                isUser: false));
        }

        if (_gemini.IsConfigured && !string.IsNullOrEmpty(_contextMarkdown))
        {
            _chatHandle = _gemini.CreateChatSession(
                PromptBuilder.FollowUpChatSystemPrompt,
                _contextMarkdown,
                _historySnapshot);
        }
    }

    public void EnsureContextReady(string markdownContext, IEnumerable<ChatMessage>? previousMessages)
    {
        _contextMarkdown = markdownContext;
        _historySnapshot = previousMessages?.ToList();
    }

    public void InvalidateHandle()
    {
        _chatHandle = null;
    }

    public void Clear()
    {
        _chatHandle = null;
        _contextMarkdown = string.Empty;
        _historySnapshot = null;
        Messages.Clear();
        InputText = string.Empty;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsSending)
            return;

        if (!_gemini.IsConfigured)
        {
            Messages.Add(new ChatMessage("Please configure a valid API key and select a model in Settings first.", false));
            return;
        }

        var userText = InputText.Trim();
        InputText = string.Empty;
        IsSending = true;

        ChatMessage? aiPlaceholder = null;
        try
        {
            // Create chat handle lazily BEFORE appending the new user message so it isn't duplicated in history
            _chatHandle ??= _gemini.CreateChatSession(
                PromptBuilder.FollowUpChatSystemPrompt,
                _contextMarkdown,
                _historySnapshot);

            var userMsg = new ChatMessage(userText, isUser: true);
            Messages.Add(userMsg);

            aiPlaceholder = new ChatMessage("…", isUser: false);
            Messages.Add(aiPlaceholder);

            var buffer = new StringBuilder();
            var lastFlush = DateTime.UtcNow;

            var progress = new Progress<string>(chunk =>
            {
                buffer.Append(chunk);
                var now = DateTime.UtcNow;
                if ((now - lastFlush).TotalMilliseconds >= 80)
                {
                    lastFlush = now;
                    var text = buffer.ToString();
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (aiPlaceholder != null)
                            aiPlaceholder.Content = text;
                    });
                }
            });

            var fullReply = await _chatHandle.SendMessageAsync(userText, progress);
            aiPlaceholder.Content = fullReply;

            var completedAiMsg = new ChatMessage(fullReply, isUser: false);
            var idx = Messages.IndexOf(aiPlaceholder);
            if (idx >= 0)
                Messages[idx] = completedAiMsg;

            ExchangeCompleted?.Invoke(userMsg, completedAiMsg);
        }
        catch (Exception ex)
        {
            if (aiPlaceholder != null)
                Messages.Remove(aiPlaceholder);

            Messages.Add(new ChatMessage($"Error: {ex.Message}", isUser: false));
        }
        finally
        {
            IsSending = false;
        }
    }
}
