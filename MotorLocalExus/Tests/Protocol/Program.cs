using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

// No Program, MotorBiometrico, native capture, SDK matching, or USB APIs are invoked.
var directory = Path.Combine(Path.GetTempPath(), "hexodus-protocol-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var key = Enumerable.Repeat((byte)17, 32).ToArray();
var protocol = new MotorProtocol("TEST-KIOSK", Convert.ToBase64String(key));
var options = new MotorOptions { KioskId = "TEST-KIOSK", DeviceSecret = Convert.ToBase64String(key), AllowedOrigins = new[] { "https://gym.example" } };
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); Console.WriteLine("PASS " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (ProtocolException) { Check(true, name); return; } throw new Exception("FAILED: " + name); }
string Cache(string revision, object[] rows, DateTimeOffset? issued = null) => protocol.Sign(new { v = 1, purpose = "cache", kioskId = "TEST-KIOSK", revision,
    issuedAtUtc = MotorProtocol.Utc(issued ?? DateTimeOffset.UtcNow), expiresAtUtc = MotorProtocol.Utc((issued ?? DateTimeOffset.UtcNow).AddMinutes(4)), baseDeDatos = rows });
string Receipt(string id) => protocol.Sign(new { v = 1, purpose = "receipt", kioskId = "TEST-KIOSK", eventId = id, processedAtUtc = MotorProtocol.Utc(DateTimeOffset.UtcNow) });
try {
    string signed = protocol.Sign(new { v = 1, purpose = "receipt", kioskId = "TEST-KIOSK", eventId = Guid.NewGuid().ToString(), processedAtUtc = "2026-01-01T00:00:00Z" });
    var parts = signed.Split('.');
    Check(parts[1] == MotorProtocol.Base64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0]))), "HMAC sobre segmento ASCII exacto");
    using (protocol.Verify(signed, "receipt", false)) Check(true, "verifica token firmado");
    Reject(() => protocol.Verify(signed, "session", false), "separación de propósito");
    Reject(() => protocol.Verify(parts[0] + "." + new string('A', 43), "receipt", false), "firma alterada rechazada");
    var expired = protocol.Sign(new { v = 1, purpose = "session", kioskId = "TEST-KIOSK", issuedAtUtc = "2020-01-01T00:00:00Z", expiresAtUtc = "2020-01-01T00:01:00Z" });
    Reject(() => protocol.Verify(expired, "session", true, 600), "sesión caducada rechazada");

    var cache = new BiometricsCache(options, protocol);
    string revisionA = new('a', 64), revisionB = new('b', 64);
    var snapshotTime = DateTimeOffset.UtcNow.AddSeconds(-10);
    Check(cache.State == "not_initialized", "caché no inicializada distinguible");
    cache.Load(new PeticionMatch { Snapshot = Cache(revisionA, Array.Empty<object>(), snapshotTime) });
    Check(cache.State == "valid_empty" && cache.Snapshot.Authenticated, "snapshot vacío firmado válido");
    Reject(() => cache.Load(new PeticionMatch { Snapshot = Cache(revisionB, Array.Empty<object>(), snapshotTime.AddSeconds(-1)) }), "snapshot antiguo no reemplaza al nuevo aunque su hash sea mayor");
    Check(cache.Snapshot.Revision == revisionA, "conserva revisión anterior al fallar");
    Reject(() => cache.Load(new PeticionMatch { Snapshot = Cache(revisionB, new object[] { new { codigoSocio = "S1", huellaTemplate = Convert.ToBase64String(Encoding.UTF8.GetBytes("<!DOCTYPE f><Fmd/>")), habilitado = true } }) }), "DTD rechazada antes de SDK");
    Check(cache.Snapshot.Revision == revisionA && cache.State == "sync_failed", "fallo de plantilla conserva snapshot atómicamente");
    Reject(() => cache.Load(new PeticionMatch { Snapshot = Cache(revisionB, Array.Empty<object>(), snapshotTime) }), "misma fecha con distinto hash se rechaza sin elegir arbitrariamente");
    Reject(() => cache.Load(new PeticionMatch { Snapshot = Cache("42", Array.Empty<object>()) }), "contador del contrato anterior se rechaza explícitamente");
    string revisionNew = new('0', 64);
    string latest = Cache(revisionNew, Array.Empty<object>(), snapshotTime.AddSeconds(1));
    cache.Load(new PeticionMatch { Snapshot = latest });
    Check(cache.Snapshot.Revision == revisionNew && cache.State == "valid_empty", "snapshot más reciente acepta hash menor sin contador persistente");
    cache.Load(new PeticionMatch { Snapshot = latest });
    Check(cache.Snapshot.Revision == revisionNew, "reenviar mismo snapshot confirmado es idempotente");

    string firstId, firstInstance;
    using (var queue = new DurableEventQueue(directory, protocol, 100)) {
        Check(queue.Ready, "cola durable inicializada");
        using (var competitor = new DurableEventQueue(directory, protocol, 100)) Check(!competitor.Ready, "exclusión entre procesos/instancias");
        for (int i = 0; i < 65; i++) queue.RegistrarMatch(new EventoHuella { CodigoSocio = "S" + i, FechaUtc = DateTime.UtcNow });
        var page = queue.Obtener(0, 20);
        firstId = page.Eventos[0].EventId; firstInstance = page.Eventos[0].InstanceId;
        Check(page.Eventos.Count == 20 && page.HasMore && page.NextCursor == 20 && page.LatestId == 65, "página no salta al latestId");
        Check(queue.Obtener(20, 50).Eventos.Count == 45 && queue.Count == 65, "leer no elimina pendientes");
        Reject(() => queue.Acknowledge(signed[..^2] + "AA"), "recibo falso no borra");
        Check(queue.Count == 65, "pendientes intactos ante ACK inválido");
    }
    using (var restarted = new DurableEventQueue(directory, protocol, 100)) {
        Check(restarted.Ready && restarted.Count == 65 && restarted.InstanceId != firstInstance, "reinicio conserva eventos y cambia instancia");
        Check(restarted.Obtener(0, 1).Eventos[0].EventId == firstId, "UUID estable tras reinicio");
        restarted.Acknowledge(Receipt(firstId)); restarted.Acknowledge(Receipt(firstId));
        Check(restarted.Count == 64, "ACK idempotente confirmado durable");
        Check(restarted.RegistrarMatch(new EventoHuella { CodigoSocio = "NEW" }).Id == 66, "secuencia monotónica tras ACK y reinicio");
        Check(restarted.Obtener(1000, 20).CursorReset, "cursor adelantado indica reinicio");
    }
    var smallDir = Path.Combine(directory, "small");
    using (var small = new DurableEventQueue(smallDir, protocol, 1)) {
        small.RegistrarMatch(new EventoHuella { CodigoSocio = "S1" });
        Reject(() => small.RegistrarMatch(new EventoHuella { CodigoSocio = "S2" }), "cola llena rechaza sin evicción");
        Check(small.Count == 1, "no pierde pendiente por límite");
    }
    File.WriteAllText(Path.Combine(smallDir, "pending-events-v1.json"), "corrupt-test-fixture");
    using (var corrupt = new DurableEventQueue(smallDir, protocol, 1)) Check(!corrupt.Ready, "journal corrupto bloquea sin borrar");

    // Synthetic event backlog only: this does not measure identification against 600 fingerprints.
    const int backlogSize = 720;
    var loadDirectory = Path.Combine(directory, "load");
    var originals = new Dictionary<string, HuellaMotorEventDto>();
    var enqueueTimer = System.Diagnostics.Stopwatch.StartNew();
    using (var loadQueue = new DurableEventQueue(loadDirectory, protocol, 10000)) {
        for (int i = 0; i < backlogSize; i++) {
            var entry = loadQueue.RegistrarMatch(new EventoHuella {
                CodigoSocio = "LOAD-" + (i % 600), FechaUtc = DateTime.UtcNow.AddDays(-3).AddSeconds(i)
            });
            originals.Add(entry.EventId, entry);
        }
        Check(loadQueue.Count == backlogSize && originals.Values.Select(e => e.CodigoSocio).Distinct().Count() == 600,
            "carga: 720 eventos persistidos de 600 socios sinteticos");
    }
    enqueueTimer.Stop();
    long journalBytes = new FileInfo(Path.Combine(loadDirectory, "pending-events-v1.json")).Length;
    var recoveryTimer = System.Diagnostics.Stopwatch.StartNew();
    var recovered = new List<HuellaMotorEventDto>();
    using (var loadQueue = new DurableEventQueue(loadDirectory, protocol, 10000)) {
        Check(loadQueue.Ready && loadQueue.Count == backlogSize, "carga: reinicio recupera todos los pendientes");
        long cursor = 0;
        do {
            var page = loadQueue.Obtener(cursor, 20);
            if (page.Eventos.Count == 0) throw new Exception("FAILED: carga sin progreso de cursor");
            recovered.AddRange(page.Eventos);
            cursor = page.NextCursor;
            if (!page.HasMore) break;
        } while (true);
        Check(recovered.Count == backlogSize && recovered.Select(e => e.EventId).Distinct().Count() == backlogSize &&
            recovered.Select(e => e.Id).SequenceEqual(Enumerable.Range(1, backlogSize).Select(i => (long)i)),
            "carga: paginacion completa ordenada sin perder ni repetir eventos");
        Check(recovered.All(e => originals.TryGetValue(e.EventId, out var original) &&
            e.EventProof == original.EventProof && e.InstanceId == original.InstanceId && e.CapturedAtUtc == original.CapturedAtUtc),
            "carga: reintentos conservan firma, UUID, instancia y fecha originales");
        Check(loadQueue.Obtener(0, 20).Eventos.Select(e => e.EventId).SequenceEqual(recovered.Take(20).Select(e => e.EventId)) &&
            loadQueue.Count == backlogSize, "carga: releer tras fallo de red no elimina pendientes");
    }
    recoveryTimer.Stop();
    var ackTimer = System.Diagnostics.Stopwatch.StartNew();
    using (var loadQueue = new DurableEventQueue(loadDirectory, protocol, 10000)) {
        foreach (var entry in recovered.Take(backlogSize / 2)) {
            string receipt = Receipt(entry.EventId);
            loadQueue.Acknowledge(receipt);
            loadQueue.Acknowledge(receipt); // Simulates a lost ACK response and a repeated request.
        }
    }
    using (var loadQueue = new DurableEventQueue(loadDirectory, protocol, 10000)) {
        Check(loadQueue.Ready && loadQueue.Count == backlogSize / 2 &&
            loadQueue.Obtener(0, 1).Eventos[0].EventId == recovered[backlogSize / 2].EventId,
            "carga: reinicio intermedio conserva solo eventos no confirmados");
        foreach (var entry in recovered) loadQueue.Acknowledge(Receipt(entry.EventId));
        Check(loadQueue.Count == 0, "carga: ACK repetidos vacian cola sin eventos fantasma");
    }
    ackTimer.Stop();
    using (var loadQueue = new DurableEventQueue(loadDirectory, protocol, 10000)) {
        Check(loadQueue.Ready && loadQueue.Count == 0 &&
            loadQueue.RegistrarMatch(new EventoHuella { CodigoSocio = "AFTER-LOAD" }).Id == backlogSize + 1,
            "carga: cola vacia durable conserva secuencia tras reiniciar");
    }
    Console.WriteLine($"LOAD synthetic_events={backlogSize} journal_bytes={journalBytes} " +
        $"enqueue_ms={enqueueTimer.Elapsed.TotalMilliseconds:F0} recover_and_read_ms={recoveryTimer.Elapsed.TotalMilliseconds:F0} " +
        $"ack_and_restart_ms={ackTimer.Elapsed.TotalMilliseconds:F0}; disk-dependent, no fingerprint matching.");

    var now = DateTimeOffset.UtcNow;
    string session = protocol.Sign(new { v = 1, purpose = "session", kioskId = "TEST-KIOSK", scope = "kiosk", codigoSocio = (string?)null,
        origin = "https://gym.example", issuedAtUtc = MotorProtocol.Utc(now), expiresAtUtc = MotorProtocol.Utc(now.AddMinutes(9)) });
    async Task<int> Guard(string origin, string host, string path, string token) {
        var ctx = new DefaultHttpContext(); ctx.Request.Host = new HostString(host); ctx.Request.Path = path;
        ctx.Request.Headers.Origin = origin; ctx.Request.Headers["X-Motor-Token"] = token;
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback; ctx.Response.Body = new MemoryStream();
        await MotorAccess.Guard(ctx, c => { c.Response.StatusCode = 204; return Task.CompletedTask; }, options, protocol);
        return ctx.Response.StatusCode;
    }
    Check(await Guard("https://gym.example", "localhost:4000", "/eventos", session) == 204, "origen autorizado y scope válido");
    Check(await Guard("https://evil.example", "localhost:4000", "/eventos", session) == 403, "origen ajeno bloqueado activamente");
    Check(await Guard("https://gym.example", "evil.example", "/eventos", session) == 403, "Host ajeno bloqueado");
    Check(await Guard("https://gym.example", "localhost:4000", "/enrolar", session) == 403, "scope kiosco no puede enrolar");
    Check(await Guard("https://gym.example", "localhost:4000", "/eventos", "") == 401, "sin capacidad bloqueado");
    Console.WriteLine($"{passed} checks passed; no hardware was invoked.");
} finally {
    // Only this unique test directory is removed; never the configured production queue.
    Directory.Delete(directory, recursive: true);
}
