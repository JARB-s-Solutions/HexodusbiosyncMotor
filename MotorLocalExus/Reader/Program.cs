using System.Diagnostics;
using System.IO.Pipes;
using DPUruNet;
using Exus;

// No device key, member cache, database access or attendance authorization lives in this process.
// Only this component touches the SDK's session-bound capture API.
Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);
string pipeName = args.SkipWhile(a => a != "--pipe").Skip(1).FirstOrDefault() ?? ReaderWire.PipeName;
string serviceImage = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Service", "MotorLocalExus.Service.exe"));
if (!args.Contains("--worker")) {
    using var mutex = new Mutex(true, "Local\\MotorLocalExus.Reader.Supervisor", out bool first);
    if (!first) return;
    int delay = 1000;
    while (File.Exists(Environment.ProcessPath!) && File.Exists(serviceImage)) {
        var started = Stopwatch.GetTimestamp();
        try {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--worker"); info.ArgumentList.Add("--pipe"); info.ArgumentList.Add(pipeName);
            using var child = Process.Start(info)!;
            await child.WaitForExitAsync();
        } catch { }
        delay = Stopwatch.GetElapsedTime(started).TotalSeconds > 30 ? 1000 : Math.Min(delay * 2, 30000);
        await Task.Delay(delay);
    }
    return;
}

while (File.Exists(serviceImage)) {
    try {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connect = new CancellationTokenSource(5000)) await pipe.ConnectAsync(connect.Token);
        if (!ReaderWire.PeerIs(pipe, true, serviceImage)) { await Task.Delay(3000); continue; }
        while (pipe.IsConnected) {
            using var idle = new CancellationTokenSource(30000);
            var command = await ReaderWire.Read<ReaderCommand>(pipe, idle.Token);
            if (!Guid.TryParseExact(command.Id, "N", out _) || command.Kind is not ("status" or "capture" or "capture-manual" or "enroll")) throw new InvalidDataException("Comando no admitido.");
            int limit = command.Kind == "enroll" ? 185000 : command.Kind == "capture-manual" ? 20000 : 10000;
            using var operation = new CancellationTokenSource(limit);
            var task = Task.Run(async () => {
                async Task Message(string message) => await ReaderWire.Write(pipe, new ReaderReply { Id = command.Id, Kind = "message", Message = message }, operation.Token);
                Fmd? template = null;
                try {
                    switch (command.Kind) {
                        case "status": await MotorBiometrico.EnsureReaderAsync(operation.Token); break;
                        case "capture": template = await MotorBiometrico.CapturarHuellaSilenciosaAsync(operation.Token); break;
                        case "capture-manual": template = await MotorBiometrico.CapturarHuellaAsync(Message, operation.Token); break;
                        case "enroll": template = await MotorBiometrico.EnrolarSocioAsync(Message, operation.Token); break;
                    }
                    await ReaderWire.Write(pipe, new ReaderReply { Id = command.Id, Template = template == null ? null : Fmd.SerializeXml(template),
                        Connected = MotorBiometrico.LectorConectado, Error = MotorBiometrico.UltimoError, LastUseUtc = MotorBiometrico.UltimoUsoUtc }, operation.Token);
                } finally { template = null; }
            });
            try { await task.WaitAsync(TimeSpan.FromMilliseconds(limit + 1000)); }
            catch (TimeoutException) { Environment.Exit(2); } // Supervisor recovers a stuck native SDK process.
        }
    } catch { await Task.Delay(1000); }
}
