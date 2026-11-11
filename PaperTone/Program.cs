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
    private readonly PenMonitor pen;
    private bool enabled = true;

    public TrayApp()
    {
        audio = new ScratchAudio();

        var menu = new ContextMenuStrip();
        statusItem = new ToolStripMenuItem("Pen: starting…") { Enabled = false };
        enabledItem = new ToolStripMenuItem("Sound enabled") { Checked = true, CheckOnClick = true };
        enabledItem.CheckedChanged += (_, _) =>
        {
            enabled = enabledItem.Checked;
            if (!enabled) audio.SetPressure(0);
        };

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

        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
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
            (pressure, moving) => audio.SetPressure(enabled && moving ? pressure : 0),
            message => statusItem.Text = message);
    }

    public void Dispose()
    {
        pen.Dispose();
        audio.Dispose();
        tray.Visible = false;
        tray.Dispose();
    }
}

internal sealed class ScratchAudio : IDisposable
{
    private readonly WaveOutEvent output;
    private readonly PressureScratchProvider source;

    public ScratchAudio()
    {
        source = new PressureScratchProvider(48000);
        output = new WaveOutEvent { DesiredLatency = 30, NumberOfBuffers = 2 };
        output.Init(source);
        output.Play();
    }

    public void SetPressure(float pressure) => source.Pressure = Math.Clamp(pressure, 0f, 1f);
    public void SetMasterVolume(float volume) => source.MasterVolume = Math.Clamp(volume, 0f, 1f);
    public void Dispose() => output.Dispose();
}

// Prototype paper-grain synthesizer. Pressure controls loudness and grain density.
internal sealed class PressureScratchProvider : NAudio.Wave.ISampleProvider
{
    private readonly Random random = new();
    private float pressure;
    private float master = 0.75f;
    private float filtered;
    private float envelope;
    private int grainCounter;
    private float grain;

    public PressureScratchProvider(int sampleRate) =>
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

    public WaveFormat WaveFormat { get; }
    public float Pressure { get => Volatile.Read(ref pressure); set => Volatile.Write(ref pressure, value); }
    public float MasterVolume { get => Volatile.Read(ref master); set => Volatile.Write(ref master, value); }

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float p = Pressure;
            float target = p < 0.02f ? 0f : MathF.Pow(p, 0.72f);
            envelope += (target - envelope) * (target > envelope ? 0.035f : 0.012f);

            float white = (float)(random.NextDouble() * 2.0 - 1.0);
            filtered = filtered * 0.72f + white * 0.28f;
            if (--grainCounter <= 0)
            {
                grainCounter = Math.Max(12, (int)(100 - p * 78));
                grain = (float)(random.NextDouble() * 2.0 - 1.0) * (0.15f + p * 0.5f);
            }

            float texture = filtered * 0.72f + grain * 0.28f;
            buffer[offset + i] = Math.Clamp(texture * envelope * MasterVolume * 0.20f, -0.95f, 0.95f);
        }
        return count;
    }
}

// Receives global pen packets through the tablet driver's WinTab system context.
internal sealed class PenMonitor : NativeWindow, IDisposable
{
    private const uint WTI_DEFSYSCTX = 4;
    private const uint WTI_DEVICES = 100;
    private const uint DVC_NPRESSURE = 15;
    private const uint CXO_SYSTEM = 0x0001;
    private const uint CXO_MESSAGES = 0x0004;
    private const uint PK_X = 0x0080;
    private const uint PK_Y = 0x0100;
    private const uint PK_NORMAL_PRESSURE = 0x0400;
    private const uint WT_DEFBASE = 0x7FF0;
    private const int WT_PACKET = 0x7FF0;
    private const int MotionTimeoutMs = 45;

    private readonly Action<float, bool> sample;
    private readonly Action<string> status;
    private readonly System.Windows.Forms.Timer watchdog;
    private IntPtr context;
    private int lastX = int.MinValue;
    private int lastY = int.MinValue;
    private int pressureMaximum = 1023;
    private long lastMotionAt;
    private bool disposed;

    public PenMonitor(Action<float, bool> sample, Action<string> status)
    {
        this.sample = sample;
        this.status = status;
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
            if (WTInfoA(WTI_DEFSYSCTX, 0, ref lc) == 0)
                throw new InvalidOperationException("The tablet driver did not provide a WinTab system context.");

            lc.Options |= CXO_SYSTEM | CXO_MESSAGES;
            lc.MessageBase = WT_DEFBASE;
            lc.PacketData = PK_X | PK_Y | PK_NORMAL_PRESSURE;
            lc.PacketMode = 0;
            lc.MoveMask = PK_X | PK_Y | PK_NORMAL_PRESSURE;

            var axis = new Axis();
            if (WTInfoAxis(WTI_DEVICES + lc.Device, DVC_NPRESSURE, ref axis) != 0 && axis.Max > axis.Min)
                pressureMaximum = axis.Max - axis.Min;

            context = WTOpenA(Handle, ref lc, true);
            if (context == IntPtr.Zero)
                throw new InvalidOperationException("The WinTab system context could not be opened. Verify the Huion driver and WinTab support.");

            status($"Pen: WinTab connected (0–{pressureMaximum})");
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
            uint serial = unchecked((uint)m.LParam.ToInt64());
            if (WTPacket(context, serial, ref packet))
            {
                bool moving = packet.X != lastX || packet.Y != lastY;
                lastX = packet.X;
                lastY = packet.Y;
                float pressure = Math.Clamp((packet.Pressure / (float)pressureMaximum), 0f, 1f);

                if (moving && pressure > 0.015f)
                {
                    lastMotionAt = Environment.TickCount64;
                    sample(pressure, true);
                }
                else
                {
                    sample(0f, false);
                }
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
