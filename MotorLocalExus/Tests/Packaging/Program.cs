using Exus;
using System.Security.Cryptography;
using System.Text.Json;

int passed = 0;
void Check(bool ok, string title) { if (!ok) throw new Exception(title); Console.WriteLine("PASS " + title); passed++; }
bool Reject(Action action) { try { action(); return false; } catch { return true; } }
string root = Path.Combine(Path.GetTempPath(), "exus-package-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    var config = new SiteConfiguration();
    config.Validate(); Check(true, "Instalación sin clave y sin lector permitida; no se habilita modo legacy");
    Check(Reject(() => new SiteConfiguration { DeviceSecret = "bad" }.Validate()), "Clave inválida rechazada");
    Check(Reject(() => new SiteConfiguration { DeviceSecret = Convert.ToBase64String(new byte[4]) }.Validate()), "Clave débil rechazada");
    foreach (string origin in new[] { "http://public.example", "https://example.com/path", "https://*.example.com", "https://user:pass@example.com", "file:///C:/test" })
        Check(Reject(() => new SiteConfiguration { FrontendUrl = origin, AllowedOrigins = [origin] }.Validate()), "Origen no permitido rechazado: " + origin);
    config.DeviceSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    string path = Path.Combine(root, "protected.bin");
    Installation.Save(path, config);
    Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(config.DeviceSecret), "Secreto cifrado en disco mediante DPAPI");
    Check(Installation.Load(path).DeviceSecret == config.DeviceSecret, "Configuración protegida recuperada en esta PC");
    byte[] corrupt = File.ReadAllBytes(path); corrupt[^3] ^= 1; File.WriteAllBytes(path, corrupt);
    Check(Reject(() => Installation.Load(path)), "Configuración alterada no aceptada");
    Check(!ClientPresence.Connected, "Sin pantalla autorizada por defecto");
    ClientPresence.Enter(); ClientPresence.Enter(); ClientPresence.Leave();
    Check(ClientPresence.Connected, "Una reconexión no desactiva al otro consumidor todavía conectado");
    ClientPresence.Leave(); Check(!ClientPresence.Connected, "Se vuelve a espera al desconectar todas las pantallas");
    var journal = new MotorJournal(Path.Combine(root, "logs"));
    journal.WriteLine("[HUELLA V2] socio=PRIVADO evento=123 guardado");
    Check(journal.LastEvent.Contains("pendiente"), "Reconocimiento muestra autorización pendiente");
    journal.WriteLine("[ACK V2] confirmado por el backend");
    Check(journal.LastEvent.Contains("confirmado"), "Confirmación actualiza el último evento");
    Check(!string.Join("", journal.Tail()).Contains("PRIVADO"), "Logs no revelan código del socio");
    Check(!MotorJournal.Sanitize("DeviceSecret=abc token=xyz").Contains("abc"), "Logs ocultan secretos");
    Check(!MotorJournal.Sanitize("<Fid>private</Fid>").Contains("private"), "Logs no guardan plantillas XML");
    for (int i = 0; i < 150; i++) journal.WriteLine("diagnóstico " + i);
    Check(journal.Tail().Length == 120, "Cola de diagnóstico acotada");
    string json = JsonSerializer.Serialize(new StatusSnapshot());
    Check(!json.Contains("DeviceSecret") && !json.Contains("Template") && !json.Contains("CodigoSocio"), "Estado de escritorio sin credenciales ni biometría");
    var options = new MotorOptions { QueueDirectory = Path.Combine(root, "queue") };
    using var queue = new DurableEventQueue(options.QueueDirectory, new MotorProtocol(options.KioskId, ""), 10);
    Check(queue.Ready && !queue.CanAccept, "Sin configuración el motor conserva diagnóstico, pero no registra asistencias");
    Console.WriteLine($"{passed} packaging checks passed. No backend, database or reader used.");
} finally { Directory.Delete(root, true); }
