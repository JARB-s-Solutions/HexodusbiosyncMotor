using Exus;

internal sealed class ConfigurationForm : Form
{
    private readonly TextBox _origin = new() { Dock = DockStyle.Top, PlaceholderText = "https://tu-dominio" };
    private readonly TextBox _kiosk = new() { Dock = DockStyle.Top };
    private readonly TextBox _secret = new() { Dock = DockStyle.Top, UseSystemPasswordChar = true };
    private readonly Label _notice = new() { AutoSize = true, MaximumSize = new Size(580, 0) };
    private readonly Button _save = new() { Text = "Guardar e iniciar motor", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
    private readonly ToolTip _help = new() { AutoPopDelay = 12000, InitialDelay = 250, ReshowDelay = 100, ShowAlways = true };
    private string[] _origins = [];
    private string _loadedFrontend = "";
    private bool _saving;

    public ConfigurationForm(string? profile, bool setup)
    {
        Text = setup ? "HEXODUS BioSync - configuración inicial" : "HEXODUS BioSync - configuración";
        Size = new Size(680, 620); MinimumSize = Size; StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10); BackColor = Color.White; MaximizeBox = false;
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(26) };
        root.Controls.Add(new Label { Text = "Configuración de comunicación", AutoSize = true, Font = new Font("Segoe UI", 19, FontStyle.Bold), Margin = new Padding(0, 0, 0, 15) });
        root.Controls.Add(new Label { Text = "Estos datos se guardan una sola vez. El motor iniciará con Windows y no volverá a pedir permisos diariamente. El lector puede conectarse después.", AutoSize = true, MaximumSize = new Size(590, 0), Margin = new Padding(0, 0, 0, 14) });
        root.Controls.Add(_notice);
        Field(root, "Dirección del frontend HEXODUS (sin ruta)", _origin, "Escribe la dirección exacta donde funciona HEXODUS, por ejemplo https://tu-dominio.com o http://localhost:3000.");
        Field(root, "Identificador de esta terminal", _kiosk, "Nombre único de esta computadora. Debe coincidir con la terminal autorizada en HEXODUS.");
        Field(root, "Clave de comunicación del motor", _secret, "Permite que el motor se comunique de forma segura con HEXODUS. No es la contraseña de Supabase y no debes compartirla.");
        var import = new Button { Text = "Importar configuracion protegida...", AutoSize = true, Margin = new Padding(0, 12, 0, 12) };
        import.Click += (_, _) => { using var dialog = new OpenFileDialog { Filter = "Configuracion protegida|*.exusconfig", CheckFileExists = true }; if (dialog.ShowDialog(this) == DialogResult.OK) LoadProfile(dialog.FileName); };
        root.Controls.Add(import);
        root.Controls.Add(new Label { Text = "Puerto local automático: localhost:4000. Si todavía no tienes la clave, puedes continuar; el motor quedará instalado y esperará la configuración antes de registrar asistencias.", AutoSize = true, MaximumSize = new Size(590, 0), Margin = new Padding(0, 0, 0, 15) });
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(_save);
        var cancel = new Button { Text = "Cancelar", AutoSize = true, Padding = new Padding(10, 5, 10, 5), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel); root.Controls.Add(buttons); Controls.Add(root);
        _save.Click += async (_, _) => await Save(); CancelButton = cancel;
        FormClosing += (_, e) => { if (_saving && DialogResult != DialogResult.OK) e.Cancel = true; };
        Fill(new SiteConfiguration());
        string installed = Installation.ConfigPath(Installation.DataRoot);
        if (File.Exists(installed)) LoadProfile(installed);
        else if (profile != null && File.Exists(profile)) LoadProfile(profile);
        else
        {
            // Una instalación nueva no debe quedar apuntando a localhost por accidente.
            // El dominio real cambia entre instalaciones y se captura una sola vez aquí.
            if (setup) { _origin.Clear(); _loadedFrontend = ""; _origins = []; }
            _notice.Text = "Sin configuración previa. Escribe el dominio exacto de HEXODUS para esta instalación.";
        }
    }

    private void Field(Control root, string label, TextBox input, string helpText)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 4), Width = 590 };
        row.Controls.Add(new Label { Text = label, AutoSize = true, MaximumSize = new Size(545, 0), Margin = new Padding(0, 5, 8, 0) });
        var help = new Button { Text = "?", Width = 28, Height = 28, FlatStyle = FlatStyle.Flat, TabStop = false, Margin = new Padding(0), AccessibleName = "Ayuda" };
        help.FlatAppearance.BorderColor = Color.SteelBlue;
        _help.SetToolTip(help, helpText);
        help.Click += (_, _) => MessageBox.Show(helpText, "Ayuda de configuración", MessageBoxButtons.OK, MessageBoxIcon.Information);
        row.Controls.Add(help); root.Controls.Add(row);
        input.Width = 590; root.Controls.Add(input);
    }

    private void Fill(SiteConfiguration config) { _origin.Text = config.FrontendUrl; _loadedFrontend = config.FrontendUrl; _kiosk.Text = config.KioskId; _secret.Text = config.DeviceSecret; _origins = config.AllowedOrigins; }
    private void LoadProfile(string path)
    {
        try { Fill(Installation.Load(path)); _notice.Text = "Configuracion protegida cargada. Puedes continuar con los valores preparados."; _notice.ForeColor = Color.DarkGreen; }
        catch { _notice.Text = "No se pudo abrir esa configuracion. Puede pertenecer a otra computadora. Completa los datos o continua sin clave."; _notice.ForeColor = Color.DarkGoldenrod; }
    }
    private async Task Save()
    {
        var config = new SiteConfiguration { FrontendUrl = _origin.Text.Trim().TrimEnd('/'), KioskId = _kiosk.Text.Trim(), DeviceSecret = _secret.Text.Trim(), AllowedOrigins = _loadedFrontend == _origin.Text.Trim().TrimEnd('/') ? _origins : [_origin.Text.Trim().TrimEnd('/')] };
        try
        {
            config.Validate(); _saving = true; _save.Enabled = false; ControlBox = false; _save.Text = "Configurando el servicio...";
            await Task.Run(() => SetupActions.SaveAndStart(config));
            DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _saving = false; _save.Enabled = true; ControlBox = true; _save.Text = "Guardar e iniciar motor"; }
    }
}
