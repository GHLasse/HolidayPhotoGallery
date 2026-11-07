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

    private async void FindPeople_Click(object sender, RoutedEventArgs e)
    {
        if (!WaitsForBusyWork())
            await FindPeopleAsync(SelectedPhotos, again: true);
    }

    private async void FindPeopleInAll_Click(object sender, RoutedEventArgs e)
    {
        if (!WaitsForBusyWork())
            await FindPeopleAsync(ViewModel.Photos.ToList());
    }

    private void PeopleSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PeopleSortCombo.SelectedIndex < 0 || PeopleSortCombo.SelectedIndex == ViewModel.PeopleSortIndex)
            return;
        ViewModel.PeopleSortIndex = PeopleSortCombo.SelectedIndex;
        _ = ShowPeoplePageAsync();
    }

    /// <param name="again">
    /// Looks at photos already looked at once more (the selected photos): faces hidden by mistake come back,
    /// and named faces keep their names, which are in the files.
    /// </param>
    private async Task FindPeopleAsync(IReadOnlyList<PhotoItemViewModel> photos, bool again = false)
    {
        if (photos.Count == 0)
        {
            ViewModel.Warn(Tr("Select the photos to find people in."));
            return;
        }
        if (!await EnsureFaceModelAsync())
            return;
        await ViewModel.FindFacesAsync(photos, again);
    }

    /// <summary>Asks to download the face models when they aren't there yet; false when they still aren't.</summary>
    private async Task<bool> EnsureFaceModelAsync()
    {
        if (MainViewModel.IsFaceModelInstalled)
            return true;
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
        return await dialog.ShowThemedAsync() == ContentDialogResult.Primary && await ViewModel.DownloadFaceModelAsync();
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
            // Everyone in any of the photos, with in how many, like the tags (X-1).
            var counts = photos.SelectMany(p => p.Photo.People.Select(person => person.Name).Distinct(StringComparer.CurrentCultureIgnoreCase))
                .GroupBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => (Name: g.Key, Count: g.Count()))
                .OrderByDescending(p => p.Count).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            foreach (var (name, _) in counts.Where(p => !_latestFaces.ContainsKey(p.Name)))
                _latestFaces[name] = await ViewModel.GetLatestFaceAsync(name);
            if (version != _peopleVersion)
                return;
            var focusWasHere = FocusIsIn(PeopleSection);
            PeopleList.Children.Clear();
            foreach (var (name, count) in counts)
            {
                PeopleList.Children.Add(Row(FacePicture(_latestFaces.GetValueOrDefault(name), FaceSize), Label($"{name} ({count})"),
                    IconButton("\uE711", Tr("Remove {0} from the selected photos", name), async () => await ViewModel.RemovePersonFromAsync(photos, name))));
            }
            PeopleList.Children.Add(AddPersonBox(photos));
            PeopleHint.Text = Tr("Adds the person to all {0} selected photos, without marking a face.", photos.Count);
            PeopleHint.Visibility = Visibility.Visible;
            PeopleSection.Visibility = Visibility.Visible;
            if (focusWasHere)
                FocusPhoto();
            return;
        }

        var item = photos[0];
        var orientation = item.Photo.Orientation;
        var allFaces = await ViewModel.GetFacesAsync(item.Path);
        var hidden = allFaces.Any(f => f.Ignored);
        var faces = allFaces
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

        // Rebuilding the rows takes away the box or button that had the focus, and WinUI then hands it to the
        // next control down: the Date taken box, where a rating key typed a digit into the date (Lasse, 2026-11-07).
        var hadFocus = FocusIsIn(PeopleSection);
        PeopleList.Children.Clear();
        foreach (var face in faces)
            PeopleList.Children.Add(FaceRow(face, suggestions.GetValueOrDefault(face.Id)));

        for (var i = 0; i < withoutFace.Count; i++)
            PeopleList.Children.Add(PersonRow(item, withoutFace[i], pictures[i]));
        // The search is offered right here when it can do something: the photo was not looked at yet, or
        // faces were hidden and looking again brings them back. Faces are not looked for in videos (Lasse, 2026-10-27).
        if (!item.IsVideo && (!looked || hidden))
        {
            var find = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { AppIcons.Element(AppIcons.FindPeople, 14), new TextBlock { Text = looked ? Tr("Look again") : Tr("Find people") } },
                },
            };
            ToolTipService.SetToolTip(find, looked ? Tr("Look for people in this photo again; hidden faces come back") : Tr("Find people in the selected photos"));
            find.Click += FindPeople_Click;
            PeopleList.Children.Add(find);
        }
        PeopleList.Children.Add(AddPersonBox([item]));

        PeopleHint.Text = item.IsVideo && faces.Count == 0 && withoutFace.Count == 0 ? Tr("Faces are only looked for in photos, not in videos.")
            : looked && faces.Count == 0 && withoutFace.Count == 0 ? Tr("No faces were found in this photo.") : "";
        PeopleHint.Visibility = PeopleHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PeopleSection.Visibility = Visibility.Visible;
        if (hadFocus)
            FocusPhoto();
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
            content = NameBox(async name =>
            {
                FocusPhoto();
                await ViewModel.NameFacesAsync([face], name);
            });
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

    private Button IconButton(string glyph, string tooltip, Func<Task> click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += async (_, _) =>
        {
            // Hand the focus back to the photo first: disabling the focused button (and the rows being rebuilt
            // after) would otherwise move it on to the Date taken box below.
            FocusPhoto();
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
        PrepareSuggestions(box);
        box.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            var text = sender.Text.Trim();
            ShowSuggestions(sender, text.Length == 0
                ? []
                : ViewModel.KnownNames.Where(n => n.Contains(text, StringComparison.CurrentCultureIgnoreCase)).Take(8).ToList());
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
            FocusPhoto(); // so the next key rates the photo instead of typing into a box
            // Written in the background; the filter row shows how far it is.
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

    /// <summary>The right-click menu item that goes through the faces that may be this person.</summary>
    private MenuFlyoutItem ReviewMatchesItem(string person)
    {
        var item = new MenuFlyoutItem { Text = Tr("Go through possible matches"), Icon = new FontIcon { Glyph = "\uE77B" } };
        ToolTipService.SetToolTip(item, Tr("Only the unnamed faces that may be {0}", person));
        item.Click += (_, _) => _ = ReviewMatchesOfAsync(person);
        return item;
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

    // Faces whose group was skipped, so their group comes last the next time the dialog opens (until the app closes).
    private readonly HashSet<long> _skippedFaces = [];

    private async void NamePeople_Click(object sender, RoutedEventArgs e)
    {
        if (!WaitsForBusyWork())
            await NamePeopleAsync();
    }

    private async Task NamePeopleAsync()
    {
        var found = await ViewModel.GroupUnnamedFacesAsync();
        // Groups skipped earlier wait behind the others.
        var groups = found.OrderBy(g => g.Faces.Count(f => _skippedFaces.Contains(f.Id)) * 2 >= g.Faces.Count).ToList();
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
        await NameGroupsAsync(groups, person: null);
    }

    /// <summary>Right-click on a person: goes through only the unnamed faces that may be them.</summary>
    private async Task ReviewMatchesOfAsync(string person)
    {
        if (WaitsForBusyWork())
            return;
        var faces = await ViewModel.FindLikelyFacesOfAsync(person);
        if (faces.Count > 0)
        {
            // One group with the person as the fixed suggestion; it is shown a page at a time, most alike first.
            var group = new FaceGroup(faces, notThese => notThese.Contains(person, StringComparer.CurrentCultureIgnoreCase)
                ? null
                : new NameSuggestion(person, 1));
            if (!await NameGroupsAsync([group], person))
                return; // Done was pressed: the user stops here.
        }
        var next = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = faces.Count > 0 ? Tr("All possible matches for {0} done", person) : Tr("No more faces that may be {0}", person),
            Content = faces.Count > 0
                ? Tr("You went through every unnamed face that may be {0}. Continue with the other people found?", person)
                : Tr("No unnamed face looks like {0}. Continue with the other people found?", person),
            PrimaryButtonText = Tr("Continue"),
            CloseButtonText = Tr("Not now"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await next.ShowThemedAsync() == ContentDialogResult.Primary)
            await NamePeopleAsync();
    }

    /// <summary>
    /// The dialog for naming groups of faces. Each group comes with the person it most likely is, so most of
    /// the time it is only confirmed; "Not this person" offers the next most likely one. With
    /// <paramref name="person"/> it goes through the faces that may be that person.
    /// </summary>
    /// <returns>True when every group was gone through, false when Done closed the dialog first.</returns>
    private async Task<bool> NameGroupsAsync(List<FaceGroup> groups, string? person)
    {
        // Big groups are shown a page at a time, so every face that gets a name has been seen.
        const int Shown = 40;
        var index = 0;
        var start = 0;
        var finished = false;
        string? lastName = null;
        NameSuggestion? suggestion = null;
        var notNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var heading = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var guess = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var faces = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 68, ItemHeight = 68, MaximumRowsOrColumns = 8 };
        var nameBox = NameBox(_ => Task.CompletedTask);
        nameBox.Header = Tr("Who is this?");
        nameBox.PlaceholderText = Tr("Name");
        var problem = new TextBlock { Foreground = ThemeBrush("SystemFillColorCriticalBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.Firebrick), TextWrapping = TextWrapping.Wrap };
        var notThis = new Button { Content = Tr("Not this person") };
        var hide = new Button { Content = Tr("Hide these faces") };
        ToolTipService.SetToolTip(hide, Tr("Not people to name: hide these faces"));
        var toggles = new List<(FaceChoice Choice, FoundFace Face)>();

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = person is null ? Tr("Name the people found") : Tr("Faces that may be {0}", person),
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
                        guess,
                        nameBox,
                        problem,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { notThis, hide } },
                    },
                },
            },
            PrimaryButtonText = Tr("Name them"),
            SecondaryButtonText = person is null ? Tr("Skip for now") : Tr("Skip these"),
            CloseButtonText = Tr("Done"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;

        // A new group starts with its own most likely person.
        void Begin()
        {
            start = 0;
            lastName = null;
            notNames.Clear();
            suggestion = groups[index].Suggestion;
            Show();
        }

        void Show()
        {
            var group = groups[index];
            var page = group.Faces.Count > Shown
                ? Tr(" (showing {0} to {1})", start + 1, Math.Min(start + Shown, group.Faces.Count))
                : "";
            heading.Text = person is null
                ? Tr("Group {0} of {1}: {2} face(s) that look like the same person{3}. ", index + 1, groups.Count, group.Faces.Count, page) +
                  Tr("Click a face that doesn't belong to leave it out.")
                : Tr("{0} unnamed face(s) that may be {1}, most alike first{2}. ", group.Faces.Count, person, page) +
                  Tr("Click a face that isn't {0} to leave it out.", person);
            faces.Children.Clear();
            toggles.Clear();
            foreach (var face in group.Faces.Skip(start).Take(Shown))
            {
                var choice = new FaceChoice(FacePicture(face.Thumbnail, 56));
                ToolTipService.SetToolTip(choice.Element, PhotoToolTip(face.Path));
                faces.Children.Add(choice.Element);
                toggles.Add((choice, face));
            }
            var name = lastName ?? suggestion?.Name;
            nameBox.Text = name ?? "";
            guess.Text = person is not null ? ""
                : name is null ? (notNames.Count > 0 ? Tr("Nobody else you named looks like them. If you know who it is, type the name.")
                                                     : Tr("Nobody you named looks like them. If you know who it is, type the name."))
                : lastName is not null || suggestion!.Similarity >= FaceAnalyzer.SamePerson ? Tr("They look like {0}.", name)
                : Tr("They might be {0}.", name);
            guess.Visibility = guess.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            notThis.IsEnabled = name is not null;
            ToolTipService.SetToolTip(notThis, name is null ? null : person is null
                ? Tr("These faces are not {0}: offer the next most likely person", name)
                : Tr("These faces are not {0}", name));
            problem.Text = "";
        }

        IReadOnlyList<FoundFace> Chosen() => [.. toggles.Where(t => t.Choice.IsChosen).Select(t => t.Face)];
        IReadOnlyList<FoundFace> LeftOut() => [.. toggles.Where(t => !t.Choice.IsChosen).Select(t => t.Face)];

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
            index++;
            if (index >= groups.Count)
            {
                finished = true;
                dialog.Hide();
            }
            else
                Begin();
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
            // The names are written in the background (the filter row shows how far it is), so the next
            // faces come up at once and Done closes the dialog straight away.
            _ = ViewModel.NameFacesAsync(chosen, name);
            // Faces left out of someone's possible matches are not them, so they aren't offered again.
            if (person is not null && string.Equals(name, person, StringComparison.CurrentCultureIgnoreCase))
                _ = ViewModel.RejectNameAsync(LeftOut(), person);
            Next(name);
        };
        // The group's faces not yet named go to the end of the list, and come last next time too.
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (person is not null)
            {
                Next();
                return;
            }
            var group = groups[index];
            var rest = group.Faces.Skip(start).ToList();
            foreach (var face in rest)
                _skippedFaces.Add(face.Id);
            if (index == groups.Count - 1)
            {
                finished = true;
                dialog.Hide();
                return;
            }
            groups.RemoveAt(index);
            groups.Add(group.With(rest));
            Begin();
        };
        // Not this person: remembered for the chosen faces and the rest of the group, then the next most
        // likely person is offered (or the box is left empty for a name).
        notThis.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            if (name.Length == 0)
                name = suggestion?.Name ?? "";
            if (name.Length == 0)
                return;
            var chosen = Chosen();
            if (chosen.Count == 0)
            {
                problem.Text = Tr("Every face is left out.");
                return;
            }
            if (person is not null)
            {
                // Only these faces: the next pages are other faces that may still be them.
                _ = ViewModel.RejectNameAsync(chosen, name);
                Next();
                return;
            }
            _ = ViewModel.RejectNameAsync([.. chosen, .. groups[index].Faces.Skip(start + Shown)], name);
            notNames.Add(name);
            lastName = null;
            suggestion = groups[index].SuggestWithout(notNames);
            Show();
            nameBox.Focus(FocusState.Programmatic);
        };
        hide.Click += (_, _) =>
        {
            var chosen = Chosen();
            if (chosen.Count > 0)
                _ = ViewModel.IgnoreFacesAsync(chosen);
            Next();
        };

        Begin();
        await dialog.ShowThemedAsync();
        return finished;
    }
}

/// <summary>
/// A face in the naming dialog: chosen faces have a round outline in the design colour, left-out ones
/// fade. Click or Space switches it.
/// </summary>
internal sealed class FaceChoice
{
    public FaceChoice(Border picture)
    {
        Element = new Border
        {
            Width = 64,
            Height = 64,
            Padding = new Thickness(2),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(32),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = picture,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
        };
        Element.Tapped += (_, _) => Switch();
        Element.KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Space or Windows.System.VirtualKey.Enter)
            {
                Switch();
                e.Handled = true;
            }
        };
        Paint();
    }

    public Border Element { get; }

    public bool IsChosen { get; private set; } = true;

    private void Switch()
    {
        IsChosen = !IsChosen;
        Paint();
    }

    private void Paint()
    {
        Element.BorderBrush = IsChosen ? MainWindow.ThemeBrush("AccentFillColorDefaultBrush") : null;
        Element.Opacity = IsChosen ? 1 : 0.35;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Element, IsChosen ? Tr("Face, included") : Tr("Face, left out"));
    }
}
