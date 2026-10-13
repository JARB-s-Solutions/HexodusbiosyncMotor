using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Exus;

public static class ClientPresence {
    private static int _connections;
    public static bool Connected => Volatile.Read(ref _connections) > 0;
    public static void Enter() => Interlocked.Increment(ref _connections);
    public static void Leave() => Interlocked.Decrement(ref _connections);
}
public sealed record DiagnosticsOptions(string PipeName);

// A separate, read-only local IPC channel. It never gives a desktop user a motor capability/key.
public sealed class MotorDiagnostics : BackgroundService {
    private readonly BiometricsCache _cache;
    private readonly DurableEventQueue _queue;
    private readonly DetectorStatus _detector;
    private readonly MotorProtocol _protocol;
    private readonly SiteConfiguration _site;
    private readonly MotorJournal _journal;
    private readonly DiagnosticsOptions _options;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    public MotorDiagnostics(BiometricsCache cache, DurableEventQueue queue, DetectorStatus detector, MotorProtocol protocol,
        SiteConfiguration site, MotorJournal journal, DiagnosticsOptions options) {
        _cache = cache; _queue = queue; _detector = detector; _protocol = protocol; _site = site; _journal = journal; _options = options;
    }
    public StatusSnapshot Snapshot() {
        var current = _cache.Snapshot;
        return new StatusSnapshot {
            StartedAt = _started, Configured = _protocol.IsConfigured, ReaderConnected = MotorBiometrico.LectorConectado,
            ReaderAgentConnected = MotorBiometrico.AuxiliarConectado,
            ReaderError = MotorBiometrico.UltimoError, Detector = _detector.State, ScreenConnected = ClientPresence.Connected,
            Fingerprints = current.Enabled.Count, Pending = _queue.Count, QueueReady = _queue.Ready,
            CacheStale = _cache.IsStale(current), LastSyncUtc = current.LastSyncUtc,
            LastEvent = _journal.LastEvent, LastEventAt = _journal.LastEventAt, FrontendUrl = _site.FrontendUrl, Logs = _journal.Tail()
        };
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await Task.WhenAll(Serve(stoppingToken), Watchdog(stoppingToken));
    }
    private async Task Serve(CancellationToken stop) {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        while (!stop.IsCancellationRequested) {
            try {
                using var pipe = NamedPipeServerStreamAcl.Create(_options.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 65536, security);
                await pipe.WaitForConnectionAsync(stop);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                // No commands are accepted. Every client gets a sanitized status snapshot and EOF.
                byte[] response = JsonSerializer.SerializeToUtf8Bytes(Snapshot());
                await pipe.WriteAsync(response, timeout.Token);
                await pipe.FlushAsync(timeout.Token);
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) {
                await Task.Delay(300, stop);
            }
        }
    }
    private async Task Watchdog(CancellationToken stop) {
        try {
            while (!stop.IsCancellationRequested) {
                await Task.Delay(TimeSpan.FromSeconds(10), stop);
                if (Environment.TickCount64 - Interlocked.Read(ref _detector.LastHeartbeat) > 90000) {
                    Console.WriteLine("[RECUPERACION] El lector no respondió durante 90 segundos. Reinicio controlado del servicio.");
                    // SCM recovery restarts the service. No pending events are discarded.
                    Environment.Exit(1);
                }
            }
        } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}

public sealed class MotorJournal : TextWriter {
    private readonly object _gate = new();
    private readonly Queue<string> _tail = new();
    private readonly string _path;
    private string _buffer = "";
    public override Encoding Encoding => Encoding.UTF8;
    public string LastEvent { get; private set; } = "Esperando la pantalla de asistencia.";
    public DateTimeOffset? LastEventAt { get; private set; }
    public MotorJournal(string directory) { Directory.CreateDirectory(directory); _path = Path.Combine(directory, "motor.log"); }
    public string[] Tail() { lock (_gate) return _tail.ToArray(); }
    public override void Write(char value) {
        lock (_gate) {
            if (value == '\n') { Commit(_buffer.TrimEnd('\r')); _buffer = ""; }
            else if (_buffer.Length < 4096) _buffer += value;
        }
    }
    public override void Write(string? value) { if (value != null) lock (_gate) foreach (char c in value) Write(c); }
    public override void WriteLine(string? value) { lock (_gate) { Write(value); Write('\n'); } }
    public static string Sanitize(string line) {
        line = Regex.Replace(line, @"socio=[^;\s]+", "socio=[oculto]", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"(?i)(DeviceSecret|Authorization|X-Motor-Token|password|token)\s*[:=]\s*\S+", "$1=[oculto]");
        // No XML/template material is retained even if a future SDK diagnostic accidentally prints it.
        if (line.Contains("<Fid", StringComparison.OrdinalIgnoreCase) || line.Contains("<Fmd", StringComparison.OrdinalIgnoreCase)) return "[DIAGNOSTICO] Plantilla biométrica omitida.";
        return line.Length > 1000 ? line[..1000] : line;
    }
    private void Commit(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return;
        string line = Sanitize(raw);
        if (line.Contains("[ACK V2]")) { LastEvent = "Evento confirmado por el backend."; LastEventAt = DateTimeOffset.UtcNow; }
        else if (line.Contains("[HUELLA V2]")) { LastEvent = "Huella reconocida; autorización pendiente."; LastEventAt = DateTimeOffset.UtcNow; }
        else if (line.Contains("[CACHE V2]")) { LastEvent = "Socios con huella sincronizados."; LastEventAt = DateTimeOffset.UtcNow; }
        string stamped = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {line}";
        _tail.Enqueue(stamped); while (_tail.Count > 120) _tail.Dequeue();
        try {
            if (File.Exists(_path) && new FileInfo(_path).Length > 1024 * 1024) {
                if (File.Exists(_path + ".1")) File.Move(_path + ".1", _path + ".2", true);
                File.Move(_path, _path + ".1", true);
            }
            File.AppendAllText(_path, stamped + Environment.NewLine, Encoding.UTF8);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
