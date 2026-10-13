using DPUruNet;

public sealed class DetectorStatus {
    public long LastHeartbeat = Environment.TickCount64;
    public string State { get; set; } = "starting";
    public string? LastError { get; set; }
}

public sealed class DeteccionContinuaService : BackgroundService {
    private readonly BiometricsCache _cache;
    private readonly DurableEventQueue _queue;
    private readonly DetectorStatus _status;
    private readonly MotorOptions _options;
    private readonly Dictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal);
    public DeteccionContinuaService(BiometricsCache cache, DurableEventQueue queue, DetectorStatus status, MotorOptions options) {
        _cache = cache; _queue = queue; _status = status; _options = options;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        int backoff = 1000;
        DateTimeOffset nextReaderCheck = DateTimeOffset.MinValue;
        try {
            while (!stoppingToken.IsCancellationRequested) {
                try {
                    Interlocked.Exchange(ref _status.LastHeartbeat, Environment.TickCount64);
                    if (DateTimeOffset.UtcNow >= nextReaderCheck) {
                        await MotorBiometrico.EnsureReaderAsync(stoppingToken);
                        nextReaderCheck = DateTimeOffset.UtcNow.AddSeconds(5);
                    }
                    if (!MotorBiometrico.LectorConectado) {
                        _status.State = "waiting_reader";
                        await Task.Delay(backoff, stoppingToken);
                        backoff = Math.Min(backoff * 2, 10000);
                        nextReaderCheck = DateTimeOffset.MinValue;
                        continue;
                    }
                    backoff = 1000;
                    if (!_queue.CanAccept) {
                        _status.State = "queue_blocked";
                        _status.LastError = _queue.LastError ?? "Cola llena o dispositivo sin configurar.";
                        await Task.Delay(1000, stoppingToken);
                        continue;
                    }
                    if (!ClientPresence.Connected) {
                        _status.State = "waiting_screen";
                        await Task.Delay(250, stoppingToken);
                        continue;
                    }
                    var current = _cache.Snapshot;
                    if (!current.EstaCargada || current.Enabled.Count == 0) {
                        _status.State = "waiting_cache";
                        await Task.Delay(1000, stoppingToken);
                        continue;
                    }
                    // A manual enrolment/capture has priority over the continuous
                    // detector. Do not start another short USB capture while it is
                    // waiting for the reader lock.
                    if (MotorBiometrico.ManualCaptureRequested) {
                        _status.State = "enrolling";
                        await Task.Delay(100, stoppingToken);
                        continue;
                    }
                    _status.State = MotorBiometrico.Enrolando ? "enrolling" : "scanning";
                    var fingerprint = await MotorBiometrico.CapturarHuellaSilenciosaAsync(stoppingToken);
                    if (fingerprint == null) {
                        // Includes an occupied reader and normal timeouts; prevents a spin loop.
                        await Task.Delay(100, stoppingToken);
                        continue;
                    }
                    // A finger captured while the authorized screen disconnected is not queued.
                    if (!ClientPresence.Connected) continue;
                    // A member can be saved while capture waits for a finger. Compare against
                    // the latest complete cache, retaining the same snapshot for result lookup.
                    current = _cache.Snapshot;
                    if (!current.EstaCargada || current.Enabled.Count == 0) {
                        _status.State = "waiting_cache";
                        continue;
                    }
                    long identifyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    var identified = Comparison.Identify(fingerprint, 0, current.Enabled.Select(entry => entry.Template), MotorOptions.IdentificationThreshold, 1);
                    if (identified.ResultCode != Constants.ResultCode.DP_SUCCESS) {
                        _status.LastError = "No se pudo comparar la captura biométrica.";
                        await Task.Delay(500, stoppingToken);
                        continue;
                    }
                    if (identified.Indexes.Length == 0) continue;
                    string code = current.Enabled[identified.Indexes[0][0]].CodigoSocio;
                    var now = DateTimeOffset.UtcNow;
                    foreach (string expired in _cooldown.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray()) _cooldown.Remove(expired);
                    if (_cooldown.TryGetValue(code, out var until) && until > now) continue;
                    Console.WriteLine($"[MATCH V2] {DateTime.UtcNow:O} socio={code}; plantillas={current.Enabled.Count}; comparacion_ms={System.Diagnostics.Stopwatch.GetElapsedTime(identifyStarted).TotalMilliseconds:F1}");
                    // Persist first: no event is advertised or considered delivered before durable storage.
                    _queue.RegistrarMatch(new EventoHuella { CodigoSocio = code, FechaUtc = now.UtcDateTime });
                    _cooldown[code] = now.AddMilliseconds(_options.CooldownMs);
                    _status.LastError = null;
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) {
                    _status.LastError = ex is ProtocolException error ? error.Message : "Error de lectura. Se reintentará de forma controlada.";
                    _status.State = "error";
                    Console.WriteLine($"[DETECTOR V2] {DateTime.UtcNow:O} {ex.GetType().Name}; code={(ex is ProtocolException pe ? pe.Code : "READER_ERROR")}; {_status.LastError}");
                    await Task.Delay(backoff, stoppingToken);
                    backoff = Math.Min(backoff * 2, 10000);
                }
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _status.State = "stopped"; }
    }
}
