using System.Text.Json;

// One small atomic journal avoids an external database dependency. Never evicts pending events.
public sealed class DurableEventQueue : IDisposable {
    private sealed class State {
        public int Version { get; set; } = 1;
        public string KioskId { get; set; } = "";
        public long Sequence { get; set; }
        public List<HuellaMotorEventDto> Pending { get; set; } = new();
    }
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Capture before reading pending events: a commit between read and wait cannot be missed.
    public Task CaptureChange() { lock (_gate) return _changed.Task; }
    private readonly string _path;
    private readonly MotorProtocol _protocol;
    private readonly int _limit;
    private readonly bool _legacy;
    private State _state;
    private FileStream? _ownership;
    public string InstanceId { get; } = Guid.NewGuid().ToString();
    public string? LastError { get; private set; }
    public int? LastIoErrorCode { get; private set; }
    public bool Ready { get; private set; }
    public int Count { get { lock (_gate) return _state.Pending.Count; } }
    public bool CanAccept { get { lock (_gate) return Ready && _state.Pending.Count < _limit && (_protocol.IsConfigured || _legacy); } }

    public DurableEventQueue(string directory, MotorProtocol protocol, int limit, bool legacy = false) {
        _protocol = protocol; _limit = limit; _legacy = legacy;
        _path = Path.Combine(Path.GetFullPath(directory), "pending-events-v1.json");
        _state = new State { KioskId = protocol.KioskId };
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Single writer across processes, including the interval before Kestrel binds its port.
            _ownership = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(_path)) {
                var file = new FileInfo(_path);
                if (file.Length > 256L * 1024 * 1024) throw new IOException("journal limit");
                var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(_path)) ?? throw new IOException("journal null");
                Validate(state);
                _state = state;
            } else Persist(_state);
            Ready = true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ProtocolException or InvalidOperationException) {
            // Corruption is never silently repaired by discarding records.
            LastError = "No se pudo abrir o validar la cola persistente. Conserva el archivo y revisa permisos/integridad.";
            Ready = false;
            _ownership?.Dispose();
            _ownership = null;
        }
    }

    public void Dispose() { lock (_gate) { Ready = false; _ownership?.Dispose(); _ownership = null; } }

    private void Validate(State state) {
        if (state.Version != 1 || state.KioskId != _protocol.KioskId || state.Sequence < 0 || state.Pending == null ||
            state.Pending.Any(e => e == null || e.Id <= 0 || e.Id > state.Sequence || !Guid.TryParse(e.EventId, out _) || !Guid.TryParse(e.InstanceId, out _)) ||
            state.Pending.Select(e => e.Id).Distinct().Count() != state.Pending.Count || state.Pending.Select(e => e.EventId).Distinct().Count() != state.Pending.Count)
            throw new IOException("Invalid event journal");
        foreach (var entry in state.Pending) {
            if (entry.EventProof == null && _legacy) continue;
            using var payload = _protocol.Verify(entry.EventProof, "recognition", false);
            if (MotorProtocol.Text(payload.RootElement, "eventId") != entry.EventId || MotorProtocol.Text(payload.RootElement, "instanceId") != entry.InstanceId ||
                MotorProtocol.Text(payload.RootElement, "codigoSocio") != entry.CodigoSocio || MotorProtocol.ReadUtc(payload.RootElement, "capturedAtUtc").UtcDateTime != entry.CapturedAtUtc)
                throw new IOException("Event journal signature mismatch");
        }
    }

    public HuellaMotorEventDto RegistrarMatch(EventoHuella input) {
        lock (_gate) {
            if (!Ready) throw new ProtocolException("QUEUE_UNAVAILABLE", LastError ?? "Cola no disponible.");
            if (_state.Pending.Count >= _limit) {
                LastError = "La cola está llena. Procesa las asistencias pendientes para continuar.";
                throw new ProtocolException("QUEUE_FULL", LastError);
            }
            string eventId = Guid.NewGuid().ToString();
            DateTime captured = input.FechaUtc == default ? DateTime.UtcNow : input.FechaUtc.ToUniversalTime();
            var entry = new HuellaMotorEventDto {
                Id = checked(_state.Sequence + 1), EventId = eventId, InstanceId = InstanceId,
                CapturedAtUtc = captured, ReceivedAt = captured, CodigoSocio = input.CodigoSocio,
                EventProof = _protocol.IsConfigured ? _protocol.Sign(new { v = 1, purpose = "recognition", kioskId = _protocol.KioskId,
                    eventId, instanceId = InstanceId, codigoSocio = input.CodigoSocio, tipo = "IN", capturedAtUtc = MotorProtocol.Utc(captured) }) : null,
                Raw = new() { ["codigoSocio"] = input.CodigoSocio, ["fechaUtc"] = captured, ["origen"] = input.Origen }
            };
            var next = new State { KioskId = _state.KioskId, Sequence = entry.Id, Pending = _state.Pending.Append(entry).ToList() };
            Commit(next);
            Console.WriteLine($"[HUELLA V2] {captured:O} socio={entry.CodigoSocio} evento={entry.EventId} guardado; pendiente de validacion.");
            return entry;
        }
    }

    public HuellaMotorEventsData Obtener(long after, int take) {
        lock (_gate) {
            bool reset = after > _state.Sequence;
            if (reset) after = 0;
            var pending = _state.Pending.Where(e => e.Id > after).OrderBy(e => e.Id);
            var page = pending.Take(Math.Clamp(take, 0, 50)).ToList();
            long cursor = page.Count > 0 ? page[^1].Id : after;
            return new HuellaMotorEventsData { Eventos = page, LatestId = _state.Sequence, NextCursor = cursor,
                HasMore = pending.Any(e => e.Id > cursor), InstanceId = InstanceId, CursorReset = reset };
        }
    }

    public void Acknowledge(string receipt) {
        using var payload = _protocol.Verify(receipt, "receipt", false);
        string eventId = MotorProtocol.Text(payload.RootElement, "eventId");
        if (!Guid.TryParse(eventId, out _)) throw new ProtocolException("INVALID_RECEIPT", "Recibo inválido.");
        MotorProtocol.ReadUtc(payload.RootElement, "processedAtUtc");
        lock (_gate) {
            if (!Ready) throw new ProtocolException("QUEUE_UNAVAILABLE", LastError ?? "Cola no disponible.");
            var pending = _state.Pending.FirstOrDefault(e => e.EventId == eventId);
            if (pending == null) return;
            Commit(new State { KioskId = _state.KioskId, Sequence = _state.Sequence, Pending = _state.Pending.Where(e => e.EventId != eventId).ToList() });
            Console.WriteLine($"[ACK V2] {DateTime.UtcNow:O} evento={eventId} confirmado por el backend. Pendientes={_state.Pending.Count}; total_desde_reconocimiento_ms={(DateTime.UtcNow - pending.CapturedAtUtc).TotalMilliseconds:F1}");
        }
    }

    private void Commit(State next) {
        try {
            Persist(next); _state = next; LastError = null; LastIoErrorCode = null;
            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LastIoErrorCode = ex.HResult & 0xffff;
            Console.WriteLine($"[QUEUE V2] {DateTime.UtcNow:O} escritura fallida: {ex.GetType().Name}, Windows={LastIoErrorCode}, HRESULT=0x{ex.HResult:X8}. No se confirma la operacion.");
            LastError = "No se pudo guardar la cola. La asistencia no está confirmada; revisa espacio y permisos.";
            throw new ProtocolException("QUEUE_WRITE_FAILED", LastError);
        }
    }
    private void Persist(State state) {
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                JsonSerializer.Serialize(file, state);
                file.Flush(flushToDisk: true);
            }
            for (int attempt = 0; ; attempt++) {
                try {
                    if (File.Exists(_path)) File.Replace(temporary, _path, null);
                    else File.Move(temporary, _path);
                    break;
                } catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && attempt < 3) {
                    // A transient Windows sharing/lock violation has not replaced the file.
                    // Keep the same flushed temporary journal and retry, never acknowledge early.
                    int delay = new[] { 20, 60, 140 }[attempt];
                    Console.WriteLine($"[QUEUE V2] {DateTime.UtcNow:O} archivo ocupado Windows={ex.HResult & 0xffff}; reintento {attempt + 1}/3 en {delay} ms.");
                    Thread.Sleep(delay);
                }
            }
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
