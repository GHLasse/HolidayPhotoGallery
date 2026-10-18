using CommunityToolkit.Mvvm.ComponentModel;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// The metadata panel. With several photos selected it shows what they have in common, and
/// saving changes only the fields the user edited, so nothing else in those files is touched.
/// </summary>
public sealed class MetadataEditorViewModel : ObservableObject
{
    private IReadOnlyList<PhotoItemViewModel> _items = [];
    private IReadOnlyList<string> _originalCommonTags = [];
    private bool _loading;

    private string _caption = "";
    private string _tagsText = "";
    private double _rating = -1;
    private string _dateText = "";
    private string _offsetText = "";
    private bool _captionChanged, _tagsChanged, _ratingChanged, _dateChanged;
    private string _summary = "No photo selected";
    private string _details = "";
    private string _captionPlaceholder = "Add a caption";
    private string? _validationError;

    public IReadOnlyList<PhotoItemViewModel> Items => _items;

    public bool HasSelection => _items.Count > 0;

    public bool IsSingle => _items.Count == 1;

    public bool IsDirty => _captionChanged || _tagsChanged || _ratingChanged || _dateChanged;

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Details { get => _details; private set => SetProperty(ref _details, value); }

    public string CaptionPlaceholder { get => _captionPlaceholder; private set => SetProperty(ref _captionPlaceholder, value); }

    public string? ValidationError { get => _validationError; private set => SetProperty(ref _validationError, value); }

    public string Caption
    {
        get => _caption;
        set
        {
            if (SetProperty(ref _caption, value) && !_loading)
                MarkChanged(ref _captionChanged);
        }
    }

    /// <summary>Tags separated by commas. With several photos selected: the tags they all share.</summary>
    public string TagsText
    {
        get => _tagsText;
        set
        {
            if (SetProperty(ref _tagsText, value) && !_loading)
                MarkChanged(ref _tagsChanged);
        }
    }

    /// <summary>RatingControl value: -1 is "not rated / mixed", otherwise 1-5.</summary>
    public double Rating
    {
        get => _rating;
        set
        {
            if (SetProperty(ref _rating, value) && !_loading)
                MarkChanged(ref _ratingChanged);
        }
    }

    /// <summary>Date taken as "yyyy-MM-dd HH:mm:ss" (camera local time). Only editable for one photo.</summary>
    public string DateText
    {
        get => _dateText;
        set
        {
            if (SetProperty(ref _dateText, value) && !_loading)
                MarkChanged(ref _dateChanged);
        }
    }

    /// <summary>UTC offset such as "+02:00", or empty when unknown.</summary>
    public string OffsetText
    {
        get => _offsetText;
        set
        {
            if (SetProperty(ref _offsetText, value) && !_loading)
                MarkChanged(ref _dateChanged);
        }
    }

    public void Load(IReadOnlyList<PhotoItemViewModel> items)
    {
        _loading = true;
        try
        {
            _items = items;
            _captionChanged = _tagsChanged = _ratingChanged = _dateChanged = false;
            ValidationError = null;

            var photos = items.Select(i => i.Photo).ToList();
            _originalCommonTags = TagList.Common(photos.Select(p => p.Tags).ToList());
            TagsText = TagList.Format(_originalCommonTags);

            var captions = photos.Select(p => p.Caption ?? "").Distinct().ToList();
            Caption = captions.Count == 1 ? captions[0] : "";
            CaptionPlaceholder = captions.Count > 1 ? "(different captions)" : "Add a caption";

            var ratings = photos.Select(p => p.Rating).Distinct().ToList();
            Rating = ratings.Count == 1 && ratings[0] > 0 ? ratings[0] : -1;

            var date = items.Count == 1 ? photos[0].DateTaken : null;
            DateText = date?.ToDisplayDate() ?? "";
            OffsetText = date?.ToOffsetString() ?? "";

            Summary = items.Count switch
            {
                0 => "No photo selected",
                1 => photos[0].FileName,
                _ => $"{items.Count} photos selected",
            };
            Details = items.Count == 1 ? DescribeSingle(photos[0]) : "";
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>Puts the fields back to what the files contain.</summary>
    public void Revert() => Load(_items);

    /// <summary>
    /// Builds the edit for one of the selected photos, or returns false (with <see cref="ValidationError"/> set)
    /// when the date the user typed cannot be understood.
    /// </summary>
    public bool TryBuildChanges(PhotoItemViewModel item, out MetadataChanges changes)
    {
        changes = new MetadataChanges();
        DateTaken? date = null;

        if (_dateChanged && IsSingle)
        {
            if (string.IsNullOrWhiteSpace(DateText))
            {
                ValidationError = "Enter a date such as 2024-07-01 14:30:00.";
                return false;
            }
            if (!DateTaken.TryParse(DateText, out var parsed))
            {
                ValidationError = $"\"{DateText}\" is not a date. Use the form 2024-07-01 14:30:00.";
                return false;
            }
            TimeSpan? offset = null;
            if (!string.IsNullOrWhiteSpace(OffsetText))
            {
                if (!DateTaken.TryParseOffset(OffsetText, out var o))
                {
                    ValidationError = $"\"{OffsetText}\" is not a time zone offset. Use the form +02:00, or leave it empty.";
                    return false;
                }
                offset = o;
            }
            date = parsed with { Offset = offset ?? parsed.Offset };
        }

        ValidationError = null;
        changes = new MetadataChanges
        {
            Caption = _captionChanged ? Caption : null,
            Rating = _ratingChanged ? (Rating < 1 ? 0 : (int)Math.Round(Rating)) : null,
            Tags = _tagsChanged ? TagList.ApplyEdit(item.Photo.Tags, _originalCommonTags, TagList.Parse(TagsText)) : null,
            DateTaken = date,
        };
        return true;
    }

    private void MarkChanged(ref bool flag)
    {
        flag = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private static string DescribeSingle(Core.Library.IndexedPhoto photo)
    {
        var lines = new List<string>();
        if (photo.Width is { } w && photo.Height is { } h)
        {
            var (dw, dh) = Orientation.SwapsDimensions(photo.Orientation) ? (h, w) : (w, h);
            lines.Add($"{dw} × {dh} pixels");
        }
        lines.Add(FormatSize(photo.FileSize));
        lines.Add(photo.Folder);
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}
