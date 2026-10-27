namespace PhotoGallery.App.ViewModels;

/// <summary>
/// Tips for new users (navigation proposal D): a one-line banner the first time a view opens or a command
/// has been clicked three times, each shown once. Settings › App turns them off or shows them all again.
/// </summary>
public sealed partial class MainViewModel
{
    public bool ShowTips => _settings.ShowTips;

    public void SaveShowTips(bool on)
    {
        if (on == _settings.ShowTips)
            return;
        _settings.ShowTips = on;
        _settings.Save(SettingsPath);
    }

    /// <summary>Shows the tip as a banner unless it was shown before or tips are off.</summary>
    public void TipOnce(string id, string text)
    {
        if (!_settings.ShowTips || _settings.SeenTips.Contains(id))
            return;
        _settings.SeenTips.Add(id);
        _settings.Save(SettingsPath);
        Inform(text);
    }

    public void ShowTipsAgain()
    {
        _settings.ShowTips = true;
        _settings.SeenTips.Clear();
        _settings.Save(SettingsPath);
    }
}
