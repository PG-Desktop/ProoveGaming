using System.Runtime.InteropServices;

namespace ProoveGaming.Device.Testing
{
	// Captures HID input via the Windows Raw Input API. Written for the BLAZE DPI button; the
	// Listen(vid, pid, ...) overload makes the plumbing reusable for any device.
	//
	// Captures vendor HID input from the mouse via the Windows Raw Input API
	// (RegisterRawInputDevices + WM_INPUT) — the only way to read input reports from
	// the mouse/keyboard collections that ReadFile is denied on (error 5). This is
	// almost certainly how VS750 reads the live DPI-stage change on button press.
	//
	// Creates a message-only window, enumerates the device's HID top-level
	// collections, registers each (usagePage, usage) with RIDEV_INPUTSINK, and dumps
	// every WM_INPUT HID payload so we can see which byte tracks the DPI stage.
	public static class RawInputDpi
	{
		private const int VID = 0x04D9;
		private const int PID = 0xA245;

		// Invoked for each HID report while listening; when null, reports are dumped to the console.
		private static Action<byte[]>? _onReport;

		public static void Run() =>
			Listen(30, "Registered. Press the PHYSICAL DPI BUTTON slowly for ~25s.\nWatch for a HID payload whose byte steps with the DPI stage.", null);

		// Registers the device's HID collections and pumps WM_INPUT for `seconds`, routing each
		// report to `onReport` (or dumping it if null). Read-only — never writes to the device.
		public static void Listen(int seconds, string prompt, Action<byte[]>? onReport) =>
			Listen(VID, PID, seconds, prompt, onReport, BlazeExtraPairs);

		// Same, for any device. Raw Input is the only way to read input reports from collections
		// Windows claims for itself (consumer-control, keyboard, mouse), where ReadFile is denied —
		// which is exactly the case for a headset's media and function buttons.
		public static void Listen(int vid, int pid, int seconds, string prompt,
			Action<byte[]>? onReport, IEnumerable<(ushort Page, ushort Usage)>? extraPairs = null)
		{
			_onReport = onReport;
			try
			{
				ListenCore(vid, pid, seconds, prompt, extraPairs);
			}
			finally
			{
				_onReport = null;
			}
		}

		// BLAZE's vendor collection (MI_01 Col04) plus the consumer pages enumeration can miss.
		private static readonly (ushort Page, ushort Usage)[] BlazeExtraPairs =
		[
			(0xFF03, 0x00F0),
			(0x000C, 0x0001),
			(0x000C, 0x0080),
			(0x0001, 0x0080),
		];

		private static void ListenCore(int vid, int pid, int seconds, string prompt,
			IEnumerable<(ushort Page, ushort Usage)>? extraPairs)
		{
			var hwnd = CreateMessageWindow();
			if (hwnd == IntPtr.Zero) { Console.WriteLine("CreateWindow failed"); return; }

			// Enumerate raw input devices; find our device's HID collections.
			var pairs = new HashSet<(ushort page, ushort usage)>();
			uint count = 0, size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
			GetRawInputDeviceList(null, ref count, size);
			var list = new RAWINPUTDEVICELIST[count];
			GetRawInputDeviceList(list, ref count, size);

			foreach (var dev in list)
			{
				// Device name (path) to match VID/PID.
				uint nameLen = 0;
				GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref nameLen);
				var nameBuf = Marshal.AllocHGlobal((int)nameLen * 2);
				GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICENAME, nameBuf, ref nameLen);
				var name = Marshal.PtrToStringUni(nameBuf) ?? "";
				Marshal.FreeHGlobal(nameBuf);
				if (!name.Contains($"vid_{vid:x4}", StringComparison.OrdinalIgnoreCase) ||
					!name.Contains($"pid_{pid:x4}", StringComparison.OrdinalIgnoreCase))
					continue;

				var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
				uint infoSize = info.cbSize;
				GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICEINFO, ref info, ref infoSize);

				// Map each collection to a (usagePage, usage) to register.
				ushort page, usage;
				string kind;
				switch (dev.dwType)
				{
					case 0: page = 0x01; usage = 0x02; kind = "MOUSE"; break;      // generic desktop / mouse
					case 1: page = 0x01; usage = 0x06; kind = "KEYBOARD"; break;   // generic desktop / keyboard
					default: page = info.hid.usUsagePage; usage = info.hid.usUsage; kind = "HID"; break;
				}
				pairs.Add((page, usage));
				Console.WriteLine($"  {kind}: usagePage=0x{page:X4} usage=0x{usage:X4} (hid p=0x{info.hid.usUsagePage:X4} u=0x{info.hid.usUsage:X4})");
			}

			if (pairs.Count == 0) { Console.WriteLine("No matching HID collections found."); return; }

			pairs.RemoveWhere(p => p.page == 0); // (0,0) is invalid to register

			// Registering a collection enumeration may have missed costs nothing and can only add
			// coverage, so callers pass the ones they know their device declares.
			foreach (var (page, usage) in extraPairs ?? [])
				pairs.Add((page, usage));
			var rids = pairs.Select(p => new RAWINPUTDEVICE
			{
				usUsagePage = p.page,
				usUsage = p.usage,
				dwFlags = RIDEV_INPUTSINK,
				hwndTarget = hwnd,
			}).ToArray();

			if (!RegisterRawInputDevices(rids, (uint)rids.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
			{
				Console.WriteLine($"RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");
				return;
			}

			Console.WriteLine();
			Console.WriteLine(prompt);
			Console.WriteLine();

			var end = DateTime.UtcNow.AddSeconds(seconds);
			while (DateTime.UtcNow < end)
			{
				while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
				{
					TranslateMessage(ref msg);
					DispatchMessage(ref msg);
				}
				Thread.Sleep(10);
			}
			Console.WriteLine("Done.");
		}

		// Held in a static field so the GC never collects the delegate the OS calls.
		private static WndProc? _proc;

		private static IntPtr CreateMessageWindow()
		{
			_proc = WindowProc;
			var hInstance = GetModuleHandle(null);

			var wc = new WNDCLASS
			{
				lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
				hInstance = hInstance,
				lpszClassName = "X43RawInputWnd",
			};
			ushort atom = RegisterClass(ref wc);
			if (atom == 0)
				Console.WriteLine($"RegisterClass failed: {Marshal.GetLastWin32Error()}");

			var hwnd = CreateWindowEx(0, wc.lpszClassName, "X43", 0, 0, 0, 0, 0,
				HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
			if (hwnd == IntPtr.Zero)
				Console.WriteLine($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
			return hwnd;
		}

		private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
		{
			if (msg == WM_INPUT)
			{
				uint dwSize = 0;
				GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
				var buf = Marshal.AllocHGlobal((int)dwSize);
				try
				{
					if (GetRawInputData(lParam, RID_INPUT, buf, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == dwSize)
					{
						var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
						if (header.dwType == RIM_TYPEHID)
						{
							// After header: RAWHID { dwSizeHid, dwCount, bRawData[] }
							int off = Marshal.SizeOf<RAWINPUTHEADER>();
							uint sizeHid = (uint)Marshal.ReadInt32(buf, off);
							uint hidCount = (uint)Marshal.ReadInt32(buf, off + 4);
							int dataOff = off + 8;
							for (int i = 0; i < hidCount; i++)
							{
								var report = new byte[sizeHid];
								Marshal.Copy(buf + dataOff + i * (int)sizeHid, report, 0, (int)sizeHid);
								if (_onReport != null)
									_onReport(report);
								else
									Console.WriteLine($"  WM_INPUT HID: {BitConverter.ToString(report)}");
							}
						}
					}
				}
				finally { Marshal.FreeHGlobal(buf); }
			}
			return DefWindowProc(hwnd, msg, wParam, lParam);
		}

		// ── P/Invoke ──────────────────────────────────────────────────────────
		private const int RIM_TYPEHID = 2;
		private const uint RIDI_DEVICENAME = 0x20000007;
		private const uint RIDI_DEVICEINFO = 0x2000000b;
		private const uint RIDEV_INPUTSINK = 0x00000100;
		private const uint RID_INPUT = 0x10000003;
		private const uint WM_INPUT = 0x00FF;
		private static readonly IntPtr HWND_MESSAGE = new(-3);

		private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

		[StructLayout(LayoutKind.Sequential)]
		private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

		[StructLayout(LayoutKind.Sequential)]
		private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

		[StructLayout(LayoutKind.Sequential)]
		private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

		[StructLayout(LayoutKind.Sequential)]
		private struct RID_DEVICE_INFO_HID { public uint dwVendorId; public uint dwProductId; public uint dwVersionNumber; public ushort usUsagePage; public ushort usUsage; }

		[StructLayout(LayoutKind.Explicit)]
		private struct RID_DEVICE_INFO
		{
			[FieldOffset(0)] public uint cbSize;
			[FieldOffset(4)] public uint dwType;
			[FieldOffset(8)] public RID_DEVICE_INFO_HID hid;
			// The keyboard union member is the largest (24 bytes), so the whole
			// struct is 32 bytes. Windows rejects the call if cbSize is smaller.
			[FieldOffset(28)] private uint _tail;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct WNDCLASS
		{
			public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
			public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
			[MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
			[MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

		[DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);
		[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
		[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, ref RID_DEVICE_INFO pData, ref uint pcbSize);
		[DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
		[DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);
		[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);
		[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr lpParam);
		[DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
		private const uint PM_REMOVE = 0x0001;
		[DllImport("user32.dll")] private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint wRemoveMsg);
		[DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
		[DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
	}
}
