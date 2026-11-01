using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

// Unity 6000.x + Input System. Attach once to an empty GameObject.
public sealed class AirplaneUdpController : MonoBehaviour
{
    public string espIp = "192.168.4.1";
    public int espPort = 8000;
    public bool showStatus = true;
    [Range(1, 100)] public int throttleStepPercent = 5;
    [Range(1f, 100f)] public float sliderMaxPercent = 25f;
    [Range(0.1f, 5f)] public float fineStepPercent = 0.1f;
    public bool invertPitch = false;
    public bool invertRoll = false;
    public bool invertYaw = false;

    private int MaxThrottleSetting => Mathf.RoundToInt(Mathf.Clamp(sliderMaxPercent, 1f, 100f) * 10f);
    private bool CanAdjustThrottle => enabled && udp != null && hasToken && hasAck && receiverArmed &&
        focused && !paused && !blocked && wasDeadman &&
        Time.realtimeSinceStartupAsDouble - lastReceive < AckTimeout &&
        Time.realtimeSinceStartupAsDouble - lastFrame < AckTimeout;
    private int throttleSetting; // Protocol units: 0..1000. Reset on every stop.
    private UdpClient udp;
    private IPAddress targetIp;
    private uint token, sequence, lastAck;
    private bool hasToken, hasAck, receiverArmed, receiverReady;
    private bool focused = true, paused, blocked = true;
    private bool wasDeadman, wasStop;
    private Gamepad selectedPad;
    private double nextSend, lastReceive, lastFrame;
    private string status = "Disconnected";
    private const double SendPeriod = 0.02, AckTimeout = 0.20;

    void Awake()
    {
#if !ENABLE_LEGACY_INPUT_MANAGER
        Debug.LogWarning("Slider test panel: set Player Settings > Active Input Handling to Both, then restart Unity.");
#endif
    }

    void OnEnable()
    {
        try
        {
            targetIp = IPAddress.Parse(espIp);
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.Client.Blocking = false;
            focused = Application.isFocused;
            paused = false;
            lastFrame = Time.realtimeSinceStartupAsDouble;
            ResetLink();
        }
        catch (Exception ex) { status = ex.Message; udp?.Close(); udp = null; }
    }

    void ResetLink()
    {
        hasToken = hasAck = receiverArmed = receiverReady = false;
        blocked = true;
        throttleSetting = 0;
        wasDeadman = wasStop = false;
        sequence = 0;
        status = "Connecting / release enable";
    }

    void Update()
    {
        if (udp == null) return;
        double now = Time.realtimeSinceStartupAsDouble;
        // Never continue a motor command after a main-thread stall.
        if (now - lastFrame >= AckTimeout) { SendStop(); ResetLink(); }
        lastFrame = now;
        if (hasToken && now - lastReceive >= AckTimeout) { SendStop(); ResetLink(); }
        try { Receive(now); }
        catch (SocketException ex) { Fail(ex.Message); return; }
        Gamepad pad = Gamepad.current;
        if (pad != selectedPad) { EmergencyStop(); selectedPad = pad; }
        bool padAvailable = pad != null && pad.added;
        Keyboard keyboard = Keyboard.current;
        bool valid = focused && !paused && (padAvailable || keyboard != null);
        // When no gamepad is attached, hold Space to enable mouse-only bench testing.
        // Source changes are handled as a stop above; the new enable must be released first.
        bool deadman = valid && (padAvailable ? pad.leftShoulder.isPressed : keyboard.spaceKey.isPressed);
        bool stop = (padAvailable && pad.buttonEast.isPressed) ||
            (keyboard != null && keyboard.escapeKey.isPressed);
        // Sample stopping actions every rendered frame, independently of send cadence.
        if ((wasDeadman && !deadman) || (stop && !wasStop)) EmergencyStop();
        wasDeadman = deadman;
        wasStop = stop;
        if (!valid || stop) blocked = true;
        if (valid && !stop && !deadman) blocked = false;
        bool requestArm = valid && !stop && !blocked && deadman;
        // Process taps on every frame, even between 50 Hz network sends.
        // Pressing/holding D-pad before ARMED never queues a later increase.
        if (!requestArm || !hasToken || !hasAck || !receiverArmed)
            throttleSetting = 0;
        else
        {
            bool up = padAvailable && pad.dpad.up.wasPressedThisFrame;
            bool down = padAvailable && pad.dpad.down.wasPressedThisFrame;
            if (up != down)
            {
                int step = Mathf.Clamp(throttleStepPercent, 1, 100) * 10;
                throttleSetting = Mathf.Clamp(throttleSetting + (up ? step : -step), 0, MaxThrottleSetting);
            }
        }
        throttleSetting = Mathf.Clamp(throttleSetting, 0, MaxThrottleSetting);
        if (now < nextSend) return;
        nextSend = now + SendPeriod; // No catch-up burst of old commands.
        if (!hasToken) { SendText("PLHELLO"); return; }
        Vector2 stick = valid && padAvailable ? pad.rightStick.ReadValue() : Vector2.zero;
        float yaw = valid && padAvailable ? pad.leftStick.x.ReadValue() : 0f;
        SendCommand(requestArm, requestArm ? throttleSetting : 0,
            Axis(stick.x, invertRoll), Axis(-stick.y, invertPitch), Axis(yaw, invertYaw));
        status = !valid ? "No active controls / stopped" : blocked ? "STOP / release enable" :
            receiverArmed ? "ARMED / adjust slider while holding enable" : receiverReady ? "READY / hold enable for 1 second" :
            "DISARMED / release enable, then hold for 1 second";
    }

    static int Axis(float value, bool inverse)
    {
        if (Mathf.Abs(value) < 0.05f) return 0;
        return Mathf.RoundToInt(Mathf.Clamp(value * (inverse ? -1 : 1), -1f, 1f) * 1000f);
    }

    void Receive(double now)
    {
        for (int i = 0; i < 32 && udp.Available > 0; ++i)
        {
            IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
            string text = Encoding.ASCII.GetString(udp.Receive(ref peer));
            if (!peer.Address.Equals(targetIp) || peer.Port != espPort) continue;
            string[] fields = text.Split(',');
            uint receivedToken;
            if (fields.Length == 2 && fields[0] == "PLH" && !hasToken &&
                uint.TryParse(fields[1], out receivedToken))
            {
                token = receivedToken; hasToken = true; hasAck = false;
                blocked = true; sequence = 0; lastReceive = now;
            }
            else if (fields.Length == 5 && fields[0] == "PLT" && hasToken &&
                uint.TryParse(fields[1], out receivedToken) && receivedToken == token &&
                uint.TryParse(fields[2], out uint ack) &&
                (fields[3] == "0" || fields[3] == "1") && (fields[4] == "0" || fields[4] == "1"))
            {
                uint delta = unchecked(ack - lastAck);
                // Accept strictly progressing ACKs for commands already sent.
                if (sequence == 0 || ack >= sequence || (hasAck && (delta == 0 || delta >= 0x80000000u))) continue;
                hasAck = true; lastAck = ack; lastReceive = now;
                receiverArmed = fields[3] == "1"; receiverReady = fields[4] == "1";
            }
        }
    }

    void SendCommand(bool arm, int throttle, int roll, int pitch, int yaw)
    {
        // Renegotiate before sequence wrap. A new link always requires re-arming.
        if (sequence == uint.MaxValue) { ResetLink(); return; }
        string packet = string.Format(CultureInfo.InvariantCulture, "PL1,{0},{1},{2},{3},{4},{5},{6}",
            token, sequence++, arm ? 1 : 0, throttle, roll, pitch, yaw);
        SendText(packet);
    }
    void SendText(string text)
    {
        try { byte[] bytes = Encoding.ASCII.GetBytes(text); udp.Send(bytes, bytes.Length, new IPEndPoint(targetIp, espPort)); }
        catch (SocketException ex) { Fail(ex.Message); }
    }
    void Fail(string error) { ResetLink(); status = "Network error: " + error; }
    void SendStop() { if (udp != null && hasToken) SendCommand(false, 0, 0, 0, 0); }
    public void EmergencyStop() { blocked = true; throttleSetting = 0; SendStop(); }
    void OnApplicationFocus(bool value) { focused = value; if (!value) EmergencyStop(); }
    void OnApplicationPause(bool value) { paused = value; if (value) EmergencyStop(); }
    void OnDisable() { SendStop(); udp?.Close(); udp = null; ResetLink(); }
    // Slider input uses percent units (not measured motor speed).
    public void SetThrottlePercent(float percent)
    {
        if (float.IsNaN(percent) || float.IsInfinity(percent)) { EmergencyStop(); return; }
        if (!CanAdjustThrottle) { throttleSetting = 0; return; }
        throttleSetting = Mathf.RoundToInt(Mathf.Clamp(percent, 0f, sliderMaxPercent) * 10f);
    }

    void OnGUI()
    {
        // Requires Active Input Handling = Both for runtime IMGUI input events.
        // This built-in panel needs no Canvas, EventSystem or extra UI package.
        Matrix4x4 previousMatrix = GUI.matrix;
        bool previousEnabled = GUI.enabled;
        Color previousBackground = GUI.backgroundColor;
        float scale = Mathf.Clamp(Screen.width / 740f, 0.45f, 1.5f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        GUI.Box(new Rect(16, 16, 700, 320), "ESP32 MOTOR / SLIDER TEST v4");
        if (showStatus) GUI.Label(new Rect(32, 45, 660, 26), status);
        GUI.Label(new Rect(32, 75, 660, 26), "Enable: hold L1 (no gamepad: hold Space) | Esc / Circle / B: STOP");
        GUI.Label(new Rect(32, 105, 660, 26), "Throttle setting: " + (throttleSetting / 10f).ToString("0.0") +
            "%   |   Slider range: 0 - " + sliderMaxPercent.ToString("0.#") + "%");
        GUI.enabled = previousEnabled && CanAdjustThrottle;
        float requested = GUI.HorizontalSlider(new Rect(40, 145, 640, 30),
            throttleSetting / 10f, 0f, Mathf.Clamp(sliderMaxPercent, 1f, 100f));
        // Do not write an unchanged value during repaint/layout events.
        if (Mathf.Abs(requested - throttleSetting / 10f) > 0.001f) SetThrottlePercent(requested);
        string fine = Mathf.Clamp(fineStepPercent, 0.1f, 5f).ToString("0.#");
        if (GUI.Button(new Rect(32, 183, 130, 36), "- " + fine + "%"))
            SetThrottlePercent(throttleSetting / 10f - Mathf.Clamp(fineStepPercent, 0.1f, 5f));
        if (GUI.Button(new Rect(174, 183, 130, 36), "+ " + fine + "%"))
            SetThrottlePercent(throttleSetting / 10f + Mathf.Clamp(fineStepPercent, 0.1f, 5f));
        GUI.enabled = previousEnabled;
        if (GUI.Button(new Rect(316, 183, 130, 36), "ZERO")) SetThrottlePercent(0f);
        GUI.backgroundColor = new Color(1f, 0.3f, 0.3f);
        if (GUI.Button(new Rect(458, 183, 224, 36), "STOP / DISARM")) EmergencyStop();
        GUI.backgroundColor = previousBackground;
        GUI.Label(new Rect(32, 235, 660, 26), CanAdjustThrottle ?
            "Slider enabled | D-pad Up/Down also available | ESP32 pulse cap: 1250 us" :
            "Slider locked at zero | release enable, wait 0.5s, then hold for 1s (after boot 5s)");
        GUI.Label(new Rect(32, 263, 660, 26), "Setting is a command, not measured RPM. Remove propeller for testing.");
#if !ENABLE_LEGACY_INPUT_MANAGER
        GUI.Label(new Rect(32, 291, 660, 26), "UI INPUT DISABLED: set Active Input Handling = Both, then restart Unity.");
#endif
        GUI.matrix = previousMatrix;
        GUI.enabled = previousEnabled;
        GUI.backgroundColor = previousBackground;
    }
}
