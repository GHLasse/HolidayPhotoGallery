// Temporary diagnostic (E-12 crash): runs the noise model on DirectML in its own process.
using Microsoft.ML.OnnxRuntime;
using PhotoGallery.Core.Editing;

var folder = args[0];
var mode = args.Length > 1 ? args[1] : "gpu";
var model = DenoiseModel.ModelPath(folder);
Console.WriteLine($"mode={mode} providers={string.Join(",", OrtEnv.Instance().GetAvailableProviders())}");
using (var meta = new InferenceSession(model))
{
    foreach (var (name, m) in meta.InputMetadata)
        Console.WriteLine($"input {name}: dims=[{string.Join(",", m.Dimensions)}] symbolic=[{string.Join(",", m.SymbolicDimensions)}]");
    foreach (var (name, m) in meta.OutputMetadata)
        Console.WriteLine($"output {name}: dims=[{string.Join(",", m.Dimensions)}] symbolic=[{string.Join(",", m.SymbolicDimensions)}]");
}
Console.Out.Flush();
using var denoiser = Denoiser.Load(model, processorOnly: mode == "cpu");
Console.WriteLine($"loaded, onGraphicsCard={denoiser.OnGraphicsCard}");
Console.Out.Flush();
const int w = 300, h = 190;
var rnd = new Random(1);
var px = new byte[w * h * 4];
for (var i = 0; i < px.Length; i++) px[i] = i % 4 == 3 ? (byte)255 : (byte)Math.Clamp(120 + rnd.Next(-30, 31), 0, 255);
var watch = System.Diagnostics.Stopwatch.StartNew();
var outPx = denoiser.Denoise(px, w, h, new Progress<double>(p => Console.WriteLine($"progress {p:P0}")));
Console.WriteLine($"done in {watch.ElapsedMilliseconds} ms, onGraphicsCard={denoiser.OnGraphicsCard}, sample={outPx[0]},{outPx[1]},{outPx[2]}");
