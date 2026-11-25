using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using LectureSmith.ViewModels;
using System.Collections.Specialized;

namespace LectureSmith.Views;

public partial class ChatPanelView : UserControl
{
    private ChatPanelViewModel? _subscribedVm;

    public ChatPanelView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (_subscribedVm != null)
                _subscribedVm.Messages.CollectionChanged -= OnMessagesChanged;

            _subscribedVm = DataContext as ChatPanelViewModel;
            if (_subscribedVm != null)
                _subscribedVm.Messages.CollectionChanged += OnMessagesChanged;
        };
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            Dispatcher.UIThread.Post(() =>
            {
                this.FindControl<ScrollViewer>("ChatScroller")?.ScrollToEnd();
            });
        }
    }

    private void ChatInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (DataContext is ChatPanelViewModel vm && vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
