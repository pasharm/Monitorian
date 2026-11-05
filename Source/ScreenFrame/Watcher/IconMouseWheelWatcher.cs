using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

using static ScreenFrame.WindowHelper;

namespace ScreenFrame.Watcher;

/// <summary>
/// Watcher of mouse wheel rotated while mouse pointer is over NotifyIcon
/// </summary>
/// <remarks>
/// A low-level mouse hook is set only while mouse pointer is over NotifyIcon. It runs in
/// a dedicated thread so that it will not be affected by the UI thread being busy.
/// </remarks>
internal class IconMouseWheelWatcher : IDisposable
{
	#region Win32

	[DllImport("User32.dll")]
	private static extern IntPtr SetWindowsHookEx(
		int idHook,
		LowLevelMouseProc lpfn,
		IntPtr hMod,
		uint dwThreadId);

	private delegate IntPtr LowLevelMouseProc(
		int nCode,
		IntPtr wParam,
		IntPtr lParam);

	[DllImport("User32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("User32.dll")]
	private static extern IntPtr CallNextHookEx(
		IntPtr hhk,
		int nCode,
		IntPtr wParam,
		IntPtr lParam);

	[DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr GetModuleHandle(string lpModuleName);

	[StructLayout(LayoutKind.Sequential)]
	private struct MSLLHOOKSTRUCT
	{
		public POINT pt;
		public uint mouseData;
		public uint flags;
		public uint time;
		public IntPtr dwExtraInfo;
	}

	private const int WH_MOUSE_LL = 14;
	private const int HC_ACTION = 0;
	private const uint WM_MOUSEWHEEL = 0x020A;

	#endregion

	private readonly Action<int> _onMouseWheel;
	private readonly Dispatcher _dispatcher;

	public IconMouseWheelWatcher(Action<int> onMouseWheel)
	{
		this._onMouseWheel = onMouseWheel ?? throw new ArgumentNullException(nameof(onMouseWheel));
		this._dispatcher = Dispatcher.CurrentDispatcher;
	}

	private volatile bool _isWatching;

	/// <summary>
	/// Whether mouse wheel is being watched
	/// </summary>
	public bool IsWatching => _isWatching;

	/// <summary>
	/// Starts watching mouse wheel within the specified rectangle.
	/// </summary>
	/// <param name="iconRect">Rectangle of NotifyIcon in physical coordinates</param>
	public void Start(Rect iconRect)
	{
		if (_isWatching)
			return;

		_isWatching = true;
		_hookDispatcher ??= CreateHookDispatcher();
		_hookDispatcher.InvokeAsync(() => AddHook(iconRect));
	}

	/// <summary>
	/// Stops watching mouse wheel.
	/// </summary>
	public void Stop()
	{
		_hookDispatcher?.InvokeAsync(RemoveHook);
	}

	#region Hook thread

	private Dispatcher _hookDispatcher;

	private static Dispatcher CreateHookDispatcher()
	{
		Dispatcher dispatcher = null;
		using var created = new ManualResetEventSlim();

		var thread = new Thread(() =>
		{
			dispatcher = Dispatcher.CurrentDispatcher;
			created.Set();
			Dispatcher.Run();
		})
		{ IsBackground = true };
		thread.Start();

		created.Wait();
		return dispatcher;
	}

	// The following members are accessed only in the hook thread.
	private IntPtr _hook;
	private LowLevelMouseProc _hookProc;
	private Rect _iconRect;
	private DispatcherTimer _timer;
	private ForegroundWindowWatcher _watcher;

	private void AddHook(Rect iconRect)
	{
		_iconRect = iconRect;
		_hookProc ??= HookProc; // Prevent delegate of non-static method from being garbage collected.
		_hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
		if (_hook == IntPtr.Zero)
		{
			_isWatching = false;
			return;
		}

		// The location of mouse move in the hook is not used to check if mouse pointer leaves
		// the rectangle because it is not clipped to the screen yet and so can go outside the
		// rectangle when mouse pointer is pushed against the bottom edge of the screen.
		if (_timer is null)
		{
			_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
			_timer.Tick += (_, _) =>
			{
				if (!CursorHelper.TryGetCursorLocation(out Point location) || !_iconRect.Contains(location))
					RemoveHook();
			};
		}
		_timer.Start();

		// If the foreground window changes, another window may come over NotifyIcon.
		_watcher ??= new ForegroundWindowWatcher(RemoveHook);
		try
		{
			_watcher.AddHook();
		}
		catch (Win32Exception)
		{
			// The timer will remove the hook anyway.
		}
	}

	private void RemoveHook()
	{
		if (_hook == IntPtr.Zero)
			return;

		_timer.Stop();
		_watcher.RemoveHook();
		UnhookWindowsHookEx(_hook);
		_hook = IntPtr.Zero;
		_isWatching = false;
	}

	private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
	{
		if ((nCode == HC_ACTION) && ((uint)wParam == WM_MOUSEWHEEL))
		{
			var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
			if (_iconRect.Contains(data.pt))
			{
				var delta = (short)(data.mouseData >> 16);
				_dispatcher.InvokeAsync(() => _onMouseWheel.Invoke(delta), DispatcherPriority.Input);

				// Consume mouse wheel so that it will not reach the taskbar.
				return new IntPtr(1);
			}
			_hookDispatcher.InvokeAsync(RemoveHook);
		}
		return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
	}

	#endregion

	#region IDisposable

	/// <summary>
	/// Stops watching mouse wheel and shuts down the hook thread.
	/// </summary>
	public void Dispose()
	{
		if (_hookDispatcher is null)
			return;

		_hookDispatcher.InvokeAsync(RemoveHook);
		_hookDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
	}

	#endregion
}
