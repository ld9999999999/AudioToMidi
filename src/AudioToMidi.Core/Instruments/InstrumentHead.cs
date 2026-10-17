using System.Reflection;
using System.Text.Json;

namespace AudioToMidi.Core.Instruments;

/// <summary>
/// Multinomial logistic regression on pooled YAMNet embeddings. The trained weights are embedded as
/// instrument_head.json.
/// </summary>
public sealed class InstrumentHead
{
    public sealed class Model
    {
        public string[] Classes { get; set; } = [];
        public float[] Mean { get; set; } = [];
        public float[] Std { get; set; } = [];
        public float[][] Weights { get; set; } = [];
        public float[] Bias { get; set; } = [];

        /// <summary>"embedding" (1024), "scores" (521 log AudioSet scores) or "both" (concatenated).</summary>
        public string Features { get; set; } = "embedding";

        /// <summary>Weight of <see cref="InstrumentRecognizer.AudioSetPrior"/> in the final blend (0 = head only).</summary>
        public float PriorWeight { get; set; }

        /// <summary>Prior weight for bass tracks (AudioSet cannot tell synth bass from electric bass).</summary>
        public float BassPriorWeight { get; set; }

        public string Notes { get; set; } = "";
    }

    readonly Model _model;
    readonly int[] _classIndex; // head output -> InstrumentClasses.All index

    public InstrumentHead(Model model)
    {
        _model = model;
        _classIndex = model.Classes.Select(InstrumentClasses.IndexOf).ToArray();
        if (_classIndex.Any(i => i < 0)) throw new InvalidDataException("Instrument head has unknown classes.");
    }

    public static InstrumentHead? LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AudioToMidi.Core.instrument_head.json");
        if (stream is null) return null;
        return new InstrumentHead(JsonSerializer.Deserialize<Model>(stream)!);
    }

    /// <summary>Build the feature vector the head was trained on.</summary>
    public static float[] Features(string kind, float[] embedding, float[] scores)
    {
        var logScores = scores.Select(v => MathF.Log(v + 1e-4f)).ToArray();
        return kind switch
        {
            "scores" => logScores,
            "both" => [.. embedding, .. logScores],
            _ => embedding,
        };
    }

    /// <summary>Return probabilities indexed like <see cref="InstrumentClasses.All"/> (0 for classes the head lacks).</summary>
    public double[] Predict(float[] embedding, float[] scores, TrackRole role = TrackRole.Any)
    {
        var x = Features(_model.Features, embedding, scores);
        int c = _model.Classes.Length;
        var logits = new double[c];
        for (int k = 0; k < c; k++)
        {
            double z = _model.Bias[k];
            var w = _model.Weights[k];
            for (int i = 0; i < x.Length; i++)
                z += w[i] * ((x[i] - _model.Mean[i]) / _model.Std[i]);
            logits[k] = z;
        }
        double max = logits.Max(), sum = 0;
        for (int k = 0; k < c; k++) { logits[k] = Math.Exp(logits[k] - max); sum += logits[k]; }

        var result = new double[InstrumentClasses.All.Count];
        for (int k = 0; k < c; k++) result[_classIndex[k]] = logits[k] / sum;
        float priorWeight = role == TrackRole.Bass ? _model.BassPriorWeight : _model.PriorWeight;
        return Blend(result, InstrumentRecognizer.AudioSetPrior(scores), priorWeight);
    }

    /// <summary>Blend two distributions as a weighted geometric mean, then renormalise.</summary>
    public static double[] Blend(double[] head, double[] prior, double priorWeight)
    {
        if (priorWeight <= 0) return head;
        var r = head.Select((h, i) => Math.Exp((1 - priorWeight) * Math.Log(h + 1e-6) + priorWeight * Math.Log(prior[i] + 1e-6))).ToArray();
        double sum = r.Sum();
        return r.Select(v => v / sum).ToArray();
    }
}
