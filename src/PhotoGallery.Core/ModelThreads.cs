using Microsoft.ML.OnnxRuntime;

namespace PhotoGallery.Core;

/// <summary>
/// How many processor threads the AI models may use. By default ONNX Runtime's threads keep spinning on the
/// processor between steps, waiting for the next one. With the face and tagging models busy in the background
/// at the same time, each with a thread for nearly every core, that kept every core at 100 % for minutes and
/// left nothing for the window, so the app stopped reacting while the background work went on (Lasse
/// 2026-11-27, after naming people on many photos). Now the threads sleep when they have nothing to do, and the
/// models that run quietly in the background take at most half the cores.
/// </summary>
public static class ModelThreads
{
    /// <summary>For a model the user is waiting for (an edit tool): all cores but one.</summary>
    public static int ForUser => Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>For a model that mostly runs in the background (faces, auto-tagging): half the cores, at most 4.</summary>
    public static int ForBackground => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Sets the thread count and stops the threads spinning while they wait.</summary>
    public static void Apply(SessionOptions options, int threads)
    {
        options.IntraOpNumThreads = threads;
        options.InterOpNumThreads = 1;
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
    }
}
