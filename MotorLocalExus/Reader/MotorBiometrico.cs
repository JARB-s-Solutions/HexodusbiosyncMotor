using DPUruNet;

public static class MotorBiometrico {
    private static Reader? _lectorGlobal = null;
    private static readonly SemaphoreSlim _lectorLock = new(1, 1);
    private static DateTime? _ultimoUsoUtc = null;
    private static string _ultimoError = "";
    private static bool _enrolando;
    public static bool LectorConectado => _lectorGlobal != null;
    public static bool LectorOcupado => _lectorLock.CurrentCount == 0;
    public static bool Enrolando => _enrolando;
    public static string Estado => !LectorConectado ? (_ultimoError.Length > 0 ? "error" : "disconnected") : LectorOcupado ? "busy" : "ready";
    public static DateTime? UltimoUsoUtc => _ultimoUsoUtc;
    public static string UltimoError => _ultimoError;

    // Conecta el hardware 1 sola vez y lo deja abierto
    private static void InicializarLector() {
        try {
            if (_lectorGlobal == null) {
                ReaderCollection readers = ReaderCollection.GetReaders();
                if (readers.Count > 0) {
                    var candidate = readers[0];
                    var opened = candidate.Open(Constants.CapturePriority.DP_PRIORITY_EXCLUSIVE);
                    if (opened != Constants.ResultCode.DP_SUCCESS) {
                        candidate.Dispose();
                        _ultimoError = opened.ToString();
                        return;
                    }
                    if (candidate.Capabilities.Resolutions.Length == 0) {
                        candidate.Dispose();
                        _ultimoError = "READER_NO_RESOLUTION";
                        return;
                    }
                    _lectorGlobal = candidate;
                    _ultimoError = "";
                    Console.WriteLine("[HARDWARE] Lector USB conectado y en espera permanente.");
                }
            }
        } catch (Exception ex) {
            _ultimoError = "READER_INITIALIZATION_FAILED";
            Console.WriteLine("[HARDWARE] No se pudo inicializar el lector (" + ex.GetType().Name + ").");
        }
    }

    // Auto-recuperación por si desconectan el cable
    private static void ReiniciarLector() {
        if (_lectorGlobal != null) {
            try { _lectorGlobal.Dispose(); } catch { }
            _lectorGlobal = null;
        }
        // The detector applies backoff before reopening; do not retry recursively.
    }

    public static async Task EnsureReaderAsync(CancellationToken cancellationToken) {
        if (!await _lectorLock.WaitAsync(0, cancellationToken)) return;
        try {
            if (_lectorGlobal == null) InicializarLector();
            else if (_lectorGlobal.GetStatus() != Constants.ResultCode.DP_SUCCESS) {
                _ultimoError = "READER_STATUS_FAILED";
                ReiniciarLector();
            }
        } catch (Exception ex) {
            _ultimoError = "READER_STATUS_FAILED";
            Console.WriteLine("[HARDWARE] Estado USB no disponible (" + ex.GetType().Name + ").");
            ReiniciarLector();
        } finally { _lectorLock.Release(); }
    }

    private static bool IsHardwareFailure(Constants.ResultCode code) => code is Constants.ResultCode.DP_DEVICE_FAILURE or Constants.ResultCode.DP_INVALID_DEVICE or Constants.ResultCode.DP_DEVICE_BUSY;

    private static async Task<CaptureResult> CaptureAsync(int timeout, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = _lectorGlobal ?? throw new InvalidOperationException("Reader unavailable");
        // SDK capture is synchronous. Keep the semaphore held until native capture actually ends.
        var result = await Task.Run(() => reader.Capture(Constants.Formats.Fid.ANSI, Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT, timeout, reader.Capabilities.Resolutions[0]));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public static async Task<Fmd?> CapturarHuellaAsync(Func<string, Task> onMessage, CancellationToken cancellationToken = default) {
        await _lectorLock.WaitAsync(cancellationToken);
        try {
            _ultimoUsoUtc = DateTime.UtcNow;
            if (_lectorGlobal == null) InicializarLector();
            if (_lectorGlobal == null) {
                _ultimoError = "READER_NOT_FOUND";
                await onMessage("Error: lector no encontrado. Conecta el USB y vuelve a intentarlo.");
                return null;
            }

            Fmd? fmdFinal = null;
            // ¡La lectura ahora arranca instantáneamente!
            CaptureResult captureResult = await CaptureAsync(15000, cancellationToken);

            // Si el lector fue desconectado a la fuerza, el SDK lanza DP_DEVICE_FAILURE
            if (IsHardwareFailure(captureResult.ResultCode) || captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_READER_FAILED) {
                await onMessage("Lector desconectado. Reiniciando...");
                _ultimoError = "DP_DEVICE_FAILURE";
                ReiniciarLector();
                return null;
            }

            if (captureResult.ResultCode == Constants.ResultCode.DP_SUCCESS && captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_GOOD) {
                DataResult<Fmd> fmdResult = FeatureExtraction.CreateFmdFromFid(captureResult.Data, Constants.Formats.Fmd.ANSI);
                if (fmdResult.ResultCode == Constants.ResultCode.DP_SUCCESS) { fmdFinal = fmdResult.Data; _ultimoError = ""; }
                else _ultimoError = "FMD_EXTRACTION_FAILED";
            } else if (captureResult.ResultCode != Constants.ResultCode.DP_SUCCESS) {
                _ultimoError = captureResult.ResultCode.ToString();
                await onMessage($"El lector no completó la lectura ({_ultimoError}).");
            } else if (captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_CANCELED) {
                _ultimoError = "CAPTURE_CANCELED";
                await onMessage("La captura fue cancelada.");
            } else if (captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_TIMED_OUT) {
                _ultimoError = "CAPTURE_TIMEOUT";
                await onMessage("No se detectó un dedo a tiempo.");
            } else if (captureResult.Quality != Constants.CaptureQuality.DP_QUALITY_GOOD) {
                _ultimoError = "CAPTURE_QUALITY_LOW";
                await onMessage("La lectura no tuvo calidad suficiente. Limpia el sensor e inténtalo de nuevo.");
            }

            // ELIMINAMOS EL _lectorGlobal.Dispose() PARA MANTENERLO VIVO
            return fmdFinal;
        } finally {
            _lectorLock.Release();
        }
    }

    public static async Task<Fmd?> CapturarHuellaSilenciosaAsync(CancellationToken cancellationToken) {
        if (!await _lectorLock.WaitAsync(0, cancellationToken)) {
            return null;
        }

        try {
            _ultimoUsoUtc = DateTime.UtcNow;
            if (_lectorGlobal == null) InicializarLector();
            if (_lectorGlobal == null) return null;

            CaptureResult captureResult = await CaptureAsync(1000, cancellationToken);

            if (IsHardwareFailure(captureResult.ResultCode) || captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_READER_FAILED) {
                Console.WriteLine("[DETECTOR] Lector desconectado. Reiniciando...");
                _ultimoError = "DP_DEVICE_FAILURE";
                ReiniciarLector();
                return null;
            }

            if (captureResult.ResultCode != Constants.ResultCode.DP_SUCCESS || captureResult.Quality != Constants.CaptureQuality.DP_QUALITY_GOOD) {
                if (captureResult.ResultCode != Constants.ResultCode.DP_SUCCESS && captureResult.Quality is not (Constants.CaptureQuality.DP_QUALITY_TIMED_OUT or Constants.CaptureQuality.DP_QUALITY_NO_FINGER)) {
                    _ultimoError = captureResult.ResultCode.ToString();
                }
                return null;
            }

            DataResult<Fmd> fmdResult = FeatureExtraction.CreateFmdFromFid(captureResult.Data, Constants.Formats.Fmd.ANSI);
            if (fmdResult.ResultCode != Constants.ResultCode.DP_SUCCESS) { _ultimoError = "FMD_EXTRACTION_FAILED"; return null; }

            _ultimoError = "";
            return fmdResult.Data;
        } finally {
            _lectorLock.Release();
        }
    }

    public static async Task<Fmd?> EnrolarSocioAsync(Func<string, Task> onMessage, CancellationToken cancellationToken = default) {
        await _lectorLock.WaitAsync(cancellationToken);
        try {
            _enrolando = true;
            _ultimoUsoUtc = DateTime.UtcNow;
            if (_lectorGlobal == null) InicializarLector();
            if (_lectorGlobal == null) {
                _ultimoError = "READER_NOT_FOUND";
                await onMessage("Lector no conectado.");
                return null;
            }

            List<Fmd> preEnrollmentFmds = new List<Fmd>();
            int count = 0;

            while (count < 4) {
                cancellationToken.ThrowIfCancellationRequested();
                await onMessage($"[Toque {count + 1} de 4] Ponga el dedo en el lector...");

                CaptureResult captureResult = await CaptureAsync(15000, cancellationToken);

                if (IsHardwareFailure(captureResult.ResultCode) || captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_READER_FAILED) {
                    await onMessage("Lector desconectado.");
                    _ultimoError = "DP_DEVICE_FAILURE";
                    ReiniciarLector();
                    return null;
                }

                if (captureResult.ResultCode == Constants.ResultCode.DP_SUCCESS) {
                    if (captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_GOOD) {
                        DataResult<Fmd> fmdResult = FeatureExtraction.CreateFmdFromFid(captureResult.Data, Constants.Formats.Fmd.ANSI);
                        if (fmdResult.ResultCode == Constants.ResultCode.DP_SUCCESS) {
                            preEnrollmentFmds.Add(fmdResult.Data);
                            count++;
                            _ultimoError = "";
                            await onMessage($"-> Lectura {count} exitosa. Levanta el dedo por favor.");
                            await Task.Delay(1000, cancellationToken);
                        } else {
                            _ultimoError = "FMD_EXTRACTION_FAILED";
                            await onMessage("No se pudo procesar la lectura. Retira el dedo y vuelve a intentarlo.");
                        }
                    } else if (captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_CANCELED) {
                        _ultimoError = "CAPTURE_CANCELED";
                        await onMessage("La captura fue cancelada.");
                        break;
                    } else if (captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_TIMED_OUT) {
                        _ultimoError = "CAPTURE_TIMEOUT";
                        await onMessage("No se detectó un dedo a tiempo.");
                    } else {
                        _ultimoError = "CAPTURE_QUALITY_LOW";
                        await onMessage($"-> Mala calidad. Limpie el sensor.");
                        await Task.Delay(1000, cancellationToken);
                    }
                } else {
                    _ultimoError = captureResult.ResultCode.ToString();
                    await onMessage($"El lector no completó la lectura ({_ultimoError}).");
                    break;
                }
            }

            if (preEnrollmentFmds.Count == 4) {
                DataResult<Fmd> result = DPUruNet.Enrollment.CreateEnrollmentFmd(Constants.Formats.Fmd.ANSI, preEnrollmentFmds);
                if (result.ResultCode == Constants.ResultCode.DP_SUCCESS) { _ultimoError = ""; return result.Data; }
                _ultimoError = "ENROLLMENT_FMD_FAILED";
                await onMessage("No se pudo generar la plantilla final. Repite las cuatro lecturas.");
            } else if (string.IsNullOrWhiteSpace(_ultimoError)) {
                _ultimoError = "ENROLLMENT_INCOMPLETE";
                await onMessage("La captura quedó incompleta. Se requieren cuatro lecturas.");
            }
            return null;
        } finally {
            _enrolando = false;
            _lectorLock.Release();
        }
    }
}
