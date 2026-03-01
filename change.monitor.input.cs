// -- ============================
// -- Monitor Input Switcher for Streamer.bot
// -- Uses DXVA2 API to change monitor input source (e.g., DisplayPort to HDMI) on supported monitors. 
// -- Configurable target monitor selection.
// -- Saves last input source to a state file to toggle between two ports on each execution.
// -- Configuration:
// -- - TARGET_MONITOR_MODE: "PRIMARY", "FIRST", or "MONITOR_NAME:substring" to select target monitor
// -- - PORT_1 and PORT_2: Define the two input sources to toggle between (e.g., "DISPLAYPORT", "HDMI1"). 
// -- -         See INPUT_SOURCES dictionary for valid options.
// -- - PUBLIC_CHANGE_MESSAGE: Set to true to send a chat message on successful input change, false to only log in Streamer.bot logs.
// -- Usage: Call Execute() without parameters to toggle monitor input. Logs actions and errors to Streamer.bot log 
// --       and sends a chat message on successful change.
// -- Note: Monitor and input support depends on hardware and drivers. Check Streamer.bot logs for details on 
// --       detected monitors and any errors. Test carefully to confirm compatibility with your specific setup.
// -- Developed by SimmyDizzle (twitch.tv/simmydizzle) on 2026-02-20. 
// -- ============================
// -- Changelog:
// -- Updated 2026-03-01 to add generic VCP get/set methods and example channel point redemption method for a blackening effect.
// -- Updated 2026-02-26 to add public change message option and improved logging.
// -- ============================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

public class CPHInline
{
	// -- ============================
	// -- DXVA2 P/INVOKE (smart method)
	// -- ============================
	[DllImport("dxva2.dll", SetLastError = true)]
	private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
		IntPtr hMonitor,
		out uint pdwNumberOfPhysicalMonitors);

	[DllImport("dxva2.dll", SetLastError = true)]
	private static extern bool GetPhysicalMonitorsFromHMONITOR(
		IntPtr hMonitor,
		uint dwPhysicalMonitorArraySize,
		[Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

	[DllImport("dxva2.dll", SetLastError = true)]
	private static extern bool SetVCPFeature(
		IntPtr hMonitor,
		byte bVCPCode,
		uint dwNewValue);

	[DllImport("dxva2.dll", SetLastError = true)]
	private static extern bool GetVCPFeatureAndVCPFeatureReply(
		IntPtr hMonitor,
		byte bVCPCode,
		out uint pvct,
		out uint pdwCurrentValue,
		out uint pdwMaximumValue);

	[DllImport("dxva2.dll", SetLastError = true)]
	private static extern bool DestroyPhysicalMonitor(IntPtr hPhysicalMonitor);

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

	[DllImport("user32.dll")]
	private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

	private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    // -- ============================
	// -- Monitor info tracking
	private struct MonitorInfo
	{
		public IntPtr hMonitor;
		public string name;
		public int x;
		public int y;
		public int width;
		public int height;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int x;
		public int y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int left;
		public int top;
		public int right;
		public int bottom;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct PHYSICAL_MONITOR
	{
		public IntPtr hPhysicalMonitor;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
		public char[] szPhysicalMonitorDescription;
	}

	// -- ============================
	// -- MONITOR INPUT SOURCE CODES
	// -- ============================
	// -- DDC-CI VCP Code 0x60 = Input Source Selection
	private static readonly Dictionary<string, byte> INPUT_SOURCES = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
	{
		{ "VGA", 0x01 },
		{ "VGA1", 0x01 },
		{ "DVI", 0x03 },
		{ "DVI1", 0x03 },
		{ "DVI2", 0x04 },
		{ "HDMI", 0x11 },
		{ "HDMI1", 0x11 },
		{ "HDMI 1", 0x11 },
		{ "HDMI2", 0x12 },
		{ "HDMI 2", 0x12 },
		{ "DISPLAYPORT", 0x0F },
		{ "DP", 0x0F },
		{ "DISPLAY PORT", 0x0F },
		{ "USB-C", 0x1B }
	};
	
	private static readonly string STATE_FILE = @"C:\streamerbot\modules\monitor_input_state.txt";

	private const byte VCP_INPUT_SOURCE = 0x60;
	
	// -- ============================
	// -- VCP CODES FOR ALL MONITOR SETTINGS
	// -- ============================
	// -- Reference: https://en.wikipedia.org/wiki/Display_Data_Channel#VCP_codes
	private static readonly Dictionary<string, byte> VCP_CODES = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
	{
		{ "BRIGHTNESS", 0x10 },
		{ "CONTRAST", 0x12 },
		{ "COLOR_PRESET", 0x14 },
		{ "RED_GAIN", 0x16 },
		{ "GREEN_GAIN", 0x18 },
		{ "BLUE_GAIN", 0x1A },
		{ "INPUT_SOURCE", 0x60 },
		{ "ACTIVE_CONTROL", 0x52 },
		{ "HORIZONTAL_POSITION", 0xAC },
		{ "VERTICAL_POSITION", 0xAE },
		{ "HORIZONTAL_SIZE", 0xB6 },
		{ "DISPLAY_MODE", 0xC0 },
		{ "POWER_CONTROL", 0xC6 },
		{ "DPMS_CONTROL", 0xD6 },
		{ "OSD_CONTROL", 0xF0 }
	};
	
	// -- ============================
	// -- CONFIGURATION
	// -- ============================
	// -- Set to one of:
	// -- - "PRIMARY" : Use primary/default monitor (0,0 coordinates)
	// -- - "FIRST" : Use first monitor found
	// -- - "MONITOR_NAME:substring" : Match monitor by name (e.g., "MONITOR_NAME:Dell" or "MONITOR_NAME:LG")
	private const string TARGET_MONITOR_MODE = "PRIMARY";
	
    private const string PORT_1 = "DISPLAYPORT";
    private const string PORT_2 = "HDMI1";

	// -- Instance fields for monitor enumeration
	private List<MonitorInfo> allMonitors = new List<MonitorInfo>();
	private bool foundAndSet = false;
    private bool publicChangeMessage = false; // -- set to true to send a chat message on successful input change, false to only log in Streamer.bot logs
	private byte targetInputCodeStatic = 0;
	private IntPtr targetMonitorDisplayHandle = IntPtr.Zero;

    // -- ============================
	// -- GENERIC GET/SET METHODS FOR ANY VCP CODE
	// -- ============================
	
	// -- Generic method to get current value of any VCP code.
	// -- Returns current value (0-100 typical), or -1 if unsupported or error occurs.
	public int GetVCPValue(string vcpName)
	{
		try
		{
			if (!VCP_CODES.ContainsKey(vcpName))
			{
				CPH.LogError("[MonitorSettings] Unknown VCP setting: " + vcpName);
				return -1;
			}
			
			byte vcpCode = VCP_CODES[vcpName];
			return GetVCPValueDXVA2(vcpCode, vcpName);
		}
		catch (Exception ex)
		{
			CPH.LogError("[MonitorSettings] Error getting VCP value for " + vcpName + ": " + ex.Message);
			return -1;
		}
	}
	
	// -- Generic method to set value of any VCP code.
	// -- Returns true if successfully set on at least one monitor, false on error.
	public bool SetVCPValue(string vcpName, uint newValue)
	{
		try
		{
			if (!VCP_CODES.ContainsKey(vcpName))
			{
				CPH.LogError("[MonitorSettings] Unknown VCP setting: " + vcpName);
				return false;
			}
			
			byte vcpCode = VCP_CODES[vcpName];
			CPH.LogInfo("[MonitorSettings] Setting " + vcpName + " to " + newValue);
			return SetVCPValueDXVA2(vcpCode, newValue, vcpName);
		}
		catch (Exception ex)
		{
			CPH.LogError("[MonitorSettings] Error setting VCP value for " + vcpName + ": " + ex.Message);
			return false;
		}
	}
	
	// -- ============================
	// -- BRIGHTNESS CONTROL (VCP 0x10)
	// -- ============================
	public int GetBrightness()
	{
		return GetVCPValue("BRIGHTNESS");
	}
	
	public bool SetBrightness(uint value)
	{
		if (value > 100) value = 100;
		return SetVCPValue("BRIGHTNESS", value);
	}
	

    // -- ============================
    // -- CHANNEL POINT REDEMPTION METHODS
    // -- Each method corresponds to a channel point redemption and calls the brightness fade animation.
    // -- ============================
    public bool ChannelPointRedemption_HardToSee() {
        return BrightnessFadeToBlackAndRestore(100,20);
    }
	
	// -- ============================
	// -- CHANNEL POINT REDEMPTION: BLACKOUT
	// -- ============================
	// -- Creates a complete blackout by putting the monitor into standby mode.
	// -- This is more reliable than brightness/contrast as it uses power management.
	// -- Waits 3 seconds in standby, then restores monitor to normal operation.
	public bool ChannelPointRedemption_Blackout()
	{
		try
		{
			CPH.LogInfo("[ChannelPoints] Blackout redemption triggered");
			
			// -- Get current settings before blackout
			int originalBrightness = GetBrightness();
			int originalContrast = GetContrast();
			int originalRedGain = GetRedGain();
			int originalGreenGain = GetGreenGain();
			int originalBlueGain = GetBlueGain();
			int originalPower = GetPowerControl();
			
			CPH.LogInfo("[ChannelPoints] Original settings saved - Power: " + originalPower + ", Brightness: " + originalBrightness + ", Contrast: " + originalContrast);
			
			// -- First, fade brightness, contrast, and color gains to 0 for safety
			CPH.LogInfo("[ChannelPoints] Setting display parameters to 0");
			SetBrightness(0);
			System.Threading.Thread.Sleep(300);
			SetContrast(0);
			System.Threading.Thread.Sleep(300);
			SetRedGain(0);
			System.Threading.Thread.Sleep(100);
			SetGreenGain(0);
			System.Threading.Thread.Sleep(100);
			SetBlueGain(0);
			System.Threading.Thread.Sleep(500);
			
			// -- Now put monitor into standby mode for complete blackout
			CPH.LogInfo("[ChannelPoints] Putting monitor into standby mode");
			SetPowerStandby();
			System.Threading.Thread.Sleep(500); // -- Wait for standby to engage
			
			CPH.LogInfo("[ChannelPoints] Monitor in standby - complete blackout, waiting 3 seconds");
			System.Threading.Thread.Sleep(3000); // -- Wait 3 seconds in complete darkness
			
			// -- Restore monitor from standby
			CPH.LogInfo("[ChannelPoints] Restoring monitor from standby");
			SetPowerOn();
			System.Threading.Thread.Sleep(1000); // -- Wait for monitor to wake up
			
			// -- Restore all original settings
			CPH.LogInfo("[ChannelPoints] Restoring display settings");
			SetBrightness((uint)originalBrightness);
			System.Threading.Thread.Sleep(300);
			SetContrast((uint)originalContrast);
			System.Threading.Thread.Sleep(300);
			if (originalRedGain >= 0) SetRedGain((uint)originalRedGain);
			if (originalGreenGain >= 0) SetGreenGain((uint)originalGreenGain);
			if (originalBlueGain >= 0) SetBlueGain((uint)originalBlueGain);
			System.Threading.Thread.Sleep(500);
			
			CPH.LogInfo("[ChannelPoints] Blackout effect complete - monitor fully restored");
			return true;
		}
		catch (Exception ex)
		{
			CPH.LogError("[ChannelPoints] Error during blackout effect: " + ex.Message);
			return false;
		}
	}

	// -- ============================
	// -- BRIGHTNESS FADE ANIMATION
	// -- ============================
	// -- Saves current brightness, fades to black over specified duration,
	// -- waits 10 seconds in darkness, then fades back to original brightness.
	// -- Useful for transition effects or screen blanking.
	// -- Parameters:
	// --   stepDuration: milliseconds between each brightness step (default 100)
	// --   numSteps: number of steps to fade down/up (default 20, total fade ~2 seconds each direction)
	public bool BrightnessFadeToBlackAndRestore(int stepDuration = 100, int numSteps = 20)
	{
		try
		{
			CPH.LogInfo("[MonitorSettings] Starting brightness fade to black and restore");
			
			// -- Get current brightness
			int originalBrightness = GetBrightness();
			if (originalBrightness < 0)
			{
				CPH.LogError("[MonitorSettings] Could not read current brightness");
				return false;
			}
			
			CPH.LogInfo("[MonitorSettings] Original brightness: " + originalBrightness);
			
			// -- Fade down to 0
			CPH.LogInfo("[MonitorSettings] Fading brightness down to 0 over " + (stepDuration * numSteps) + "ms");
			uint stepSize = (uint)originalBrightness / (uint)numSteps;
			
			for (int i = 0; i <= numSteps; i++)
			{
				uint brightness = (uint)(originalBrightness - (stepSize * i));
				if (brightness > 100) brightness = 0; // -- Clamp to 0
				
				SetBrightness(brightness);
				CPH.LogDebug("[MonitorSettings] Fade step " + i + ": brightness = " + brightness);
				
				if (i < numSteps) // -- Don't wait after final step
				{
					System.Threading.Thread.Sleep(stepDuration);
				}
			}
			
			CPH.LogInfo("[MonitorSettings] Brightness at 0, waiting 10 seconds");
			System.Threading.Thread.Sleep(10000); // -- Wait 10 seconds in darkness
			
			// -- Fade back up to original
			CPH.LogInfo("[MonitorSettings] Fading brightness back up to " + originalBrightness + " over " + (stepDuration * numSteps) + "ms");
			
			for (int i = 0; i <= numSteps; i++)
			{
				uint brightness = (uint)((stepSize * i));
				if (brightness > (uint)originalBrightness) brightness = (uint)originalBrightness; // -- Clamp to original
				
				SetBrightness(brightness);
				CPH.LogDebug("[MonitorSettings] Restore step " + i + ": brightness = " + brightness);
				
				if (i < numSteps) // -- Don't wait after final step
				{
					System.Threading.Thread.Sleep(stepDuration);
				}
			}
			
			CPH.LogInfo("[MonitorSettings] Brightness fade and restore complete");
			return true;
		}
		catch (Exception ex)
		{
			CPH.LogError("[MonitorSettings] Error during brightness fade: " + ex.Message);
			return false;
		}
	}
	
	// -- ============================
	// -- CONTRAST CONTROL (VCP 0x12)
	// -- ============================
	public int GetContrast()
	{
		return GetVCPValue("CONTRAST");
	}
	
	public bool SetContrast(uint value)
	{
		if (value > 100) value = 100;
		return SetVCPValue("CONTRAST", value);
	}
	
	// -- ============================
	// -- COLOR PRESET (VCP 0x14)
	// -- ============================
	// -- Common values: 1=Preserve, 5=6500K, 6=D50, 8=D65, 9=D75, 10=D93, 11=Native
	public int GetColorPreset()
	{
		return GetVCPValue("COLOR_PRESET");
	}
	
	public bool SetColorPreset(uint preset)
	{
		return SetVCPValue("COLOR_PRESET", preset);
	}
	
	// -- ============================
	// -- COLOR GAINS (Red, Green, Blue)
	// -- ============================
	public int GetRedGain()
	{
		return GetVCPValue("RED_GAIN");
	}
	
	public bool SetRedGain(uint value)
	{
		if (value > 100) value = 100;
		return SetVCPValue("RED_GAIN", value);
	}
	
	public int GetGreenGain()
	{
		return GetVCPValue("GREEN_GAIN");
	}
	
	public bool SetGreenGain(uint value)
	{
		if (value > 100) value = 100;
		return SetVCPValue("GREEN_GAIN", value);
	}
	
	public int GetBlueGain()
	{
		return GetVCPValue("BLUE_GAIN");
	}
	
	public bool SetBlueGain(uint value)
	{
		if (value > 100) value = 100;
		return SetVCPValue("BLUE_GAIN", value);
	}
	
	// -- ============================
	// -- POSITION CONTROL
	// -- ============================
	public int GetHorizontalPosition()
	{
		return GetVCPValue("HORIZONTAL_POSITION");
	}
	
	public bool SetHorizontalPosition(uint value)
	{
		if (value > 100) value = 100;
		return SetVCPValue("HORIZONTAL_POSITION", value);
	}
	
	public int GetVerticalPosition()
	{
		return GetVCPValue("VERTICAL_POSITION");
	}
	
	public bool SetVerticalPosition(uint value)
	{
		if (value > 100) value = 100;
		return SetVCPValue("VERTICAL_POSITION", value);
	}
	
	// -- ============================
	// -- SIZE CONTROL
	// -- ============================
	public int GetHorizontalSize()
	{
		return GetVCPValue("HORIZONTAL_SIZE");
	}
	
	public bool SetHorizontalSize(uint value)
	{
		if (value > 100) value = 100;
		return SetVCPValue("HORIZONTAL_SIZE", value);
	}
	
	// -- ============================
	// -- DISPLAY MODE (VCP 0xC0)
	// -- ============================
	// -- Values: 0=Standard, 1=Movie, 2=Game, 3=Culture, 4=Sports, 5=Web, 6=Custom0-15
	public int GetDisplayMode()
	{
		return GetVCPValue("DISPLAY_MODE");
	}
	
	public bool SetDisplayMode(uint mode)
	{
		return SetVCPValue("DISPLAY_MODE", mode);
	}
	
	// -- ============================
	// -- POWER CONTROL (VCP 0xC6)
	// -- ============================
	// -- Values: 1=On, 4=Standby, 5=Sleep
	public int GetPowerControl()
	{
		return GetVCPValue("POWER_CONTROL");
	}
	
	public bool SetPowerOn()
	{
		return SetVCPValue("POWER_CONTROL", 1);
	}
	
	public bool SetPowerStandby()
	{
		return SetVCPValue("POWER_CONTROL", 4);
	}
	
	public bool SetPowerSleep()
	{
		return SetVCPValue("POWER_CONTROL", 5);
	}
	
	// -- ============================
	// -- DPMS CONTROL (VCP 0xD6)
	// -- ============================
	// -- Disable/Enable: 0=Disabled, 1=Enabled
	public int GetDPMSControl()
	{
		return GetVCPValue("DPMS_CONTROL");
	}
	
	public bool SetDPMSEnabled(bool enabled)
	{
		return SetVCPValue("DPMS_CONTROL", enabled ? 1U : 0U);
	}
	
	// -- ============================
	// -- OSD CONTROL (VCP 0xF0)
	// -- ============================
	// -- On/Off: 0=Disabled, 1=Enabled
	public int GetOSDControl()
	{
		return GetVCPValue("OSD_CONTROL");
	}
	
	public bool SetOSDEnabled(bool enabled)
	{
		return SetVCPValue("OSD_CONTROL", enabled ? 1U : 0U);
	}

    // -- ============================
	// -- DXVA2 IMPLEMENTATION METHODS FOR GET/SET
	// -- ============================
	
	// -- Internal method: Gets VCP value from target monitor via DXVA2 API.
	// -- Enumerates monitors using TARGET_MONITOR_MODE, reads VCP code, returns current value.
	// -- Returns value 0-100+ if successful, -1 on error or if monitor doesn't support code.
	private int GetVCPValueDXVA2(byte vcpCode, string vcpName)
	{
		try
		{
			// -- Collect all monitors
			allMonitors.Clear();
			EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorCollectionCallback, IntPtr.Zero);
			
			if (allMonitors.Count == 0)
			{
				CPH.LogError("[MonitorSettings] No monitors found for getting " + vcpName);
				return -1;
			}
			
			// -- Find target monitor
			MonitorInfo? targetMonitor = FindTargetMonitor();
			if (!targetMonitor.HasValue)
			{
				CPH.LogError("[MonitorSettings] Could not find target monitor for " + vcpName);
				return -1;
			}
			
			// -- Get physical monitors from target display
			if (!GetNumberOfPhysicalMonitorsFromHMONITOR(targetMonitor.Value.hMonitor, out uint numMonitors))
			{
				CPH.LogError("[MonitorSettings] Could not get physical monitors for " + vcpName);
				return -1;
			}
			
			if (numMonitors == 0)
			{
				CPH.LogError("[MonitorSettings] No physical monitors on target display for " + vcpName);
				return -1;
			}
			
			PHYSICAL_MONITOR[] physicalMonitors = new PHYSICAL_MONITOR[numMonitors];
			if (!GetPhysicalMonitorsFromHMONITOR(targetMonitor.Value.hMonitor, numMonitors, physicalMonitors))
			{
				CPH.LogError("[MonitorSettings] Could not get physical monitor handles for " + vcpName);
				return -1;
			}
			
			// -- Try to read from each physical monitor
			for (int i = 0; i < numMonitors; i++)
			{
				try
				{
					if (GetVCPFeatureAndVCPFeatureReply(
						physicalMonitors[i].hPhysicalMonitor,
						vcpCode,
						out uint pvct,
						out uint currentValue,
						out uint maxValue))
					{
						string description = new string(physicalMonitors[i].szPhysicalMonitorDescription).TrimEnd('\0');
						CPH.LogInfo("[MonitorSettings] Read " + vcpName + " from " + description + ": " + currentValue + " (max: " + maxValue + ")");
						return (int)currentValue;
					}
				}
				catch (Exception ex)
				{
					CPH.LogInfo("[MonitorSettings] Error reading from physical monitor: " + ex.Message);
				}
				finally
				{
					if (physicalMonitors[i].hPhysicalMonitor != IntPtr.Zero)
					{
						DestroyPhysicalMonitor(physicalMonitors[i].hPhysicalMonitor);
					}
				}
			}
			
			CPH.LogWarn("[MonitorSettings] Could not read " + vcpName + " from any monitor (may not be supported)");
			return -1;
		}
		catch (Exception ex)
		{
			CPH.LogError("[MonitorSettings] GetVCPValueDXVA2 error for " + vcpName + ": " + ex.Message);
			return -1;
		}
	}
	
	// -- Internal method: Sets VCP value on target monitor via DXVA2 API.
	// -- Enumerates monitors using TARGET_MONITOR_MODE, sets VCP code on all physical monitors.
	// -- Returns true if set succeeds on at least one monitor, false on error.
	private bool SetVCPValueDXVA2(byte vcpCode, uint newValue, string vcpName)
	{
		try
		{
			// -- Collect all monitors
			allMonitors.Clear();
			EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorCollectionCallback, IntPtr.Zero);
			
			if (allMonitors.Count == 0)
			{
				CPH.LogError("[MonitorSettings] No monitors found for setting " + vcpName);
				return false;
			}
			
			// -- Find target monitor
			MonitorInfo? targetMonitor = FindTargetMonitor();
			if (!targetMonitor.HasValue)
			{
				CPH.LogError("[MonitorSettings] Could not find target monitor for " + vcpName);
				return false;
			}
			
			targetMonitorDisplayHandle = targetMonitor.Value.hMonitor;
			targetInputCodeStatic = vcpCode;
			
			// -- Get physical monitors from target display
			if (!GetNumberOfPhysicalMonitorsFromHMONITOR(targetMonitor.Value.hMonitor, out uint numMonitors))
			{
				CPH.LogError("[MonitorSettings] Could not get physical monitors for " + vcpName);
				return false;
			}
			
			if (numMonitors == 0)
			{
				CPH.LogError("[MonitorSettings] No physical monitors on target display for " + vcpName);
				return false;
			}
			
			PHYSICAL_MONITOR[] physicalMonitors = new PHYSICAL_MONITOR[numMonitors];
			if (!GetPhysicalMonitorsFromHMONITOR(targetMonitor.Value.hMonitor, numMonitors, physicalMonitors))
			{
				CPH.LogError("[MonitorSettings] Could not get physical monitor handles for " + vcpName);
				return false;
			}
			
			// -- Try to set on each physical monitor
			bool anySuccess = false;
			for (int i = 0; i < numMonitors; i++)
			{
				try
				{
					string description = new string(physicalMonitors[i].szPhysicalMonitorDescription).TrimEnd('\0');
					CPH.LogInfo("[MonitorSettings] Setting " + vcpName + " on " + description + " to " + newValue);
					
					if (SetVCPFeature(physicalMonitors[i].hPhysicalMonitor, vcpCode, newValue))
					{
						CPH.LogInfo("[MonitorSettings] Successfully set " + vcpName + " to " + newValue);
						anySuccess = true;
					}
					else
					{
						int error = Marshal.GetLastWin32Error();
						CPH.LogWarn("[MonitorSettings] SetVCPFeature failed for " + vcpName + " (Error: " + error.ToString() + ")");
					}
				}
				catch (Exception ex)
				{
					CPH.LogInfo("[MonitorSettings] Error setting on physical monitor: " + ex.Message);
				}
				finally
				{
					if (physicalMonitors[i].hPhysicalMonitor != IntPtr.Zero)
					{
						DestroyPhysicalMonitor(physicalMonitors[i].hPhysicalMonitor);
					}
				}
			}
			
			return anySuccess;
		}
		catch (Exception ex)
		{
			CPH.LogError("[MonitorSettings] SetVCPValueDXVA2 error for " + vcpName + ": " + ex.Message);
			return false;
		}
	}
	
	// -- Helper method: Find target monitor based on TARGET_MONITOR_MODE setting.
	private MonitorInfo? FindTargetMonitor()
	{
		if (TARGET_MONITOR_MODE == "PRIMARY")
		{
			foreach (var mon in allMonitors)
			{
				if (mon.x == 0 && mon.y == 0)
				{
					return mon;
				}
			}
			if (allMonitors.Count > 0)
			{
				return allMonitors[0];
			}
		}
		else if (TARGET_MONITOR_MODE == "FIRST")
		{
			if (allMonitors.Count > 0)
			{
				return allMonitors[0];
			}
		}
		else if (TARGET_MONITOR_MODE.StartsWith("MONITOR_NAME:"))
		{
			string searchStr = TARGET_MONITOR_MODE.Substring("MONITOR_NAME:".Length);
			foreach (var mon in allMonitors)
			{
				if (mon.name.IndexOf(searchStr, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return mon;
				}
			}
		}
		
		return null;
	}

    // -- ============================
	// -- Main entry point called by Streamer.bot. Gets toggle input, validates it, applies monitor input change, and saves state.
	// -- Call without parameters; returns true if input was successfully changed, false on any error.
	public bool Execute()
	{
		try
		{
			CPH.LogInfo("[MonitorInput] Monitor input change requested");
			
			string targetInput = PORT_1; // -- our default (see list above (MONITOR INPUT SOURCE CODES) for valid options)
			targetInput = GetToggleInput();
			CPH.LogInfo("[MonitorInput] Toggling to: " + targetInput);
			
			if (!INPUT_SOURCES.ContainsKey(targetInput))
			{
				string validInputs = string.Join(", ", INPUT_SOURCES.Keys);
				CPH.LogError("[MonitorInput] Invalid input source: " + targetInput);
				CPH.LogError("[MonitorInput] Valid options: " + validInputs);
				return false;
			}
			
			byte inputCode = INPUT_SOURCES[targetInput];
			bool success = SetMonitorInputDXVA2(inputCode);
			
			if (success)
			{
				SaveCurrentInput(targetInput);
				CPH.LogInfo("[MonitorInput] Successfully switched to: " + targetInput);
				if (publicChangeMessage)
                    CPH.SendMessage("Monitor switched to " + targetInput);
				return true;
			}
			else
			{
				CPH.LogWarn("[MonitorInput] Failed to change monitor input");
			}
		}
		catch (Exception ex)
		{
			CPH.LogError("[MonitorInput] Error: " + ex.Message);
			return false;
		}

        // -- should we reach this point, it means the input change failed but no exceptions were thrown. Return true to avoid retry loops, but log a warning.
        CPH.LogWarn("[MonitorInput] Monitor input change did not succeed but no exceptions were thrown");
        return true;
	}

	// -- Reads last saved monitor port from state file and returns the opposite port to toggle between configured ports.
	// -- Returns PORT_1 if state file doesn't exist, is unreadable, or contains unrecognized value. Defaults to PORT_1 on any error.
	private string GetToggleInput()
	{
		try
		{
			if (File.Exists(STATE_FILE))
			{
				string lastInput = File.ReadAllText(STATE_FILE).Trim();
				CPH.LogInfo("[MonitorInput] Last input was: " + lastInput);
				
				if (lastInput.StartsWith(PORT_2, StringComparison.OrdinalIgnoreCase))
				{
					return PORT_1;
				}
				else if (lastInput.StartsWith(PORT_1, StringComparison.OrdinalIgnoreCase))
				{
					return PORT_2;
				}
			}
		}
		catch (Exception ex)
		{
			CPH.LogWarn("[MonitorInput] Could not read state file: " + ex.Message);
		}
		
		return PORT_1;
	}

    // -- ============================
	// -- Persists current input source to state file for toggle tracking between executions. Creates parent directory if it doesn't exist.
	// -- Pass the input source name (e.g., "DISPLAYPORT" or "HDMI1"). Logs warnings if write fails but doesn't throw exceptions.
	private void SaveCurrentInput(string inputSource)
	{
		try
		{
			string directory = Path.GetDirectoryName(STATE_FILE);
			if (!Directory.Exists(directory))
				Directory.CreateDirectory(directory);
			
			File.WriteAllText(STATE_FILE, inputSource);
			CPH.LogInfo("[MonitorInput] Saved state: " + inputSource);
		}
		catch (Exception ex)
		{
			CPH.LogWarn("[MonitorInput] Could not save state: " + ex.Message);
		}
	}

    // -- ============================
	// -- Enumerates all displays, identifies target monitor using TARGET_MONITOR_MODE setting, then applies VCP input change via DXVA2 API.
	// -- Pass VCP input code (e.g., 0x0F for DisplayPort, 0x12 for HDMI2). Returns true if at least one monitor was successfully set.
	private bool SetMonitorInputDXVA2(byte inputCode)
	{
		try
		{
			CPH.LogInfo("[MonitorInput] Setting monitor input to code 0x" + inputCode.ToString("X2") + " using DXVA2 API");
			
			// -- First pass: collect all monitors
			allMonitors.Clear();
			EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorCollectionCallback, IntPtr.Zero);
			// -- Log all monitors found
			CPH.LogInfo("[MonitorInput] Found " + allMonitors.Count + " monitors:");
			for (int i = 0; i < allMonitors.Count; i++)
			{
				bool isPrimary = (allMonitors[i].x == 0 && allMonitors[i].y == 0);
				string primaryStr = isPrimary ? " [PRIMARY]" : "";
				CPH.LogInfo("[MonitorInput]   [" + i + "] " + allMonitors[i].name + " at (" + allMonitors[i].x + ", " + allMonitors[i].y + ") " + allMonitors[i].width + "x" + allMonitors[i].height + primaryStr);
			}
			
			if (allMonitors.Count == 0)
			{
				CPH.LogError("[MonitorInput] No monitors found");
				return false;
			}
			
			// -- Find target monitor based on configuration
			MonitorInfo? targetMonitor = null;
			
			if (TARGET_MONITOR_MODE == "PRIMARY")
			{
				// -- Find primary monitor (0,0)
				foreach (var mon in allMonitors)
				{
					if (mon.x == 0 && mon.y == 0)
					{
						targetMonitor = mon;
						CPH.LogInfo("[MonitorInput] Selected PRIMARY monitor: " + mon.name);
						break;
					}
				}
				if (!targetMonitor.HasValue && allMonitors.Count > 0)
				{
					targetMonitor = allMonitors[0];
					CPH.LogInfo("[MonitorInput] No primary monitor found at (0,0), using first: " + allMonitors[0].name);
				}
			}
			else if (TARGET_MONITOR_MODE == "FIRST")
			{
				targetMonitor = allMonitors[0];
				CPH.LogInfo("[MonitorInput] Selected FIRST monitor: " + allMonitors[0].name);
			}
			else if (TARGET_MONITOR_MODE.StartsWith("MONITOR_NAME:"))
			{
				string searchStr = TARGET_MONITOR_MODE.Substring("MONITOR_NAME:".Length);
				foreach (var mon in allMonitors)
				{
					if (mon.name.IndexOf(searchStr, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						targetMonitor = mon;
						CPH.LogInfo("[MonitorInput] Selected monitor by name pattern '" + searchStr + "': " + mon.name);
						break;
					}
				}
				if (!targetMonitor.HasValue)
				{
					CPH.LogError("[MonitorInput] No monitor matching name pattern '" + searchStr + "' found");
					return false;
				}
			}
			
			if (!targetMonitor.HasValue)
			{
				CPH.LogError("[MonitorInput] Could not determine target monitor");
				return false;
			}
			
			// -- Second pass: apply to target monitor only
			foundAndSet = false;
			targetInputCodeStatic = inputCode;
			targetMonitorDisplayHandle = targetMonitor.Value.hMonitor;
			CPH.LogInfo("[MonitorInput] Target monitor handle: " + targetMonitorDisplayHandle.ToString());
			
			return SetInputOnTargetMonitor();
		}
		catch (Exception ex)
		{
			CPH.LogError("[MonitorInput] SetMonitorInputDXVA2 error: " + ex.Message);
			return false;
		}
	}
	
    // -- ============================
	// -- Applies VCP code 0x60 (input source) to physical monitors on target display handle. Validates monitor response with GetVCPFeatureAndVCPFeatureReply before writing.
	// -- Uses pre-set targetMonitorDisplayHandle and targetInputCodeStatic fields. Returns true if any physical monitor SetVCPFeature call succeeds.
	private bool SetInputOnTargetMonitor()
	{
		try
		{
			CPH.LogInfo("[MonitorInput] Applying input change to target monitor...");
			
			if (targetMonitorDisplayHandle == IntPtr.Zero)
			{
				CPH.LogError("[MonitorInput] Target monitor handle is invalid");
				return false;
			}
			
			// -- Get number of physical monitors on this display
			if (!GetNumberOfPhysicalMonitorsFromHMONITOR(targetMonitorDisplayHandle, out uint numMonitors))
			{
				CPH.LogError("[MonitorInput] Could not get physical monitors count for target");
				return false;
			}
			
			CPH.LogInfo("[MonitorInput] Target display has " + numMonitors + " physical monitors");
			
			if (numMonitors == 0)
			{
				CPH.LogError("[MonitorInput] No physical monitors on target display");
				return false;
			}
			
			// -- Get physical monitor handles
			PHYSICAL_MONITOR[] physicalMonitors = new PHYSICAL_MONITOR[numMonitors];
			if (!GetPhysicalMonitorsFromHMONITOR(targetMonitorDisplayHandle, numMonitors, physicalMonitors))
			{
				CPH.LogError("[MonitorInput] Could not get physical monitor handles");
				return false;
			}
			
			// -- Try to set input on each physical monitor
			for (int i = 0; i < numMonitors; i++)
			{
				try
				{
					string description = new string(physicalMonitors[i].szPhysicalMonitorDescription).TrimEnd('\0');
					CPH.LogInfo("[MonitorInput] Setting input on physical monitor: " + description);
					
					// -- Get current input first (to verify it's responding)
					if (GetVCPFeatureAndVCPFeatureReply(
						physicalMonitors[i].hPhysicalMonitor,
						VCP_INPUT_SOURCE,
						out uint pvct,
						out uint currentValue,
						out uint maxValue))
					{
						CPH.LogInfo("[MonitorInput] Current input: 0x" + currentValue.ToString("X2") + ", Max: 0x" + maxValue.ToString("X2"));
					}
					
					// -- Set the new input source
					if (SetVCPFeature(physicalMonitors[i].hPhysicalMonitor, VCP_INPUT_SOURCE, targetInputCodeStatic))
					{
						CPH.LogInfo("[MonitorInput] SetVCPFeature succeeded for monitor");
						foundAndSet = true;
					}
					else
					{
						int error = Marshal.GetLastWin32Error();
						CPH.LogInfo("[MonitorInput] SetVCPFeature failed (Error: " + error.ToString() + ")");
					}
				}
				catch (Exception ex)
				{
					CPH.LogInfo("[MonitorInput] Error setting input on physical monitor: " + ex.Message);
				}
				finally
				{
					// -- Always destroy the monitor handle
					if (physicalMonitors[i].hPhysicalMonitor != IntPtr.Zero)
					{
						DestroyPhysicalMonitor(physicalMonitors[i].hPhysicalMonitor);
					}
				}
			}
			
			if (foundAndSet)
			{
				CPH.LogInfo("[MonitorInput] Successfully set monitor input via DXVA2");
				return true;
			}
			else
			{
				CPH.LogWarn("[MonitorInput] Failed to set monitor input via DXVA2");
				return false;
			}
		}
		catch (Exception ex)
		{
			CPH.LogError("[MonitorInput] SetInputOnTargetMonitor error: " + ex.Message);
			return false;
		}
	}

    // -- ============================	
	// -- Enumeration callback invoked once per display monitor by EnumDisplayMonitors. Retrieves monitor name, position, and size, then adds to allMonitors list.
	// -- Always returns true to continue enumeration. Automatically destroys physical monitor handles after collecting info. Called internally during monitor enumeration.
	private bool MonitorCollectionCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
	{
		try
		{
			string desc = "Monitor";
			// -- Try to get physical monitor description for naming
			if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint numPhysical))
			{
				if (numPhysical > 0)
				{
					PHYSICAL_MONITOR[] physMonitors = new PHYSICAL_MONITOR[numPhysical];
					if (GetPhysicalMonitorsFromHMONITOR(hMonitor, numPhysical, physMonitors))
					{
						if (physMonitors[0].szPhysicalMonitorDescription != null)
							desc = new string(physMonitors[0].szPhysicalMonitorDescription).TrimEnd('\0');
								// -- Clean up handles
						for (int i = 0; i < numPhysical; i++)
							DestroyPhysicalMonitor(physMonitors[i].hPhysicalMonitor);
					}
				}
			}
			
			allMonitors.Add(new MonitorInfo
			{
				hMonitor = hMonitor,
				name = desc,
				x = lprcMonitor.left,
				y = lprcMonitor.top,
				width = lprcMonitor.right - lprcMonitor.left,
				height = lprcMonitor.bottom - lprcMonitor.top
			});
		}
		catch (Exception ex)
		{
			CPH.LogInfo("[MonitorInput] Error collecting monitor info: " + ex.Message);
		}
		return true;
	}
}
