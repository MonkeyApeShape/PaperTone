using System.Runtime.InteropServices;
using System.Windows.Forms;
using NAudio.Wave;

namespace PaperTone;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var app = new TrayApp();
        Application.Run();
    }
}

internal sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem enabledItem;
    private readonly ScratchAudio audio;
    private readonly PenDiagnosticsForm diagnostics;
    private readonly PenMonitor pen;

    public TrayApp()
    {
        audio = new ScratchAudio();
        diagnostics = new PenDiagnosticsForm();

        var menu = new ContextMenuStrip();
        statusItem = new ToolStripMenuItem("Pen: starting…") { Enabled = false };
        enabledItem = new ToolStripMenuItem("Sound enabled") { Checked = true, CheckOnClick = true };
        enabledItem.CheckedChanged += (_, _) =>
        {
            if (!enabledItem.Checked) audio.SetPressure(0);
        };

        menu.Items.Add(statusItem);
        menu.Items.Add("Show pen monitor", null, (_, _) =>
        {
            diagnostics.Show();
            diagnostics.Activate();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Test sound (1.5 seconds)", null, (_, _) => audio.TestSound());

        var volumeMenu = new ToolStripMenuItem("Volume");
        foreach (var level in new[] { 25, 50, 75, 100 })
        {
            var item = new ToolStripMenuItem($"{level}%") { Checked = level == 75 };
            item.Click += (_, _) =>
            {
                audio.SetMasterVolume(level / 100f);
                foreach (ToolStripMenuItem child in volumeMenu.DropDownItems)
                    child.Checked = ReferenceEquals(child, item);
            };
            volumeMenu.DropDownItems.Add(item);
        }

        menu.Items.Add(enabledItem);
        menu.Items.Add(volumeMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "PaperTone — pen sound",
            ContextMenuStrip = menu,
            Visible = true
        };

        pen = new PenMonitor(
            (pressure, moving) => audio.SetPressure(enabledItem.Checked && moving ? pressure : 0),
            message =>
            {
                statusItem.Text = message;
                diagnostics.SetStatus(message);
            },
            (count, x, y, rawPressure, normalizedPressure, moving) =>
                diagnostics.UpdateReading(count, x, y, rawPressure, normalizedPressure, moving));
    }

    public void Dispose()
    {
        pen.Dispose();
        diagnostics.Dispose();
        audio.Dispose();
        tray.Visible = false;
        tray.Dispose();
    }
}


internal sealed class PenDiagnosticsForm : Form
{
    private readonly Label stateLabel;
    private readonly Label statusLabel;
    private readonly Label packetLabel;
    private readonly Label pressureLabel;
    private readonly Label positionLabel;
    private readonly Label motionLabel;

    public PenDiagnosticsForm()
    {
        Text = "PaperTone — Pen Monitor";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new System.Drawing.Size(310, 154);

        stateLabel = MakeLabel("WAITING FOR PEN", 12, 10, 286, 25, 14, true);
        statusLabel = MakeLabel("Starting WinTab…", 12, 38, 286, 18, 8, false);
        packetLabel = MakeLabel("Packets: 0", 12, 62, 135, 18, 9, false);
        pressureLabel = MakeLabel("Pressure: —", 155, 62, 143, 18, 9, false);
        positionLabel = MakeLabel("Position: —", 12, 88, 286, 18, 9, false);
        motionLabel = MakeLabel("Movement: —", 12, 112, 286, 18, 9, false);

        Controls.AddRange(new Control[] { stateLabel, statusLabel, packetLabel, pressureLabel, positionLabel, motionLabel });
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private static Label MakeLabel(string text, int x, int y, int width, int height, float size, bool bold)
    {
        return new Label
        {
            Text = text,
            Location = new System.Drawing.Point(x, y),
            Size = new System.Drawing.Size(width, height),
            Font = new System.Drawing.Font("Segoe UI", size, bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular),
            AutoEllipsis = true
        };
    }

    public void SetStatus(string message) => statusLabel.Text = message;

    public void UpdateReading(long count, int x, int y, uint rawPressure, float normalizedPressure, bool moving)
    {
        bool contact = normalizedPressure > 0.005f;
        stateLabel.Text = contact ? (moving ? "PEN DOWN — MOVING" : "PEN DOWN — STILL") : (moving ? "HOVERING — MOVING" : "HOVER / IDLE");
        packetLabel.Text = $"Packets: {count:N0}";
        pressureLabel.Text = $"Pressure: {rawPressure} ({normalizedPressure:P0})";
        positionLabel.Text = $"Position: X {x:N0}   Y {y:N0}";
        motionLabel.Text = $"Movement: {(moving ? "coordinates changing" : "coordinates unchanged")}";
    }
}

internal sealed class ScratchAudio : IDisposable
{
    private readonly WaveOutEvent output;
    private readonly PressureScratchProvider source;
    private readonly System.Windows.Forms.Timer testTimer;

    public ScratchAudio()
    {
        source = new PressureScratchProvider(48000);
        output = new WaveOutEvent { DesiredLatency = 50, NumberOfBuffers = 3 };
        output.Init(source);
        output.Play();

        testTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        testTimer.Tick += (_, _) =>
        {
            testTimer.Stop();
            source.TestPressure = 0;
        };
    }

    public void SetPressure(float pressure) => source.Pressure = Math.Clamp(pressure, 0f, 1f);
    public void SetMasterVolume(float volume) => source.MasterVolume = Math.Clamp(volume, 0f, 1f);

    public void TestSound()
    {
        source.TestPressure = 0.8f;
        testTimer.Stop();
        testTimer.Start();
    }

    public void Dispose()
    {
        testTimer.Stop();
        testTimer.Dispose();
        output.Dispose();
    }
}

// Procedural paper-grain sound. TestPressure lets the tray menu test audio independently of the pen.
internal sealed class PressureScratchProvider : NAudio.Wave.ISampleProvider
{
    private readonly Random random = new();
    private float pressure;
    private float testPressure;
    private float master = 0.75f;
    private float filtered;
    private float envelope;
    private int grainCounter;
    private float grain;

    public PressureScratchProvider(int sampleRate) =>
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

    public WaveFormat WaveFormat { get; }
    public float Pressure { get => Volatile.Read(ref pressure); set => Volatile.Write(ref pressure, value); }
    public float TestPressure { get => Volatile.Read(ref testPressure); set => Volatile.Write(ref testPressure, value); }
    public float MasterVolume { get => Volatile.Read(ref master); set => Volatile.Write(ref master, value); }

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float p = Math.Max(Pressure, TestPressure);
            float target = p < 0.02f ? 0f : MathF.Pow(p, 0.72f);
            envelope += (target - envelope) * (target > envelope ? 0.0035f : 0.0012f);

            float white = (float)(random.NextDouble() * 2.0 - 1.0);
            filtered = filtered * 0.72f + white * 0.28f;
            if (--grainCounter <= 0)
            {
                grainCounter = Math.Max(12, (int)(100 - p * 78));
                grain = (float)(random.NextDouble() * 2.0 - 1.0) * (0.15f + p * 0.5f);
            }

            float texture = filtered * 0.72f + grain * 0.28f;
            buffer[offset + i] = Math.Clamp(texture * envelope * MasterVolume * 0.55f, -0.95f, 0.95f);
        }
        return count;
    }
}

// Receives pen packets through a non-system WinTab digitizing context. This must not control the OS cursor.
internal sealed class PenMonitor : NativeWindow, IDisposable
{
    private const uint WTI_DEFCONTEXT = 3;
    private const uint WTI_DEVICES = 100;
    private const uint DVC_NPRESSURE = 15;
    private const uint CXO_SYSTEM = 0x0001;
    private const uint CXO_MESSAGES = 0x0004;
    private const uint PK_X = 0x0080;
    private const uint PK_Y = 0x0100;
    private const uint PK_NORMAL_PRESSURE = 0x0400;
    private const uint WT_DEFBASE = 0x7FF0;
    private const int WT_PACKET = 0x7FF0;
    private const int MotionTimeoutMs = 60;

    private readonly Action<float, bool> sample;
    private readonly Action<string> status;
    private readonly Action<long, int, int, uint, float, bool> reading;
    private readonly System.Windows.Forms.Timer watchdog;
    private IntPtr context;
    private int lastX = int.MinValue;
    private int lastY = int.MinValue;
    private int pressureMinimum;
    private int pressureMaximum = 1023;
    private long lastMotionAt;
    private long packetCount;
    private bool disposed;

    public PenMonitor(
        Action<float, bool> sample,
        Action<string> status,
        Action<long, int, int, uint, float, bool> reading)
    {
        this.sample = sample;
        this.status = status;
        this.reading = reading;
        CreateHandle(new CreateParams { Caption = "PaperTone global pen listener" });

        watchdog = new System.Windows.Forms.Timer { Interval = 12 };
        watchdog.Tick += (_, _) =>
        {
            if (Environment.TickCount64 - lastMotionAt > MotionTimeoutMs)
                this.sample(0f, false);
        };
        watchdog.Start();

        try
        {
            var lc = new LogContext { Name = new string('\0', 40) };
            if (WTInfoA(WTI_DEFCONTEXT, 0, ref lc) == 0)
                throw new InvalidOperationException("The tablet driver did not provide a WinTab digitizing context.");

            // Request packet messages without claiming system-cursor control.
            lc.Options &= ~CXO_SYSTEM;
            lc.Options |= CXO_MESSAGES;
            lc.MessageBase = WT_DEFBASE;
            lc.PacketData = PK_X | PK_Y | PK_NORMAL_PRESSURE;
            lc.PacketMode = 0;
            lc.MoveMask = PK_X | PK_Y | PK_NORMAL_PRESSURE;

            var axis = new Axis();
            if (WTInfoAxis(WTI_DEVICES + lc.Device, DVC_NPRESSURE, ref axis) != 0 && axis.Max > axis.Min)
            {
                pressureMinimum = axis.Min;
                pressureMaximum = axis.Max;
            }

            context = WTOpenA(Handle, ref lc, true);
            if (context == IntPtr.Zero)
                throw new InvalidOperationException("The WinTab digitizing context could not be opened. Verify the Huion driver and WinTab support.");

            status($"Pen: connected; waiting for packets ({pressureMinimum}–{pressureMaximum})");
        }
        catch (DllNotFoundException)
        {
            status("Pen unavailable: Huion WinTab driver DLL not found");
        }
        catch (Exception ex)
        {
            status("Pen unavailable: " + ex.Message);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WT_PACKET && context != IntPtr.Zero)
        {
            var packet = new Packet();
            uint serial = unchecked((uint)m.WParam.ToInt64());
            IntPtr packetContext = new IntPtr(m.LParam.ToInt64());
            if (WTPacket(packetContext, serial, ref packet))
            {
                packetCount++;
                bool moving = packet.X != lastX || packet.Y != lastY;
                lastX = packet.X;
                lastY = packet.Y;
                float pressure = Math.Clamp((packet.Pressure - pressureMinimum) / (float)Math.Max(1, pressureMaximum - pressureMinimum), 0f, 1f);

                bool inContact = pressure > 0.005f;
                reading(packetCount, packet.X, packet.Y, packet.Pressure, pressure, moving);

                if (moving && inContact)
                {
                    lastMotionAt = Environment.TickCount64;
                    sample(pressure, true);
                }
                else
                {
                    sample(0f, false);
                }

                if (packetCount == 1 || packetCount % 30 == 0)
                    status($"Pen: receiving packets ({packetCount}); pressure {packet.Pressure}; {(moving && pressure > 0.005f ? "drawing" : "hover/idle")}");
            }
            else
            {
                status("Pen: packet message received, but WTPacket failed");
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        watchdog.Stop();
        watchdog.Dispose();
        sample(0f, false);
        if (context != IntPtr.Zero)
        {
            WTClose(context);
            context = IntPtr.Zero;
        }
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct LogContext
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 40)] public string Name;
        public uint Options, Status, Locks, MessageBase, Device, PacketRate, PacketData, PacketMode, MoveMask;
        public uint ButtonDownMask, ButtonUpMask;
        public int InOrgX, InOrgY, InOrgZ, InExtX, InExtY, InExtZ;
        public int OutOrgX, OutOrgY, OutOrgZ, OutExtX, OutExtY, OutExtZ;
        public uint SensX, SensY, SensZ;
        public int SysMode, SysOrgX, SysOrgY, SysOrgZ, SysExtX, SysExtY, SysExtZ;
        public uint SysSensX, SysSensY, SysSensZ;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Axis
    {
        public int Min, Max;
        public uint Units;
        public int Resolution;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Packet
    {
        public int X, Y;
        public uint Pressure;
    }

    [DllImport("Wintab32.dll", EntryPoint = "WTInfoA", CharSet = CharSet.Ansi)]
    private static extern uint WTInfoA(uint category, uint index, ref LogContext output);

    [DllImport("Wintab32.dll", EntryPoint = "WTInfoA", CharSet = CharSet.Ansi)]
    private static extern uint WTInfoAxis(uint category, uint index, ref Axis output);

    [DllImport("Wintab32.dll", EntryPoint = "WTOpenA", CharSet = CharSet.Ansi)]
    private static extern IntPtr WTOpenA(IntPtr hwnd, ref LogContext context, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("Wintab32.dll", EntryPoint = "WTPacket")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTPacket(IntPtr context, uint serial, ref Packet packet);

    [DllImport("Wintab32.dll", EntryPoint = "WTClose")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTClose(IntPtr context);
}
