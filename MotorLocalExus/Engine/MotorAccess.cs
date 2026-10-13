using System.Net;
using System.Text.Json;

public static class MotorAccess {
    public const string SessionItem = "MotorSession";
    public static async Task Guard(HttpContext context, RequestDelegate next, MotorOptions options, MotorProtocol protocol) {
        string host = context.Request.Host.Host.Trim('[', ']');
        var remote = context.Connection.RemoteIpAddress;
        if (host != "localhost" && host != "127.0.0.1" && host != "::1" || remote != null && !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)) {
            await Reject(context, 403, "LOCAL_ONLY", "El motor solo acepta conexiones locales."); return;
        }
        if (context.Request.Path == "/") { await next(context); return; }
        string origin = context.Request.Headers.Origin.ToString();
        if (!options.AllowedOrigins.Contains(origin, StringComparer.Ordinal)) {
            await Reject(context, 403, "ORIGIN_NOT_ALLOWED", "Origen no autorizado para operar el motor."); return;
        }
        if (context.Request.Method == "OPTIONS") { await next(context); return; }
        if (context.Request.ContentLength > options.MaxRequestBytes) {
            await Reject(context, 413, "REQUEST_TOO_LARGE", "La solicitud excede el límite."); return;
        }
        string token = context.Request.Headers["X-Motor-Token"].ToString();
        if (options.AllowLegacy && string.IsNullOrEmpty(token)) { await next(context); return; }
        try {
            using var payload = protocol.Verify(token, "session", true, 600);
            var session = payload.RootElement;
            string scope = MotorProtocol.Text(session, "scope");
            if (MotorProtocol.Text(session, "origin") != origin || scope is not ("kiosk" or "enrollment"))
                throw new ProtocolException("INVALID_SCOPE", "La autorización no corresponde a este origen u operación.");
            string path = context.Request.Path.Value ?? "";
            if (path == "/enrolar" && scope != "enrollment" || path is "/eventos" or "/eventos/stream" or "/eventos/ack" or "/comparar" && scope != "kiosk") {
                await Reject(context, 403, "INVALID_SCOPE", "La sesión no autoriza esta operación."); return;
            }
            if (!session.TryGetProperty("codigoSocio", out var target) || target.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new ProtocolException("INVALID_TOKEN", "La autorización no tiene un objetivo válido.");
            if (target.ValueKind == JsonValueKind.String && (string.IsNullOrWhiteSpace(target.GetString()) || target.GetString()!.Length > 100))
                throw new ProtocolException("INVALID_TOKEN", "La autorización no tiene un objetivo válido.");
            context.Items[SessionItem] = session.Clone();
            await next(context);
        } catch (ProtocolException ex) { await Reject(context, 401, ex.Code, ex.Message); }
    }
    public static string? EnrollmentTarget(HttpContext context) => context.Items.TryGetValue(SessionItem, out var session) && session is JsonElement root &&
        root.GetProperty("codigoSocio").ValueKind == JsonValueKind.String ? root.GetProperty("codigoSocio").GetString() : null;
    private static async Task Reject(HttpContext context, int status, string code, string message) {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { success = false, code, message });
    }
}
