using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Winference.App.ViewModels;
using Winference.Core.Models;

namespace Winference.App.Controls;

/// <summary>A question or answer on the board canvas. The header drags it; the footer holds an answer's actions.</summary>
public sealed partial class BoardCardControl : UserControl
{
    private readonly BoardViewModel _board;
    private readonly ModelLibraryViewModel _library;
    private readonly Func<Guid, double?> _heightOf;
    private UIElement? _dragSurface;
    private Point _dragStart;
    private (double X, double Y) _dragOrigin;
    private bool _dragging;
    private bool _expanded;

    public BoardCardControl(BoardCardViewModel card, BoardViewModel board, ModelLibraryViewModel library, Func<Guid, double?> heightOf)
    {
        Card = card;
        _board = board;
        _library = library;
        _heightOf = heightOf;
        InitializeComponent();
        var question = card.IsQuestion;
        KindGlyph.Glyph = question ? "" : "";
        KindLabel.Text = question ? "Question" : "Answer";
        QuestionText.Visibility = question ? Visibility.Visible : Visibility.Collapsed;
        AnswerArea.Visibility = question ? Visibility.Collapsed : Visibility.Visible;
        Footer.Visibility = question ? Visibility.Collapsed : Visibility.Visible;
    }

    public BoardCardViewModel Card { get; }

    /// <summary>Raised when "Follow up" is chosen, so the page can focus the question box.</summary>
    public event EventHandler? FollowUpRequested;

    private void AnswerText_SizeChanged(object sender, SizeChangedEventArgs args) =>
        MoreButton.Visibility = !_expanded && AnswerText.ActualHeight > AnswerScroll.MaxHeight + 1 || _expanded
            ? Visibility.Visible : Visibility.Collapsed;

    private void More_Click(object sender, RoutedEventArgs args)
    {
        _expanded = !_expanded;
        AnswerScroll.MaxHeight = _expanded ? double.PositiveInfinity : 280;
        MoreButton.Content = _expanded ? "Show less" : "Show more";
    }

    private void Handle_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (Parent is not UIElement surface || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragSurface = surface;
        _dragStart = args.GetCurrentPoint(surface).Position;
        _dragOrigin = (Card.X, Card.Y);
        _dragging = Handle.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void Handle_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_dragging || _dragSurface is null) return;
        var point = args.GetCurrentPoint(_dragSurface).Position;
        _board.Move(Card, _dragOrigin.X + point.X - _dragStart.X, _dragOrigin.Y + point.Y - _dragStart.Y);
        args.Handled = true;
    }

    private void Handle_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_dragging) return;
        Handle.ReleasePointerCapture(args.Pointer);
        EndDrag();
        args.Handled = true;
    }

    private void Handle_PointerCaptureLost(object sender, PointerRoutedEventArgs args) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _board.Moved();
    }

    private void Star_Click(object sender, RoutedEventArgs args) => _board.ToggleStar(Card);

    private async void Again_Click(object sender, RoutedEventArgs args) => await _board.AgainAsync(Card, _heightOf);

    private void FollowUp_Click(object sender, RoutedEventArgs args)
    {
        _board.FollowUp = Card;
        FollowUpRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Stop_Click(object sender, RoutedEventArgs args) => _board.StopWriting();

    private void Delete_Click(object sender, RoutedEventArgs args) => _board.Delete(Card);

    private void Copy_Click(object sender, RoutedEventArgs args)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(Card.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    /// <summary>Lists the models on this PC. Choosing one loads it and answers the same question beside this answer.</summary>
    private void Compare_Click(object sender, RoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        var installed = _library.OnThisPc.ToList();
        foreach (var model in installed)
        {
            var current = model.Status == ModelStatus.Current;
            var item = new MenuFlyoutItem
            {
                Text = $"{model.Name} · {model.Badge}" + (current ? " (loaded)" : string.Empty),
                IsEnabled = model.CanAct || current
            };
            item.Click += async (_, _) => await _board.CompareAsync(Card, model, _heightOf);
            menu.Items.Add(item);
        }
        if (installed.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "No other models on this PC yet", IsEnabled = false });
        menu.ShowAt(CompareButton);
    }
}
