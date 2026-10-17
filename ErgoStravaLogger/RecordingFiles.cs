using System.Text.Json;

namespace ErgoStravaLogger;

public static class RecordingFiles
{
    public static long? ActivityId(string file)
    {
        try
        {
            if (!File.Exists(file + ".upload.json")) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file + ".upload.json"));
            if (doc.RootElement.TryGetProperty("ActivityId", out var id) && id.TryGetInt64(out var number) && number > 0) return number;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return null;
    }

    public static bool IsUploaded(string file) => ActivityId(file).HasValue;

    public static object[] List(string directory) => Directory.EnumerateFiles(directory, "*.tcx")
        .Select(path => new FileInfo(path)).OrderByDescending(info => info.LastWriteTimeUtc).Take(30)
        .Select(info => (object)new { name = info.Name, bytes = info.Length, createdAt = info.LastWriteTimeUtc,
            uploaded = IsUploaded(info.FullName), activityId = ActivityId(info.FullName), error = (string?)null }).ToArray();
}
