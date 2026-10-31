 # Acer Nitro WMI Fan Control plugin




Plugin for [FanControl](https://github.com/Rem0o/FanControl.Releases) that allows you to change fan speed on Acer Nitro laptops that use the `AcerGamingFunction` WMI class in `root\wmi`

---

**Features**

- Fully controllable CPU and GPU fans.
- CPU and GPU fan RPM readout.
- Force fans to 100% if CPU or GPU reaches 100°C.
- FanControl takes over by switching the EC to custom fan mode; when every control is released, or FanControl closes, the EC goes back to auto fan mode.
- The EC has one global custom/auto switch. If you control only one fan, the other follows a built-in failsafe curve instead of being left at a random speed.
- Fan state is re-sent every 15 s so an EC reset (sleep/resume) is repaired.

---

**Notes**

- Do not adjust the fan speed using NitroSense or other fan tools at the same time; they will fight over the EC.
- Acer background services may reset or fight fan behaviour; its better to disable them.
- AI was used to help with making this plugin.
- Requires `.NET 10`.

---

**Installation**

1. Download the latest release zip.
2. Copy `FanControl.AcerNitroWmiPlugin.dll` into FanControl's `Plugins` folder, or install the plugin zip via FanControl GUI (`Settings` > `Install plugin ...`)

---

**Building from source**

1. Download the latest source code zip.
2. Open CMD in the project's root folder.
3. Run `dotnet build -c Release`.
4. Copy `FanControl.AcerNitroWmiPlugin.dll` into FanControl's `Plugins` folder.

---

**Disclaimer: Unofficial open-source FanControl plugin. Not affiliated with Acer or FanControl's developers. Use at your own risk. I'm not responsible for any hardware damage or data loss.**


