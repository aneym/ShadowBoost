using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32;

public class ShadowBoost : Form
{
    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("dxva2.dll")]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll")]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    private const int HOTKEY_ID = 1;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_D = 0x44;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Blue;
    }

    // Colors - improved contrast
    private static Color bgColor = Color.FromArgb(22, 22, 26);
    private static Color cardColor = Color.FromArgb(38, 38, 44);
    private static Color accentColor = Color.FromArgb(124, 127, 255);
    private static Color accentHover = Color.FromArgb(150, 152, 255);
    private static Color textColor = Color.FromArgb(248, 248, 248);
    private static Color textDim = Color.FromArgb(180, 180, 190);
    private static Color successColor = Color.FromArgb(74, 222, 128);
    private static Color dangerColor = Color.FromArgb(248, 113, 113);
    private static Color sliderBg = Color.FromArgb(55, 55, 65);

    private NotifyIcon trayIcon;
    private ContextMenuStrip trayMenu;
    private ToolStripMenuItem toggleItem;
    private ToolStripMenuItem statusItem;
    private TrackBar gammaSlider;
    private TrackBar brightnessSlider;
    private TrackBar blackLiftSlider;
    private TrackBar contrastSlider;
    private TrackBar hwBrightnessSlider;
    private Label gammaLabel;
    private Label brightnessLabel;
    private Label blackLiftLabel;
    private Label contrastLabel;
    private Label hwBrightnessLabel;
    private Label ddcStatusLabel;
    private Label windowStatusLabel;
    private Button windowToggleBtn;
    private bool isEnabled = false;
    private bool isInitializing = true;
    private string settingsPath;
    private IntPtr physicalMonitor = IntPtr.Zero;
    private bool ddcSupported = false;
    private Timer ddcRetryTimer;
    private int ddcRetryCount = 0;
    private const int DDC_MAX_RETRIES = 5;
    private const int DDC_RETRY_INTERVAL = 3000; // 3 seconds between retries

    public ShadowBoost()
    {
        settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShadowBoost.txt");

        // Form setup
        this.Text = "ShadowBoost";
        this.Size = new Size(380, 820);
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.ShowInTaskbar = true;
        this.BackColor = bgColor;
        this.ForeColor = textColor;
        this.Font = new Font("Segoe UI", 9.5f);

        // Dark title bar
        try
        {
            int value = 1;
            DwmSetWindowAttribute(this.Handle, 20, ref value, 4);
        }
        catch { }

        int y = 24;
        int margin = 24;
        int contentWidth = this.ClientSize.Width - (margin * 2);

        // Header
        Label titleLabel = new Label();
        titleLabel.Text = "ShadowBoost";
        titleLabel.Font = new Font("Segoe UI", 18f, FontStyle.Bold);
        titleLabel.ForeColor = textColor;
        titleLabel.Location = new Point(margin, y);
        titleLabel.AutoSize = true;
        this.Controls.Add(titleLabel);

        Label subtitleLabel = new Label();
        subtitleLabel.Text = "Enhance dark visibility in games";
        subtitleLabel.Font = new Font("Segoe UI", 9f);
        subtitleLabel.ForeColor = textDim;
        subtitleLabel.Location = new Point(margin, y + 32);
        subtitleLabel.AutoSize = true;
        this.Controls.Add(subtitleLabel);
        y += 70;

        // Toggle section
        Panel togglePanel = CreateCard(margin, y, contentWidth, 60);
        this.Controls.Add(togglePanel);

        windowToggleBtn = new Button();
        windowToggleBtn.Text = "ENABLE";
        windowToggleBtn.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        windowToggleBtn.FlatStyle = FlatStyle.Flat;
        windowToggleBtn.FlatAppearance.BorderSize = 0;
        windowToggleBtn.BackColor = accentColor;
        windowToggleBtn.ForeColor = Color.White;
        windowToggleBtn.Size = new Size(100, 36);
        windowToggleBtn.Location = new Point(16, 12);
        windowToggleBtn.Cursor = Cursors.Hand;
        windowToggleBtn.Click += (s, e) => Toggle();
        windowToggleBtn.MouseEnter += (s, e) => windowToggleBtn.BackColor = accentHover;
        windowToggleBtn.MouseLeave += (s, e) => windowToggleBtn.BackColor = isEnabled ? dangerColor : accentColor;
        togglePanel.Controls.Add(windowToggleBtn);

        windowStatusLabel = new Label();
        windowStatusLabel.Text = "Status: OFF";
        windowStatusLabel.Font = new Font("Segoe UI", 10f);
        windowStatusLabel.ForeColor = textDim;
        windowStatusLabel.Location = new Point(130, 20);
        windowStatusLabel.AutoSize = true;
        togglePanel.Controls.Add(windowStatusLabel);

        Label hotkeyHint = new Label();
        hotkeyHint.Text = "Ctrl+Shift+D";
        hotkeyHint.Font = new Font("Segoe UI", 8f);
        hotkeyHint.ForeColor = textDim;
        hotkeyHint.Location = new Point(contentWidth - 90, 22);
        hotkeyHint.AutoSize = true;
        togglePanel.Controls.Add(hotkeyHint);
        y += 76;

        // Preset buttons panel
        Panel presetPanel = CreateCard(margin, y, contentWidth, 44);
        this.Controls.Add(presetPanel);

        Button resetBtn = new Button();
        resetBtn.Text = "BALANCED";
        resetBtn.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        resetBtn.FlatStyle = FlatStyle.Flat;
        resetBtn.FlatAppearance.BorderSize = 1;
        resetBtn.FlatAppearance.BorderColor = accentColor;
        resetBtn.BackColor = cardColor;
        resetBtn.ForeColor = accentColor;
        resetBtn.Size = new Size(90, 28);
        resetBtn.Location = new Point(12, 8);
        resetBtn.Cursor = Cursors.Hand;
        resetBtn.Click += (s, e) => ApplyPreset(10, 70, 105, 100);
        presetPanel.Controls.Add(resetBtn);

        Button aggressiveBtn = new Button();
        aggressiveBtn.Text = "AGGRESSIVE";
        aggressiveBtn.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        aggressiveBtn.FlatStyle = FlatStyle.Flat;
        aggressiveBtn.FlatAppearance.BorderSize = 1;
        aggressiveBtn.FlatAppearance.BorderColor = successColor;
        aggressiveBtn.BackColor = cardColor;
        aggressiveBtn.ForeColor = successColor;
        aggressiveBtn.Size = new Size(100, 28);
        aggressiveBtn.Location = new Point(110, 8);
        aggressiveBtn.Cursor = Cursors.Hand;
        aggressiveBtn.Click += (s, e) => ApplyPreset(18, 55, 115, 105);
        presetPanel.Controls.Add(aggressiveBtn);

        Button subtleBtn = new Button();
        subtleBtn.Text = "SUBTLE";
        subtleBtn.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        subtleBtn.FlatStyle = FlatStyle.Flat;
        subtleBtn.FlatAppearance.BorderSize = 1;
        subtleBtn.FlatAppearance.BorderColor = textDim;
        subtleBtn.BackColor = cardColor;
        subtleBtn.ForeColor = textDim;
        subtleBtn.Size = new Size(75, 28);
        subtleBtn.Location = new Point(218, 8);
        subtleBtn.Cursor = Cursors.Hand;
        subtleBtn.Click += (s, e) => ApplyPreset(4, 82, 100, 100);
        presetPanel.Controls.Add(subtleBtn);

        y += 54;

        // Black Lift slider (most important for dark visibility)
        y = CreateSliderSection("Black Lift", "Raises black level to reveal shadows", margin, y, contentWidth,
            0, 30, 5, out blackLiftSlider, out blackLiftLabel, "percent");

        // Gamma slider
        y = CreateSliderSection("Gamma", "Lower = brighter shadows, Higher = darker", margin, y, contentWidth,
            30, 100, 70, out gammaSlider, out gammaLabel, "gamma");

        // Contrast slider
        y = CreateSliderSection("Contrast", "Difference between dark and light", margin, y, contentWidth,
            50, 150, 100, out contrastSlider, out contrastLabel, "percent");

        // Brightness slider
        y = CreateSliderSection("Brightness", "Overall screen brightness", margin, y, contentWidth,
            50, 130, 100, out brightnessSlider, out brightnessLabel, "percent");

        // Hardware Brightness slider
        y = CreateSliderSection("Monitor Brightness", "Hardware DDC/CI control", margin, y, contentWidth,
            0, 100, 50, out hwBrightnessSlider, out hwBrightnessLabel, "percent");

        // DDC Status
        ddcStatusLabel = new Label();
        ddcStatusLabel.Text = "DDC/CI: Checking...";
        ddcStatusLabel.Font = new Font("Segoe UI", 8f);
        ddcStatusLabel.ForeColor = textDim;
        ddcStatusLabel.Location = new Point(margin, y);
        ddcStatusLabel.AutoSize = true;
        this.Controls.Add(ddcStatusLabel);
        y += 30;

        // Startup toggle button
        Panel startupPanel = CreateCard(margin, y, contentWidth, 50);
        this.Controls.Add(startupPanel);

        Label startupLabel = new Label();
        startupLabel.Text = "Start with Windows";
        startupLabel.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        startupLabel.ForeColor = textColor;
        startupLabel.Location = new Point(16, 16);
        startupLabel.AutoSize = true;
        startupPanel.Controls.Add(startupLabel);

        Button startupBtn = new Button();
        bool inStartup = IsInStartup();
        startupBtn.Text = inStartup ? "ON" : "OFF";
        startupBtn.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        startupBtn.FlatStyle = FlatStyle.Flat;
        startupBtn.FlatAppearance.BorderSize = 0;
        startupBtn.BackColor = inStartup ? successColor : Color.FromArgb(80, 80, 90);
        startupBtn.ForeColor = Color.White;
        startupBtn.Size = new Size(60, 30);
        startupBtn.Location = new Point(contentWidth - 76, 10);
        startupBtn.Cursor = Cursors.Hand;
        startupBtn.Click += (s, e) => {
            bool newState = !IsInStartup();
            SetStartup(newState);
            startupBtn.Text = newState ? "ON" : "OFF";
            startupBtn.BackColor = newState ? successColor : Color.FromArgb(80, 80, 90);
        };
        startupPanel.Controls.Add(startupBtn);

        // Event handlers for sliders
        blackLiftSlider.ValueChanged += Slider_ValueChanged;
        gammaSlider.ValueChanged += Slider_ValueChanged;
        contrastSlider.ValueChanged += Slider_ValueChanged;
        brightnessSlider.ValueChanged += Slider_ValueChanged;
        hwBrightnessSlider.ValueChanged += HwBrightnessSlider_ValueChanged;

        // Setup tray icon
        SetupTrayIcon();

        // Register hotkey
        RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_D);

        // Initialize DDC
        InitializeDDC();

        LoadSettings();

        // Initialization complete - enable real-time slider updates
        isInitializing = false;

        // Hide on close, don't exit
        this.FormClosing += (s, e) => {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
        };
    }

    private Panel CreateCard(int x, int y, int width, int height)
    {
        Panel panel = new Panel();
        panel.Location = new Point(x, y);
        panel.Size = new Size(width, height);
        panel.BackColor = cardColor;
        return panel;
    }

    private int CreateSliderSection(string title, string description, int x, int y, int width,
        int min, int max, int defaultVal, out TrackBar slider, out Label valueLabel, string format)
    {
        Panel card = CreateCard(x, y, width, 80);
        this.Controls.Add(card);

        Label titleLbl = new Label();
        titleLbl.Text = title;
        titleLbl.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        titleLbl.ForeColor = textColor;
        titleLbl.Location = new Point(16, 12);
        titleLbl.AutoSize = true;
        card.Controls.Add(titleLbl);

        Label descLbl = new Label();
        descLbl.Text = description;
        descLbl.Font = new Font("Segoe UI", 8f);
        descLbl.ForeColor = textDim;
        descLbl.Location = new Point(16, 30);
        descLbl.AutoSize = true;
        card.Controls.Add(descLbl);

        slider = new TrackBar();
        slider.Location = new Point(12, 48);
        slider.Size = new Size(width - 70, 25);
        slider.Minimum = min;
        slider.Maximum = max;
        slider.Value = defaultVal;
        slider.TickStyle = TickStyle.None;
        slider.BackColor = cardColor;
        card.Controls.Add(slider);

        valueLabel = new Label();
        if (format == "gamma")
            valueLabel.Text = (defaultVal / 100.0).ToString("0.00");
        else
            valueLabel.Text = defaultVal + "%";
        valueLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        valueLabel.ForeColor = accentColor;
        valueLabel.Location = new Point(width - 54, 52);
        valueLabel.Size = new Size(45, 20);
        valueLabel.TextAlign = ContentAlignment.MiddleRight;
        card.Controls.Add(valueLabel);

        return y + 90;
    }

    private void SetupTrayIcon()
    {
        trayMenu = new ContextMenuStrip();
        trayMenu.BackColor = cardColor;
        trayMenu.ForeColor = textColor;
        trayMenu.ShowImageMargin = false;
        trayMenu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());

        statusItem = new ToolStripMenuItem("Status: OFF");
        statusItem.ForeColor = textDim;
        statusItem.Enabled = false;
        trayMenu.Items.Add(statusItem);

        trayMenu.Items.Add(new ToolStripSeparator());

        toggleItem = new ToolStripMenuItem("Enable ShadowBoost");
        toggleItem.Click += (s, e) => Toggle();
        trayMenu.Items.Add(toggleItem);

        ToolStripMenuItem settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (s, e) => { this.Show(); this.BringToFront(); };
        trayMenu.Items.Add(settingsItem);

        trayMenu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) => ExitApp();
        trayMenu.Items.Add(exitItem);

        trayIcon = new NotifyIcon();
        trayIcon.Text = "ShadowBoost - Ctrl+Shift+D";
        trayIcon.Icon = SystemIcons.Application;
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (s, e) => { this.Show(); this.BringToFront(); };
    }

    private class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Color.FromArgb(60, 60, 70); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(60, 60, 70); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(60, 60, 70); } }
        public override Color MenuBorder { get { return Color.FromArgb(50, 50, 58); } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(38, 38, 44); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(38, 38, 44); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(38, 38, 44); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(38, 38, 44); } }
        public override Color SeparatorDark { get { return Color.FromArgb(60, 60, 70); } }
        public override Color SeparatorLight { get { return Color.FromArgb(60, 60, 70); } }
    }

    private void InitializeDDC()
    {
        ddcRetryCount = 0;
        TryConnectDDC();
    }

    private bool TryConnectDDC()
    {
        try
        {
            // Clean up previous monitor handle if exists
            if (physicalMonitor != IntPtr.Zero)
            {
                DestroyPhysicalMonitor(physicalMonitor);
                physicalMonitor = IntPtr.Zero;
            }

            IntPtr foundMonitor = IntPtr.Zero;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData)
            {
                uint numMonitors;
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out numMonitors) && numMonitors > 0)
                {
                    PHYSICAL_MONITOR[] monitors = new PHYSICAL_MONITOR[numMonitors];
                    if (GetPhysicalMonitorsFromHMONITOR(hMonitor, numMonitors, monitors))
                    {
                        foundMonitor = monitors[0].hPhysicalMonitor;
                    }
                }
                return false;
            }, IntPtr.Zero);

            if (foundMonitor != IntPtr.Zero)
            {
                physicalMonitor = foundMonitor;
                uint min, current, max;
                if (GetMonitorBrightness(physicalMonitor, out min, out current, out max))
                {
                    ddcSupported = true;
                    hwBrightnessSlider.Enabled = true;
                    hwBrightnessSlider.Value = (int)current;
                    ddcStatusLabel.Text = "DDC/CI: Connected";
                    ddcStatusLabel.ForeColor = successColor;
                    StopDDCRetry();
                    return true;
                }
            }

            // Failed - schedule retry if we haven't exceeded max retries
            ddcRetryCount++;
            if (ddcRetryCount < DDC_MAX_RETRIES)
            {
                ddcStatusLabel.Text = "DDC/CI: Waiting... (" + ddcRetryCount + "/" + DDC_MAX_RETRIES + ")";
                ddcStatusLabel.ForeColor = Color.FromArgb(251, 191, 36);
                hwBrightnessSlider.Enabled = false;
                StartDDCRetry();
            }
            else
            {
                ddcStatusLabel.Text = "DDC/CI: Not available";
                ddcStatusLabel.ForeColor = dangerColor;
                hwBrightnessSlider.Enabled = false;
                StopDDCRetry();
            }
            return false;
        }
        catch
        {
            ddcRetryCount++;
            if (ddcRetryCount < DDC_MAX_RETRIES)
            {
                ddcStatusLabel.Text = "DDC/CI: Retrying... (" + ddcRetryCount + "/" + DDC_MAX_RETRIES + ")";
                ddcStatusLabel.ForeColor = Color.FromArgb(251, 191, 36);
                hwBrightnessSlider.Enabled = false;
                StartDDCRetry();
            }
            else
            {
                ddcStatusLabel.Text = "DDC/CI: Error";
                ddcStatusLabel.ForeColor = dangerColor;
                hwBrightnessSlider.Enabled = false;
                StopDDCRetry();
            }
            return false;
        }
    }

    private void StartDDCRetry()
    {
        if (ddcRetryTimer == null)
        {
            ddcRetryTimer = new Timer();
            ddcRetryTimer.Interval = DDC_RETRY_INTERVAL;
            ddcRetryTimer.Tick += (s, e) => TryConnectDDC();
        }
        ddcRetryTimer.Start();
    }

    private void StopDDCRetry()
    {
        if (ddcRetryTimer != null)
        {
            ddcRetryTimer.Stop();
            ddcRetryTimer.Dispose();
            ddcRetryTimer = null;
        }
    }

    private void Slider_ValueChanged(object sender, EventArgs e)
    {
        blackLiftLabel.Text = blackLiftSlider.Value + "%";
        gammaLabel.Text = (gammaSlider.Value / 100.0).ToString("0.00");
        contrastLabel.Text = contrastSlider.Value + "%";
        brightnessLabel.Text = brightnessSlider.Value + "%";

        // Apply gamma immediately for real-time preview (but not during initialization)
        if (!isInitializing)
        {
            ApplyGamma();
            SaveSettings();
        }
    }

    private void HwBrightnessSlider_ValueChanged(object sender, EventArgs e)
    {
        hwBrightnessLabel.Text = hwBrightnessSlider.Value + "%";

        if (!isInitializing && ddcSupported && physicalMonitor != IntPtr.Zero)
        {
            SetMonitorBrightness(physicalMonitor, (uint)hwBrightnessSlider.Value);
            SaveSettings();
        }
    }

    private void ApplyPreset(int blackLift, int gamma, int contrast, int brightness)
    {
        blackLiftSlider.Value = blackLift;
        gammaSlider.Value = gamma;
        contrastSlider.Value = contrast;
        brightnessSlider.Value = brightness;
    }

    private void Toggle()
    {
        isEnabled = !isEnabled;

        if (isEnabled)
        {
            // Apply current slider settings
            ApplyGamma();
            toggleItem.Text = "Disable ShadowBoost";
            statusItem.Text = "Status: ON";
            trayIcon.Text = "ShadowBoost - ON";
            windowToggleBtn.Text = "DISABLE";
            windowToggleBtn.BackColor = dangerColor;
            windowStatusLabel.Text = "Status: ON";
            windowStatusLabel.ForeColor = successColor;
        }
        else
        {
            // Reset to neutral/normal gamma
            ResetGamma();
            toggleItem.Text = "Enable ShadowBoost";
            statusItem.Text = "Status: OFF";
            trayIcon.Text = "ShadowBoost - OFF";
            windowToggleBtn.Text = "ENABLE";
            windowToggleBtn.BackColor = accentColor;
            windowStatusLabel.Text = "Status: OFF";
            windowStatusLabel.ForeColor = textDim;
        }
    }

    private void ApplyGamma()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        RAMP ramp = new RAMP();
        ramp.Red = new ushort[256];
        ramp.Green = new ushort[256];
        ramp.Blue = new ushort[256];

        // Get slider values
        double blackLift = blackLiftSlider.Value / 100.0;  // 0-0.30 (lifts blacks)
        double gamma = gammaSlider.Value / 100.0;          // 0.30-1.0 (lower = brighter shadows)
        double contrast = contrastSlider.Value / 100.0;    // 0.5-1.5
        double brightness = brightnessSlider.Value / 100.0; // 0.5-1.3

        for (int i = 0; i < 256; i++)
        {
            double normalized = i / 255.0;

            // Step 1: Black lift FIRST - raise the floor for dark pixels
            // This adds a base amount that fades out as pixels get brighter
            double adjusted = normalized + blackLift * (1.0 - normalized);

            // Step 2: Apply gamma curve (affects shadows most)
            adjusted = Math.Pow(adjusted, gamma);

            // Step 3: Apply contrast around midpoint (0.5)
            // Now contrast works on the already-lifted image
            adjusted = (adjusted - 0.5) * contrast + 0.5;

            // Step 4: Apply brightness
            adjusted = adjusted * brightness;

            // Clamp to valid range
            adjusted = Math.Max(0.0, Math.Min(1.0, adjusted));

            ushort value = (ushort)(adjusted * 65535);
            ramp.Red[i] = value;
            ramp.Green[i] = value;
            ramp.Blue[i] = value;
        }

        SetDeviceGammaRamp(hdc, ref ramp);
        ReleaseDC(IntPtr.Zero, hdc);
    }

    private void ResetGamma()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        RAMP ramp = new RAMP();
        ramp.Red = new ushort[256];
        ramp.Green = new ushort[256];
        ramp.Blue = new ushort[256];

        for (int i = 0; i < 256; i++)
        {
            ramp.Red[i] = (ushort)(i * 256);
            ramp.Green[i] = (ushort)(i * 256);
            ramp.Blue[i] = (ushort)(i * 256);
        }

        SetDeviceGammaRamp(hdc, ref ramp);
        ReleaseDC(IntPtr.Zero, hdc);
    }

    private void SaveSettings()
    {
        try
        {
            string settings = string.Format("{0},{1},{2},{3},{4}",
                blackLiftSlider.Value,
                gammaSlider.Value,
                contrastSlider.Value,
                brightnessSlider.Value,
                hwBrightnessSlider.Value);
            File.WriteAllText(settingsPath, settings);
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                string[] parts = File.ReadAllText(settingsPath).Split(',');
                // New format: blackLift, gamma, contrast, brightness, hwBrightness
                if (parts.Length >= 5)
                {
                    blackLiftSlider.Value = Clamp(int.Parse(parts[0]), blackLiftSlider.Minimum, blackLiftSlider.Maximum);
                    gammaSlider.Value = Clamp(int.Parse(parts[1]), gammaSlider.Minimum, gammaSlider.Maximum);
                    contrastSlider.Value = Clamp(int.Parse(parts[2]), contrastSlider.Minimum, contrastSlider.Maximum);
                    brightnessSlider.Value = Clamp(int.Parse(parts[3]), brightnessSlider.Minimum, brightnessSlider.Maximum);
                    if (ddcSupported) hwBrightnessSlider.Value = Clamp(int.Parse(parts[4]), hwBrightnessSlider.Minimum, hwBrightnessSlider.Maximum);
                }
                // Old format compatibility: gamma, brightness, hwBrightness
                else if (parts.Length >= 2)
                {
                    gammaSlider.Value = Clamp(int.Parse(parts[0]), gammaSlider.Minimum, gammaSlider.Maximum);
                    brightnessSlider.Value = Clamp(int.Parse(parts[1]), brightnessSlider.Minimum, brightnessSlider.Maximum);
                    if (parts.Length > 2 && ddcSupported) hwBrightnessSlider.Value = Clamp(int.Parse(parts[2]), hwBrightnessSlider.Minimum, hwBrightnessSlider.Maximum);
                }
            }
        }
        catch { }
    }

    private int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private bool IsInStartup()
    {
        try
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false);
            return key != null && key.GetValue("ShadowBoost") != null;
        }
        catch { return false; }
    }

    private void SetStartup(bool enable)
    {
        try
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (enable)
            {
                key.SetValue("ShadowBoost", "\"" + Application.ExecutablePath + "\"");
            }
            else
            {
                key.DeleteValue("ShadowBoost", false);
            }
        }
        catch { }
    }

    private void ExitApp()
    {
        StopDDCRetry();

        if (isEnabled)
            ResetGamma();

        if (physicalMonitor != IntPtr.Zero)
            DestroyPhysicalMonitor(physicalMonitor);

        UnregisterHotKey(this.Handle, HOTKEY_ID);
        trayIcon.Visible = false;
        Application.Exit();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            Toggle();
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
        {
            if (isEnabled)
                ResetGamma();
            UnregisterHotKey(this.Handle, HOTKEY_ID);
        }
        base.OnFormClosing(e);
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ShadowBoost());
    }
}
