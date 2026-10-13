using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Exus;

public sealed record ReaderBridgeOptions(string PipeName, string ReaderImage);
public sealed class ReaderBridge : BackgroundService {
    private sealed record Operation(ReaderCommand Command, Func<string, Task>? Message, CancellationToken Token, TaskCompletionSource<ReaderReply> Result);
    private readonly Channel<Operation> _operations = Channel.CreateBounded<Operation>(1);
    private readonly ReaderBridgeOptions _options;
    private volatile bool _connected;
    public ReaderReply Status { get; private set; } = new();
    public bool Connected => _connected;
    public ReaderBridge(ReaderBridgeOptions options) { _options = options; }
    public async Task<ReaderReply> Call(string kind, Func<string, Task>? message, CancellationToken token) {
        if (!_connected) throw new IOException("Esperando al auxiliar del lector en la sesión de Windows.");
        var completion = new TaskCompletionSource<ReaderReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new ReaderCommand { Kind = kind };
        await _operations.Writer.WriteAsync(new Operation(command, message, token, completion), token);
        return await completion.Task.WaitAsync(token);
    }
    protected override async Task ExecuteAsync(CancellationToken stop) {
        var acl = new PipeSecurity();
        foreach (var sid in new[] { WellKnownSidType.LocalServiceSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        while (!stop.IsCancellationRequested) {
            try {
                using var pipe = NamedPipeServerStreamAcl.Create(_options.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 131072, acl);
                await pipe.WaitForConnectionAsync(stop);
                if (!ReaderWire.PeerIs(pipe, false, _options.ReaderImage)) { Console.WriteLine("[AUXILIAR] Conexión rechazada: proceso no autorizado."); continue; }
                _connected = true;
                Console.WriteLine("[AUXILIAR] Conectado desde la sesión de Windows. No requiere elevación diaria.");
                while (!stop.IsCancellationRequested && pipe.IsConnected) {
                    Operation? operation = null;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(stop)) {
                        idle.CancelAfter(5000);
                        try { operation = await _operations.Reader.ReadAsync(idle.Token); }
                        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                    }
                    var command = operation?.Command ?? new ReaderCommand(); // idle hardware health check; no network/HTTP
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop, operation?.Token ?? CancellationToken.None);
                    timeout.CancelAfter(command.Kind == "enroll" ? 180000 : command.Kind == "capture-manual" ? 18000 : 8000);
                    try {
                        await ReaderWire.Write(pipe, command, timeout.Token);
                        while (true) {
                            var reply = await ReaderWire.Read<ReaderReply>(pipe, timeout.Token);
                            if (reply.Id != command.Id) throw new InvalidDataException("Respuesta del lector fuera de secuencia.");
                            if (reply.Kind == "message") { if (operation?.Message != null) await operation.Message(reply.Message ?? ""); continue; }
                            if (reply.Kind != "result") throw new InvalidDataException("Respuesta del lector desconocida.");
                            bool wasReady = Status.Connected;
                            // Do not retain the fingerprint template in the public status object.
                            Status = new ReaderReply { Connected = reply.Connected, Error = reply.Error, LastUseUtc = reply.LastUseUtc };
                            if (wasReady != reply.Connected) Console.WriteLine(reply.Connected ? "[HARDWARE] Lector USB conectado mediante auxiliar de sesión." : "[HARDWARE] Lector no disponible; se reintentará automáticamente.");
                            if (command.Kind != "status")
                            {
                                string result = string.IsNullOrWhiteSpace(reply.Template) ? "sin plantilla" : "plantilla recibida";
                                string code = string.IsNullOrWhiteSpace(reply.Error) ? "OK" : reply.Error;
                                Console.WriteLine($"[AUXILIAR] operación={command.Kind}; resultado={result}; código={code}.");
                            }
                            operation?.Result.TrySetResult(reply); break;
                        }
                    } catch (Exception ex) { operation?.Result.TrySetException(ex); throw; }
                }
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException) {
                Console.WriteLine("[AUXILIAR] Conexión interrumpida; esperando reconexión.");
                try { await Task.Delay(500, stop); } catch (OperationCanceledException) { }
            } finally {
                _connected = false; Status = new ReaderReply();
                while (_operations.Reader.TryRead(out var pending)) pending.Result.TrySetException(new IOException("El auxiliar del lector se desconectó."));
            }
        }
    }
}
