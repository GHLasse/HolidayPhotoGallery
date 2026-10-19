using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

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
            ViewModel.Warn(Tr("Select the photos to find people in."));
            return;
        }
        if (!MainViewModel.IsFaceModelInstalled)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = Tr("Download the face models?"),
                Content = Tr("Finding and recognising people happens on this PC, so no photo is sent anywhere. ") +
                          Tr("It needs two small face models (YuNet and SFace from OpenCV) of about {0:N0} MB together, which are downloaded once from GitHub and kept in the app's data folder.", FaceModel.DownloadSize / 1_000_000),
                PrimaryButtonText = Tr("Download"),
                CloseButtonText = Tr("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary || !await ViewModel.DownloadFaceModelAsync())
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
        if (photos.Count == 0)
        {
            PeopleSection.Visibility = Visibility.Collapsed;
            return;
        }
        if (photos.Count > 1)
        {
            PeopleList.Children.Clear();
            PeopleList.Children.Add(AddPersonBox(photos));
            PeopleHint.Text = Tr("Adds the person to all {0} selected photos, without marking a face.", photos.Count);
            PeopleHint.Visibility = Visibility.Visible;
            PeopleSection.Visibility = Visibility.Visible;
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
        PeopleList.Children.Add(AddPersonBox([item]));

        PeopleHint.Text = !looked
            ? Tr("Use People → Find people in the selected photos to look for faces in this photo.")
            : faces.Count == 0 && withoutFace.Count == 0 ? Tr("No faces were found in this photo.") : "";
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
            action = IconButton("", Tr("Remove the name from this face"), async () => await ViewModel.UnnameFaceAsync(face));
        }
        else if (suggestion is not null)
        {
            var yes = IconButton("", Tr("Yes, this is {0}", suggestion.Name), async () => await ViewModel.NameFacesAsync([face], suggestion.Name));
            var no = IconButton("", Tr("No, this is not {0}", suggestion.Name), async () => await ViewModel.RejectSuggestionAsync(face, suggestion.Name));
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { Label(Tr("{0}?", suggestion.Name)), yes, no },
            };
        }
        else
        {
            content = NameBox(async name => await ViewModel.NameFacesAsync([face], name));
            action = IconButton("", Tr("Not someone to name: hide this face"), async () => await ViewModel.IgnoreFacesAsync([face]));
        }
        return Row(FacePicture(face.Thumbnail, FaceSize), content, action);
    }

    private UIElement PersonRow(PhotoItemViewModel item, PersonTag person, byte[]? picture) =>
        Row(FacePicture(picture, FaceSize), Label(person.Name),
            IconButton("", Tr("Remove {0} from this photo", person.Name), async () => await ViewModel.RemovePersonAsync(item, person)));

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
        var box = new AutoSuggestBox { PlaceholderText = Tr("Who is this?"), HorizontalAlignment = HorizontalAlignment.Stretch };
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

    /// <summary>A box to tag someone whose face was not found (e.g. seen from behind) on the photos.</summary>
    private AutoSuggestBox AddPersonBox(IReadOnlyList<PhotoItemViewModel> photos)
    {
        AutoSuggestBox? box = null;
        box = NameBox(name =>
        {
            box!.Text = "";
            // Written in the background; the status bar shows how far it is.
            _ = ViewModel.AddPersonAsync(photos, name);
            return Task.CompletedTask;
        });
        box.PlaceholderText = photos.Count == 1 ? Tr("Add someone not found…") : Tr("Add a person…");
        box.QueryIcon = new SymbolIcon(Symbol.AddFriend);
        return box;
    }

    private async void AddPerson_Click(object sender, RoutedEventArgs e)
    {
        var photos = SelectedPhotos;
        if (photos.Count == 0)
        {
            ViewModel.Warn(Tr("Select the photos to add a person to."));
            return;
        }
        var nameBox = NameBox(_ => Task.CompletedTask);
        nameBox.Header = Tr("Who is in these photos?");
        nameBox.PlaceholderText = Tr("Name");
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = photos.Count == 1 ? Tr("Add a person to the photo") : Tr("Add a person to {0} photos", photos.Count),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    nameBox,
                    new TextBlock
                    {
                        Text = Tr("For someone whose face was not found, e.g. seen from behind. No face is marked."),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Opacity = 0.7,
                    },
                },
            },
            PrimaryButtonText = Tr("Add"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowThemedAsync() == ContentDialogResult.Primary && nameBox.Text.Trim() is { Length: > 0 } name)
            _ = ViewModel.AddPersonAsync(photos, name);
    }

    /// <summary>
    /// A tooltip showing the whole photo a face comes from, which helps to tell who it is.
    /// The photo is only decoded when the tooltip opens.
    /// </summary>
    private static ToolTip PhotoToolTip(string path)
    {
        var image = new Image { MaxWidth = 360, MaxHeight = 360, Stretch = Stretch.Uniform };
        var tip = new ToolTip
        {
            MaxWidth = 400,
            Content = new StackPanel
            {
                Spacing = 4,
                Children = { image, new TextBlock { Text = Path.GetFileName(path), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis } },
            },
        };
        tip.Opened += async (_, _) =>
        {
            if (image.Source is not null)
                return;
            try
            {
                image.Source = await ImageLoader.DecodeAsync(path, 360);
            }
            catch (Exception)
            {
                // Unreadable photo: the tooltip shows just the file name.
            }
        };
        return tip;
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
                Title = Tr("Name the people found"),
                Content = Tr("There are no unnamed faces. Use People → Find people to look for faces in your photos first."),
                CloseButtonText = Tr("OK"),
            }.ShowThemedAsync();
            return;
        }

        // Big groups are shown a page at a time, so every face that gets a name has been seen.
        const int Shown = 40;
        var index = 0;
        var start = 0;
        string? lastName = null;
        var heading = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var faces = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 68, ItemHeight = 68, MaximumRowsOrColumns = 8 };
        var nameBox = NameBox(_ => Task.CompletedTask);
        nameBox.Header = Tr("Who is this?");
        nameBox.PlaceholderText = Tr("Name");
        var problem = new TextBlock { Foreground = ThemeBrush("SystemFillColorCriticalBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.Firebrick), TextWrapping = TextWrapping.Wrap };
        var hide = new HyperlinkButton { Content = Tr("Not people to name: hide these faces"), Padding = new Thickness(0) };
        var toggles = new List<(ToggleButton Toggle, FoundFace Face)>();

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Name the people found"),
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
                        nameBox,
                        problem,
                        hide,
                    },
                },
            },
            PrimaryButtonText = Tr("Name them"),
            SecondaryButtonText = Tr("Skip"),
            CloseButtonText = Tr("Done"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;

        void Show()
        {
            var group = groups[index];
            var page = group.Faces.Count > Shown
                ? Tr(" (showing {0} to {1})", start + 1, Math.Min(start + Shown, group.Faces.Count))
                : "";
            heading.Text = Tr("Group {0} of {1}: {2} face(s) that look like the same person{3}. ", index + 1, groups.Count, group.Faces.Count, page) +
                           Tr("Click a face that doesn't belong to leave it out.");
            faces.Children.Clear();
            toggles.Clear();
            foreach (var face in group.Faces.Skip(start).Take(Shown))
            {
                var toggle = new ToggleButton { IsChecked = true, Padding = new Thickness(2), Content = FacePicture(face.Thumbnail, 56) };
                toggle.Checked += (_, _) => toggle.Opacity = 1;
                toggle.Unchecked += (_, _) => toggle.Opacity = 0.35;
                ToolTipService.SetToolTip(toggle, PhotoToolTip(face.Path));
                faces.Children.Add(toggle);
                toggles.Add((toggle, face));
            }
            nameBox.Text = lastName ?? group.Suggestion?.Name ?? "";
            problem.Text = "";
        }

        IReadOnlyList<FoundFace> Chosen() => [.. toggles.Where(t => t.Toggle.IsChecked == true).Select(t => t.Face)];

        // The next page of this group, keeping the name just used, or else the next group.
        void Next(string? name = null)
        {
            start += Shown;
            if (start < groups[index].Faces.Count)
            {
                lastName = name ?? lastName;
                Show();
                return;
            }
            start = 0;
            lastName = null;
            index++;
            if (index >= groups.Count)
                dialog.Hide();
            else
                Show();
        }

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            args.Cancel = true;
            var name = nameBox.Text.Trim();
            var chosen = Chosen();
            if (name.Length == 0 || chosen.Count == 0)
            {
                problem.Text = name.Length == 0 ? Tr("Type a name first.") : Tr("Every face is left out.");
                return;
            }
            // The names are written in the background (the status bar shows how far it is), so the next
            // faces come up at once and Done closes the dialog straight away.
            _ = ViewModel.NameFacesAsync(chosen, name);
            Next(name);
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            Next();
        };
        hide.Click += (_, _) =>
        {
            var chosen = Chosen();
            if (chosen.Count > 0)
                _ = ViewModel.IgnoreFacesAsync(chosen);
            Next();
        };

        Show();
        await dialog.ShowThemedAsync();
    }
}
