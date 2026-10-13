using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// Isolated loopback integration test. No USB, native matching, backend or production DB.
var directory = Path.Combine(Path.GetTempPath(), "hexodus-sse-test-" + Guid.NewGuid().ToString("N"));
var key = Convert.ToBase64String(Enumerable.Repeat((byte)29, 32).ToArray());
var options = new MotorOptions { KioskId = "SSE-TEST", DeviceSecret = key, AllowedOrigins = new[] { "http://localhost:3000" } };
var protocol = new MotorProtocol(options.KioskId, key);
using var queue = new DurableEventQueue(directory, protocol, 100);
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var app = builder.Build();
app.Use((context, next) => MotorAccess.Guard(context, next, options, protocol));
app.MapGet("/eventos/stream", (HttpContext context) => MotorEventStream.Run(context, queue, () => new { ready = true }));
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
string Token(string scope = "kiosk", int seconds = 60) => protocol.Sign(new { v = 1, purpose = "session", kioskId = options.KioskId,
    scope, origin = options.AllowedOrigins[0], codigoSocio = (string?)null, issuedAtUtc = MotorProtocol.Utc(DateTimeOffset.UtcNow),
    expiresAtUtc = MotorProtocol.Utc(DateTimeOffset.UtcNow.AddSeconds(seconds)) });
async Task<HttpResponseMessage> Open(string? token, string origin = "http://localhost:3000") {
    using var request = new HttpRequestMessage(HttpMethod.Get, "/eventos/stream");
    request.Headers.Add("Origin", origin);
    if (token != null) request.Headers.Add("X-Motor-Token", token);
    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
}
async Task<JsonElement> Frame(StreamReader reader, string wanted) {
    string type = "";
    while (true) {
        string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (line == null) throw new Exception("Unexpected EOF");
        if (line.StartsWith("event: ")) type = line[7..];
        if (line.StartsWith("data: ") && type == wanted) return JsonDocument.Parse(line[6..]).RootElement.Clone();
    }
}
try {
    using (var denied = await Open(null)) Check(denied.StatusCode == HttpStatusCode.Unauthorized, "stream sin token rechazado");
    using (var denied = await Open(Token("enrollment"))) Check(denied.StatusCode == HttpStatusCode.Forbidden, "scope de captura no permite SSE");
    using (var denied = await Open(Token(), "https://invalid.example")) Check(denied.StatusCode == HttpStatusCode.Forbidden, "origen ajeno rechazado");
    string firstId;
    using (var response = await Open(Token())) {
        Check(response.Content.Headers.ContentType?.MediaType == "text/event-stream", "tipo SSE");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await Frame(reader, "ready");
        await Frame(reader, "status");
        var change = queue.CaptureChange();
        var watch = Stopwatch.StartNew();
        var first = queue.RegistrarMatch(new EventoHuella { CodigoSocio = "SSE-FIXTURE" });
        firstId = first.EventId;
        await change.WaitAsync(TimeSpan.FromSeconds(1));
        var frame = await Frame(reader, "events");
        Check(frame.GetProperty("data").GetProperty("eventos")[0].GetProperty("eventId").GetString() == firstId, "push inmediato del evento persistido");
        Console.WriteLine("Recognition-to-SSE test transport: " + watch.Elapsed.TotalMilliseconds.ToString("F2") + " ms (includes local journal write)");
        Check(queue.Count == 1, "entrega SSE no borra evento sin ACK");
    }
    using (var response = await Open(Token())) {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var replay = await Frame(reader, "events");
        Check(replay.GetProperty("data").GetProperty("eventos")[0].GetProperty("eventId").GetString() == firstId, "reconexion recupera mismo UUID sin ACK");
        queue.Acknowledge(protocol.Sign(new { v = 1, purpose = "receipt", kioskId = options.KioskId, eventId = firstId, processedAtUtc = MotorProtocol.Utc(DateTimeOffset.UtcNow) }));
        Check(queue.Count == 0, "solo recibo firmado confirma entrega");
        for (int i = 0; i < 65; i++) queue.RegistrarMatch(new EventoHuella { CodigoSocio = "BURST-" + i });
        var ids = new HashSet<string>();
        while (ids.Count < 65) {
            var page = await Frame(reader, "events");
            foreach (var item in page.GetProperty("data").GetProperty("eventos").EnumerateArray()) ids.Add(item.GetProperty("eventId").GetString()!);
        }
        Check(ids.Count == 65 && queue.Count == 65, "rafaga paginada completa sin perder pendientes");
    }
    using (var response = await Open(Token(seconds: 1))) {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(4));
        Check(true, "conexion termina al expirar la autorizacion");
    }
    Console.WriteLine($"{passed} SSE checks passed");
} finally {
    await app.StopAsync();
    await app.DisposeAsync();
    queue.Dispose();
    // Only this test's newly created directory, never a live motor queue.
    Directory.Delete(directory, recursive: true);
}
