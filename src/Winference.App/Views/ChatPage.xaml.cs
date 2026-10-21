using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using Winference.App.Controls;
using Winference.App.Services;
using Winference.App.ViewModels;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Winference.App.Views;

public sealed partial class ChatPage : Page
{
    public event EventHandler? ChooseModelRequested;
    private bool _initialized;
    private readonly ChatViewModel? _chat;
    private readonly ModelLibraryViewModel _library;
    private readonly ReadAloudService _readAloud;
    private readonly DispatcherQueueTimer _scrollTimer;
    private readonly DispatcherQueueTimer _announcementTimer;
    private readonly HashSet<ChatMessageViewModel> _messageSubscriptions = [];
    private bool _subscribed;
    private bool _followLatest = true;
    private bool _hasUnseenOutput;
    private bool _wasGenerating;
    private bool _isComposing;
    private bool _suppressEnterUntilRelease;
    private bool _keyboardFocusMove;
    private double _lastVerticalOffset;

    public ShellViewModel ViewModel { get; }

    public void FocusComposer()
    {
        if (IsLoaded && Composer.IsEnabled) Composer.Focus(FocusState.Programmatic);
    }

    /// <summary>Opens the model picker from the keyboard.</summary>
    public void OpenModelPicker()
    {
        if (ChooseModelButton.IsEnabled) ModelFlyout.ShowAt(ChooseModelButton);
    }

    public ChatPage(ShellViewModel viewModel, ModelLibraryViewModel library, ReadAloudService readAloud)
    {
        ViewModel = viewModel;
        _library = library;
        _readAloud = readAloud;
        InitializeComponent();
        DataContext = viewModel;
        var picker = new ModelListControl(library, compact: true);
        picker.ActionInvoked += (_, _) => ModelFlyout.Hide();
        ModelListHost.Content = picker;
        _chat = viewModel.Chat;
        _scrollTimer = DispatcherQueue.CreateTimer();
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(60);
        _scrollTimer.IsRepeating = false;
        _scrollTimer.Tick += ScrollTimer_Tick;
        _announcementTimer = DispatcherQueue.CreateTimer();
        _announcementTimer.Interval = TimeSpan.FromSeconds(4);
        _announcementTimer.IsRepeating = false;
        _announcementTimer.Tick += (_, _) => ResponseAnnouncement.Text = string.Empty;
        _initialized = true;
    }
}
