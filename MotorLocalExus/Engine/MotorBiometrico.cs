using DPUruNet;
using Exus;

// El servicio corre en Session 0 y no debe abrir el SDK USB directamente.
// Toda captura se ejecuta en el auxiliar interactivo, que comparte la sesion
// del usuario donde Windows expone el lector DigitalPersona.
public static class MotorBiometrico
{
    public static ReaderBridge? Bridge { get; set; }
    private static readonly SemaphoreSlim OperationLock = new(1, 1);
    private static string _ultimoError = "";
    private static DateTime? _ultimoUsoUtc;
    private static bool _enrolando;
    private static int _manualCaptureRequests;

    public static bool LectorConectado => Bridge?.Connected == true && Bridge.Status.Connected;
    public static bool AuxiliarConectado => Bridge?.Connected == true;
    public static bool LectorOcupado => OperationLock.CurrentCount == 0;
    public static bool Enrolando => _enrolando;
    public static bool ManualCaptureRequested => Volatile.Read(ref _manualCaptureRequests) > 0;
    public static string Estado => !AuxiliarConectado ? "waiting_agent" : !LectorConectado ? (_ultimoError.Length > 0 ? "error" : "disconnected") : LectorOcupado ? "busy" : "ready";
    public static DateTime? UltimoUsoUtc => _ultimoUsoUtc ?? Bridge?.Status.LastUseUtc;
    public static string UltimoError => _ultimoError.Length > 0 ? _ultimoError : Bridge?.Status.Error ?? "";

    public static async Task EnsureReaderAsync(CancellationToken cancellationToken)
    {
        if (ManualCaptureRequested) return;
        var bridge = Bridge;
        if (bridge == null || !bridge.Connected) return;
        try { Apply(await bridge.Call("status", null, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { _ultimoError = ex.Message; }
    }

    public static Task<Fmd?> CapturarHuellaAsync(Func<string, Task> onMessage, CancellationToken cancellationToken = default) => Capture("capture-manual", onMessage, cancellationToken, manual: true);
    public static Task<Fmd?> CapturarHuellaSilenciosaAsync(CancellationToken cancellationToken = default) => Capture("capture", null, cancellationToken, manual: false);

    private static async Task<Fmd?> Capture(string kind, Func<string, Task>? onMessage, CancellationToken cancellationToken, bool manual)
    {
        if (manual) Interlocked.Increment(ref _manualCaptureRequests);
        var bridge = Bridge;
        bool acquired = false;
        try
        {
            if (bridge == null || !bridge.Connected)
            {
                _ultimoError = "READER_AGENT_NOT_CONNECTED";
                if (onMessage != null) await onMessage("El auxiliar del lector aún no está iniciado.");
                return null;
            }
            // The continuous detector uses short captures. Manual capture waits for
            // that one operation to finish, while the request flag prevents the
            // detector from taking the reader again before enrolment starts.
            if (!manual && ManualCaptureRequested) return null;
            if (manual)
                await OperationLock.WaitAsync(cancellationToken);
            else if (!await OperationLock.WaitAsync(0, cancellationToken))
                return null;
            acquired = true;
            _ultimoUsoUtc = DateTime.UtcNow;
            var reply = await bridge.Call(kind, onMessage, cancellationToken);
            Apply(reply);
            return Deserialize(reply.Template);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ultimoError = "CAPTURE_CANCELED";
            return null;
        }
        catch (Exception ex)
        {
            _ultimoError = "READER_BRIDGE_ERROR";
            Console.WriteLine("[MOTOR] Fallo de comunicación con el auxiliar (" + ex.GetType().Name + ").");
            if (onMessage != null) await onMessage("No se pudo comunicar con el lector. Se reintentará automáticamente.");
            return null;
        }
        finally
        {
            if (acquired) OperationLock.Release();
            if (manual) Interlocked.Decrement(ref _manualCaptureRequests);
        }
    }

    public static async Task<Fmd?> EnrolarSocioAsync(Func<string, Task> onMessage, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _manualCaptureRequests);
        bool acquired = false;
        try
        {
            var bridge = Bridge;
            if (bridge == null || !bridge.Connected)
            {
                _ultimoError = "READER_AGENT_NOT_CONNECTED";
                await onMessage("El auxiliar del lector aún no está iniciado.");
                return null;
            }
            // Wait for the current one-second detector capture instead of rejecting
            // the enrolment. The request flag gives this operation priority.
            await OperationLock.WaitAsync(cancellationToken);
            acquired = true;
            _enrolando = true; _ultimoUsoUtc = DateTime.UtcNow;
            var reply = await bridge.Call("enroll", onMessage, cancellationToken);
            Apply(reply);
            var enrollment = Deserialize(reply.Template);
            if (enrollment == null && string.IsNullOrWhiteSpace(_ultimoError)) _ultimoError = "CAPTURE_FAILED";
            return enrollment;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ultimoError = "CAPTURE_CANCELED";
            return null;
        }
        catch (Exception ex)
        {
            _ultimoError = "READER_BRIDGE_ERROR";
            Console.WriteLine("[MOTOR] Fallo de enrolamiento en el auxiliar (" + ex.GetType().Name + ").");
            await onMessage("No se pudo comunicar con el lector. Se reintentará automáticamente.");
            return null;
        }
        finally
        {
            _enrolando = false;
            if (acquired) OperationLock.Release();
            Interlocked.Decrement(ref _manualCaptureRequests);
        }
    }

    private static void Apply(ReaderReply reply)
    {
        _ultimoError = reply.Error ?? "";
        if (reply.LastUseUtc.HasValue) _ultimoUsoUtc = reply.LastUseUtc;
    }

    private static Fmd? Deserialize(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return Fmd.DeserializeXml(xml); }
        catch { _ultimoError = "FMD_INVALIDA"; return null; }
    }
}
