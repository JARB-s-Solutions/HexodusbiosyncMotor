using System.Diagnostics;
using System.Text.Json;
using Exus;

internal sealed class StatusForm : Form {
    private readonly Label _summary = new() { AutoSize = true, Text = "Consultando el servicio…", Font = new Font("Segoe UI", 13, FontStyle.Bold) };
    private readonly Dictionary<string, Label> _values = new();
    private readonly TextBox _logs = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9) };
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2500 };
    private readonly string? _pipe;
    private readonly bool _startHidden;
    private bool _busy;
    private bool _exit;
    private StatusSnapshot? _last;
    public StatusForm(bool hidden, string? pipe) {
        _pipe = pipe; _startHidden = hidden;
        Text = "HEXODUS BioSync"; Size = new Size(830, 750); MinimumSize = new Size(720, 640);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Color.White;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 6 };
        for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = "HEXODUS BioSync", AutoSize = true, Font = new Font("Segoe UI", 22, FontStyle.Bold), Margin = new Padding(0,0,0,10) });
        root.Controls.Add(_summary);
        var grid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(0,15,0,12) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (string key in new[] { "Servicio", "Lector de huellas", "Pantalla de asistencia", "Socios con huella", "Eventos pendientes", "Última sincronización", "Último evento", "Comunicación" }) {
            grid.Controls.Add(new Label { Text = key, AutoSize = true, Margin = new Padding(0,4,15,4) });
            var value = new Label { Text = "—", AutoSize = true, MaximumSize = new Size(520,0), Margin = new Padding(0,4,0,4) };
            _values[key] = value; grid.Controls.Add(value);
        }
        root.Controls.Add(grid);
        root.Controls.Add(new Label { Text = "Información técnica del motor · no modifica datos de HEXODUS", AutoSize = true, Margin = new Padding(0,5,0,8) });
        root.Controls.Add(_logs);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0,12,0,0) };
        AddButton(actions, "Actualizar", async () => await RefreshStatus());
        AddButton(actions, "Exportar diagnóstico", Export);
        AddButton(actions, "Configuración…", Configure);
        AddButton(actions, "Ocultar", () => Hide());
        root.Controls.Add(actions); Controls.Add(root);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Ver estado", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Cerrar ventana de estado", null, (_, _) => { _exit = true; Close(); });
        _tray = new NotifyIcon { Icon = Icon, Text = "HEXODUS BioSync", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        _timer.Tick += async (_, _) => {
            // Reintenta el auxiliar en cada actualización. Esto cubre el
            // caso de Windows donde el servicio/tray arranca antes que el
            // controlador USB o el proceso auxiliar es detenido por una
            // política de seguridad temporal.
            SetupActions.StartReaderAgent();
            await RefreshStatus();
        };
        Shown += async (_, _) => { if (_startHidden) Hide(); await RefreshStatus(); _timer.Start(); };
        FormClosing += (_, e) => { if (!_exit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _tray.Dispose(); } base.Dispose(disposing); }
    private static void AddButton(Control parent, string label, Action action) {
        var button = new Button { Text = label, AutoSize = true, Padding = new Padding(7,4,7,4), Margin = new Padding(0,0,8,0) };
        button.Click += (_, _) => action(); parent.Controls.Add(button);
    }
    private async Task RefreshStatus() {
        if (_busy || IsDisposed) return; _busy = true;
        try {
            var status = await StatusClient.Read(_pipe); if (IsDisposed) return; _last = status;
            _summary.Text = !status.Configured ? "Motor activo · falta configurar la comunicación" :
                !status.QueueReady ? "Motor activo · revisa el almacenamiento de eventos" :
                !status.ReaderConnected ? "Motor activo · esperando el lector" :
                !status.ScreenConnected ? "Motor listo · esperando la pantalla de asistencia" :
                status.Detector == "error" ? "Motor activo · reintentando la lectura" : "Motor conectado a la pantalla de asistencia";
            _summary.ForeColor = status.Configured && status.QueueReady ? Color.FromArgb(26,110,78) : Color.DarkGoldenrod;
            _values["Servicio"].Text = "Activo · inicio automático con Windows";
            _values["Lector de huellas"].Text = status.ReaderConnected ? "Conectado" : "No disponible · comprueba conexión y controlador";
            _values["Pantalla de asistencia"].Text = status.ScreenConnected ? "Conectada (SSE)" :
                $"Esperando conexión autorizada · frontend: {status.FrontendUrl}";
            _values["Socios con huella"].Text = status.Fingerprints.ToString() + (status.CacheStale && status.Fingerprints > 0 ? " · pendiente de sincronizar" : "");
            _values["Eventos pendientes"].Text = status.Pending.ToString();
            _values["Última sincronización"].Text = status.LastSyncUtc?.ToLocalTime().ToString("g") ?? "Todavía no se han cargado socios";
            _values["Último evento"].Text = status.LastEvent + (status.LastEventAt is { } at ? " · " + at.ToLocalTime().ToString("T") : "");
            _values["Comunicación"].Text = status.Configured
                ? $"Configurada · frontend: {status.FrontendUrl} · motor local: localhost:4000"
                : "Pendiente · el motor puede permanecer encendido";
            string text = string.Join(Environment.NewLine, status.Logs);
            if (_logs.Text != text) { _logs.Text = text; _logs.SelectionStart = _logs.TextLength; _logs.ScrollToCaret(); }
            _tray.Text = status.ReaderConnected ? "HEXODUS BioSync · lector conectado" : "HEXODUS BioSync · esperando lector";
        } catch {
            if (IsDisposed) return;
            _last = null; _summary.Text = "No se pudo consultar el servicio"; _summary.ForeColor = Color.DarkGoldenrod;
            foreach (var value in _values.Values) value.Text = "—";
            try { using var service = SetupActions.Service(); _values["Servicio"].Text = service == null ? "No instalado" : service.Status.ToString(); } catch { _values["Servicio"].Text = "No disponible"; }
            _values["Comunicación"].Text = "Se volverá a comprobar automáticamente";
        } finally { _busy = false; }
    }
    private void Export() {
        if (_last == null) { MessageBox.Show("Espera a que el servicio responda para exportar su diagnóstico.", Text); return; }
        using var dialog = new SaveFileDialog { Filter = "Diagnóstico JSON|*.json", FileName = "MotorLocalExus-diagnostico-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" };
        if (dialog.ShowDialog(this) == DialogResult.OK) {
            try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_last, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
    private void Configure() {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = "--configure" }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
