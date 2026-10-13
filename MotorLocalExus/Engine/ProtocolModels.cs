using System.Text.Json.Serialization;

public sealed class PeticionMatch {
    public string? Snapshot { get; set; }
    public List<SocioDb>? BaseDeDatos { get; set; }
}
public sealed class SocioDb {
    public string CodigoSocio { get; set; } = "";
    public string HuellaTemplate { get; set; } = "";
}
public sealed class AckRequest { public string? Receipt { get; set; } }
public sealed class EventoHuella {
    public string CodigoSocio { get; set; } = "";
    public DateTime FechaUtc { get; set; }
    public string Origen { get; set; } = "lector-automatico";
}
public sealed class HuellaMotorEventDto {
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("eventId")] public string EventId { get; set; } = "";
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("capturedAtUtc")] public DateTime CapturedAtUtc { get; set; }
    [JsonPropertyName("receivedAt")] public DateTime ReceivedAt { get; set; }
    [JsonPropertyName("codigoSocio")] public string CodigoSocio { get; set; } = "";
    [JsonPropertyName("eventProof")] public string? EventProof { get; set; }
    [JsonPropertyName("confidence")] public int? Confidence { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; } = true;
    [JsonPropertyName("message")] public string Message { get; set; } = "Huella reconocida; autorización pendiente.";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "match";
    [JsonPropertyName("source")] public string Source { get; set; } = "motor";
    [JsonPropertyName("raw")] public Dictionary<string, object?> Raw { get; set; } = new();
}
public sealed class HuellaMotorEventsData {
    [JsonPropertyName("eventos")] public List<HuellaMotorEventDto> Eventos { get; set; } = new();
    [JsonPropertyName("latestId")] public long LatestId { get; set; }
    [JsonPropertyName("nextCursor")] public long NextCursor { get; set; }
    [JsonPropertyName("hasMore")] public bool HasMore { get; set; }
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("cursorReset")] public bool CursorReset { get; set; }
}
