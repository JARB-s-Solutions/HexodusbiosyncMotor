using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

// SSE delivery is not an acknowledgement. Only the existing signed receipt removes an event.
public static class MotorEventStream {
    public static async Task Run(HttpContext context, DurableEventQueue queue, Func<object> status) {
        if (!queue.Ready) {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { success = false, code = "QUEUE_UNAVAILABLE" });
            return;
        }
        // No anonymous/legacy stream, tokens in URLs, or connections surviving authorization expiry.
        if (!context.Items.TryGetValue(MotorAccess.SessionItem, out var value) || value is not JsonElement session) {
            context.Response.StatusCode = 401;
            return;
        }
        var remaining = MotorProtocol.ReadUtc(session, "expiresAtUtc") - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) { context.Response.StatusCode = 401; return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        lifetime.CancelAfter(remaining);
        var stop = lifetime.Token;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        long after = long.TryParse(context.Request.Query["after"], out var parsed) ? Math.Max(0, parsed) : 0;
        string? previousStatus = null;
        var lastWrite = DateTimeOffset.UtcNow;
        async Task Write(string frame) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await context.Response.WriteAsync(frame, timeout.Token);
            await context.Response.Body.FlushAsync(timeout.Token);
            lastWrite = DateTimeOffset.UtcNow;
        }
        try {
            ClientPresence.Enter();
            Console.WriteLine($"[SSE V2] {DateTime.UtcNow:O} conexion={context.TraceIdentifier} abierta.");
            await Write("event: ready\ndata: " + JsonSerializer.Serialize(new { instanceId = queue.InstanceId, transport = "sse" }) + "\n\n");
            while (!stop.IsCancellationRequested) {
                var changed = queue.CaptureChange();
                var page = queue.Obtener(after, 20);
                if (page.CursorReset) after = 0;
                if (page.Eventos.Count > 0 || page.CursorReset) {
                    await Write("event: events\ndata: " + JsonSerializer.Serialize(new { success = true, data = page }) + "\n\n");
                    foreach (var item in page.Eventos)
                        Console.WriteLine($"[SSE V2] {DateTime.UtcNow:O} evento={item.EventId} enviado; reconocimiento-a-envio_ms={(DateTime.UtcNow - item.CapturedAtUtc).TotalMilliseconds:F1}");
                    after = page.NextCursor; // Per-connection delivery cursor only; queue remains durable.
                }
                string currentStatus = JsonSerializer.Serialize(status());
                if (currentStatus != previousStatus) {
                    await Write("event: status\ndata: " + currentStatus + "\n\n");
                    previousStatus = currentStatus;
                }
                if (page.HasMore) continue;
                // Local diagnostics only; no HTTP polling and no cloud requests.
                // WaitAsync releases its timer on a signal and never accumulates waiters.
                try { await changed.WaitAsync(TimeSpan.FromSeconds(3), stop); }
                catch (TimeoutException) {
                    if (DateTimeOffset.UtcNow - lastWrite >= TimeSpan.FromSeconds(15)) await Write(": heartbeat\n\n");
                }
            }
        } catch (OperationCanceledException) { }
        catch (IOException ex) { Console.WriteLine($"[SSE V2] {DateTime.UtcNow:O} conexion={context.TraceIdentifier}; error de transporte Windows={ex.HResult & 0xffff}"); }
        finally {
            ClientPresence.Leave();
            string reason = context.RequestAborted.IsCancellationRequested ? "cliente desconectado" : lifetime.IsCancellationRequested ? "autorizacion caducada" : "transporte interrumpido";
            Console.WriteLine($"[SSE V2] {DateTime.UtcNow:O} conexion={context.TraceIdentifier} cerrada: {reason}.");
        }
    }
}
