namespace PhotoGallery.Core.Editing;

/// <summary>
/// Remembers that an AI model was at work on the graphics card. A graphics driver can end the whole app while a model
/// runs (inside DirectML, where no error can be caught); the mark it leaves then makes the next run use the processor.
/// </summary>
public sealed class GraphicsCardGuard(string folder, string name)
{
    private string Mark => Path.Combine(folder, name + ".graphics-card-running");

    /// <summary>True when the app ended last time while this model ran on the graphics card.</summary>
    public bool EndedTheAppBefore => File.Exists(Mark);

    /// <summary>Marks the start of a run on the graphics card; disposing the result marks its end.</summary>
    public IDisposable Running()
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Mark, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the mark, a crash simply isn't remembered.
        }
        return new End(Mark);
    }

    /// <summary>Lets the graphics card be tried again (after the user changed the setting, for example).</summary>
    public void Forget()
    {
        try
        {
            File.Delete(Mark);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class End(string mark) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                File.Delete(mark);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
