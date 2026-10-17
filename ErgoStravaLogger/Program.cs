using System.Runtime.InteropServices;

namespace ErgoStravaLogger;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(arg => arg is "--help" or "-h") || args.FirstOrDefault() == "help")
        {
            Console.WriteLine("""
                Daum Premium 8i → Strava
                  web             Webinterface und automatische Aufzeichnung (Standard)
                  record          Aufzeichnung ohne Webinterface
                  diagnose        Softwareversion und einen Messdatensatz abfragen
                  simulate        TCX in data/simulation erzeugen, ohne Gerät/Upload
                  upload-pending  Lokale TCX-Dateien senden / Verarbeitung prüfen
                  auth-url        Strava-Freigabe-URL ausgeben
                  exchange-code   OAuth-Code als Argument oder STRAVA_AUTH_CODE speichern
                Optionen: --config <Pfad>, --duration <Sekunden>, --no-device, --help
                """);
            return 0;
        }
        try
        {
            var configPath = Environment.GetEnvironmentVariable("ERGO_CONFIG") ?? DefaultConfigPath();
            var positional = new List<string>();
            int? duration = null;
            var noDevice = false;
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--config" && index + 1 < args.Length) configPath = args[++index];
                else if (args[index] == "--duration" && index + 1 < args.Length)
                {
                    if (!int.TryParse(args[++index], out var seconds) || seconds < 1)
                        throw new ArgumentException("--duration benötigt eine positive Sekundenzahl.");
                    duration = seconds;
                }
                else if (args[index] == "--no-device") noDevice = true;
                else if (args[index].StartsWith('-')) throw new ArgumentException($"Unbekannte Option: {args[index]}");
                else positional.Add(args[index]);
            }
            var command = positional.FirstOrDefault() ?? "web";
            if (command is not ("web" or "record" or "diagnose" or "simulate" or "upload-pending" or "auth-url" or "exchange-code"))
                throw new ArgumentException($"Unbekannter Befehl: {command}");
            if (positional.Count > (command == "exchange-code" ? 2 : 1)) throw new ArgumentException("Zu viele Argumente.");
            using var shutdown = new CancellationTokenSource();
            if (duration.HasValue) shutdown.CancelAfter(TimeSpan.FromSeconds(duration.Value));
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
            using var signal = OperatingSystem.IsLinux()
                ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); })
                : null;
            var config = LoggerConfig.Load(configPath);
            if (noDevice) config.RecordingEnabled = false;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var strava = new StravaClient(config.Strava, http);
            if (command == "auth-url") { Console.WriteLine(strava.GetAuthorizationUrl()); return 0; }
            if (command == "diagnose")
            {
                await using var device = new DaumClient(config);
                Console.WriteLine($"Softwareversion: {await device.ReadVersionAsync(shutdown.Token)}");
                Console.WriteLine($"Messwerte: {await device.ReadTrainingAsync(shutdown.Token)}");
                return 0;
            }

            Directory.CreateDirectory(config.OutputDirectory);
            using var directoryLock = new FileStream(Path.Combine(config.OutputDirectory, ".logger.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (command == "exchange-code")
            {
                var code = positional.ElementAtOrDefault(1) ?? Environment.GetEnvironmentVariable("STRAVA_AUTH_CODE");
                if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("OAuth-Code als Argument oder STRAVA_AUTH_CODE setzen.");
                await strava.ExchangeCodeAsync(code, shutdown.Token);
                Console.WriteLine("Strava verbunden; Tokens lokal gespeichert.");
                return 0;
            }
            var runtime = new LoggerRuntime(config, strava);
            if (command == "web") { await LoggerWeb.RunAsync(config, strava, runtime, shutdown.Token); return 0; }
            if (command == "upload-pending") return await runtime.UploadPendingAsync(shutdown.Token) ? 0 : 1;
            if (command == "simulate")
            {
                config.OutputDirectory = Path.Combine(config.OutputDirectory, "simulation");
                config.MinimumTrainingSeconds = 0;
                await using var simulation = new TrainingRecorder(config);
                var start = DateTimeOffset.UtcNow;
                for (var i = 0; i < 12; i++)
                    await simulation.AddAsync(new DeviceSample(150 + i, 80, 120 + i, 7, i * 7, TimeSpan.FromSeconds(i)), start.AddSeconds(i));
                await simulation.FinishAsync();
                Console.WriteLine("Simulation abgeschlossen; kein Strava-Upload.");
                return 0;
            }
            await runtime.RunAsync(shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"Fehler: {ex.Message}"); return 1; }
    }

    private static string DefaultConfigPath()
    {
        foreach (var path in new[] { "config.local.json", "config.json", "ErgoStravaLogger/config.local.json", "ErgoStravaLogger/config.json",
            Path.Combine(AppContext.BaseDirectory, "config.local.json"), Path.Combine(AppContext.BaseDirectory, "config.json") })
            if (File.Exists(path)) return path;
        throw new FileNotFoundException("Konfiguration nicht gefunden. Nutze --config <Pfad>.");
    }
}
