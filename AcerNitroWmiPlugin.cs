using System;
using System.Collections.Generic;
using System.Linq;
using FanControl.Plugins;

namespace FanControl.AcerNitroWmiPlugin
{
    /// <summary>
    /// FanControl plugin for Acer Nitro laptops (designed for the Nitro V 15) that talk to the
    /// embedded controller through the AcerGamingFunction WMI class
	/// Exposes CPU/GPU fan RPM, EC temperatures and one speed control per fan.
    /// </summary>
    public sealed class AcerNitroWmiPlugin : IPlugin2, IDisposable
    {
        private const int MinPercent = 1;            // firmware floor; 0% is never sent
        private const int MaxPercent = 100;
        private const int FailsafeTemp = 100;         // °C: force 100% on every fan regardless of FanControl
        private const int ReassertAfterUpdates = 15; // ~15 s: re-send state so an EC reset (sleep, Acer services) is repaired
        private const int SafeDefaultPercent = 60;   // Failsafe fan speed % used for an uncontrolled fan when no temperature is readable

        // Fallback curves that drive a fan FanControl is NOT controlling
        // while the other fan is. The EC has a single global custom/auto switch, so a fan can't stay on
        // firmware-auto while its sibling is manual.
        private static readonly int[][] CpuFallbackCurve = { new[] { 30, 20 }, new[] { 45, 35 }, new[] { 60, 50 }, new[] { 75, 70 }, new[] { 100, 100 } };
        private static readonly int[][] GpuFallbackCurve = { new[] { 30, 20 }, new[] { 45, 40 }, new[] { 60, 55 }, new[] { 75, 75 }, new[] { 100, 100 } };

        private readonly IPluginLogger _logger;
        private readonly object _ctl = new object();
        private readonly List<FanChannel> _fans = new List<FanChannel>();
        private readonly List<TempChannel> _temps = new List<TempChannel>();

        private AcerWmi _wmi;
        private FanChannel _cpuFan;
        private TempChannel _cpuTemp;
        private TempChannel _gpuTemp;
        private bool _manualMode;
        private bool _exitHookAdded;
        private bool _behaviorFailureLogged;
        private int _updatesSinceSend;

        public AcerNitroWmiPlugin() { }

        public AcerNitroWmiPlugin(IPluginLogger logger)
        {
            _logger = logger;
        }

        public string Name => "Acer Nitro WMI";

        public void Initialize()
        {
            Close();
            try
            {
                var wmi = new AcerWmi();
                if (!wmi.Connect())
                {
                    Log("AcerGamingFunction WMI class not found (unsupported model, or FanControl is not running as admin); plugin disabled.");
                    wmi.Dispose();
                    return;
                }

                int? cpuTemp = wmi.ReadSensor(AcerWmi.CpuTemperature, AcerWmi.ByteMask);
                int? cpuRpm = wmi.ReadSensor(AcerWmi.CpuFanRpm, AcerWmi.WordMask);
                int? gpuRpm = wmi.ReadSensor(AcerWmi.GpuFanRpm, AcerWmi.WordMask);
                if (cpuTemp == null && cpuRpm == null && gpuRpm == null)
                {
                    Log("Acer WMI class is present but every sensor read failed; plugin disabled.");
                    wmi.Dispose();
                    return;
                }

                _wmi = wmi;

                _cpuTemp = new TempChannel { Id = "AcerNitro/CpuTemp", Name = "Nitro CPU Temp (EC)" };
                _gpuTemp = new TempChannel { Id = "AcerNitro/GpuTemp", Name = "Nitro GPU Temp (EC)" };
                _temps.Add(_cpuTemp);
                _temps.Add(_gpuTemp);

                _cpuFan = new FanChannel
                {
                    Key = "CpuFan",
                    Label = "Nitro CPU Fan",
                    RpmAddress = AcerWmi.CpuFanRpm,
                    Devices = new[] { AcerWmi.CpuFanDevice },
                    Temp = _cpuTemp,
                    FallbackCurve = CpuFallbackCurve
                };
                _fans.Add(_cpuFan);

                if (gpuRpm != null)
                {
                    _fans.Add(new FanChannel
                    {
                        Key = "GpuFan",
                        Label = "Nitro GPU Fan",
                        RpmAddress = AcerWmi.GpuFanRpm,
                        Devices = new[] { AcerWmi.GpuFanDeviceA, AcerWmi.GpuFanDeviceB },
                        Temp = _gpuTemp,
                        FallbackCurve = GpuFallbackCurve
                    });
                }

                Log(string.Format("Initialized: {0} fan(s), {1} temperature sensor(s).", _fans.Count, _temps.Count));

                if (!_exitHookAdded)
                {
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreAuto();
                    _exitHookAdded = true;
                }
            }
            catch (Exception ex)
            {
                Log("Initialize failed: " + ex.Message);
                if (_wmi != null)
                    _wmi.Dispose();
                _wmi = null;
                _fans.Clear();
                _temps.Clear();
            }
        }

        public void Load(IPluginSensorsContainer container)
        {
            if (_wmi == null)
                return;

            foreach (var fan in _fans)
            {
                container.FanSensors.Add(new FanSensor(fan));
                container.ControlSensors.Add(new ControlSensor(this, fan));
            }
            foreach (var t in _temps)
                container.TempSensors.Add(new TempSensor(t));
        }

        public void Update()
        {
            var wmi = _wmi;
            if (wmi == null)
                return;

            try
            {
                _cpuTemp.Value = ReadTemp(wmi, AcerWmi.CpuTemperature);

                // The EC reports 0 while the dGPU is asleep, and the address differs between BIOS versions.
                float? gpu = ReadTemp(wmi, AcerWmi.GpuTemperaturePrimary);
                if (gpu == null) gpu = ReadTemp(wmi, AcerWmi.GpuTemperatureFallbackA);
                if (gpu == null) gpu = ReadTemp(wmi, AcerWmi.GpuTemperatureFallbackB);
                _gpuTemp.Value = gpu;

                foreach (var fan in _fans)
                {
                    int? rpm = wmi.ReadSensor(fan.RpmAddress, AcerWmi.WordMask);
                    fan.Rpm = rpm.HasValue ? (float?)rpm.Value : null;
                }

                lock (_ctl)
                {
                    if (AnyRequested())
                    {
                        _updatesSinceSend++;
                        ApplyControl(_updatesSinceSend >= ReassertAfterUpdates);
                    }
                    else if (_manualMode)
                    {
                        RestoreAutoCore(); // a previous restore failed; keep retrying until the EC is back on auto
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Update failed: " + ex.Message);
            }
        }

        public void Close()
        {
            RestoreAuto();
            lock (_ctl)
            {
                _fans.Clear();
                _temps.Clear();
                _cpuFan = null;
                _cpuTemp = null;
                _gpuTemp = null;
                if (_wmi != null)
                {
                    _wmi.Dispose();
                    _wmi = null;
                }
            }
        }

        public void Dispose() => Close();

        // ---- control -------------------------------------------------------------

        internal void RequestPercent(FanChannel fan, float percent)
        {
            if (float.IsNaN(percent) || float.IsInfinity(percent))
                return;

            lock (_ctl)
            {
                fan.Requested = ClampPercent((int)Math.Round(percent));
                ApplyControl(false);
            }
        }

        internal void ReleaseControl(FanChannel fan)
        {
            lock (_ctl)
            {
                fan.Requested = null;
                if (!AnyRequested())
                    RestoreAutoCore();
                else
                    ApplyControl(false);
            }
        }

        private bool AnyRequested()
        {
            return _fans.Any(f => f.Requested.HasValue);
        }

        private void ApplyControl(bool force)
        {
            if (_wmi == null || !AnyRequested())
                return;

            bool failsafe = IsHot(_cpuTemp) || IsHot(_gpuTemp);

            var targets = new int[_fans.Count];
            bool changed = !_manualMode || force;
            for (int i = 0; i < _fans.Count; i++)
            {
                var fan = _fans[i];
                int target = failsafe ? MaxPercent : (fan.Requested ?? FallbackPercent(fan));
                targets[i] = ClampPercent(target);
                if (fan.LastSent != targets[i])
                    changed = true;
            }

            if (!changed)
                return;

            // switch the EC to custom fan behavior, then write each fan.
            if (!_wmi.SetFanBehavior(AcerWmi.FanBehaviorCustom))
            {
                if (!_behaviorFailureLogged)
                {
                    Log("EC rejected the custom fan behavior command; will retry.");
                    _behaviorFailureLogged = true;
                }
                foreach (var f in _fans)
                    f.LastSent = null;
                return;
            }

            _behaviorFailureLogged = false;
            _manualMode = true;
            _updatesSinceSend = 0;

            for (int i = 0; i < _fans.Count; i++)
            {
                var fan = _fans[i];
                bool ok = true;
                foreach (ulong device in fan.Devices)
                {
                    if (!_wmi.SetFanSpeed(device, targets[i]))
                        ok = false; // keep going so the other device is still written
                }
                fan.LastSent = ok ? (int?)targets[i] : null; // null => retried on the next update
            }
        }

        private void RestoreAuto()
        {
            lock (_ctl)
                RestoreAutoCore();
        }

        private void RestoreAutoCore()
        {
            foreach (var f in _fans)
            {
                f.LastSent = null;
                f.Requested = null;
            }

            if (!_manualMode || _wmi == null)
                return;

            bool ok = false;
            try
            {
                ok = _wmi.SetFanBehavior(AcerWmi.FanBehaviorAuto);
            }
            catch { /* best effort */ }

            if (ok)
            {
                _manualMode = false;
                _updatesSinceSend = 0;
            }
        }

        private int FallbackPercent(FanChannel fan)
        {
            float? t = fan.Temp != null ? fan.Temp.Value : null;
            if (!t.HasValue)
            {
                // GPU temp unknown (dGPU asleep): follow the CPU fan.
                if (_cpuFan != null && fan != _cpuFan)
                    return FallbackPercent(_cpuFan);
                return SafeDefaultPercent;
            }
            return Interpolate(fan.FallbackCurve, t.Value);
        }

        private static int Interpolate(int[][] curve, float temp)
        {
            if (temp <= curve[0][0])
                return curve[0][1];
            for (int i = 0; i < curve.Length - 1; i++)
            {
                int x0 = curve[i][0], y0 = curve[i][1];
                int x1 = curve[i + 1][0], y1 = curve[i + 1][1];
                if (temp <= x1)
                    return (int)Math.Round(y0 + (temp - x0) / (x1 - x0) * (y1 - y0));
            }
            return curve[curve.Length - 1][1];
        }

        private static bool IsHot(TempChannel t)
        {
            return t != null && t.Value.HasValue && t.Value.Value >= FailsafeTemp;
        }

        private static int ClampPercent(int percent)
        {
            return Math.Max(MinPercent, Math.Min(MaxPercent, percent));
        }

        private static float? ReadTemp(AcerWmi wmi, uint address)
        {
            int? v = wmi.ReadSensor(address, AcerWmi.ByteMask);
            if (!v.HasValue || v.Value <= 0 || v.Value > 120)
                return null;
            return v.Value;
        }

        private void Log(string message)
        {
            try { if (_logger != null) _logger.Log("[Acer Nitro WMI] " + message); } catch { }
        }
    }

    internal sealed class FanChannel
    {
        public string Key;
        public string Label;
        public uint RpmAddress;
        public ulong[] Devices;
        public TempChannel Temp;
        public int[][] FallbackCurve;
        public float? Rpm;
        public int? Requested;
        public int? LastSent;
    }

    internal sealed class TempChannel
    {
        public string Id;
        public string Name;
        public float? Value;
    }

    internal sealed class FanSensor : IPluginSensor
    {
        private readonly FanChannel _fan;
        public FanSensor(FanChannel fan) { _fan = fan; }
        public string Id => "AcerNitro/" + _fan.Key;
        public string Name => _fan.Label;
        public float? Value { get; private set; }
        public void Update() => Value = _fan.Rpm;
    }

    internal sealed class TempSensor : IPluginSensor
    {
        private readonly TempChannel _t;
        public TempSensor(TempChannel t) { _t = t; }
        public string Id => _t.Id;
        public string Name => _t.Name;
        public float? Value { get; private set; }
        public void Update() => Value = _t.Value;
    }

    internal sealed class ControlSensor : IPluginControlSensor2
    {
        private readonly AcerNitroWmiPlugin _plugin;
        private readonly FanChannel _fan;

        public ControlSensor(AcerNitroWmiPlugin plugin, FanChannel fan)
        {
            _plugin = plugin;
            _fan = fan;
        }

        public string Id => "AcerNitro/" + _fan.Key + "Control";
        public string Name => _fan.Label + " Control";
        public string PairedFanSensorId => "AcerNitro/" + _fan.Key;
        public float? Value { get; private set; }

        public void Update()
        {
            Value = _fan.Requested.HasValue ? (float?)_fan.Requested.Value : null;
        }

        public void Set(float val) => _plugin.RequestPercent(_fan, val);
        public void Reset() => _plugin.ReleaseControl(_fan);
    }
}
