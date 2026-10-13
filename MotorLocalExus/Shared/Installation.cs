using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Exus;

public static class Installation {
    public const string ServiceName = "MotorLocalExus";
    public const string PipeName = "MotorLocalExus.Status.v1";
    public static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Motor Local Exus");
    public static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MotorLocalExus.configuration.v1");
    public static string ConfigPath(string root) => Path.Combine(root, "private", "configuration.bin");
    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void RestrictDirectory(string path) {
        Directory.CreateDirectory(path);
        if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("No se permite una carpeta de datos redirigida.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.LocalServiceSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    public static void Save(string path, SiteConfiguration config) {
        config.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config);
        try {
            byte[] encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
            string temp = path + ".tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(encrypted); file.Flush(true); }
            File.Move(temp, path, true);
        } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static SiteConfiguration Load(string path) {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("La configuración excede el tamaño permitido.");
        byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
        try {
            var config = JsonSerializer.Deserialize<SiteConfiguration>(bytes) ?? throw new InvalidDataException("Configuración vacía.");
            config.Validate(); return config;
        } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public sealed class SiteConfiguration {
    public int Version { get; set; } = 1;
    public string KioskId { get; set; } = "KIOSKO-HUELLA-01";
    public string[] AllowedOrigins { get; set; } = ["http://localhost:3000"];
    public string DeviceSecret { get; set; } = "";
    public string FrontendUrl { get; set; } = "http://localhost:3000";
    public void Validate() {
        if (Version != 1 || string.IsNullOrWhiteSpace(KioskId) || KioskId.Length > 100) throw new InvalidDataException("Identificador de terminal inválido.");
        if (AllowedOrigins is null || AllowedOrigins.Length is < 1 or > 10 || AllowedOrigins.Any(s => !ValidOrigin(s)))
            throw new InvalidDataException("Usa direcciones exactas: https://tu-dominio o http://localhost:3000, sin rutas ni comodines.");
        if (!ValidOrigin(FrontendUrl) || !AllowedOrigins.Contains(FrontendUrl)) throw new InvalidDataException("La dirección de HEXODUS debe estar entre los orígenes permitidos.");
        if (!string.IsNullOrEmpty(DeviceSecret)) {
            byte[] key;
            try { key = Convert.FromBase64String(DeviceSecret); } catch { throw new InvalidDataException("La clave de comunicación debe estar en formato Base64."); }
            if (key.Length is < 32 or > 128) throw new InvalidDataException("La clave de comunicación requiere entre 32 y 128 bytes.");
            CryptographicOperations.ZeroMemory(key);
        }
    }
    private static bool ValidOrigin(string s) => Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
        uri.GetLeftPart(UriPartial.Authority) == s && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback);
}

public sealed class StatusSnapshot {
    public string Version { get; set; } = "2.2.0";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StartedAt { get; set; }
    public bool Configured { get; set; }
    public bool ReaderConnected { get; set; }
    public bool ReaderAgentConnected { get; set; }
    public string ReaderError { get; set; } = "";
    public string Detector { get; set; } = "starting";
    public bool ScreenConnected { get; set; }
    public int Fingerprints { get; set; }
    public int Pending { get; set; }
    public bool QueueReady { get; set; }
    public bool CacheStale { get; set; }
    public DateTimeOffset? LastSyncUtc { get; set; }
    public string LastEvent { get; set; } = "Esperando la pantalla de asistencia.";
    public DateTimeOffset? LastEventAt { get; set; }
    public string FrontendUrl { get; set; } = "";
    public string[] Logs { get; set; } = [];
}
