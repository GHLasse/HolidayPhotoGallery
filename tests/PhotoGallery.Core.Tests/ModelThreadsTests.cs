using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoGallery.Core.Tests;

public class ModelThreadsTests
{
    [Fact]
    public void Background_models_leave_cores_free()
    {
        Assert.InRange(ModelThreads.ForBackground, 1, 4);
        Assert.True(ModelThreads.ForBackground <= ModelThreads.ForUser || Environment.ProcessorCount == 1);
        Assert.True(ModelThreads.ForBackground <= Math.Max(1, Environment.ProcessorCount / 2));
    }

    [Fact]
    public void A_model_runs_with_threads_that_do_not_spin()
    {
        // ONNX Runtime checks the settings when the model is loaded, so a misspelt one would fail here.
        using var options = new SessionOptions();
        ModelThreads.Apply(options, ModelThreads.ForBackground);
        using var session = new InferenceSession(IdentityModel(), options);
        var input = new DenseTensor<float>(new[] { 4.5f }, [1]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("x", input)]);
        Assert.Equal(4.5f, results.First().AsTensor<float>()[0]);
    }

    // The smallest model there is: y = x, one float. Written out as ONNX's protobuf by hand.
    private static byte[] IdentityModel()
    {
        static byte[] Field(int number, byte[] value) => [(byte)(number << 3 | 2), .. Varint((ulong)value.Length), .. value];
        static byte[] Number(int number, ulong value) => [(byte)(number << 3), .. Varint(value)];
        static byte[] Text(int number, string value) => Field(number, System.Text.Encoding.UTF8.GetBytes(value));
        static byte[] Varint(ulong value)
        {
            var bytes = new List<byte>();
            do
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                bytes.Add(value == 0 ? b : (byte)(b | 0x80));
            }
            while (value != 0);
            return [.. bytes];
        }
        // ValueInfo: name, type { tensor_type { elem_type = FLOAT, shape { dim { dim_value = 1 } } } }
        static byte[] Value(string name) =>
            [.. Text(1, name), .. Field(2, Field(1, [.. Number(1, 1), .. Field(2, Field(1, Number(1, 1)))]))];

        byte[] node = [.. Text(1, "x"), .. Text(2, "y"), .. Text(4, "Identity")];
        byte[] graph = [.. Field(1, node), .. Text(2, "g"), .. Field(11, Value("x")), .. Field(12, Value("y"))];
        return [.. Number(1, 7), .. Field(7, graph), .. Field(8, Number(2, 13))];
    }
}
