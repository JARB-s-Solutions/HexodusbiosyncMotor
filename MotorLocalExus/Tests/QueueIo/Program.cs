using System.Diagnostics;

if (!OperatingSystem.IsWindows()) { Console.WriteLine("Windows-only file lock test"); return; }
var tempRoot = Path.GetFullPath(Path.GetTempPath());
var directory = Path.GetFullPath(Path.Combine(tempRoot, "hexodus-queue-io-" + Guid.NewGuid().ToString("N")));
if (!directory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid test directory");
var path = Path.Combine(directory, "pending-events-v1.json");
var key = Convert.ToBase64String(Enumerable.Repeat((byte)23, 32).ToArray());
var protocol = new MotorProtocol("ISOLATED-IO-TEST", key);
int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
try {
    using (var queue = new DurableEventQueue(directory, protocol, 10)) {
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            var watch = Stopwatch.StartNew();
            try { queue.RegistrarMatch(new EventoHuella { CodigoSocio = "ISOLATED-REJECTED" }); throw new Exception("Should fail while held"); }
            catch (ProtocolException error) { Check(error.Code == "QUEUE_WRITE_FAILED", "permanent lock fails without advertising event"); }
            Check(queue.LastIoErrorCode is 32 or 33, "Windows sharing violation classified");
            Check(queue.Count == 0, "failed write does not change in-memory queue");
            Check(watch.Elapsed < TimeSpan.FromSeconds(3), "bounded retries do not hang reader");
        }
        var heldShort = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(50); heldShort.Dispose(); });
        queue.RegistrarMatch(new EventoHuella { CodigoSocio = "ISOLATED-RECOVERED" });
        await release;
        Check(queue.Count == 1 && queue.LastError == null && queue.LastIoErrorCode == null, "short lock recovers and clears queue error");
    }
    using (var reopened = new DurableEventQueue(directory, protocol, 10)) {
        Check(reopened.Count == 1 && reopened.Obtener(0, 20).Eventos[0].CodigoSocio == "ISOLATED-RECOVERED", "recovered write survives restart without phantom event");
    }
    Console.WriteLine($"{checks} IO checks passed; no live queue, USB or database accessed");
} finally {
    foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
