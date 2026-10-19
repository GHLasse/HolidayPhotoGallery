using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;

namespace PhotoGallery.App;

/// <summary>The People menu, the faces in the info pane and the dialog for naming groups of faces.</summary>
public sealed partial class MainWindow
{
    private const double FaceSize = 48;
    private int _peopleVersion;

    private async void FindPeople_Click(object sender, RoutedEventArgs e) => await FindPeopleAsync(SelectedPhotos);

    private async void FindPeopleInAll_Click(object sender, RoutedEventArgs e) => await FindPeopleAsync(ViewModel.Photos.ToList());

    private async Task FindPeopleAsync(IReadOnlyList<PhotoItemViewModel> photos)
    {
        if (photos.Count == 0)
        {
            ViewModel.ErrorMessage = "Select the photos to find people in.";
            return;
        }
        if (!MainViewModel.IsFaceModelInstalled)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Download the face models?",
                Content = "Finding and recognising people happens on this PC, so no photo is sent anywhere. " +
                          $"It needs two small face models (YuNet and SFace from OpenCV) of about {FaceModel.DownloadSize / 1_000_000:N0} MB together, " +
                          "which are downloaded once from GitHub and kept in the app's data folder.",
                PrimaryButtonText = "Download",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !await ViewModel.DownloadFaceModelAsync())
                return;
        }
        await ViewModel.FindFacesAsync(photos);
    }

    // ---- Info pane ----

    /// <summary>Shows the faces of the selected photo with their names, suggestions or a box to name them.</summary>
    private async Task RefreshPeoplePanelAsync()
    {
        var version = ++_peopleVersion;
        var photos = SelectedPhotos;
        if (photos.Count != 1)
        {
            PeopleSection.Visibility = Visibility.Collapsed;
            return;
        }

        var item = photos[0];
        var orientation = item.Photo.Orientation;
        var faces = (await ViewModel.GetFacesAsync(item.Path))
            .Where(f => !f.Ignored)
            .OrderBy(f => f.Box.ToUpright(orientation).X)
            .ToList();
        var looked = faces.Count > 0 || await ViewModel.IsFaceScannedAsync(item.Path);
        // People named in the file whose face was not found (e.g. tagged in Windows Live Photo Gallery).
        var withoutFace = item.Photo.People
            .Where(p => !faces.Any(f => f.Person is not null && FaceLinks.PersonFor(f.Box, [p]) is not null))
            .ToList();
        var pictures = new List<byte[]?>();
        foreach (var person in withoutFace)
            pictures.Add(await ViewModel.GetPersonPictureAsync(item, person));
        var suggestions = new Dictionary<long, NameSuggestion?>();
        foreach (var face in faces.Where(f => f.Person is null))
            suggestions[face.Id] = await ViewModel.SuggestAsync(face);
        if (version != _peopleVersion)
            return; // Another photo was selected meanwhile.

        PeopleList.Children.Clear();
        foreach (var face in faces)
            PeopleList.Children.Add(FaceRow(face, suggestions.GetValueOrDefault(face.Id)));

        for (var i = 0; i < withoutFace.Count; i++)
            PeopleList.Children.Add(PersonRow(item, withoutFace[i], pictures[i]));

        PeopleHint.Text = !looked
            ? "Use People → Find people in the selected photos to look for faces in this photo."
            : faces.Count == 0 && withoutFace.Count == 0 ? "No faces were found in this photo." : "";
        PeopleHint.Visibility = PeopleHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PeopleSection.Visibility = Visibility.Visible;
    }

    private UIElement FaceRow(FoundFace face, NameSuggestion? suggestion)
    {
        UIElement content;
        UIElement? action = null;
        if (face.Person is { } name)
        {
            content = Label(name);
            action = IconButton("", "Remove the name from this face", async () => await ViewModel.UnnameFaceAsync(face));
        }
        else if (suggestion is not null)
        {
            var yes = IconButton("", $"Yes, this is {suggestion.Name}", async () => await ViewModel.NameFacesAsync([face], suggestion.Name));
            var no = IconButton("", $"No, this is not {suggestion.Name}", async () => await ViewModel.RejectSuggestionAsync(face, suggestion.Name));
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { Label($"{suggestion.Name}?"), yes, no },
            };
        }
        else
        {
            content = NameBox(async name => await ViewModel.NameFacesAsync([face], name));
            action = IconButton("", "Not someone to name: hide this face", async () => await ViewModel.IgnoreFacesAsync([face]));
        }
        return Row(FacePicture(face.Thumbnail, FaceSize), content, action);
    }

    private UIElement PersonRow(PhotoItemViewModel item, PersonTag person, byte[]? picture) =>
        Row(FacePicture(picture, FaceSize), Label(person.Name),
            IconButton("", $"Remove {person.Name} from this photo", async () => await ViewModel.RemovePersonAsync(item, person)));

    private static Grid Row(UIElement picture, UIElement content, UIElement? action)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (content is FrameworkElement element)
            element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn((FrameworkElement)content, 1);
        grid.Children.Add(picture);
        grid.Children.Add(content);
        if (action is FrameworkElement button)
        {
            button.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }
        return grid;
    }

    private static TextBlock Label(string text) => new() { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };

    private static Button IconButton(string glyph, string tooltip, Func<Task> click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await click();
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    /// <summary>A box to type a name in, offering the names already used in the library.</summary>
    private AutoSuggestBox NameBox(Func<string, Task> submit)
    {
        var box = new AutoSuggestBox { PlaceholderText = "Who is this?", HorizontalAlignment = HorizontalAlignment.Stretch };
        box.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            var text = sender.Text.Trim();
            sender.ItemsSource = text.Length == 0
                ? null
                : ViewModel.KnownNames.Where(n => n.Contains(text, StringComparison.CurrentCultureIgnoreCase)).Take(8).ToList();
        };
        box.SuggestionChosen += (sender, args) => sender.Text = args.SelectedItem as string ?? sender.Text;
        box.QuerySubmitted += async (sender, args) =>
        {
            var name = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
            if (name.Length == 0)
                return;
            sender.IsEnabled = false;
            await submit(name);
            sender.IsEnabled = true;
        };
        return box;
    }

    /// <summary>A round picture of a face; a person symbol when there is none.</summary>
    private static Border FacePicture(byte[]? thumbnail, double size)
    {
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = ThemeBrush("CardBackgroundFillColorSecondaryBrush"),
            Child = new FontIcon { Glyph = "", FontSize = size / 2.5, Opacity = 0.5 },
        };
        if (thumbnail is not null)
            LoadFace(border, thumbnail, size);
        return border;
    }

    // Theme brushes live in the theme dictionaries; looked up carefully, so a missing one only loses its colour.
    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private static async void LoadFace(Border border, byte[] thumbnail, double size)
    {
        try
        {
            var source = await ImageLoader.FromEncodedAsync(thumbnail);
            border.Child = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // A damaged thumbnail keeps the person symbol.
        }
    }

    // ---- Naming groups of faces ----

    private async void NamePeople_Click(object sender, RoutedEventArgs e)
    {
        var groups = await ViewModel.GroupUnnamedFacesAsync();
        if (groups.Count == 0)
        {
            ViewModel.ErrorMessage = null;
            await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Name the people found",
                Content = "There are no unnamed faces. Use People → Find people to look for faces in your photos first.",
                CloseButtonText = "OK",
            }.ShowAsync();
            return;
        }

        const int Shown = 40;
        var index = 0;
        var heading = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var faces = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 68, ItemHeight = 68, MaximumRowsOrColumns = 8 };
        var more = new TextBlock { FontSize = 12, Opacity = 0.7 };
        var nameBox = NameBox(_ => Task.CompletedTask);
        nameBox.Header = "Who is this?";
        nameBox.PlaceholderText = "Name";
        var problem = new TextBlock { Foreground = ThemeBrush("SystemFillColorCriticalBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.Firebrick), TextWrapping = TextWrapping.Wrap };
        var hide = new HyperlinkButton { Content = "Not people to name: hide these faces", Padding = new Thickness(0) };
        var toggles = new List<(ToggleButton Toggle, FoundFace Face)>();

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Name the people found",
            Content = new ScrollViewer
            {
                MaxHeight = 560,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        heading,
                        faces,
                        more,
                        nameBox,
                        problem,
                        hide,
                    },
                },
            },
            PrimaryButtonText = "Name them",
            SecondaryButtonText = "Skip",
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;

        void Show()
        {
            var group = groups[index];
            heading.Text = $"Group {index + 1} of {groups.Count}: {group.Faces.Count} face(s) that look like the same person. " +
                           "Click a face that doesn't belong to leave it out.";
            faces.Children.Clear();
            toggles.Clear();
            foreach (var face in group.Faces.Take(Shown))
            {
                var toggle = new ToggleButton { IsChecked = true, Padding = new Thickness(2), Content = FacePicture(face.Thumbnail, 56) };
                toggle.Checked += (_, _) => toggle.Opacity = 1;
                toggle.Unchecked += (_, _) => toggle.Opacity = 0.35;
                ToolTipService.SetToolTip(toggle, Path.GetFileName(face.Path));
                faces.Children.Add(toggle);
                toggles.Add((toggle, face));
            }
            more.Text = group.Faces.Count > Shown ? $"and {group.Faces.Count - Shown} more like these" : "";
            more.Visibility = more.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            nameBox.Text = group.Suggestion?.Name ?? "";
            problem.Text = "";
        }

        IReadOnlyList<FoundFace> Chosen() =>
            [.. toggles.Where(t => t.Toggle.IsChecked == true).Select(t => t.Face), .. groups[index].Faces.Skip(Shown)];

        void Next()
        {
            index++;
            if (index >= groups.Count)
                dialog.Hide();
            else
                Show();
        }

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var name = nameBox.Text.Trim();
            var chosen = Chosen();
            if (name.Length == 0 || chosen.Count == 0)
            {
                problem.Text = name.Length == 0 ? "Type a name first." : "Every face is left out.";
                return;
            }
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
            try
            {
                await ViewModel.NameFacesAsync(chosen, name);
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = true;
                deferral.Complete();
            }
            Next();
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            Next();
        };
        hide.Click += async (_, _) =>
        {
            var chosen = Chosen();
            if (chosen.Count > 0)
                await ViewModel.IgnoreFacesAsync(chosen);
            Next();
        };

        Show();
        await dialog.ShowAsync();
    }
}
