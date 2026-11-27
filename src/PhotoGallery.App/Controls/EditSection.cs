using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace PhotoGallery.App.Controls;

/// <summary>
/// A section of the Edit pane (Lasse, 2026-11-27): a thin line above it instead of a box around it, its name, a dot
/// while it holds changes not saved yet, and a chevron. Clicking the name folds the section away or opens it again;
/// the pane remembers that per section. <see cref="IsSubsection"/> makes a slim, quieter row inside a section (such
/// as "All 15 looks") with no line above it.
/// </summary>
[ContentProperty(Name = nameof(Body))]
public sealed partial class EditSection : UserControl
{
    private readonly Border _line = new() { BorderThickness = new Thickness(0, 1, 0, 0) };
    private readonly Button _header = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0, 10, 2, 8),
    };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Ellipse _dot = new() { Width = 7, Height = 7, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly FontIcon _chevron = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentPresenter _body = new();
    private bool _isExpanded = true, _isSubsection, _hasChanges;
    private string _headerText = "";

    public EditSection()
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _title, _dot } };
        var headerGrid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
            Children = { titleRow, _chevron },
        };
        Grid.SetColumn(_chevron, 1);
        _header.Content = headerGrid;
        _header.Click += (_, _) => IsExpanded = !IsExpanded;
        Content = new StackPanel { Children = { _line, _header, _body } };
        ActualThemeChanged += (_, _) => Paint();
        Loaded += (_, _) => Paint();
        Apply();
    }

    /// <summary>The section's name (or, as a subsection, the row's text).</summary>
    public string Header
    {
        get => _headerText;
        set
        {
            _headerText = value ?? "";
            _title.Text = _headerText;
            AutomationProperties.SetName(_header, _headerText);
        }
    }

    /// <summary>What is in the section.</summary>
    public UIElement? Body
    {
        get => _body.Content as UIElement;
        set => _body.Content = value;
    }

    /// <summary>Open (the default) or folded away.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            Apply();
            ExpandedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A slim row inside a section: smaller muted text and no line above it.</summary>
    public bool IsSubsection
    {
        get => _isSubsection;
        set
        {
            _isSubsection = value;
            Apply();
            Paint();
        }
    }

    /// <summary>Shows the dot: something in the section was changed and isn't saved yet.</summary>
    public bool HasChanges
    {
        get => _hasChanges;
        set
        {
            _hasChanges = value;
            _dot.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdateHelpText();
        }
    }

    /// <summary>The name the pane remembers the folding under (Settings).</summary>
    public string Key { get; set; } = "";

    /// <summary>Raised when the section was folded or opened, by a click or from code.</summary>
    public event EventHandler? ExpandedChanged;

    private void Apply()
    {
        _body.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        _chevron.Glyph = _isExpanded ? "" : ""; // up when open, down when folded
        _line.Visibility = _isSubsection ? Visibility.Collapsed : Visibility.Visible;
        _title.FontSize = _isSubsection ? 12 : 13;
        _title.FontWeight = _isSubsection ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        _header.Padding = _isSubsection ? new Thickness(0, 6, 2, 4) : new Thickness(0, 10, 2, 8);
        UpdateHelpText();
    }

    private void UpdateHelpText()
    {
        // Screen readers hear "expanded" or "collapsed" and whether something in it was changed.
        var state = _isExpanded ? Core.Localization.Strings.Tr("Section open") : Core.Localization.Strings.Tr("Section folded away");
        AutomationProperties.SetHelpText(_header, _hasChanges ? state + ", " + Core.Localization.Strings.Tr("has changes") : state);
    }

    private void Paint()
    {
        _line.BorderBrush = MainWindow.ThemeBrush("DividerStrokeColorDefaultBrush");
        _dot.Fill = MainWindow.ThemeBrush("AccentTextFillColorPrimaryBrush");
        _title.Foreground = MainWindow.ThemeBrush(_isSubsection ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        _chevron.Foreground = MainWindow.ThemeBrush("TextFillColorSecondaryBrush");
    }
}
