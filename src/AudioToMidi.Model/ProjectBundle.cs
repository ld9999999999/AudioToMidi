using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioToMidi.Model;

/// <summary>
/// A project bundle is a folder, by convention named <c>song.a2m</c>:
/// <code>
/// song.a2m/
///   analysis.json     the <see cref="AnalysisDocument"/>
///   song.mid          all parts as MIDI, in time with the audio
///   stems/*.wav       the separated audio the parts were transcribed from
/// </code>
/// </summary>
public static class ProjectBundle
{
    public const string Extension = ".a2m";
    public const string AnalysisFileName = "analysis.json";
    public const string StemsFolder = "stems";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(AnalysisDocument document) => JsonSerializer.Serialize(document, JsonOptions);

    public static AnalysisDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<AnalysisDocument>(json, JsonOptions)
                       ?? throw new InvalidDataException("The analysis file is empty.");
        if (document.SchemaVersion > AnalysisDocument.CurrentSchemaVersion)
            throw new NotSupportedException(
                $"The analysis was written by a newer version (format {document.SchemaVersion}; this one reads up to {AnalysisDocument.CurrentSchemaVersion}).");
        return document;
    }

    /// <summary>Read <c>analysis.json</c> from a bundle folder.</summary>
    public static AnalysisDocument Load(string bundleFolder) =>
        Deserialize(File.ReadAllText(Path.Combine(bundleFolder, AnalysisFileName)));

    /// <summary>Write <c>analysis.json</c> into a bundle folder, and create the folder if needed.</summary>
    public static void Save(string bundleFolder, AnalysisDocument document)
    {
        Directory.CreateDirectory(bundleFolder);
        File.WriteAllText(Path.Combine(bundleFolder, AnalysisFileName), Serialize(document));
    }

    /// <summary>Get the full path of a stem's audio file. Throw if the path points outside the bundle.</summary>
    public static string StemPath(string bundleFolder, StemFile stem)
    {
        var root = Path.GetFullPath(bundleFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, stem.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException($"Stem path \"{stem.Path}\" is outside the bundle.");
        return path;
    }
}
