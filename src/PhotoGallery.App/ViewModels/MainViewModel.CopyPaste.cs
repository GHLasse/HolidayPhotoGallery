using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// Copying the details of one photo and pasting them onto others (M-4), like GeoSetter: the location, date,
/// camera, tags, caption, rating or copyright that another editing program lost. Written into the files, with Undo.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>What was copied last; null until a photo's details are copied.</summary>
    public CopiedMetadata? CopiedDetails { get; private set; }

    public string CopiedFrom => CopiedDetails is null ? "" : Path.GetFileName(CopiedDetails.Source.Path);

    public MetadataParts PasteParts
    {
        get => _settings.PasteParts;
        set
        {
            _settings.PasteParts = value;
            _settings.Save(SettingsPath);
        }
    }

    public bool PasteKeepsGaps
    {
        get => _settings.PasteKeepsGaps;
        set
        {
            _settings.PasteKeepsGaps = value;
            _settings.Save(SettingsPath);
        }
    }

    public async Task CopyDetailsAsync(PhotoItemViewModel item)
    {
        if (_metadata is null)
            return;
        try
        {
            CopiedDetails = new CopiedMetadata(await _metadata.ReadAsync(item.Path), await _metadata.ReadCameraAsync(item.Path));
            Inform(Tr("Copied the details of {0}. Select photos and press Ctrl+Shift+V to paste them.", item.FileName));
        }
        catch (ExifToolException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Writes the ticked parts of the copied details into the photos.</summary>
    public async Task PasteDetailsAsync(IReadOnlyList<PhotoItemViewModel> items, MetadataParts parts, bool keepGaps)
    {
        if (_metadata is null || CopiedDetails is not { } copied || items.Count == 0 || parts == MetadataParts.None)
            return;
        IReadOnlyDictionary<string, PhotoMetadata> current;
        var cameras = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            current = await _metadata.ReadAsync(items.Select(i => i.Path).ToList());
            if (parts.HasFlag(MetadataParts.Camera))
            {
                foreach (var item in items)
                    cameras[item.Path] = await _metadata.ReadCameraAsync(item.Path);
            }
        }
        catch (ExifToolException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        var byPath = items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var targets = items.Select(i => current.TryGetValue(i.Path, out var m) ? m : null).OfType<PhotoMetadata>().ToList();
        var plan = MetadataPaste.Plan(copied, targets, parts, keepGaps);
        if (plan.Count == 0)
        {
            Inform(Tr("The selected photos already have these details."));
            return;
        }
        var edits = plan.Select(p => (byPath[p.Path], p.Changes)).ToList();
        var before = plan.Select(p => (byPath[p.Path], MetadataPaste.Undo(current[p.Path],
            cameras.TryGetValue(p.Path, out var c) ? c : new Dictionary<string, string>(), p.Changes))).ToList();

        var failures = await WriteChangesAsync(edits);
        ErrorMessage = failures.Count == 0 ? null : Tr("Some photos could not be saved.") + " " + string.Join(" ", failures);
        Inform(Tr("Pasted the details of {0} onto {1}.", Path.GetFileName(copied.Source.Path), PhotoCount(edits.Count - failures.Count)), async () =>
        {
            await WriteChangesAsync(before);
            ReloadEditor();
            await RefreshTagsAsync();
        });
        ReloadEditor();
        await RefreshTagsAsync();
    }

    private void ReloadEditor()
    {
        if (Editor.Items.Count > 0)
            Editor.Load(Editor.Items.ToList());
    }
}
