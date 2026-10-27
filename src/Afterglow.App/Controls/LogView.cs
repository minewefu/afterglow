using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Afterglow.Core.Stress;

namespace Afterglow.App.Controls;

/// <summary>
/// A run's whole log — the stepper's, a certification's — in a read-only text
/// box: it scrolls, and its text can be selected and copied (#7). It used to be
/// a TextBlock showing the last 10–14 lines, so anything earlier in the run
/// could not be read at all.
/// <para>
/// Bind <see cref="Lines"/> to the status's log. A <see cref="RunLogLines"/>
/// numbers its lines across the run, so each update appends only the lines that
/// are new (<see cref="RunLogCursor"/>): the progress ticks in between cost
/// nothing, and a reader's selection survives the run's next line. Replacing
/// the whole text on every status would re-lay out the log once a second and
/// clear the selection each time.
/// </para>
/// <para>
/// It follows the newest line while the reader is at the bottom. Scrolled up
/// to read, the position holds as lines arrive; scrolling back to the bottom
/// (or Ctrl+End) resumes following. A new run starts at its newest line.
/// </para>
/// <para>
/// The look (Theme/Controls.xaml) is the inset log panel it replaces: Bg1,
/// monospace 12/17, muted text, at most about 14 lines tall.
/// </para>
/// </summary>
public sealed class LogView : TextBox
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines), typeof(IReadOnlyList<string>), typeof(LogView),
        new FrameworkPropertyMetadata(null, OnLinesChanged));

    /// <summary>Within this many pixels of the end counts as "at the bottom".</summary>
    private const double BottomSlack = 2;

    /// <summary>
    /// Appending keeps lines the run log has since dropped; at twice the log's
    /// cap the box is reset to the log as it stands, so it stays bounded too.
    /// That reset is the only full re-render within a run: once per ~5,000
    /// lines past the cap, a length no real run reaches.
    /// </summary>
    private readonly RunLogCursor _cursor = new(2L * (RunLog.DefaultCapacity + 1));

    private RunLogLines? _run;
    private bool _hasText;
    private bool _follow = true;

    static LogView()
    {
        // Behaviour, not looks, so it holds without the theme's style: the box
        // is read-only and keeps no undo history (programmatic appends are
        // recorded otherwise, a second copy of the log).
        IsReadOnlyProperty.OverrideMetadata(typeof(LogView), new FrameworkPropertyMetadata(true));
        IsUndoEnabledProperty.OverrideMetadata(typeof(LogView), new FrameworkPropertyMetadata(false));
        TextWrappingProperty.OverrideMetadata(typeof(LogView), new FrameworkPropertyMetadata(TextWrapping.Wrap));
        VerticalScrollBarVisibilityProperty.OverrideMetadata(
            typeof(LogView), new FrameworkPropertyMetadata(ScrollBarVisibility.Auto));
        HorizontalScrollBarVisibilityProperty.OverrideMetadata(
            typeof(LogView), new FrameworkPropertyMetadata(ScrollBarVisibility.Disabled));
    }

    public LogView()
    {
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    /// <summary>The log to show; a <see cref="RunLogLines"/> is shown incrementally.</summary>
    public IReadOnlyList<string>? Lines
    {
        get => (IReadOnlyList<string>?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    /// <summary>True while the view keeps the newest line in sight.</summary>
    public bool IsFollowing => _follow;

    private static void OnLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((LogView)d).Show((IReadOnlyList<string>?)e.NewValue ?? RunLogLines.Empty);

    private void Show(IReadOnlyList<string> lines)
    {
        var update = _cursor.Advance(lines);
        if (update.Reset)
        {
            // A new run (or a plain list) starts at its newest line. The same
            // run reset to its cap keeps whatever the reader was doing.
            var run = lines as RunLogLines;
            if (run is null || _run is null || !run.IsSameRun(_run))
            {
                _follow = true;
            }

            _run = run;
            Text = Join(lines, 0);
            _hasText = lines.Count > 0;
        }
        else if (update.From < lines.Count)
        {
            _run = (RunLogLines)lines;
            AppendText((_hasText ? Environment.NewLine : string.Empty) + Join(lines, update.From));
            _hasText = true;
        }
        else
        {
            return;
        }

        if (_follow)
        {
            ScrollToEnd();
        }
    }

    private static string Join(IReadOnlyList<string> lines, int from)
    {
        var text = new StringBuilder();
        for (int i = from; i < lines.Count; i++)
        {
            if (i > from)
            {
                text.AppendLine();
            }

            text.Append(lines[i]);
        }

        return text.ToString();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scroller)
        {
            return;
        }

        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            // Only the offset moved, so the reader scrolled (wheel, scroll bar,
            // keys) or our own scroll to the end landed: follow from here on
            // exactly when that left the view at the bottom.
            _follow = scroller.VerticalOffset >= scroller.ScrollableHeight - BottomSlack;
        }
        else if (_follow)
        {
            // New lines, or a resize that re-wrapped them: stay on the newest.
            scroller.ScrollToEnd();
        }
    }

    /// <summary>
    /// The log sits on a page that scrolls too. A scroll viewer swallows the
    /// wheel even when it cannot move, so the page would stall whenever the
    /// pointer crossed the log; at the log's top or bottom the wheel goes on
    /// to the page instead.
    /// </summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseWheel(e);
        if (e.Handled || GetTemplateChild("PART_ContentHost") is not ScrollViewer scroller)
        {
            return;
        }

        bool atTop = scroller.VerticalOffset <= 0;
        bool atBottom = scroller.VerticalOffset >= scroller.ScrollableHeight;
        if ((e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
        {
            e.Handled = true;
            (VisualTreeHelper.GetParent(this) as UIElement)?.RaiseEvent(
                new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = MouseWheelEvent,
                    Source = this,
                });
        }
    }
}
