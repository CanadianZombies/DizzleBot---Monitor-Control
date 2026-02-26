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
// -- Developed by SimmyDizzle (twitch.tv/simmydizzle) on 2026-02-26. Updated 2026-06-01 to add public change message option and improved logging.
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
