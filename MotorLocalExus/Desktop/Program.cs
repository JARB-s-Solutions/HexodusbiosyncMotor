using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Exus;

internal static class Program {
    [STAThread]
    private static int Main(string[] args) {
        ApplicationConfiguration.Initialize();
        try {
            if (args.Contains("--preflight")) return SetupActions.Preflight(Argument(args, "--result"));
            if (args.Contains("--stop")) { SetupActions.Stop(); return 0; }
            if (args.Contains("--start")) { SetupActions.Start(); return 0; }
            if (args.Contains("--remove")) { SetupActions.Remove(); return 0; }
            if (args.Contains("--pending")) return SetupActions.Pending() > 0 ? 10 : 0;
            if (args.Contains("--profile-from-local")) { SetupActions.CreateLocalProfile(Argument(args, "--root")); return 0; }
            if (args.Contains("--probe")) {
                var probe = Task.Run(ReaderProbe.Find);
                string found = Task.WhenAny(probe, Task.Delay(8000)).GetAwaiter().GetResult() == probe ? probe.GetAwaiter().GetResult() :
                    "El lector no respondió a tiempo. Puedes continuar la instalación sin conectarlo.";
                MessageBox.Show(found, "Comprobación opcional del lector", MessageBoxButtons.OK, MessageBoxIcon.Information); return 0;
            }
            if (args.Contains("--setup") || args.Contains("--configure")) {
                if (!Installation.IsAdmin) {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                    foreach (string arg in args) start.ArgumentList.Add(arg);
                    Process.Start(start); return 0;
                }
                using var form = new ConfigurationForm(Argument(args, "--profile"), args.Contains("--setup"));
                return form.ShowDialog() == DialogResult.OK ? 0 : 2;
            }
            if (args.Contains("--restore-profile")) {
                string? profile = Argument(args, "--restore-profile");
                if (profile == null || !File.Exists(profile)) throw new FileNotFoundException("No se encontro el perfil protegido.");
                if (!Installation.IsAdmin) {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                    foreach (string arg in args) start.ArgumentList.Add(arg);
                    Process.Start(start); return 0;
                }
                SetupActions.SaveAndStart(Installation.Load(profile));
                return 0;
            }
            if (args.Contains("--render-preview") || args.Contains("--render-configuration")) {
                using Form form = args.Contains("--render-configuration") ? new ConfigurationForm(Argument(args, "--profile"), true) : new StatusForm(false, Argument(args, "--pipe"));
                form.Show();
                var until = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Argument(args, "--output")!, System.Drawing.Imaging.ImageFormat.Png); return 0;
            }
            using var mutex = new Mutex(true, "Local\\MotorLocalExus.Desktop", out bool first);
            if (!first) {
                nint handle = NativeWindowAccess.FindWindow(null, "HEXODUS BioSync");
                if (handle != 0) { NativeWindowAccess.ShowWindow(handle, 9); NativeWindowAccess.SetForegroundWindow(handle); }
                return 0;
            }
            // El servicio se ejecuta con LocalService. Este auxiliar se inicia en
            // la sesión interactiva, donde el controlador DigitalPersona expone
            // el USB. No requiere elevación ni una configuración diaria.
            SetupActions.StartReaderAgent();
            Application.Run(new StatusForm(args.Contains("--tray"), Argument(args, "--pipe")));
            return 0;
        } catch (Exception ex) {
            MessageBox.Show(ex.Message, "HEXODUS BioSync", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1;
        }
    }
    internal static string? Argument(string[] args, string key) => args.SkipWhile(a => a != key).Skip(1).FirstOrDefault();
}

internal static class NativeWindowAccess {
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern nint FindWindow(string? className, string windowName);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint window, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint window);
}

internal static class StatusClient {
    public static async Task<StatusSnapshot> Read(string? name = null) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", name ?? Installation.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var output = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await pipe.ReadAsync(chunk, timeout.Token)) > 0) {
            if (output.Length + read > 262144) throw new InvalidDataException("Respuesta de estado demasiado grande.");
            output.Write(chunk, 0, read);
        }
        return JsonSerializer.Deserialize<StatusSnapshot>(output.ToArray()) ?? throw new InvalidDataException("Estado no disponible.");
    }
}

internal static class ReaderProbe {
    public static string Find() {
        try { return Enumerate(); }
        catch (DllNotFoundException) { return "No está disponible el controlador o el entorno de DigitalPersona. Instálalo por separado. Puedes continuar sin lector."; }
        catch (Exception) { return "No se pudo comprobar el lector. Revisa su conexión y el controlador. Puedes continuar la instalación."; }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string Enumerate() {
        var readers = DPUruNet.ReaderCollection.GetReaders();
        return readers.Count > 0 ? "Se detectó un lector DigitalPersona. Esta comprobación no captura huellas. Puedes continuar." :
            "No se detectó un lector. Comprueba la conexión y su controlador cuando vayas a utilizarlo. Puedes continuar sin lector.";
    }
}
