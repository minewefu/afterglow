using System.Globalization;
using System.Xml.Linq;

namespace Afterglow.Core.Tests;

/// <summary>
/// MainWindow draws its own title bar inside a 44px WindowChrome caption. Anything the
/// user clicks in that band must set WindowChrome.IsHitTestVisibleInChrome, or Win32
/// answers WM_NCHITTEST with HTCAPTION and the control only ever drags the window.
/// The WPF window cannot be instantiated in a headless test run, so these tests read the
/// shipped XAML as XML - they assert the source condition, not the runtime hit test.
/// </summary>
public sealed class CaptionHitTestTests
{
    private const string HitTestProperty = "WindowChrome.IsHitTestVisibleInChrome";

    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Controls a user clicks. TextBlock/Border/Ellipse stay draggable on purpose.</summary>
    private static readonly string[] InteractiveElements =
    [
        "Button", "CheckBox", "ComboBox", "ListBox", "ListView", "Menu", "MenuItem", "PasswordBox",
        "RadioButton", "RepeatButton", "Slider", "TabControl", "TextBox", "ToggleButton",
    ];

    [Fact]
    public void Gpu_selector_in_the_title_bar_is_clickable_not_a_drag_handle()
    {
        var root = MainWindowXaml();
        var titleBar = TitleBar(root);

        // Premise: the chrome caption really does cover the whole title-bar row.
        Assert.True(CaptionHeight(root) >= TitleRowHeight(root),
            "The WindowChrome caption no longer covers the title-bar row; this test's premise is stale.");

        var selector = Required(
            titleBar.Descendants(Presentation + "ComboBox").FirstOrDefault(e =>
                ((string?)e.Attribute("ItemsSource"))?.Contains("GpuOptions", StringComparison.Ordinal) == true),
            "The multi-GPU selector ComboBox is no longer in the title bar.");

        Assert.True(IsClickableInChrome(selector, titleBar),
            "The title-bar GPU selector does not set " + HitTestProperty +
            "; WM_NCHITTEST answers HTCAPTION over it, so clicking drags the window instead of opening the list.");
    }

    [Fact]
    public void Every_interactive_control_in_the_caption_band_is_clickable()
    {
        var root = MainWindowXaml();
        var titleBar = TitleBar(root);

        string[] dead =
        [
            .. titleBar.Descendants()
                .Where(e => InteractiveElements.Contains(e.Name.LocalName, StringComparer.Ordinal))
                .Where(e => !IsClickableInChrome(e, titleBar))
                .Select(Describe),
        ];

        Assert.True(dead.Length == 0,
            "Controls inside the WindowChrome caption that the mouse cannot reach (they drag the window instead): "
            + string.Join(", ", dead));
    }

    [Fact]
    public void Title_bar_keeps_a_drag_surface()
    {
        var root = MainWindowXaml();
        var titleBar = TitleBar(root);

        // IsHitTestVisibleInChrome inherits, so marking a container that holds the app name
        // would make the whole label dead to dragging - the window would only move by its edges.
        string[] blanketed =
        [
            .. titleBar.DescendantsAndSelf()
                .Where(e => e.Name.LocalName is "Grid" or "StackPanel")
                .Where(e => e.Descendants(Presentation + "TextBlock")
                    .Any(t => (string?)t.Attribute("Text") == "Afterglow"))
                .Where(SetsHitTestVisible)
                .Select(e => e.Name.LocalName),
        ];

        Assert.True(blanketed.Length == 0,
            "A container holding the window title is hit-test-visible in chrome, which stops the user dragging the window by its title bar: "
            + string.Join(", ", blanketed));
    }

    /// <summary>True when the element, or anything between it and the caption root, opts out of the caption.</summary>
    private static bool IsClickableInChrome(XElement element, XElement captionRoot)
    {
        for (XElement? e = element; e is not null; e = e.Parent)
        {
            if (SetsHitTestVisible(e))
            {
                return true;
            }

            if (ReferenceEquals(e, captionRoot))
            {
                break;
            }
        }

        return false;
    }

    private static bool SetsHitTestVisible(XElement element)
    {
        if (element.Attributes().Any(a =>
                a.Name.LocalName == HitTestProperty &&
                string.Equals(a.Value, "True", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // The caption buttons get it from a Style; resolve one StaticResource hop in this file.
        string? key = StaticResourceKey((string?)element.Attribute("Style"));
        if (key is null || element.Document is null)
        {
            return false;
        }

        var style = element.Document.Descendants(Presentation + "Style")
            .FirstOrDefault(s => (string?)s.Attribute(Xaml + "Key") == key);

        return style is not null && style.Elements(Presentation + "Setter").Any(setter =>
            ((string?)setter.Attribute("Property"))?.EndsWith(HitTestProperty, StringComparison.Ordinal) == true &&
            string.Equals((string?)setter.Attribute("Value"), "True", StringComparison.OrdinalIgnoreCase));
    }

    private static string? StaticResourceKey(string? markup) =>
        markup is not null
        && markup.StartsWith("{StaticResource ", StringComparison.Ordinal)
        && markup.EndsWith('}')
            ? markup["{StaticResource ".Length..^1].Trim()
            : null;

    private static string Describe(XElement element) =>
        element.Name.LocalName + " "
        + ((string?)element.Attribute("Content") ?? (string?)element.Attribute("ItemsSource") ?? "(unnamed)");

    private static XElement MainWindowXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Afterglow.slnx")))
        {
            dir = dir.Parent;
        }

        string repo = Required(dir,
            "Could not find the repository root (Afterglow.slnx) above " + AppContext.BaseDirectory).FullName;
        string path = Path.Combine(repo, "src", "Afterglow.App", "MainWindow.xaml");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("MainWindow.xaml not found at " + path);
        }

        return Required(XDocument.Load(path).Root, "MainWindow.xaml has no root element.");
    }

    private static XElement TitleBar(XElement root) => Required(
        root.Descendants(Presentation + "Grid").FirstOrDefault(e => (string?)e.Attribute("Grid.Row") == "0"),
        "MainWindow.xaml no longer has a Grid.Row=\"0\" title bar.");

    private static double CaptionHeight(XElement root) => ParseDouble(
        Required(root.Descendants().FirstOrDefault(e => e.Name.LocalName == "WindowChrome"),
            "MainWindow no longer uses WindowChrome.").Attribute("CaptionHeight"),
        "the WindowChrome CaptionHeight");

    private static double TitleRowHeight(XElement root)
    {
        var grid = Required(root.Elements(Presentation + "Grid").FirstOrDefault(), "MainWindow has no root Grid.");
        var rows = Required(grid.Element(Presentation + "Grid.RowDefinitions"), "The root Grid declares no rows.");
        var first = Required(rows.Elements(Presentation + "RowDefinition").FirstOrDefault(),
            "The root Grid declares no rows.");
        return ParseDouble(first.Attribute("Height"), "the title row Height");
    }

    private static double ParseDouble(XAttribute? attribute, string what) =>
        double.TryParse(Required(attribute, "Missing " + what + ".").Value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new InvalidOperationException("Could not read " + what + " as a number.");

    private static T Required<T>(T? value, string message)
        where T : class => value ?? throw new InvalidOperationException(message);
}
