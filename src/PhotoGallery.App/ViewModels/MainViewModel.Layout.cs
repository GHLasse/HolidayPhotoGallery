namespace PhotoGallery.App.ViewModels;

/// <summary>The widths of the side panes, kept in the settings so the window opens as it was left.</summary>
public sealed partial class MainViewModel
{
    public const double DefaultNavPaneWidth = 260;
    public const double DefaultInfoPaneWidth = 320;

    public double NavPaneWidth => _settings.NavPaneWidth > 0 ? _settings.NavPaneWidth : DefaultNavPaneWidth;

    public double InfoPaneWidth => _settings.InfoPaneWidth > 0 ? _settings.InfoPaneWidth : DefaultInfoPaneWidth;

    public void SavePaneWidths(double nav, double info)
    {
        nav = Math.Round(nav);
        info = Math.Round(info);
        if (nav == _settings.NavPaneWidth && info == _settings.InfoPaneWidth)
            return;
        _settings.NavPaneWidth = nav;
        _settings.InfoPaneWidth = info;
        _settings.Save(SettingsPath);
    }
}
