using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using Exus;

internal static class SetupActions {
    private static string ServiceExe => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Service", "MotorLocalExus.Service.exe"));
    internal static string ReaderExe => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Reader", "MotorLocalExus.Reader.exe"));
    public static ServiceController? Service() => ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == Installation.ServiceName);
    public static void Stop() {
        StopReaderAgents();
        using var service = Service();
        if (service == null || service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(35));
    }
    public static void StartReaderAgent() {
        // El servicio corre en Session 0 y el SDK DigitalPersona necesita un
        // proceso auxiliar dentro de la sesión interactiva del usuario. El
        // arranque de Windows puede ocurrir antes de que el servicio termine
        // de iniciar, por eso esta operación es segura y se puede reintentar.
        if (!File.Exists(ReaderExe)) return;
        try {
            foreach (var process in Process.GetProcessesByName("MotorLocalExus.Reader")) {
                try {
                    string? current = process.MainModule?.FileName;
                    if (current != null && string.Equals(Path.GetFullPath(current), ReaderExe, StringComparison.OrdinalIgnoreCase)) return;
                } catch { }
                finally { process.Dispose(); }
            }

            var start = new ProcessStartInfo(ReaderExe) {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ReaderExe)!
            };
            Process.Start(start)?.Dispose();
        } catch {
            // El siguiente ciclo de estado volverá a intentarlo. No se debe
            // cerrar la interfaz ni el motor porque el lector es opcional.
        }
    }
    public static void StopReaderAgents() {
        foreach (var process in Process.GetProcessesByName("MotorLocalExus.Reader")) {
            try {
                string? image = process.MainModule?.FileName;
                if (image != null && string.Equals(Path.GetFullPath(image), ReaderExe, StringComparison.OrdinalIgnoreCase)) process.Kill(entireProcessTree: true);
            } catch { }
            finally { process.Dispose(); }
        }
    }
    public static void Start() {
        using var service = Service();
        if (service == null || service.Status == ServiceControllerStatus.Running) return;
        service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25));
    }
    private static void Sc(params string[] args) {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(); string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000) || process.ExitCode != 0) throw new InvalidOperationException("No se pudo configurar el servicio. " + output + error);
    }
    public static void Register() {
        if (!File.Exists(ServiceExe)) throw new FileNotFoundException("No se encontró el ejecutable del servicio.");
        using var existing = Service();
        Sc(existing == null ? "create" : "config", Installation.ServiceName, "binPath=", "\"" + ServiceExe + "\"", "start=", "auto", "obj=", "NT AUTHORITY\\LocalService", "DisplayName=", "HEXODUS BioSync");
        Sc("description", Installation.ServiceName, "Motor de huellas HEXODUS. Inicia con Windows; cerrar la ventana de estado no lo detiene.");
        Sc("failure", Installation.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
        Sc("failureflag", Installation.ServiceName, "1");
        Start();
        for (int attempt = 0; attempt < 15; attempt++) {
            try { _ = StatusClient.Read().GetAwaiter().GetResult(); return; }
            catch { Thread.Sleep(500); }
        }
        throw new InvalidOperationException("El servicio no respondió después de iniciarse. Revisa el puerto 4000 y los logs del motor.");
    }
    public static int Preflight(string? result) {
        try {
            Stop();
            foreach (var ip in new[] { System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback }) {
                var listener = new System.Net.Sockets.TcpListener(ip, 4000);
                try { listener.Server.ExclusiveAddressUse = true; listener.Start(); } finally { listener.Stop(); }
            }
            return 0;
        } catch {
            if (result != null) File.WriteAllText(result, "El puerto local 4000 está ocupado o el servicio anterior no se pudo detener. Cierra la otra instancia del motor y vuelve a intentar. No se cerrará ningún otro programa automáticamente.");
            return 1;
        }
    }
    public static int Pending() {
        string file = Path.Combine(Installation.DataRoot, "private", "queue", "pending-events-v1.json");
        if (!File.Exists(file)) return 0;
        try { using var json = JsonDocument.Parse(File.ReadAllBytes(file)); return json.RootElement.GetProperty("Pending").GetArrayLength(); }
        catch { return 1; }
    }
    public static void Remove() {
        Stop();
        using var service = Service();
        if (service != null) Sc("delete", Installation.ServiceName);
        string root = Path.GetFullPath(Installation.DataRoot);
        string parent = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if (Path.GetDirectoryName(root) != parent || Path.GetFileName(root) != "Motor Local Exus") throw new InvalidOperationException("Ruta de datos inesperada.");
        if (Directory.Exists(root)) SafeRemove(new DirectoryInfo(root));
    }
    private static void SafeRemove(DirectoryInfo directory) {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) { directory.Delete(); return; }
        foreach (var file in directory.GetFiles()) file.Delete();
        foreach (var child in directory.GetDirectories()) SafeRemove(child);
        directory.Delete();
    }
    public static void SaveAndStart(SiteConfiguration config) {
        if (!Installation.IsAdmin) throw new UnauthorizedAccessException("Abre Configuración con permisos de administrador para guardar estos ajustes.");
        config.Validate();
        string privatePath = Path.Combine(Installation.DataRoot, "private");
        Installation.RestrictDirectory(Installation.DataRoot);
        Installation.RestrictDirectory(privatePath);
        string configPath = Installation.ConfigPath(Installation.DataRoot);
        SiteConfiguration? old = File.Exists(configPath) ? Installation.Load(configPath) : null;
        Stop();
        bool identityChanged = old != null && (old.DeviceSecret != config.DeviceSecret || old.KioskId != config.KioskId);
        if (identityChanged && Pending() > 0) {
            Start();
            throw new InvalidOperationException("Hay eventos pendientes. Deben procesarse antes de cambiar la clave o la terminal.");
        }
        string queuePath = Path.Combine(privatePath, "queue", "pending-events-v1.json");
        byte[]? oldEmptyQueue = identityChanged && File.Exists(queuePath) ? File.ReadAllBytes(queuePath) : null;
        if (oldEmptyQueue != null) File.Delete(queuePath); // stopped writer, verified empty; new kiosk gets a new empty journal
        Installation.Save(configPath, config);
        try { Register(); }
        catch {
            try {
                Stop();
                if (old != null && (!identityChanged || Pending() == 0)) {
                    Installation.Save(configPath, old);
                    if (oldEmptyQueue != null) File.WriteAllBytes(queuePath, oldEmptyQueue);
                    Start();
                }
            } catch { }
            throw;
        }
    }
    public static void CreateLocalProfile(string? root) {
        if (root == null) throw new ArgumentException("Falta la carpeta indicada.");
        var config = new SiteConfiguration { DeviceSecret = File.ReadAllText(Path.Combine(root, ".tool-cache", "reader-live-test", "device-secret.txt")).Trim() };
        Installation.Save(Path.Combine(root, "MotorLocalExus.exusconfig"), config);
    }
}
