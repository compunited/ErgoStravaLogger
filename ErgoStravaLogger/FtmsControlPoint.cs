using System.Buffers.Binary;

namespace ErgoStravaLogger;

public sealed record FtmsSimulationParameters(double WindSpeedMetersPerSecond, double GradePercent,
    double RollingResistance, double WindResistance);

public interface ITrainerControl
{
    bool Enabled { get; }
    bool Available { get; }
    int MinimumPowerWatts { get; }
    int MaximumPowerWatts { get; }
    int PowerStepWatts { get; }
    Task<bool> SetTargetPowerAsync(int watts, CancellationToken ct);
    Task<bool> SetSimulationAsync(FtmsSimulationParameters parameters, CancellationToken ct);
    Task<bool> StopAsync(CancellationToken ct);
}

/// <summary>FTMS procedures share one control lease across all Direct Connect sockets.</summary>
public sealed class FtmsControlPoint(ITrainerControl trainerControl)
{
    public const byte Success = 1, NotSupported = 2, InvalidParameter = 3, OperationFailed = 4, ControlNotPermitted = 5;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _leaseLock = new();
    private long? _controller;

    public async Task<byte> ProcessAsync(long clientId, ReadOnlyMemory<byte> command, CancellationToken ct = default)
    {
        if (command.Length == 0) return InvalidParameter;
        byte opcode = command.Span[0];
        int expectedLength = opcode switch { 0 or 1 or 7 => 1, 5 => 3, 8 => 2, 0x11 => 7, _ => -1 };
        if (expectedLength < 0) return NotSupported;
        if (command.Length != expectedLength) return InvalidParameter;
        if (opcode == 8 && command.Span[1] is not (1 or 2)) return InvalidParameter;
        int watts = opcode == 5 ? BinaryPrimitives.ReadInt16LittleEndian(command.Span[1..]) : 0;
        if (opcode == 5 && (watts < trainerControl.MinimumPowerWatts || watts > trainerControl.MaximumPowerWatts))
            return InvalidParameter;

        await _gate.WaitAsync(ct);
        try
        {
            Task<bool> operation;
            lock (_leaseLock)
            {
                if (!trainerControl.Enabled) return ControlNotPermitted;
                if (opcode == 0)
                {
                    if (_controller.HasValue && _controller != clientId) return ControlNotPermitted;
                    if (!trainerControl.Available) return OperationFailed;
                    _controller = clientId;
                    return Success;
                }
                if (_controller != clientId) return ControlNotPermitted;
                if (opcode == 1)
                {
                    // Reset only virtual control settings. Daum distance/time counters belong to the recording.
                    _controller = null;
                    operation = CallAsync(() => trainerControl.StopAsync(ct));
                }
                else
                {
                    if (!trainerControl.Available) return OperationFailed;
                    // Start the async engine operation while lease revocation is excluded. Its synchronous
                    // entry captures the engine generation before Release can signal Stop.
                    operation = opcode switch
                    {
                        5 => CallAsync(() => trainerControl.SetTargetPowerAsync(watts, ct)),
                        7 => Task.FromResult(true), // The bike starts from pedalling.
                        8 => CallAsync(() => trainerControl.StopAsync(ct)),
                        0x11 => CallAsync(() => trainerControl.SetSimulationAsync(new FtmsSimulationParameters(
                            BinaryPrimitives.ReadInt16LittleEndian(command.Span[1..]) / 1000d,
                            BinaryPrimitives.ReadInt16LittleEndian(command.Span[3..]) / 100d,
                            command.Span[5] / 10000d, command.Span[6] / 100d), ct)),
                        _ => Task.FromResult(false)
                    };
                }
            }
            bool succeeded = await operation;
            // A late hardware acknowledgement cannot revive a revoked control session.
            if (opcode is not (1 or 8))
                lock (_leaseLock)
                    if (_controller != clientId) return ControlNotPermitted;
            return succeeded ? Success : OperationFailed;
        }
        finally { _gate.Release(); }
    }

    public async Task ReleaseAsync(long clientId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<bool> stopping;
        lock (_leaseLock)
        {
            if (_controller != clientId) return;
            _controller = null;
            // Do not wait for an in-flight FTMS procedure: Stop immediately cancels its virtual generation.
            stopping = CallAsync(() => trainerControl.StopAsync(deadline.Token));
        }
        try { await stopping.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) { }
    }

    private static async Task<bool> CallAsync(Func<Task<bool>> operation)
    {
        try { return await operation(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
}
