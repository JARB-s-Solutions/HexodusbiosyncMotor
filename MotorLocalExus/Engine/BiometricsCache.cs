using DPUruNet;
using System.Text;
using System.Text.Json;
using System.Xml;

public sealed record CachedFingerprint(string CodigoSocio, Fmd Template, bool Habilitado);
public sealed record HuellasCacheSnapshot(bool EstaCargada, bool Authenticated, string? Revision,
    DateTimeOffset? LastSyncUtc, DateTimeOffset? ExpiresAtUtc, IReadOnlyList<CachedFingerprint> All) {
    public IReadOnlyList<CachedFingerprint> Enabled { get; } = All.Where(entry => entry.Habilitado).ToArray();
    public DateTimeOffset? IssuedAtUtc { get; init; }
}

public sealed class BiometricsCache {
    private readonly object _gate = new();
    private readonly MotorOptions _options;
    private readonly MotorProtocol _protocol;
    private HuellasCacheSnapshot _snapshot = new(false, false, null, null, null, Array.Empty<CachedFingerprint>());
    public string? LastSyncError { get; private set; }
    public BiometricsCache(MotorOptions options, MotorProtocol protocol) { _options = options; _protocol = protocol; }
    public HuellasCacheSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool IsStale(HuellasCacheSnapshot snapshot) => !snapshot.EstaCargada || snapshot.LastSyncUtc == null ||
        DateTimeOffset.UtcNow - snapshot.LastSyncUtc.Value >= TimeSpan.FromSeconds(_options.CacheStaleSeconds) ||
        snapshot.ExpiresAtUtc <= DateTimeOffset.UtcNow;
    public string State {
        get {
            var snapshot = Snapshot;
            if (LastSyncError != null) return "sync_failed";
            if (!snapshot.EstaCargada) return "not_initialized";
            if (IsStale(snapshot)) return "stale";
            return snapshot.All.Count == 0 ? "valid_empty" : "valid";
        }
    }

    public HuellasCacheSnapshot Load(PeticionMatch request) {
        try {
            HuellasCacheSnapshot next;
            if (!string.IsNullOrWhiteSpace(request.Snapshot)) {
                using var token = _protocol.Verify(request.Snapshot, "cache", true, 300, _options.MaxRequestBytes);
                var payload = token.RootElement;
                string revision = MotorProtocol.Text(payload, "revision");
                if (revision.Length != 64 || revision.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                    throw new ProtocolException("INVALID_SNAPSHOT", "La revisión del conjunto biométrico no es válida.");
                if (!payload.TryGetProperty("baseDeDatos", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > _options.MaxCacheEntries)
                    throw new ProtocolException("INVALID_SNAPSHOT", "El lote biométrico no es válido o excede el límite.");
                var entries = new List<CachedFingerprint>();
                var codes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in data.EnumerateArray()) {
                    string code = MotorProtocol.Text(entry, "codigoSocio");
                    ValidateCode(code, codes);
                    string encoded = MotorProtocol.Text(entry, "huellaTemplate");
                    if (encoded.Length > (_options.MaxTemplateBytes + 2) / 3 * 4) throw InvalidTemplate();
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(encoded); } catch (FormatException) { throw InvalidTemplate(); }
                    string xml;
                    try { xml = new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { throw InvalidTemplate(); }
                    if (!entry.TryGetProperty("habilitado", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new ProtocolException("INVALID_SNAPSHOT", "El estado del socio no es válido.");
                    entries.Add(new(code, DeserializeTemplate(xml), enabled.GetBoolean()));
                }
                next = new(true, true, revision, DateTimeOffset.UtcNow, MotorProtocol.ReadUtc(payload, "expiresAtUtc"), entries.ToArray()) {
                    IssuedAtUtc = MotorProtocol.ReadUtc(payload, "issuedAtUtc")
                };
            } else {
                if (!_options.AllowLegacy) throw new ProtocolException("SIGNED_SNAPSHOT_REQUIRED", "Se requiere una sincronización autenticada.");
                if (request.BaseDeDatos == null || request.BaseDeDatos.Count > _options.MaxCacheEntries)
                    throw new ProtocolException("INVALID_SNAPSHOT", "Se requiere una lista biométrica completa válida.");
                var entries = new List<CachedFingerprint>();
                var codes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in request.BaseDeDatos) {
                    if (entry == null) throw InvalidTemplate();
                    ValidateCode(entry.CodigoSocio, codes);
                    entries.Add(new(entry.CodigoSocio, DeserializeTemplate(entry.HuellaTemplate), true));
                }
                next = new(true, false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(_options.CacheStaleSeconds), entries.ToArray());
            }
            lock (_gate) {
                if (_snapshot.Authenticated && !next.Authenticated) throw new ProtocolException("SNAPSHOT_DOWNGRADE", "No se puede sustituir una caché autenticada por una carga heredada.");
                // Revision is a content hash, not a sortable counter. The signed database
                // timestamp orders snapshots without requiring an extra table or trigger.
                if (_snapshot.IssuedAtUtc != null && next.IssuedAtUtc < _snapshot.IssuedAtUtc)
                    throw new ProtocolException("STALE_SNAPSHOT", "La sincronización es anterior a la caché vigente.");
                if (_snapshot.IssuedAtUtc != null && next.IssuedAtUtc == _snapshot.IssuedAtUtc && next.Revision != _snapshot.Revision)
                    throw new ProtocolException("SNAPSHOT_ORDER_CONFLICT", "Dos sincronizaciones tienen la misma fecha y distinto contenido. Sincroniza nuevamente.");
                _snapshot = next;
                LastSyncError = null;
            }
            return next;
        } catch (Exception ex) when (ex is ProtocolException or JsonException or InvalidOperationException or KeyNotFoundException) {
            LastSyncError = "No se pudo validar la sincronización. Se conserva la caché anterior.";
            if (ex is ProtocolException) throw;
            throw new ProtocolException("INVALID_SNAPSHOT", LastSyncError);
        }
    }

    private static void ValidateCode(string code, HashSet<string> codes) {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100 || code.Any(char.IsControl) || !codes.Add(code))
            throw new ProtocolException("INVALID_SNAPSHOT", "El lote contiene identificadores inválidos o repetidos.");
    }
    private Fmd DeserializeTemplate(string xml) {
        if (string.IsNullOrWhiteSpace(xml) || Encoding.UTF8.GetByteCount(xml) > _options.MaxTemplateBytes) throw InvalidTemplate();
        try {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = _options.MaxTemplateBytes,
                MaxCharactersFromEntities = 0
            });
            while (reader.Read()) { }
            return Fmd.DeserializeXml(xml) ?? throw InvalidTemplate();
        } catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException) { throw InvalidTemplate(); }
    }
    private static ProtocolException InvalidTemplate() => new("INVALID_TEMPLATE", "Una plantilla biométrica es inválida. La caché anterior se conserva.");
}
