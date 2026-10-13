using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Exus;

public sealed class ReaderCommand {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "status";
}
public sealed class ReaderReply {
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "result";
    public string? Message { get; set; }
    public string? Template { get; set; }
    public bool Connected { get; set; }
    public string Error { get; set; } = "";
    public DateTime? LastUseUtc { get; set; }
}
public static class ReaderWire {
    public const string PipeName = "MotorLocalExus.Reader.v1";
    public const int MaxPacketBytes = 131072;
    public static async Task Write<T>(Stream stream, T value, CancellationToken token) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaxPacketBytes) throw new InvalidDataException("Paquete del lector demasiado grande.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token);
        await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> Read<T>(Stream stream, CancellationToken token) {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token);
        int length = BitConverter.ToInt32(prefix);
        if (length is < 2 or > MaxPacketBytes) throw new InvalidDataException("Tamaño de paquete del lector inválido.");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, token);
        return JsonSerializer.Deserialize<T>(body) ?? throw new InvalidDataException("Paquete del lector vacío.");
    }
    public static bool PeerIs(PipeStream pipe, bool peerIsServer, string expectedImage) {
        uint pid;
        bool ok = peerIsServer ? GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) : GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid);
        if (!ok) return false;
        string expectedName = Path.GetFileNameWithoutExtension(expectedImage);
        try {
            using var processInfo = Process.GetProcessById((int)pid);
            if (!string.Equals(processInfo.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase)) return false;
        } catch { return false; }
        // LocalService can be denied the full image path by some endpoint policies.
        // The pipe ACL plus the exact product process name remain enforced in that case.
        try {
            using var process = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION; no process mutation
            if (!process.IsInvalid) {
                var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
                if (QueryFullProcessImageName(process, 0, path, ref size))
                    return string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase);
            }
        } catch { }
        return true;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
}
