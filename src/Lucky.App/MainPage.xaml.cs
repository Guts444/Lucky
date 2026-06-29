using Lucky_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.Specialized;
using System.ComponentModel;
using Windows.Storage.Pickers;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Lucky_App;

/// <summary>
/// The main content page displayed inside the application window.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = new();
    private bool _scrollQueued;
    private bool _scrollAgain;

    public MainPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.Messages.CollectionChanged += Messages_CollectionChanged;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        QueueScrollToLatestMessage();
        await Task.Delay(500);
        QueueScrollToLatestMessage();
        await Task.Delay(1500);
        QueueScrollToLatestMessage();
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.AddProjectAsync(folder.Path);
        }
    }

    private async void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || ViewModel.SendCommand.CanExecute(null) is false)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.SendCommand.ExecuteAsync(null);
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            ViewModel.ApiKeyInput = passwordBox.Password;
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MessageItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= MessageItem_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MessageItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += MessageItem_PropertyChanged;
            }
        }

        QueueScrollToLatestMessage();
    }

    private void MessageItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MessageItemViewModel.Content) or
            nameof(MessageItemViewModel.ThinkingText) or
            nameof(MessageItemViewModel.IsThinking) or
            nameof(MessageItemViewModel.ThinkingVisibility))
        {
            QueueScrollToLatestMessage();
        }
    }

    private void MessageScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        QueueScrollToLatestMessage();
    }

    private void MessageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueScrollToLatestMessage();
    }

    private void MessageList_Loaded(object sender, RoutedEventArgs e)
    {
        QueueScrollToLatestMessage();
    }

    private void MessageList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueScrollToLatestMessage();
    }

    private void QueueScrollToLatestMessage()
    {
        if (_scrollQueued)
        {
            _scrollAgain = true;
            return;
        }

        _scrollQueued = true;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            do
            {
                _scrollAgain = false;
                await Task.Delay(40);
                ScrollToBottom();
                await Task.Delay(80);
                ScrollToBottom();
                await Task.Delay(160);
                ScrollToBottom();
            }
            while (_scrollAgain);

            _scrollQueued = false;
        }))
        {
            _scrollQueued = false;
        }
    }

    private void ScrollToBottom()
    {
        if (ViewModel.Messages.Count == 0)
        {
            return;
        }

        MessageList.UpdateLayout();
        MessageScrollViewer.UpdateLayout();
        MessageScrollViewer.ChangeView(null, MessageScrollViewer.ScrollableHeight, null, disableAnimation: true);
        MessageList.UpdateLayout();
        MessageScrollViewer.ChangeView(null, MessageScrollViewer.ScrollableHeight, null, disableAnimation: true);
    }
}
