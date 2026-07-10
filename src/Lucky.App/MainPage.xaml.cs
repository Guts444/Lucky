using Lucky_App.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.Specialized;
using System.ComponentModel;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

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
    private bool _followLatestMessage = true;
    private bool _isProgrammaticScroll;

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
        var isControlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) ==
                            CoreVirtualKeyStates.Down;
        if (e.Key != VirtualKey.Enter || !isControlDown || ViewModel.SendCommand.CanExecute(null) is false)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.SendCommand.ExecuteAsync(null);
    }

    private async void ConnectChatGpt_Click(object sender, RoutedEventArgs e)
    {
        var login = await ViewModel.StartCodexSignInAsync();
        if (login is null)
        {
            return;
        }

        if (!await Launcher.LaunchUriAsync(new Uri(login.AuthorizationUrl)))
        {
            ViewModel.CodexConnectionStatus = "Windows could not open the ChatGPT sign-in page.";
            ViewModel.IsCodexSignInInProgress = false;
            return;
        }

        await ViewModel.FinishCodexSignInAsync(login);
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            ViewModel.ApiKeyInput = passwordBox.Password;
        }
    }

    private void SettingsSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string section })
        {
            return;
        }

        var target = section switch
        {
            "General" => GeneralSettingsSection,
            "Model" => ModelSettingsSection,
            "Personalization" => PersonalizationSettingsSection,
            "Memory" => MemorySettingsSection,
            "Searxng" => SearxngSettingsSection,
            _ => null
        };
        if (target is null)
        {
            return;
        }

        SettingsContent.UpdateLayout();
        var offset = target.TransformToVisual(SettingsContent).TransformPoint(new Point()).Y;
        SettingsScrollViewer.ChangeView(null, Math.Max(0, offset - 12), null, disableAnimation: false);
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
        _followLatestMessage = true;
        QueueScrollToLatestMessage();
    }

    private void MessageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueScrollToLatestMessage();
    }

    private void MessageScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isProgrammaticScroll)
        {
            return;
        }

        // A scrollbar drag or keyboard/touch scroll can arrive while an automatic
        // scroll has been queued. Let that user-driven move opt out immediately
        // instead of waiting for the queued pass to pull the view back down.
        _followLatestMessage = IsNearLatestMessage();
    }

    private void MessageScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var wheelDelta = e.GetCurrentPoint(MessageScrollViewer).Properties.MouseWheelDelta;
        if (wheelDelta > 0)
        {
            // Once someone deliberately scrolls upward, streaming output must not yank them back.
            _followLatestMessage = false;
            _scrollAgain = false;
            return;
        }

        DispatcherQueue.TryEnqueue(() => _followLatestMessage = IsNearLatestMessage());
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
        if (!_followLatestMessage)
        {
            return;
        }

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
                if (!_followLatestMessage)
                {
                    break;
                }

                ScrollToBottom();
                await Task.Delay(80);
                if (!_followLatestMessage)
                {
                    break;
                }

                ScrollToBottom();
                await Task.Delay(160);
                if (!_followLatestMessage)
                {
                    break;
                }

                ScrollToBottom();
            }
            while (_scrollAgain && _followLatestMessage);

            _scrollQueued = false;
        }))
        {
            _scrollQueued = false;
        }
    }

    private void ScrollToBottom()
    {
        if (ViewModel.Messages.Count == 0 || !_followLatestMessage)
        {
            return;
        }

        _isProgrammaticScroll = true;
        try
        {
            MessageList.UpdateLayout();
            MessageScrollViewer.UpdateLayout();
            MessageScrollViewer.ChangeView(null, MessageScrollViewer.ScrollableHeight, null, disableAnimation: true);
            MessageList.UpdateLayout();
            MessageScrollViewer.ChangeView(null, MessageScrollViewer.ScrollableHeight, null, disableAnimation: true);
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() => _isProgrammaticScroll = false);
        }
    }

    private bool IsNearLatestMessage() =>
        MessageScrollViewer.ScrollableHeight - MessageScrollViewer.VerticalOffset <= 24;
}
