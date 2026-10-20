using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TaskbarStats;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "TaskbarStats.SingleInstance", out bool created);
        if (!created) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new StatsForm());
    }
}

/// <summary>
/// Janela sem borda, sempre no topo, posicionada sobre a barra de tarefas à esquerda da bandeja.
/// Arraste com o botão esquerdo para mover; botão direito abre o menu; duplo clique abre o Gerenciador de Tarefas.
/// </summary>
sealed class StatsForm : Form
{
    const int DefaultRightGap = 175;   // px entre a borda direita da tela e a janela (espaço da bandeja + relógio)
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "TaskbarStats";

    static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
    static readonly string GapFile = Path.Combine(ConfigDir, "right-gap.txt");

    readonly CpuSensor cpu = new();
    readonly GpuEngineSensor gpuEngines = new();   // uso, mesma fonte do Gerenciador de Tarefas
    readonly GpuSensor? gpu = GpuSensor.TryOpen(); // temperatura e VRAM (NVIDIA)
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly ToolTip tip = new() { InitialDelay = 400, ShowAlways = true };
    readonly ToolStripMenuItem startupItem;

    int rightGap = DefaultRightGap;
    int dragOffset = -1;
    int ticks;
    Color textColor = Color.White;
    Color labelColor = Color.Gray;
    readonly Color warnColor = Color.FromArgb(255, 140, 0);

    double cpuPct = -1;
    (int percent, double usedGb, double totalGb) ram = (-1, 0, 0);
    (int util, int temp, double vramUsedGb, double vramTotalGb) gpuData = (-1, -1, -1, -1);

    // layout
    int labelW, valueW, colGap, pad, lineH;

    public StatsForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(32, 32, 32);

        startupItem = new ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => ToggleStartup()) { Checked = IsStartupEnabled() };
        var menu = new ContextMenuStrip();
        menu.Items.Add(startupItem);
        menu.Items.Add("Voltar à posição padrão", null, (_, _) => { rightGap = DefaultRightGap; SaveGap(); PlaceWindow(); });
        menu.Items.Add("Gerenciador de Tarefas", null, (_, _) => OpenTaskManager());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Close());
        ContextMenuStrip = menu;

        LoadGap();
        ComputeLayout();

        timer.Tick += (_, _) => OnTick();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOPMOST = 0x00000008;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        PlaceWindow();
        UpdateColors();
        OnTick();
        timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        cpu.Dispose();
        gpuEngines.Dispose();
        base.OnFormClosed(e);
    }

    void OnDisplayChanged(object? sender, EventArgs e) => BeginInvoke(() => { ComputeLayout(); PlaceWindow(); UpdateColors(); });
    void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => BeginInvoke(UpdateColors);

    void OnTick()
    {
        cpuPct = cpu.Read();
        ram = MemorySensor.Read();
        if (gpu != null) gpuData = gpu.Read();
        int engineUtil = gpuEngines.Read();
        if (engineUtil >= 0) gpuData.util = engineUtil;

        // Esconde quando um app está em tela cheia (jogo, vídeo); senão reafirma o topo sobre a barra.
        bool fullscreen = ForegroundIsFullscreen();
        if (fullscreen && Visible) Hide();
        else if (!fullscreen && !Visible) Show();
        if (Visible)
        {
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (++ticks % 10 == 0) UpdateColors();
        }

        tip.SetToolTip(this, BuildTooltip());
        Invalidate();
    }

    string BuildTooltip()
    {
        var sb = new StringBuilder();
        sb.AppendLine(cpuPct < 0 ? "CPU: indisponível" : $"CPU: {cpuPct:0}%");
        sb.AppendLine(ram.percent < 0 ? "RAM: indisponível" : $"RAM: {ram.usedGb:0.0} / {ram.totalGb:0.0} GB ({ram.percent}%)");
        if (gpu == null) sb.Append($"GPU: {Fmt(gpuData.util, "%")}");
        else
        {
            sb.AppendLine($"{gpu.Name}: {Fmt(gpuData.util, "%")}  {Fmt(gpuData.temp, "°C")}");
            sb.Append(gpuData.vramTotalGb < 0 ? "VRAM: indisponível" : $"VRAM: {gpuData.vramUsedGb:0.0} / {gpuData.vramTotalGb:0.0} GB");
        }
        return sb.ToString();
    }

    static string Fmt(int v, string unit) => v < 0 ? "--" : v + unit;

    // ---------- desenho ----------

    void ComputeLayout()
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        Size M(string s) => TextRenderer.MeasureText(s, Font, Size.Empty, flags);
        labelW = Math.Max(M("CPU").Width, Math.Max(M("RAM").Width, M("GPU").Width));
        valueW = Math.Max(M("100%").Width, M("100°C").Width);
        lineH = M("CPU 0%").Height;
        float scale = DeviceDpi / 96f;
        pad = (int)(3 * scale);
        colGap = (int)(8 * scale);
        int gap = (int)(3 * scale);
        labelW += gap;
        Width = pad + labelW + valueW + colGap + labelW + valueW + pad;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int top = (Height - 2 * lineH) / 2;
        int x1 = pad;
        int x2 = pad + labelW + valueW + colGap;

        DrawCell(g, x1, top, "CPU", cpuPct < 0 ? "--" : $"{cpuPct:0}%", cpuPct >= 90);
        DrawCell(g, x1, top + lineH, "RAM", ram.percent < 0 ? "--" : $"{ram.percent}%", ram.percent >= 90);
        DrawCell(g, x2, top, "GPU", Fmt(gpuData.util, "%"), gpuData.util >= 95);
        DrawCell(g, x2, top + lineH, "", Fmt(gpuData.temp, "°C"), gpuData.temp >= 83);
    }

    void DrawCell(Graphics g, int x, int y, string label, string value, bool warn)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        if (label.Length > 0)
            TextRenderer.DrawText(g, label, Font, new Rectangle(x, y, labelW, lineH), labelColor, BackColor, flags | TextFormatFlags.Left);
        TextRenderer.DrawText(g, value, Font, new Rectangle(x + labelW, y, valueW, lineH), warn ? warnColor : textColor, BackColor, flags | TextFormatFlags.Right);
    }

    // ---------- posição ----------

    void PlaceWindow()
    {
        var scr = Screen.PrimaryScreen!;
        int tbH = scr.Bounds.Bottom - scr.WorkingArea.Bottom;
        if (tbH <= 0) tbH = (int)(48 * DeviceDpi / 96f);   // barra com ocultar automático
        Height = tbH;
        Top = scr.Bounds.Bottom - tbH;
        rightGap = Math.Clamp(rightGap, 0, scr.Bounds.Width - Width);
        Left = scr.Bounds.Right - rightGap - Width;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Clicks == 1) dragOffset = Cursor.Position.X - Left;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragOffset < 0) return;
        var b = Screen.PrimaryScreen!.Bounds;
        Left = Math.Clamp(Cursor.Position.X - dragOffset, b.Left, b.Right - Width);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragOffset < 0) return;
        dragOffset = -1;
        rightGap = Screen.PrimaryScreen!.Bounds.Right - Right;
        SaveGap();
        UpdateColors();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) OpenTaskManager();
    }

    static void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
    }

    void LoadGap()
    {
        try { if (int.TryParse(File.ReadAllText(GapFile).Trim(), out int v)) rightGap = v; } catch { }
    }

    void SaveGap()
    {
        try { Directory.CreateDirectory(ConfigDir); File.WriteAllText(GapFile, rightGap.ToString()); } catch { }
    }

    // ---------- cores ----------

    /// <summary>Copia a cor da barra de tarefas logo à esquerda da janela e escolhe texto claro ou escuro.</summary>
    void UpdateColors()
    {
        var bg = SampleScreen(Left - 4, Top + Height / 2) ?? (IsLightTaskbar() ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
        bool light = (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000 > 128;
        var fg = light ? Color.FromArgb(20, 20, 20) : Color.FromArgb(240, 240, 240);
        var lbl = Blend(fg, bg, 0.6);
        if (bg == BackColor && fg == textColor) return;
        BackColor = bg;
        textColor = fg;
        labelColor = lbl;
        Invalidate();
    }

    static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t)));

    static Color? SampleScreen(int x, int y)
    {
        if (x < 0) return null;
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        uint c = GetPixel(dc, x, y);
        ReleaseDC(IntPtr.Zero, dc);
        if (c == 0xFFFFFFFF) return null;   // CLR_INVALID
        return Color.FromArgb((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
    }

    static bool IsLightTaskbar()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    // ---------- tela cheia ----------

    bool ForegroundIsFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return false;
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(fg, out var r)) return false;
        var scr = Screen.FromHandle(fg);
        if (!scr.Primary) return false;
        var b = scr.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    // ---------- inicialização ----------

    static bool IsStartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(AppName) != null;
    }

    void ToggleStartup()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsStartupEnabled()) k.DeleteValue(AppName, false);
        else k.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
        startupItem.Checked = IsStartupEnabled();
    }

    // ---------- Win32 ----------

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
}
