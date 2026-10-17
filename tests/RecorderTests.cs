using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using ErgoStravaLogger;

namespace ErgoStravaLogger.Tests;

public static class RecorderTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-12T12:00:00Z", CultureInfo.InvariantCulture);
    private static readonly XNamespace Tcx = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";

    public static async Task RunAsync()
    {
        await TestPedallingStartsRecordingAsync();
        await TestDistanceBaselineAndCounterResetAsync();
        await TestPausesAndIdleTrimmingAsync();
        await TestDefaultPauseAndDistanceBoundaryAsync();
        await TestCadenceGapWithoutIdleTickAsync();
        await TestMinimumDurationAsync();
        await TestTruncatedJournalRecoveryAsync();
        await TestCorruptMiddleJournalAsync();
        Console.WriteLine("Recorder tests passed.");
    }

    private static async Task TestPedallingStartsRecordingAsync()
    {
        using TemporaryDirectory directory = new();
        await using TrainingRecorder recorder = new(Config(directory.Path));
        await recorder.AddAsync(Sample(cadence: 0, power: 200), Start);
        Check(!recorder.IsRecording, "selected target watts must not start a workout without pedalling");
        Equal(0, Directory.GetFiles(directory.Path).Length, "standing still creates no journal");
        await recorder.AddAsync(Sample(cadence: 65, power: 0), Start.AddSeconds(1));
        Check(recorder.IsRecording, "pedalling starts a workout even with zero watts");
        Equal(1, ReadJournal(directory.Path).Length, "first pedalling sample is journalled");
    }

    private static async Task TestDistanceBaselineAndCounterResetAsync()
    {
        using TemporaryDirectory directory = new();
        await using TrainingRecorder recorder = new(Config(directory.Path));
        await recorder.AddAsync(Sample(distance: 5000), Start);
        await recorder.AddAsync(Sample(distance: 5100), Start.AddSeconds(10));
        await recorder.AddAsync(Sample(distance: 5), Start.AddSeconds(20));
        await recorder.AddAsync(Sample(distance: 20), Start.AddSeconds(30));
        Equal(2, Directory.GetFiles(directory.Path, "*.jsonl").Length, "a lower device counter starts a separate training even if it has already advanced above zero");
        string previousExport = Directory.GetFiles(directory.Path, "*.tcx").Single();
        Equal(100d, double.Parse(XDocument.Load(previousExport).Descendants(Tcx + "DistanceMeters").First().Value,
            CultureInfo.InvariantCulture), "the completed training keeps its original session distance");
        TrainingSample[] journal = ReadJournal(directory.Path, newest: true);
        Equal(0d, journal[0].DistanceMeters, "reset establishes a new distance baseline");
        Equal(15d, journal[1].DistanceMeters, "new training distance is independent of the previous counter");
        string? export = await recorder.FinishAsync();
        Check(export is not null, "distance session exports");
        Equal(15d, double.Parse(XDocument.Load(export!).Descendants(Tcx + "Lap").Single().Element(Tcx + "DistanceMeters")!.Value,
            CultureInfo.InvariantCulture), "the reset training exports as its own activity");
    }

    private static async Task TestPausesAndIdleTrimmingAsync()
    {
        using TemporaryDirectory directory = new();
        LoggerConfig config = Config(directory.Path);
        config.IdleSeconds = 10;
        await using TrainingRecorder recorder = new(config);
        await recorder.AddAsync(Sample(distance: 1000), Start);
        await recorder.AddAsync(Sample(cadence: 0, distance: 1050), Start.AddSeconds(5));
        await recorder.FinishIfIdleAsync(Start.AddSeconds(9));
        Check(recorder.IsRecording, "a brief pause keeps the workout open");
        await recorder.AddAsync(Sample(distance: 1100), Start.AddSeconds(9));
        await recorder.AddAsync(Sample(cadence: 0, distance: 1100), Start.AddSeconds(12));
        await recorder.FinishIfIdleAsync(Start.AddSeconds(18));
        Check(recorder.IsRecording, "idle countdown resets when pedalling resumes");
        await recorder.FinishIfIdleAsync(Start.AddSeconds(19));
        Check(!recorder.IsRecording, "idle deadline closes the workout");
        string export = Directory.GetFiles(directory.Path, "*.tcx").Single();
        XDocument xml = XDocument.Load(export);
        XElement[] points = xml.Descendants(Tcx + "Trackpoint").ToArray();
        Equal(3, points.Length, "internal pause retained and trailing idle trimmed");
        Equal("0", points[1].Element(Tcx + "Cadence")!.Value, "pause sample remains in the TCX");
        Equal(9d, double.Parse(xml.Descendants(Tcx + "TotalTimeSeconds").Single().Value, CultureInfo.InvariantCulture), "idle tail excluded from duration");

        await recorder.AddAsync(Sample(distance: 2000), Start.AddSeconds(30));
        Equal(0d, ReadJournal(directory.Path, newest: true)[0].DistanceMeters, "next session has its own distance baseline");
        Check(recorder.IsRecording, "pedalling after idle creates a new session");
    }

    private static async Task TestDefaultPauseAndDistanceBoundaryAsync()
    {
        using TemporaryDirectory directory = new();
        LoggerConfig config = new() { OutputDirectory = directory.Path, MinimumTrainingSeconds = 0 };
        Equal(15, config.PauseSeconds, "the default cadence pause threshold is 15 seconds");
        Equal(0, config.IdleSeconds, "the default session has no time-based finish threshold");
        await using TrainingRecorder recorder = new(config);
        await recorder.AddAsync(Sample(distance: 1000), Start);
        await recorder.AddAsync(Sample(cadence: 0, distance: 1050), Start.AddSeconds(5));
        await recorder.FinishIfIdleAsync(Start.AddSeconds(14));
        Check(recorder.IsRecording, "a pause shorter than 15 seconds keeps the session open");
        await recorder.AddAsync(Sample(distance: 1100), Start.AddSeconds(14));
        await recorder.AddAsync(Sample(cadence: 0, power: 200, distance: 1100), Start.AddSeconds(15));
        await recorder.FinishIfIdleAsync(Start.AddSeconds(29).AddMilliseconds(-1));
        Check(recorder.IsRecording && !recorder.IsPaused, "resumed cadence resets the full 15-second pause deadline");
        await recorder.FinishIfIdleAsync(Start.AddSeconds(29));
        Check(recorder.IsRecording && recorder.IsPaused, "exactly 15 seconds pauses writing while keeping the session open");
        Equal(0, Directory.GetFiles(directory.Path, "*.tcx").Length, "a 15-second pause does not export the activity");
        Equal(3, ReadJournal(directory.Path).Length, "a long pause drops its pending zero-cadence tail");
        int journalSamples = ReadJournal(directory.Path).Length;
        await recorder.AddAsync(Sample(cadence: 0, power: 200, distance: 1100), Start.AddSeconds(30));
        await recorder.FinishIfIdleAsync(Start.AddSeconds(39));
        await recorder.AddAsync(Sample(cadence: 0, power: 200, distance: 1100), Start.AddSeconds(39));
        Check(recorder.IsRecording && recorder.IsPaused, "selected watts with zero cadence cannot resume a paused session");
        Equal(1, Directory.GetFiles(directory.Path, "*.jsonl").Length, "continued idle creates no additional journal");
        Equal(journalSamples, ReadJournal(directory.Path).Length, "continued idle writes no more samples");

        await recorder.AddAsync(Sample(distance: 1120), Start.AddSeconds(100));
        Check(recorder.IsRecording && !recorder.IsPaused, "cadence with the same device counter resumes the same session");
        Equal(1, Directory.GetFiles(directory.Path, "*.jsonl").Length, "resuming a pause uses the original journal");
        TrainingSample[] resumed = ReadJournal(directory.Path);
        Equal(4, resumed.Length, "resumption writes only the new pedalling sample after the pause");
        Equal(100d, resumed[^1].DistanceMeters, "resumption preserves session distance and excludes distance reported during the paused gap");
        Check(resumed[^1].StartsNewTrack, "resuming a long pause starts a new track within the same activity");
        await recorder.AddAsync(Sample(distance: 1130), Start.AddSeconds(101));
        await recorder.AddAsync(Sample(cadence: 0, distance: 1130), Start.AddSeconds(102));
        await recorder.FinishIfIdleAsync(Start.AddSeconds(116));
        Check(recorder.IsPaused, "the resumed session can pause again after 15 seconds");
        await recorder.FinishIfIdleAsync(Start.AddSeconds(3601));
        Check(recorder.IsRecording && recorder.IsPaused, "even an hour-long pause keeps the session open until the distance resets");
        await recorder.AddAsync(Sample(cadence: 0, power: 200, distance: 0), Start.AddSeconds(3602));
        Check(!recorder.IsRecording, "a distance reset while paused completes the old training without starting an idle one");
        string firstExport = Directory.GetFiles(directory.Path, "*.tcx").Single();
        XDocument firstXml = XDocument.Load(firstExport);
        XElement[] firstPoints = firstXml.Descendants(Tcx + "Trackpoint").ToArray();
        Equal(5, firstPoints.Length, "short pauses stay in the activity while long pauses and the final tail are omitted");
        Equal("0", firstPoints[1].Element(Tcx + "Cadence")!.Value, "the short internal pause remains in the finished activity");
        TrainingSample[] firstJournal = ReadJournal(directory.Path);
        Equal(0, firstJournal[1].PowerWatts, "a short zero-cadence pause never records selected target watts as pedalling power");
        Equal(0d, firstJournal[1].SpeedMetersPerSecond, "a short zero-cadence pause records zero speed");
        Equal(2, firstXml.Descendants(Tcx + "Track").Count(), "a long pause separates tracks without splitting the activity");
        Equal(15d, double.Parse(firstXml.Descendants(Tcx + "TotalTimeSeconds").Single().Value,
            CultureInfo.InvariantCulture), "long pauses and the final waiting period do not extend active training duration");
        Equal(110d, double.Parse(firstXml.Descendants(Tcx + "Lap").Single().Element(Tcx + "DistanceMeters")!.Value,
            CultureInfo.InvariantCulture), "resumed pedalling keeps session distance while excluding the paused gap");

        await recorder.AddAsync(Sample(cadence: 0, power: 200, distance: 0), Start.AddSeconds(3603));
        Check(!recorder.IsRecording, "zero cadence after a distance reset does not start another session");
        await recorder.AddAsync(Sample(distance: 1), Start.AddSeconds(3604));
        Check(recorder.IsRecording && !recorder.IsPaused, "new cadence after a distance reset starts a separate session");
        Equal(2, Directory.GetFiles(directory.Path, "*.jsonl").Length, "resumed cadence creates a separate journal");
        Equal(0d, ReadJournal(directory.Path, newest: true).Single().DistanceMeters,
            "resumed cadence establishes a fresh distance baseline");
        await recorder.AddAsync(Sample(distance: 11), Start.AddSeconds(3605));
        string secondExport = (await recorder.FinishAsync())!;
        Check(secondExport != firstExport, "resumed cadence exports a separate activity");
        Equal(2, XDocument.Load(secondExport).Descendants(Tcx + "Trackpoint").Count(),
            "the new activity contains only resumed pedalling samples");
    }

    private static async Task TestCadenceGapWithoutIdleTickAsync()
    {
        using TemporaryDirectory directory = new();
        LoggerConfig config = new() { OutputDirectory = directory.Path, MinimumTrainingSeconds = 0 };
        await using TrainingRecorder recorder = new(config);
        await recorder.AddAsync(Sample(distance: 1000), Start);
        await recorder.AddAsync(Sample(distance: 1005), Start.AddSeconds(1));
        await recorder.AddAsync(Sample(cadence: 0, distance: 1005), Start.AddSeconds(2));
        await recorder.AddAsync(Sample(distance: 1010), Start.AddSeconds(16));
        Equal(3, ReadJournal(directory.Path).Length,
            "a cadence sample after the pause deadline drops pending idle even without a timer tick");
        Equal(1, Directory.GetFiles(directory.Path, "*.jsonl").Length,
            "a gap at the pause deadline still resumes the same session");
        await recorder.AddAsync(Sample(distance: 2000), Start.AddSeconds(2416));
        Equal(1, Directory.GetFiles(directory.Path, "*.jsonl").Length,
            "resuming after more than 15 minutes keeps the training when its distance counter did not reset");
        Equal(0, Directory.GetFiles(directory.Path, "*.tcx").Length, "a long pause alone never completes the training");
        Check(ReadJournal(directory.Path)[^1].StartsNewTrack, "a long connection gap resumes in a new track of the same activity");
        await recorder.AddAsync(Sample(distance: 5), Start.AddSeconds(2417));
        Equal(2, Directory.GetFiles(directory.Path, "*.jsonl").Length,
            "a reset during active cadence splits the training even without a timer tick");
        Equal(1, Directory.GetFiles(directory.Path, "*.tcx").Length,
            "the reset exports the previous session before writing new-training cadence");
        Equal(0d, ReadJournal(directory.Path, newest: true).Single().DistanceMeters,
            "the reset establishes the new training's distance baseline");
    }

    private static async Task TestMinimumDurationAsync()
    {
        using TemporaryDirectory directory = new();
        LoggerConfig config = Config(directory.Path);
        config.MinimumTrainingSeconds = 10;
        await using TrainingRecorder recorder = new(config);
        await recorder.AddAsync(Sample(), Start);
        await recorder.AddAsync(Sample(distance: 10), Start.AddSeconds(9));
        Equal<string?>(null, await recorder.FinishAsync(), "short session is not exported");
        Equal(0, Directory.GetFiles(directory.Path, "*.tcx").Length, "minimum duration prevents TCX upload candidates");
        Equal(1, Directory.GetFiles(directory.Path, "*.jsonl.discarded").Length, "short workout journal is preserved");
        await recorder.AddAsync(Sample(), Start.AddSeconds(20));
        await recorder.AddAsync(Sample(distance: 10), Start.AddSeconds(30));
        Check(await recorder.FinishAsync() is not null, "exactly the configured minimum duration exports");

        config.MinimumTrainingSeconds = 0;
        await recorder.AddAsync(Sample(), Start.AddSeconds(40));
        Equal<string?>(null, await recorder.FinishAsync(), "a one-point session is not a TCX workout");
    }

    private static async Task TestTruncatedJournalRecoveryAsync()
    {
        using TemporaryDirectory directory = new();
        string path = System.IO.Path.Combine(directory.Path, "training-crash.jsonl");
        TrainingSample[] samples = new TrainingSample[]
        {
            JournalSample(0, cadence: 80, distance: 0),
            JournalSample(10, cadence: 0, distance: 100),
            JournalSample(20, cadence: 85, distance: 200),
            JournalSample(25, cadence: 0, distance: 200)
        };
        string text = string.Join('\n', samples.Select(sample => JsonSerializer.Serialize(sample))) + "\n{\"Timestamp\":\"2026";
        await File.WriteAllTextAsync(path, text);
        await using TrainingRecorder recovered = new(Config(directory.Path));
        await recovered.RecoverAsync();
        string export = System.IO.Path.ChangeExtension(path, ".tcx");
        Check(File.Exists(export), "complete journal lines recover after a truncated final line");
        Equal(3, XDocument.Load(export).Descendants(Tcx + "Trackpoint").Count(), "recovery trims the trailing idle sample");
        Check(File.Exists(path), "source journal is preserved after recovery");
        await recovered.RecoverAsync();
        Equal(1, Directory.GetFiles(directory.Path, "*.tcx").Length, "recovery does not create duplicate exports");
    }

    private static async Task TestCorruptMiddleJournalAsync()
    {
        using TemporaryDirectory directory = new();
        string path = System.IO.Path.Combine(directory.Path, "training-corrupt.jsonl");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(JournalSample(0)) + "\n{broken middle}\n" +
            JsonSerializer.Serialize(JournalSample(20)) + "\n");
        await using TrainingRecorder recorder = new(Config(directory.Path));
        try { await recorder.RecoverAsync(); }
        catch (JsonException)
        {
            Check(!File.Exists(System.IO.Path.ChangeExtension(path, ".tcx")), "corrupt interior records must not silently export incomplete data");
            return;
        }
        throw new Exception("Expected JsonException for a corrupt interior journal line.");
    }

    private static LoggerConfig Config(string output) => new()
    {
        OutputDirectory = output, MinimumTrainingSeconds = 0, IdleSeconds = 120
    };

    private static DeviceSample Sample(int cadence = 80, int power = 150, double distance = 0) =>
        new(power, cadence, 140, cadence > 0 ? 7.5 : 0, distance, TimeSpan.Zero);

    private static TrainingSample JournalSample(int seconds, int cadence = 80, double distance = 0) =>
        new(Start.AddSeconds(seconds), 150, cadence, 140, cadence > 0 ? 7.5 : 0, distance);

    private static TrainingSample[] ReadJournal(string directory, bool newest = false)
    {
        string[] paths = Directory.GetFiles(directory, "*.jsonl");
        string path = newest ? paths.Order(StringComparer.Ordinal).Last() : paths.Single();
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        List<TrainingSample> result = [];
        while (reader.ReadLine() is string line)
            result.Add(JsonSerializer.Deserialize<TrainingSample>(line)!);
        return result.ToArray();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception($"Assertion failed: {message}");
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ergostrava-recorder-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
