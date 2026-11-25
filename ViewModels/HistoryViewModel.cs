using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureSmith.Models;
using LectureSmith.Services;
using System.Collections.ObjectModel;

namespace LectureSmith.ViewModels;

public partial class HistoryViewModel : ViewModelBase
{
    private readonly IHistoryService _historyService;
    private readonly IClipboardService _clipboard;
    private readonly IFileLauncherService _launcher;

    public ObservableCollection<HistorySessionItemViewModel> AllSessions { get; } = [];
    public ObservableCollection<HistorySessionItemViewModel> FilteredSessions { get; } = [];

    [ObservableProperty] private HistorySessionItemViewModel? _selectedSession;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _activeTab = "Notes";
    [ObservableProperty] private bool _isRenderedPreview = true;

    public MarkdownViewerViewModel MarkdownViewer { get; } = new();
    public ChatPanelViewModel Chat { get; }

    public bool HasSessions => FilteredSessions.Count > 0;
    public bool HasSelectedSession => SelectedSession != null;
    public bool IsNotesTabActive => ActiveTab == "Notes";
    public bool IsChatTabActive => ActiveTab == "Chat";

    public event Action<HistorySession>? ResumeInWorkspaceRequested;

    public HistoryViewModel(
        IHistoryService historyService,
        IGeminiService geminiService,
        IClipboardService clipboard,
        IFileLauncherService launcher)
    {
        _historyService = historyService;
        _clipboard = clipboard;
        _launcher = launcher;

        Chat = new ChatPanelViewModel(geminiService)
        {
            Subtitle = "AI study assistant retains full context from this saved lecture"
        };
        Chat.ExchangeCompleted += OnChatExchangeCompleted;
    }

    public void LoadSessions()
    {
        var previousSelectedId = SelectedSession?.Id;
        var loaded = _historyService.LoadHistory();

        AllSessions.Clear();
        foreach (var session in loaded)
            AllSessions.Add(new HistorySessionItemViewModel(session));

        ApplyFilter(preferredSelectionId: previousSelectedId);
    }

    public void InvalidateChatHandle() => Chat.InvalidateHandle();

    public void ClearAll()
    {
        AllSessions.Clear();
        FilteredSessions.Clear();
        SelectedSession = null;
        MarkdownViewer.Clear();
        Chat.Clear();
        OnPropertyChanged(nameof(HasSessions));
    }

    public HistorySessionItemViewModel? FindById(string sessionId)
        => AllSessions.FirstOrDefault(s => s.Id == sessionId);

    private void ApplyFilter(string? preferredSelectionId = null)
    {
        FilteredSessions.Clear();
        var q = SearchQuery?.Trim().ToLowerInvariant() ?? string.Empty;

        foreach (var item in AllSessions)
        {
            if (string.IsNullOrEmpty(q) ||
                item.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                item.DisplayCourse.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                item.Model.SlidesFileName.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredSessions.Add(item);
            }
        }

        OnPropertyChanged(nameof(HasSessions));

        var match = !string.IsNullOrEmpty(preferredSelectionId)
            ? FilteredSessions.FirstOrDefault(s => s.Id == preferredSelectionId)
            : null;

        if (match != null)
            SelectedSession = match;
        else if (SelectedSession == null || !FilteredSessions.Contains(SelectedSession))
            SelectedSession = FilteredSessions.FirstOrDefault();
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    partial void OnSelectedSessionChanged(HistorySessionItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedSession));
        Chat.InvalidateHandle();

        if (value == null)
        {
            MarkdownViewer.Clear();
            Chat.Clear();
            return;
        }

        value.RefreshMetadata();
        MarkdownViewer.SetBaseOutputFolder(value.OutputFilePath);
        if (IsRenderedPreview)
            MarkdownViewer.UpdateMarkdown(value.MarkdownContent);
        Chat.InitializeDisplayOnly(value.DisplayTitle, value.Model.ChatHistory);
        Chat.EnsureContextReady(value.Model.MarkdownContent, value.Model.ChatHistory);
    }

    partial void OnIsRenderedPreviewChanged(bool value)
    {
        if (value && SelectedSession != null)
        {
            MarkdownViewer.SetBaseOutputFolder(SelectedSession.OutputFilePath);
            MarkdownViewer.UpdateMarkdown(SelectedSession.MarkdownContent);
        }
    }

    [RelayCommand]
    private void SetPreviewMode(string mode)
    {
        IsRenderedPreview = !string.Equals(mode, "Raw", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnActiveTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsNotesTabActive));
        OnPropertyChanged(nameof(IsChatTabActive));
    }

    private void OnChatExchangeCompleted(ChatMessage userMsg, ChatMessage aiMsg)
    {
        if (SelectedSession == null) return;

        SelectedSession.Model.ChatHistory.Add(userMsg);
        SelectedSession.Model.ChatHistory.Add(aiMsg);
        _historyService.SaveSession(SelectedSession.Model);
        SelectedSession.RefreshMetadata();
        Chat.EnsureContextReady(SelectedSession.Model.MarkdownContent, SelectedSession.Model.ChatHistory);
    }

    [RelayCommand]
    private void SelectSession(HistorySessionItemViewModel? session)
    {
        if (session != null)
            SelectedSession = session;
    }

    [RelayCommand]
    private void DeleteSession(HistorySessionItemViewModel? session)
    {
        var target = session ?? SelectedSession;
        if (target == null) return;

        _historyService.DeleteSession(target.Id);
        AllSessions.Remove(target);
        FilteredSessions.Remove(target);
        OnPropertyChanged(nameof(HasSessions));

        if (SelectedSession == target)
            SelectedSession = FilteredSessions.FirstOrDefault();
    }

    [RelayCommand]
    private void SwitchTab(string tabName) => ActiveTab = tabName;

    [RelayCommand]
    private void OpenSessionFile(HistorySessionItemViewModel? session)
    {
        var target = session ?? SelectedSession;
        if (target != null && target.FileExists)
            _launcher.OpenFile(target.OutputFilePath);
    }

    [RelayCommand]
    private void OpenSessionFolder(HistorySessionItemViewModel? session)
    {
        var target = session ?? SelectedSession;
        if (target != null && !string.IsNullOrWhiteSpace(target.OutputFilePath))
            _launcher.OpenFolderAndSelect(target.OutputFilePath);
    }

    [RelayCommand]
    private async Task CopyNotesAsync()
    {
        if (SelectedSession != null && !string.IsNullOrEmpty(SelectedSession.MarkdownContent))
            await _clipboard.SetTextAsync(SelectedSession.MarkdownContent);
    }

    [RelayCommand]
    private void ResumeInWorkspace(HistorySessionItemViewModel? session)
    {
        var target = session ?? SelectedSession;
        if (target != null)
            ResumeInWorkspaceRequested?.Invoke(target.Model);
    }
}
