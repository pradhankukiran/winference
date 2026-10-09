using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Winference.App.Presentation;

namespace Winference.App.Controls;

/// <summary>Native selectable message presentation; raw conversation text stays unchanged.</summary>
public sealed partial class MessageContent : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MessageContent), new PropertyMetadata(string.Empty, Content_Changed));
    public static readonly DependencyProperty IsUserProperty = DependencyProperty.Register(
        nameof(IsUser), typeof(bool), typeof(MessageContent), new PropertyMetadata(false, Content_Changed));

    public string Text { get => (string?)GetValue(TextProperty) ?? string.Empty; set => SetValue(TextProperty, value); }
    public bool IsUser { get => (bool)GetValue(IsUserProperty); set => SetValue(IsUserProperty, value); }

    private readonly List<RenderedBlock> _rendered = [];
    private DispatcherQueueTimer? _renderTimer;
    private bool _loaded;

    public MessageContent() => InitializeComponent();

    private static void Content_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MessageContent)sender).QueueRender();

    private void Content_Loaded(object sender, RoutedEventArgs args)
    {
        _loaded = true;
        Render();
    }

    private void Content_Unloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        if (_renderTimer is null) return;
        _renderTimer.Stop();
        _renderTimer.Tick -= RenderTimer_Tick;
        _renderTimer = null;
    }

    private void QueueRender()
    {
        if (!_loaded) return;
        if (_renderTimer is null)
        {
            _renderTimer = DispatcherQueue.CreateTimer();
            _renderTimer.Interval = TimeSpan.FromMilliseconds(80);
            _renderTimer.IsRepeating = false;
            _renderTimer.Tick += RenderTimer_Tick;
        }
        if (!_renderTimer.IsRunning) _renderTimer.Start();
    }

    private void RenderTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_loaded) Render();
    }

    private void Render()
    {
        MessageSurface.Style = (Style)Resources[IsUser ? "MessageUserSurfaceStyle" : "MessageAssistantSurfaceStyle"];
        IReadOnlyList<MessageBlock> blocks = IsUser
            ? new MessageBlock[] { new(MessageBlockKind.Paragraph, Text, Text, "plain") }
            : MessageFormatting.Parse(Text);
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (index < _rendered.Count && _rendered[index].Kind == block.Kind && _rendered[index].Plain == IsUser)
            {
                if (_rendered[index].Source != block.Source) _rendered[index].Update(block);
            }
            else
            {
                while (_rendered.Count > index)
                { _rendered.RemoveAt(index); Blocks.Children.RemoveAt(index); }
                var rendered = CreateBlock(block);
                _rendered.Add(rendered);
                Blocks.Children.Add(rendered.Element);
            }
        }
        while (_rendered.Count > blocks.Count)
        { _rendered.RemoveAt(blocks.Count); Blocks.Children.RemoveAt(blocks.Count); }
    }
}
