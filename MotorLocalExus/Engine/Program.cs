using DPUruNet;
using System.Net;
using System.Text.Json;
using Exus;
using Microsoft.Extensions.Hosting.WindowsServices;

string? diagnosticRoot = args.SkipWhile(a => a != "--diagnostic-root").Skip(1).FirstOrDefault();
bool diagnostic = !WindowsServiceHelpers.IsWindowsService() && diagnosticRoot != null;
string dataRoot = diagnostic ? Path.GetFullPath(diagnosticRoot!) : Installation.DataRoot;
var site = File.Exists(Installation.ConfigPath(dataRoot)) ? Installation.Load(Installation.ConfigPath(dataRoot)) : new SiteConfiguration();
string privateRoot = Path.Combine(dataRoot, "private");
Directory.CreateDirectory(privateRoot);
var journal = new MotorJournal(Path.Combine(privateRoot, "logs"));
Console.SetOut(journal);
Console.SetError(journal);
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddWindowsService(service => service.ServiceName = Installation.ServiceName);
// Installed configuration is protected by DPAPI and ACLs; no secrets are shipped in the installer.
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
    ["Motor:DeviceSecret"] = site.DeviceSecret, ["Motor:KioskId"] = site.KioskId,
    ["Motor:QueueDirectory"] = Path.Combine(privateRoot, "queue"), ["Motor:AllowLegacy"] = "false",
    ["Motor:ListenUrl"] = diagnostic ? "http://localhost:" + (args.SkipWhile(a => a != "--diagnostic-port").Skip(1).FirstOrDefault() ?? "4400") : "http://localhost:4000"
});
// Replace the full origin array (configuration providers otherwise merge numeric array keys).
builder.Configuration.GetSection("Motor:AllowedOrigins").GetChildren().ToList().ForEach(c => builder.Configuration[c.Path] = null);
for (int i = 0; i < site.AllowedOrigins.Length; i++) builder.Configuration[$"Motor:AllowedOrigins:{i}"] = site.AllowedOrigins[i];
var options = MotorOptions.Read(builder.Configuration);
var protocol = new MotorProtocol(options.KioskId, options.DeviceSecret);
string listenUrl = builder.Configuration["Motor:ListenUrl"] ?? "http://localhost:4000";
if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri) || listenUri.Scheme != "http" ||
    (listenUri.Host != "localhost" && (!IPAddress.TryParse(listenUri.Host.Trim('[', ']'), out var listenIp) || !IPAddress.IsLoopback(listenIp))))
    throw new InvalidOperationException("Motor:ListenUrl debe usar HTTP loopback (localhost, 127.0.0.1 o ::1).");
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = options.MaxRequestBytes);
builder.Services.AddCors(cors => cors.AddPolicy("MotorOrigins", policy => policy.WithOrigins(options.AllowedOrigins)
    .WithMethods("GET", "POST", "OPTIONS").WithHeaders("Content-Type", "X-Motor-Token")));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(protocol);
builder.Services.AddSingleton<BiometricsCache>();
builder.Services.AddSingleton(_ => new DurableEventQueue(options.QueueDirectory, protocol, options.MaxPendingEvents, options.AllowLegacy));
builder.Services.AddSingleton<DetectorStatus>();
builder.Services.AddSingleton(new ReaderBridgeOptions(
    Exus.ReaderWire.PipeName,
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Reader", "MotorLocalExus.Reader.exe"))));
builder.Services.AddSingleton<ReaderBridge>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReaderBridge>());
builder.Services.AddHostedService<DeteccionContinuaService>();
builder.Services.AddSingleton(journal);
builder.Services.AddSingleton(site);
builder.Services.AddSingleton(new DiagnosticsOptions(diagnostic ? "MotorLocalExus.Test." + Path.GetFileName(dataRoot) : Installation.PipeName));
builder.Services.AddHostedService<MotorDiagnostics>();
var app = builder.Build();
MotorBiometrico.Bridge = app.Services.GetRequiredService<ReaderBridge>();
app.UseCors("MotorOrigins");
app.Use((context, next) => MotorAccess.Guard(context, next, options, protocol));
app.Use(async (context, next) => {
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next(context);
});

async Task Stream(HttpContext context, object value) {
    await context.Response.WriteAsync(JsonSerializer.Serialize(value) + "\n", context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);
}
Task Message(HttpContext context, string text) => Stream(context, new { tipo = "mensaje", texto = text });
string EnrollmentFailureMessage(string code) => code switch
{
    "READER_AGENT_NOT_CONNECTED" => "El auxiliar del lector no está conectado. Comprueba que el motor esté iniciado.",
    "READER_NOT_FOUND" => "No se detectó el lector. Conecta el USB y vuelve a intentarlo.",
    "READER_BUSY" => "El lector está ocupado con otra lectura. Espera a que termine e inténtalo de nuevo.",
    "CAPTURE_CANCELED" => "La captura fue cancelada.",
    "CAPTURE_TIMEOUT" => "No se detectó un dedo a tiempo.",
    "CAPTURE_QUALITY_LOW" => "La lectura no tuvo calidad suficiente. Limpia el sensor e inténtalo de nuevo.",
    "FMD_EXTRACTION_FAILED" => "No se pudo procesar la lectura. Retira el dedo y vuelve a intentarlo.",
    "ENROLLMENT_INCOMPLETE" => "La captura quedó incompleta. Se requieren cuatro lecturas.",
    "ENROLLMENT_FMD_FAILED" => "No se pudo generar la plantilla final. Repite las cuatro lecturas.",
    "READER_BRIDGE_ERROR" => "No se pudo comunicar con el lector. Se reintentará automáticamente.",
    _ => "Se canceló o falló la captura. Revisa el lector e intenta de nuevo."
};

app.MapGet("/", () => Results.Ok(new { service = "HEXODUS Motor V2 SSE", transport = "sse", protocolVersion = 1, securityConfigured = protocol.IsConfigured }));
object ReadStatus(BiometricsCache cache, DurableEventQueue queue, DetectorStatus detector) {
    var current = cache.Snapshot;
    return new {
        motorVersion = "v2-sse", transport = "sse",
        cacheCargada = current.EstaCargada, huellasEnMemoria = current.Enabled.Count, plantillasParaDuplicados = current.All.Count,
        lectorConectado = MotorBiometrico.LectorConectado, auxiliarLectorConectado = MotorBiometrico.AuxiliarConectado, detectorContinuo = MotorBiometrico.Enrolando ? "enrolling" : detector.State,
        lectorOcupado = MotorBiometrico.LectorOcupado, lectorEstado = MotorBiometrico.Estado,
        ultimoUsoLectorUtc = MotorBiometrico.UltimoUsoUtc, ultimoErrorLector = MotorBiometrico.UltimoError,
        eventosPendientes = queue.Count, instanceId = queue.InstanceId, kioskId = options.KioskId,
        cacheState = cache.State, cacheStale = cache.IsStale(current), lastSyncUtc = current.LastSyncUtc,
        lastSyncError = cache.LastSyncError, revision = current.Revision, cacheAuthenticated = current.Authenticated,
        securityConfigured = protocol.IsConfigured, queueReady = queue.Ready, queueError = queue.LastError, queueIoErrorCode = queue.LastIoErrorCode,
        queueCapacity = options.MaxPendingEvents, queueFull = queue.Count >= options.MaxPendingEvents,
        detectorError = detector.LastError, legacyMode = options.AllowLegacy
    };
}
app.MapGet("/estado", (BiometricsCache cache, DurableEventQueue queue, DetectorStatus detector) => Results.Ok(ReadStatus(cache, queue, detector)));
app.MapGet("/eventos/stream", (HttpContext context, BiometricsCache cache, DurableEventQueue queue, DetectorStatus detector) => MotorEventStream.Run(context, queue, () => ReadStatus(cache, queue, detector)));
app.MapGet("/eventos", (HttpRequest request, DurableEventQueue queue) => {
    long after = long.TryParse(request.Query["after"], out long parsedAfter) ? Math.Max(0, parsedAfter) : 0;
    int take = int.TryParse(request.Query["take"], out int parsedTake) ? Math.Clamp(parsedTake, 0, 50) : 20;
    if (!queue.Ready) return Results.Json(new { success = false, code = "QUEUE_UNAVAILABLE", message = queue.LastError }, statusCode: 503);
    return Results.Ok(new { success = true, data = queue.Obtener(after, take) });
});
app.MapPost("/eventos/ack", (AckRequest request, DurableEventQueue queue) => {
    try {
        queue.Acknowledge(request.Receipt ?? "");
        return Results.Ok(new { success = true });
    } catch (ProtocolException ex) {
        return Results.Json(new { success = false, code = ex.Code, message = ex.Message }, statusCode: ex.Code.StartsWith("QUEUE_") ? 503 : 400);
    }
});
app.MapPost("/cargar-cache", (PeticionMatch request, BiometricsCache cache) => {
    try {
        var loaded = cache.Load(request);
        Console.WriteLine($"[CACHE V2] {loaded.Enabled.Count} socios habilitados con huella; {loaded.All.Count} plantillas para duplicados.");
        return Results.Ok(new { success = true, totalCargadas = loaded.Enabled.Count, revision = loaded.Revision });
    } catch (ProtocolException ex) {
        return Results.Json(new { success = false, code = ex.Code, message = ex.Message }, statusCode: 400);
    }
});
app.MapPost("/comparar", async (HttpContext context, BiometricsCache cache, DurableEventQueue queue) => {
    context.Response.ContentType = "application/x-ndjson";
    try {
        var current = cache.Snapshot;
        if (!current.EstaCargada || current.Enabled.Count == 0) {
            await Stream(context, new { tipo = "resultado", success = false, code = "CACHE_EMPTY", message = "Sin huellas disponibles en memoria." }); return;
        }
        if (!queue.CanAccept) {
            await Stream(context, new { tipo = "resultado", success = false, code = "QUEUE_UNAVAILABLE", message = "No se puede guardar el evento de reconocimiento." }); return;
        }
        await Message(context, "Ponga el dedo en el lector...");
        var fingerprint = await MotorBiometrico.CapturarHuellaAsync(msg => Message(context, msg), context.RequestAborted);
        if (fingerprint == null) {
            string code = MotorBiometrico.UltimoError;
            Console.WriteLine($"[MOTOR] Captura no completada; code={code}.");
            await Stream(context, new { tipo = "resultado", success = false, code = string.IsNullOrWhiteSpace(code) ? "CAPTURE_FAILED" : code,
                message = EnrollmentFailureMessage(code) }); return;
        }
        // Capture may have waited while the front pushed a member change.
        current = cache.Snapshot;
        if (!current.EstaCargada || current.Enabled.Count == 0) {
            await Stream(context, new { tipo = "resultado", success = false, code = "CACHE_EMPTY", message = "Sin huellas disponibles en memoria." }); return;
        }
        var identified = Comparison.Identify(fingerprint, 0, current.Enabled.Select(entry => entry.Template), MotorOptions.IdentificationThreshold, 1);
        if (identified.ResultCode == Constants.ResultCode.DP_SUCCESS && identified.Indexes.Length > 0) {
            string code = current.Enabled[identified.Indexes[0][0]].CodigoSocio;
            var recorded = queue.RegistrarMatch(new EventoHuella { CodigoSocio = code, FechaUtc = DateTime.UtcNow });
            await Stream(context, new { tipo = "resultado", success = true, codigoSocio = code, eventId = recorded.EventId,
                instanceId = recorded.InstanceId, capturedAtUtc = recorded.CapturedAtUtc, eventProof = recorded.EventProof });
        } else await Stream(context, new { tipo = "resultado", success = false, message = "Socio no reconocido." });
    } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (ProtocolException ex) { await Stream(context, new { tipo = "resultado", success = false, code = ex.Code, message = ex.Message }); }
    catch (Exception ex) {
        Console.WriteLine("[MOTOR] Comparación fallida (" + ex.GetType().Name + ").");
        await Stream(context, new { tipo = "resultado", success = false, code = "READER_ERROR", message = "No se pudo completar la lectura. Revisa el lector e intenta de nuevo." });
    }
});
app.MapGet("/enrolar", async (HttpContext context, BiometricsCache cache) => {
    context.Response.ContentType = "application/x-ndjson";
    try {
        var before = cache.Snapshot;
        bool authenticated = context.Items.ContainsKey(MotorAccess.SessionItem);
        if (authenticated && (!before.Authenticated || cache.IsStale(before))) {
            await Stream(context, new { tipo = "resultado", success = false, code = "CACHE_STALE", message = "Sincroniza las huellas antes de registrar una nueva." }); return;
        }
        await Message(context, "Iniciando enrolamiento...");
        var enrollment = await MotorBiometrico.EnrolarSocioAsync(msg => Message(context, msg), context.RequestAborted);
        if (enrollment == null) {
            string code = MotorBiometrico.UltimoError;
            Console.WriteLine($"[MOTOR] Enrolamiento no completado; code={code}.");
            await Stream(context, new { tipo = "resultado", success = false, code = string.IsNullOrWhiteSpace(code) ? "CAPTURE_FAILED" : code,
                message = EnrollmentFailureMessage(code) }); return;
        }
        var current = cache.Snapshot;
        if (authenticated && (!current.Authenticated || cache.IsStale(current) || current.Revision != before.Revision)) {
            await Stream(context, new { tipo = "resultado", success = false, code = "CACHE_STALE", message = "La información biométrica cambió. Sincroniza y repite la captura." }); return;
        }
        string? target = MotorAccess.EnrollmentTarget(context);
        var others = current.All.Where(entry => entry.CodigoSocio != target).Select(entry => entry.Template).ToArray();
        if (others.Length > 0) {
            var duplicate = Comparison.Identify(enrollment, 0, others, MotorOptions.IdentificationThreshold, 1);
            if (duplicate.ResultCode != Constants.ResultCode.DP_SUCCESS)
                throw new ProtocolException("DUPLICATE_CHECK_FAILED", "No se pudo comprobar si la huella ya existe. Intenta nuevamente.");
            if (duplicate.Indexes.Length > 0) {
                await Stream(context, new { tipo = "resultado", success = false, code = "HUELLA_DUPLICADA",
                    message = "Esta huella ya está registrada en otro socio. Verifica el registro antes de continuar." }); return;
            }
        }
        string xml = Fmd.SerializeXml(enrollment);
        var now = DateTimeOffset.UtcNow;
        string? proof = authenticated ? protocol.Sign(new { v = 1, purpose = "enrollment", kioskId = options.KioskId,
            proofId = Guid.NewGuid().ToString(), revision = current.Revision, codigoSocio = target, templateSha256 = MotorProtocol.HashTemplate(xml),
            issuedAtUtc = MotorProtocol.Utc(now), expiresAtUtc = MotorProtocol.Utc(now.AddMinutes(5)) }) : null;
        await Message(context, "Captura completada. Guarda el socio para asociar la huella.");
        await Stream(context, new { tipo = "resultado", success = true, huellaTemplate = xml, enrollmentProof = proof });
    } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (ProtocolException ex) { await Stream(context, new { tipo = "resultado", success = false, code = ex.Code, message = ex.Message }); }
    catch (Exception ex) {
        Console.WriteLine("[MOTOR] Enrolamiento fallido (" + ex.GetType().Name + ").");
        await Stream(context, new { tipo = "resultado", success = false, code = "READER_ERROR", message = "No se pudo completar la captura. Revisa el lector e intenta de nuevo." });
    }
});
Console.WriteLine("[SISTEMA] Motor V2 SSE, protocolo v1, loopback: " + listenUrl);
if (!protocol.IsConfigured) Console.WriteLine("[SISTEMA] Falta configurar Motor__DeviceSecret. Las operaciones autenticadas permanecen bloqueadas.");
try { app.Run(listenUrl); }
catch (Exception ex) { Console.WriteLine("[SISTEMA] No se pudo iniciar: " + ex.GetType().Name + ". Comprueba el puerto y la configuración."); Environment.ExitCode = 1; }
