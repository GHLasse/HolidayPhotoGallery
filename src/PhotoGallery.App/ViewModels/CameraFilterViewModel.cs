namespace PhotoGallery.App.ViewModels;

/// <summary>An entry in the camera filter list. A null camera means "all cameras".</summary>
public sealed record CameraFilterViewModel(string? Camera, string Display);
