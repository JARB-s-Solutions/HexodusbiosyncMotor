using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class MotorOptions {
    public const int IdentificationThreshold = 21474;
    public string KioskId { get; init; } = "KIOSKO-HUELLA-01";
    public string DeviceSecret { get; init; } = "";
    public string[] AllowedOrigins { get; init; } = new[] { "http://localhost:3000" };
    public bool AllowLegacy { get; init; }
    public int MaxCacheEntries { get; init; } = 10000;
    public int MaxTemplateBytes { get; init; } = 65536;
    public int MaxRequestBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxPendingEvents { get; init; } = 10000;
    public int CacheStaleSeconds { get; init; } = 300;
    public int CooldownMs { get; init; } = 3000;
    public string QueueDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexodus", "MotorQueue");

    public static MotorOptions Read(IConfiguration configuration) {
        var options = configuration.GetSection("Motor").Get<MotorOptions>() ?? new MotorOptions();
        if (string.IsNullOrWhiteSpace(options.KioskId) || options.KioskId.Length > 100 || options.MaxCacheEntries is < 1 or > 100000 ||
            options.MaxTemplateBytes is < 1024 or > 65536 || options.MaxRequestBytes is < 1024 or > 128 * 1024 * 1024 ||
            options.MaxPendingEvents is < 1 or > 100000 || options.CacheStaleSeconds is < 120 or > 300 || options.CooldownMs is < 0 or > 60000)
            throw new InvalidOperationException("Configuración Motor fuera de los límites admitidos.");
        if (options.AllowedOrigins.Length == 0 || options.AllowedOrigins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.GetLeftPart(UriPartial.Authority) != origin || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new InvalidOperationException("Motor:AllowedOrigins debe contener orígenes exactos sin rutas ni comodines.");
        return options;
    }
}

public sealed class ProtocolException : Exception {
    public string Code { get; }
    public ProtocolException(string code, string message) : base(message) { Code = code; }
}

public sealed class MotorProtocol {
    private readonly byte[]? _secret;
    public string KioskId { get; }
    public bool IsConfigured => _secret != null;
    public MotorProtocol(string kioskId, string secret) {
        KioskId = kioskId;
        if (string.IsNullOrWhiteSpace(secret)) return;
        try { _secret = Convert.FromBase64String(secret); }
        catch (FormatException) { throw new InvalidOperationException("Motor:DeviceSecret debe ser Base64 válido."); }
        if (_secret.Length < 32) throw new InvalidOperationException("Motor:DeviceSecret requiere al menos 32 bytes aleatorios.");
    }

    public string Sign(object payload) {
        if (_secret == null) throw new ProtocolException("DEVICE_NOT_CONFIGURED", "El dispositivo no está configurado.");
        string encoded = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return encoded + "." + Base64Url(HMACSHA256.HashData(_secret, Encoding.ASCII.GetBytes(encoded)));
    }

    public JsonDocument Verify(string? token, string purpose, bool timed, int maxLifetimeSeconds = 300, int maxTokenChars = 65536) {
        if (_secret == null) throw new ProtocolException("DEVICE_NOT_CONFIGURED", "El dispositivo no está configurado.");
        if (string.IsNullOrWhiteSpace(token) || token.Length > maxTokenChars) throw InvalidToken();
        var parts = token.Split('.');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))) throw InvalidToken();
        JsonDocument? document = null;
        try {
            byte[] supplied = DecodeBase64Url(parts[1]);
            byte[] expected = HMACSHA256.HashData(_secret, Encoding.ASCII.GetBytes(parts[0]));
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) throw InvalidToken();
            document = JsonDocument.Parse(DecodeBase64Url(parts[0]), new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("v").GetInt32() != 1 ||
                Text(root, "purpose") != purpose || Text(root, "kioskId") != KioskId) throw InvalidToken();
            if (timed) {
                var issued = ReadUtc(root, "issuedAtUtc");
                var expires = ReadUtc(root, "expiresAtUtc");
                var now = DateTimeOffset.UtcNow;
                if (issued > now.AddSeconds(30) || expires <= now || expires <= issued || (expires - issued).TotalSeconds > maxLifetimeSeconds + 1)
                    throw new ProtocolException("TOKEN_EXPIRED", "La autorización expiró. Actualiza la sesión o sincroniza nuevamente.");
            }
            return document;
        } catch (ProtocolException) { document?.Dispose(); throw; }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException) {
            document?.Dispose(); throw InvalidToken();
        }
    }

    public static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw InvalidToken();
    public static DateTimeOffset ReadUtc(JsonElement root, string name) {
        string text = Text(root, name);
        if (!text.EndsWith('Z') || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)) throw InvalidToken();
        return date;
    }
    public static string Utc(DateTimeOffset date) => date.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    public static string HashTemplate(string xml) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();
    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] DecodeBase64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    private static ProtocolException InvalidToken() => new("INVALID_TOKEN", "La autorización del motor no es válida.");
}
