using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ErgoStravaLogger;

/// <summary>Writes a GPS-free cycling activity in Garmin Training Center XML v2.</summary>
public static class TcxWriter
{
    private static readonly XNamespace Tcx = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
    private static readonly XNamespace Activity = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static async Task WriteAsync(string path, IReadOnlyList<TrainingSample> samples, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("A TCX activity requires at least one training sample.", nameof(samples));

        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (sample.PowerWatts is < 0 or > ushort.MaxValue || sample.CadenceRpm is < 0 or > byte.MaxValue ||
                sample.HeartRateBpm is < 1 or > byte.MaxValue ||
                !double.IsFinite(sample.SpeedMetersPerSecond) || sample.SpeedMetersPerSecond < 0 ||
                !double.IsFinite(sample.DistanceMeters) || sample.DistanceMeters < 0)
                throw new ArgumentException($"Training sample {i} contains an invalid measurement.", nameof(samples));
            if (i > 0 && (sample.Timestamp < samples[i - 1].Timestamp || sample.DistanceMeters < samples[i - 1].DistanceMeters))
                throw new ArgumentException("Training samples must have increasing timestamps and cumulative session distance.", nameof(samples));
        }

        var first = samples[0];
        var totalSeconds = RecordedSeconds(samples);
        var heartRates = samples.Where(sample => sample.HeartRateBpm.HasValue).Select(sample => sample.HeartRateBpm!.Value).ToArray();
        var lap = new XElement(Tcx + "Lap", new XAttribute("StartTime", Utc(first.Timestamp)),
            new XElement(Tcx + "TotalTimeSeconds", Number(totalSeconds)),
            new XElement(Tcx + "DistanceMeters", Number(samples[^1].DistanceMeters)),
            new XElement(Tcx + "MaximumSpeed", Number(samples.Max(sample => sample.SpeedMetersPerSecond))),
            // No metabolic calorie measurement is available from the recorded sample model.
            new XElement(Tcx + "Calories", 0));
        if (heartRates.Length > 0)
        {
            lap.Add(new XElement(Tcx + "AverageHeartRateBpm", new XElement(Tcx + "Value", (int)Math.Round(heartRates.Average()))));
            lap.Add(new XElement(Tcx + "MaximumHeartRateBpm", new XElement(Tcx + "Value", heartRates.Max())));
        }
        lap.Add(new XElement(Tcx + "Intensity", "Active"),
            new XElement(Tcx + "Cadence", (int)Math.Round(samples.Average(sample => sample.CadenceRpm))),
            new XElement(Tcx + "TriggerMethod", "Manual"));

        var track = new XElement(Tcx + "Track");
        foreach (var sample in samples)
        {
            ct.ThrowIfCancellationRequested();
            if (sample.StartsNewTrack && track.HasElements)
            {
                lap.Add(track);
                track = new XElement(Tcx + "Track");
            }
            var point = new XElement(Tcx + "Trackpoint",
                new XElement(Tcx + "Time", Utc(sample.Timestamp)),
                new XElement(Tcx + "DistanceMeters", Number(sample.DistanceMeters)));
            if (sample.HeartRateBpm is int heartRate)
                point.Add(new XElement(Tcx + "HeartRateBpm", new XElement(Tcx + "Value", heartRate)));
            point.Add(new XElement(Tcx + "Cadence", sample.CadenceRpm),
                new XElement(Tcx + "Extensions", new XElement(Activity + "TPX",
                    new XElement(Activity + "Speed", Number(sample.SpeedMetersPerSecond)),
                    new XElement(Activity + "Watts", sample.PowerWatts))));
            track.Add(point);
        }
        lap.Add(track);
        lap.Add(new XElement(Tcx + "Extensions", new XElement(Activity + "LX",
            new XElement(Activity + "AvgSpeed", Number(totalSeconds > 0 ? samples[^1].DistanceMeters / totalSeconds : 0)),
            new XElement(Activity + "AvgWatts", (int)Math.Round(TimeWeightedPower(samples))),
            new XElement(Activity + "MaxWatts", samples.Max(sample => sample.PowerWatts)))));

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(Tcx + "TrainingCenterDatabase",
                new XAttribute(XNamespace.Xmlns + "ae", Activity),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                new XAttribute(Xsi + "schemaLocation", $"{Tcx} https://www8.garmin.com/xmlschemas/TrainingCenterDatabasev2.xsd {Activity} https://www8.garmin.com/xmlschemas/ActivityExtensionv2.xsd"),
                new XElement(Tcx + "Activities", new XElement(Tcx + "Activity",
                    new XAttribute("Sport", "Biking"), new XElement(Tcx + "Id", Utc(first.Timestamp)), lap))));

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Async = true, Indent = true, Encoding = new UTF8Encoding(false) });
                await document.SaveAsync(writer, ct);
                await writer.FlushAsync();
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string Utc(DateTimeOffset timestamp) => timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static string Number(double value) => XmlConvert.ToString(value);

    /// <summary>Returns recorded time while excluding gaps before the start of a resumed track.</summary>
    public static double RecordedSeconds(IReadOnlyList<TrainingSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double seconds = 0;
        for (var i = 1; i < samples.Count; i++)
            if (!samples[i].StartsNewTrack)
                seconds += Math.Max(0, (samples[i].Timestamp - samples[i - 1].Timestamp).TotalSeconds);
        return seconds;
    }

    private static double TimeWeightedPower(IReadOnlyList<TrainingSample> samples)
    {
        var seconds = RecordedSeconds(samples);
        if (seconds <= 0)
            return samples.Average(sample => sample.PowerWatts);
        double work = 0;
        for (var i = 1; i < samples.Count; i++)
            if (!samples[i].StartsNewTrack)
                work += (samples[i].Timestamp - samples[i - 1].Timestamp).TotalSeconds *
                        (samples[i - 1].PowerWatts + samples[i].PowerWatts) / 2d;
        return work / seconds;
    }
}
