using System;
using System.Management;

namespace FanControl.AcerNitroWmiPlugin
{
    /// <summary>
    /// Thin wrapper around the Acer EC WMI interface (root\wmi, class AcerGamingFunction).
    /// Addresses, payloads and call conventions are copied 1:1 from the Nitrous app
    /// (AcerWmiProtocol / AcerWmiManager), which is confirmed working on the Nitro V 15 (ANV15-51, my personal laptop. Might not work on all models).
    /// </summary>
    internal sealed class AcerWmi : IDisposable
    {
        private const string WmiNamespace = @"root\wmi";
        private const string GamingClass = "AcerGamingFunction";

        private const string MethodGetSysInfo = "GetGamingSysInfo";
        private const string MethodSetFanBehavior = "SetGamingFanBehavior";
        private const string MethodSetFanSpeed = "SetGamingFanSpeed";

        // GetGamingSysInfo addresses (the GPU temperature address differs between BIOS versions).
        public const uint CpuTemperature = 0x0101;
        public const uint CpuFanRpm = 0x0201;
        public const uint GpuFanRpm = 0x0601;
        public const uint GpuTemperaturePrimary = 0x0A01;
        public const uint GpuTemperatureFallbackA = 0x0901;
        public const uint GpuTemperatureFallbackB = 0x0B01;

        public const ulong ByteMask = 0xFF;
        public const ulong WordMask = 0xFFFF;

        // SetGamingFanBehavior payloads.
        public const ulong FanBehaviorAuto = 0x41000F;
        public const ulong FanBehaviorCustom = 0xC3000F;

        // SetGamingFanSpeed device masks.
        public const ulong CpuFanDevice = 1;
        public const ulong GpuFanDeviceA = 2;
        public const ulong GpuFanDeviceB = 4;

        private readonly object _lock = new object();
        private ManagementObjectSearcher _searcher;
        private ManagementObjectCollection _collection;
        private ManagementObject _instance;
        private bool _disposed;

        /// <summary>True when the AcerGamingFunction class has an instance on this machine.</summary>
        public bool Connect()
        {
            lock (_lock)
                return GetInstanceLocked() != null;
        }

        /// <summary>Reads one GetGamingSysInfo value; returns null if the call failed.</summary>
        public int? ReadSensor(uint address, ulong mask)
        {
            lock (_lock)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var instance = GetInstanceLocked();
                        if (instance == null)
                            return null;

                        using (ManagementBaseObject inParams = instance.GetMethodParameters(MethodGetSysInfo))
                        {
                            inParams["gmInput"] = address; // UInt32
                            using (ManagementBaseObject outParams = instance.InvokeMethod(MethodGetSysInfo, inParams, null))
                            {
                                object raw = ReadOutput(outParams);
                                if (raw == null)
                                    return null;
                                return (int)((Convert.ToUInt64(raw) >> 8) & mask);
                            }
                        }
                    }
                    catch
                    {
                        InvalidateLocked();
                    }
                }
                return null;
            }
        }

        public bool SetFanBehavior(ulong payload)
        {
            return Invoke(MethodSetFanBehavior, payload.ToString());
        }

        /// <summary>Sets one fan device to a fixed percentage (1..100). Fan behavior must be "custom".</summary>
        public bool SetFanSpeed(ulong deviceMask, int percent)
        {
            ulong payload = deviceMask | ((ulong)percent << 8);
            return Invoke(MethodSetFanSpeed, payload.ToString());
        }

        private bool Invoke(string method, string input)
        {
            lock (_lock)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var instance = GetInstanceLocked();
                        if (instance == null)
                            return false;

                        using (ManagementBaseObject inParams = instance.GetMethodParameters(method))
                        {
                            inParams["gmInput"] = input;
                            using (ManagementBaseObject outParams = instance.InvokeMethod(method, inParams, null))
                                return outParams != null;
                        }
                    }
                    catch
                    {
                        InvalidateLocked();
                    }
                }
                return false;
            }
        }

        private static object ReadOutput(ManagementBaseObject outParams)
        {
            if (outParams == null)
                return null;
            foreach (PropertyData p in outParams.Properties)
            {
                if (p.Name == "gmOutput" || p.Name == "outValue")
                {
                    if (p.Value != null)
                        return p.Value;
                }
            }
            return null;
        }

        private ManagementObject GetInstanceLocked()
        {
            if (_disposed)
                return null;
            if (_instance != null)
                return _instance;

            _searcher = new ManagementObjectSearcher(WmiNamespace, "SELECT * FROM " + GamingClass);
            _collection = _searcher.Get();
            foreach (ManagementObject mo in _collection)
            {
                _instance = mo;
                break;
            }

            if (_instance == null)
                InvalidateLocked();
            return _instance;
        }

        private void InvalidateLocked()
        {
            try { if (_instance != null) _instance.Dispose(); } catch { }
            try { if (_collection != null) _collection.Dispose(); } catch { }
            try { if (_searcher != null) _searcher.Dispose(); } catch { }
            _instance = null;
            _collection = null;
            _searcher = null;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                InvalidateLocked();
            }
        }
    }
}
