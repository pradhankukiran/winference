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

    private RenderedBlock CreateBlock(MessageBlock block)
    {
        if (block.Kind == MessageBlockKind.Code) return CreateCode(block);
        if (block.Kind == MessageBlockKind.Table) return CreateTable(block);
        var text = Paragraph();
        FrameworkElement element = text;
        TextBlock? marker = null;
        if (block.Kind == MessageBlockKind.ListItem)
        {
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            marker = Paragraph();
            marker.MinWidth = 14;
            Grid.SetColumn(text, 1);
            grid.Children.Add(marker);
            grid.Children.Add(text);
            element = grid;
        }
        else if (block.Kind == MessageBlockKind.Quote)
            element = new Border { Style = (Style)Resources["MessageQuoteStyle"], Child = text };
        var rendered = new RenderedBlock(block.Kind, element, IsUser, value =>
        {
            if (IsUser) text.Text = value.Text;
            else SetInlineText(text, value.Text);
            if (value.Kind == MessageBlockKind.Heading)
            {
                text.Style = (Style)Resources["MessageHeadingStyle"];
                text.FontSize = value.Level == 1 ? 24 : value.Level == 2 ? 20 : 17;
                text.LineHeight = value.Level <= 2 ? 30 : 25;
                AutomationProperties.SetHeadingLevel(text, (AutomationHeadingLevel)Math.Min(value.Level, 6));
            }
            if (marker is not null)
            {
                marker.Text = value.Detail;
                element.Margin = new Thickness(value.Level * 14, 0, 0, 0);
            }
        });
        rendered.Update(block);
        return rendered;
    }

    private RenderedBlock CreateCode(MessageBlock block)
    {
        var title = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var copy = new Button { Content = "Copy code", MinHeight = 28, Padding = new Thickness(10, 4, 10, 4) };
        AutomationProperties.SetName(copy, "Copy code");
        var toolbar = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 10, 10, 0) };
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(copy, 1);
        toolbar.Children.Add(title);
        toolbar.Children.Add(copy);
        var text = new TextBlock { Style = (Style)Resources["MessageCodeStyle"], Margin = new Thickness(14, 10, 14, 14) };
        var scroll = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(toolbar);
        body.Children.Add(scroll);
        var surface = new Border { Style = (Style)Resources["MessageCodeSurfaceStyle"], Child = body };
        var payload = string.Empty;
        copy.Click += (_, _) =>
        {
            try
            {
                var package = new DataPackage();
                package.SetText(payload);
                Clipboard.SetContent(package);
                CopyNotice.IsOpen = false;
                copy.Content = "Copied";
            }
            catch (Exception) { CopyNotice.IsOpen = true; copy.Content = "Copy code"; }
        };
        var rendered = new RenderedBlock(block.Kind, surface, false, value =>
        {
            title.Text = string.IsNullOrWhiteSpace(value.Detail) ? "Code" : value.Detail;
            text.Text = payload = value.Text;
            copy.Content = "Copy code";
            copy.IsEnabled = payload.Length > 0;
        });
        rendered.Update(block);
        return rendered;
    }
}
