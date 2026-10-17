using System.Text.Json;

namespace ErgoStravaLogger;

/// <summary>Starts on pedalling, pauses on zero cadence and splits sessions when the cockpit distance resets.</summary>
public sealed class TrainingRecorder(LoggerConfig config) : IAsyncDisposable
{
    private readonly List<TrainingSample> samples = [];
    private readonly List<TrainingSample> pendingIdle = [];
    private StreamWriter? journal;
    private string? journalPath;
    private DateTimeOffset lastActive;
    private int lastActiveIndex;
    private double previousDeviceDistance;
    private double sessionDistance;

    public bool IsRecording => journal is not null;
    public bool IsPaused { get; private set; }

    public async Task AddAsync(DeviceSample data, DateTimeOffset timestamp)
    {
        // A reset may already have advanced a few meters by the time it is sampled.
        if (IsRecording && data.DistanceMeters < previousDeviceDistance)
        {
            Console.WriteLine("Distanzzähler zurückgesetzt: bisheriges Training wird abgeschlossen.");
            await FinishAsync();
        }
        // Also apply deadlines when a connection resumes without an intervening timer tick.
        await FinishIfIdleAsync(timestamp);
        // Reported power can be the selected target even while standing still.
        var active = data.CadenceRpm > 0;
        var startsNewTrack = false;
        if (!IsRecording)
        {
            if (!active) return;
            Directory.CreateDirectory(config.OutputDirectory);
            journalPath = Path.Combine(config.OutputDirectory,
                $"training-{timestamp.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            journal = new StreamWriter(new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            previousDeviceDistance = data.DistanceMeters;
            sessionDistance = 0;
            IsPaused = false;
            Console.WriteLine($"Training gestartet: {Path.GetFileNameWithoutExtension(journalPath)}");
        }
        else
        {
            startsNewTrack = IsPaused && active;
            // Ignore distance accumulated during a paused segment and tolerate cockpit counter resets.
            if (!IsPaused)
            {
                var delta = data.DistanceMeters - previousDeviceDistance;
                if (delta >= 0) sessionDistance += delta;
            }
            previousDeviceDistance = data.DistanceMeters;
        }

        var sample = new TrainingSample(timestamp, active ? data.PowerWatts : 0, data.CadenceRpm,
            data.HeartRateBpm, active ? data.SpeedMetersPerSecond : 0, sessionDistance, startsNewTrack);
        if (!active)
        {
            // Buffer brief pauses; a real pause never reaches the journal or exported measurements.
            if (!IsPaused) pendingIdle.Add(sample);
            return;
        }
        foreach (var idle in pendingIdle)
        {
            samples.Add(idle);
            await journal!.WriteLineAsync(JsonSerializer.Serialize(idle));
        }
        pendingIdle.Clear();
        if (IsPaused) Console.WriteLine("Training fortgesetzt.");
        IsPaused = false;
        samples.Add(sample);
        lastActive = timestamp;
        lastActiveIndex = samples.Count - 1;
        await journal!.WriteLineAsync(JsonSerializer.Serialize(sample));
        await journal.FlushAsync();
    }

    public async Task FinishIfIdleAsync(DateTimeOffset now)
    {
        if (!IsRecording) return;
        var idleSeconds = (now - lastActive).TotalSeconds;
        if (config.IdleSeconds > 0 && idleSeconds >= config.IdleSeconds) await FinishAsync();
        else if (idleSeconds >= config.PauseSeconds && !IsPaused)
        {
            pendingIdle.Clear();
            sessionDistance = samples[lastActiveIndex].DistanceMeters;
            IsPaused = true;
            Console.WriteLine("Training pausiert: keine Kadenz.");
        }
    }

    public async Task<string?> FinishAsync()
    {
        if (journal is null) return null;
        await journal.DisposeAsync();
        journal = null;
        IsPaused = false;
        pendingIdle.Clear();
        // Keep pauses inside a workout but omit the idle timeout at the end.
        var finished = samples.Take(lastActiveIndex + 1).ToArray();
        samples.Clear();
        var path = journalPath!;
        journalPath = null;
        return await ExportAsync(path, finished);
    }

    public async Task RecoverAsync()
    {
        Directory.CreateDirectory(config.OutputDirectory);
        foreach (var path in Directory.EnumerateFiles(config.OutputDirectory, "*.jsonl"))
        {
            if (File.Exists(Path.ChangeExtension(path, ".tcx"))) continue;
            var recovered = new List<TrainingSample>();
            // Only an incomplete LAST line may be ignored after power loss.
            var lines = await File.ReadAllLinesAsync(path);
            for (var index = 0; index < lines.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index])) continue;
                try
                {
                    recovered.Add(JsonSerializer.Deserialize<TrainingSample>(lines[index])
                        ?? throw new JsonException("Leere Trainingszeile."));
                }
                catch (JsonException) when (index == lines.Length - 1)
                {
                    Console.WriteLine($"Unvollständige letzte Zeile in {Path.GetFileName(path)} übersprungen.");
                }
            }
            var lastPedalling = recovered.FindLastIndex(sample => sample.CadenceRpm > 0);
            if (lastPedalling >= 0) recovered.RemoveRange(lastPedalling + 1, recovered.Count - lastPedalling - 1);
            Console.WriteLine($"Wiederherstellung: {Path.GetFileName(path)}");
            await ExportAsync(path, recovered);
        }
    }

    private async Task<string?> ExportAsync(string source, IReadOnlyList<TrainingSample> recorded)
    {
        if (recorded.Count < 2 || TcxWriter.RecordedSeconds(recorded) < config.MinimumTrainingSeconds)
        {
            File.Move(source, source + ".discarded", overwrite: true);
            Console.WriteLine("Kurze Aufzeichnung gespeichert; Mindestdauer für TCX nicht erreicht.");
            return null;
        }
        var target = Path.ChangeExtension(source, ".tcx");
        await TcxWriter.WriteAsync(target + ".tmp", recorded);
        File.Move(target + ".tmp", target, overwrite: true);
        Console.WriteLine($"Training gespeichert: {target} ({recorded.Count} Messpunkte)");
        return target;
    }

    public async ValueTask DisposeAsync() => await FinishAsync();
}
