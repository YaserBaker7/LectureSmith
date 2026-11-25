using LectureSmith.Models;
using LectureSmith.Services;
using LectureSmith.ViewModels;
using Xunit;

namespace LectureSmith.Tests;

public class ViewModelNavigationAndChatTests
{
    [Fact]
    public void NavigateFromSettingsToHistory_StaysOnHistory()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService());

        vm.NavigateToSettingsCommand.Execute(null);
        Assert.True(vm.IsSettingsActive);

        vm.NavigateToHistoryCommand.Execute(null);
        Assert.True(vm.IsHistoryActive);
        Assert.False(vm.IsWorkspaceActive);
        Assert.False(vm.IsSettingsActive);
    }

    [Fact]
    public async Task ChatPanel_DoesNotDuplicateFirstUserMessageAndCleansUpPlaceholderOnError()
    {
        var fakeGemini = new RecordingGeminiService();
        var chat = new ChatPanelViewModel(fakeGemini);
        chat.EnsureContextReady("# Notes", []);

        chat.InputText = "What is Big-O?";
        await chat.SendCommand.ExecuteAsync(null);

        // CreateChatSession must receive empty prior history (not duplicating "What is Big-O?")
        Assert.Empty(fakeGemini.CapturedInitialMessages);
        Assert.Equal("What is Big-O?", fakeGemini.LastSentMessage);
        Assert.Equal(2, chat.Messages.Count);
        Assert.True(chat.Messages[0].IsUser);
        Assert.False(chat.Messages[1].IsUser);
        Assert.Equal("Big-O measures asymptotic growth.", chat.Messages[1].Content);

        // Now simulate error: placeholder should be removed, leaving user message + error message
        fakeGemini.ShouldThrow = true;
        chat.InputText = "Next question?";
        await chat.SendCommand.ExecuteAsync(null);

        Assert.Equal(4, chat.Messages.Count);
        Assert.StartsWith("Error:", chat.Messages[3].Content);
    }

    private sealed class InMemorySettingsService : ISettingsService
    {
        private string _apiKey = string.Empty;
        public AppSettings Settings { get; } = new();
        public string GetApiKey() => _apiKey;
        public void SetApiKey(string apiKey) => _apiKey = apiKey;
        public void Load() { }
        public void Save() { }
        public void Reset() => _apiKey = string.Empty;
    }

    private sealed class RecordingGeminiService : IGeminiService, IGeminiChatHandle
    {
        public bool IsConfigured => true;
        public bool ShouldThrow { get; set; }
        public List<ChatMessage> CapturedInitialMessages { get; } = [];
        public string? LastSentMessage { get; private set; }

        public void SetApiKey(string apiKey) { }
        public void SetModel(string modelId, bool thinkingEnabled = false, int? outputTokenLimit = null, bool? supportsThinking = null) { }

        public Task<(string Text, TokenUsage Usage)> GenerateAsync(string systemPrompt, string userContent, IProgress<string>? progress = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<(string Text, TokenUsage Usage)> GenerateWithImagesAsync(string systemPrompt, string userContent, List<string> imagePaths, IProgress<string>? progress = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public IGeminiChatHandle CreateChatSession(string systemPrompt, string initialContext, IEnumerable<ChatMessage>? previousMessages = null)
        {
            CapturedInitialMessages.Clear();
            if (previousMessages != null)
                CapturedInitialMessages.AddRange(previousMessages);
            return this;
        }

        public Task<string> SendMessageAsync(string message, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            LastSentMessage = message;
            if (ShouldThrow)
                throw new InvalidOperationException("Simulated network drop");
            return Task.FromResult("Big-O measures asymptotic growth.");
        }
    }
}
