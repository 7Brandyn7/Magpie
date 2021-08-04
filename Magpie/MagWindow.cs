using EasyHook;
using Magpie.CursorHook;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting;
using System.Threading;
using System.Windows;
using System.Windows.Interop;


namespace Magpie {
	internal enum MagWindowStatus : int {
		Idle = 0,       // 未启动或者已关闭
		Starting = 1,   // 启动中，此状态下无法执行操作
		Running = 2     // 运行中
	}

	// 用于管理全屏窗口，该窗口在一个新线程中启动，通过事件与主线程通信
	internal class MagWindow {
		public event Action Closed;

		public IntPtr SrcWindow { get; private set; } = IntPtr.Zero;

		private Thread magThread = null;

		// 用于从全屏窗口的线程接收消息
		private event Action<int, string> StatusEvent;


		private MagWindowStatus status = MagWindowStatus.Idle;
		public MagWindowStatus Status {
			get => status;
			private set {
				status = value;
				if (status == MagWindowStatus.Idle) {
					magThread = null;
				}
			}
		}

		public MagWindow(Window parent) {
			StatusEvent += (int status, string errorMsg) => {
				if (status < 0 || status > 3) {
					return;
				}

				MagWindowStatus status_ = (MagWindowStatus)status;
				if (status_ == Status) {
					return;
				}

				if (status_ == MagWindowStatus.Idle) {
					if (Closed != null) {
						Closed.Invoke();
					}
					SrcWindow = IntPtr.Zero;
				}
				Status = status_;

				if (errorMsg != null) {
					parent.Dispatcher.Invoke(new Action(() => {
						_ = NativeMethods.SetForegroundWindow(new WindowInteropHelper(parent).Handle);
						_ = MessageBox.Show(errorMsg);
					}));
				}
			};
		}

		public void Create(
			string scaleModel,
			int captureMode,
			bool showFPS,
			bool hookCursorAtRuntime,
			bool noDisturb = false
		) {
			if (Status != MagWindowStatus.Idle) {
				return;
			}

			IntPtr hwndSrc = NativeMethods.GetForegroundWindow();
			if (!NativeMethods.IsWindow(hwndSrc)
				|| !NativeMethods.IsWindowVisible(hwndSrc)
				|| NativeMethods.GetWindowShowCmd(hwndSrc) != NativeMethods.SW_NORMAL
			) {
				// 不合法的源窗口
				return;
			}

			Status = MagWindowStatus.Starting;
			// 使用 WinRT Capturer API 需要在 MTA 中
			// C# 窗体必须使用 STA，因此将全屏窗口创建在新的线程里
			magThread = new Thread(() => {
				NativeMethods.RunMagWindow(
					(int status, IntPtr errorMsg) => StatusEvent(status, Marshal.PtrToStringUni(errorMsg)),
					hwndSrc,        // 源窗口句柄
					scaleModel,     // 缩放模式
					captureMode,    // 抓取模式
					showFPS,        // 显示 FPS
					noDisturb       // 用于调试
				);
			});
			magThread.SetApartmentState(ApartmentState.MTA);
			magThread.Start();

			if (hookCursorAtRuntime) {
				HookCursorAtRuntime(hwndSrc);
			}

			SrcWindow = hwndSrc;
		}

		public void Destory() {
			if (Status != MagWindowStatus.Running) {
				return;
			}

			// 广播 MAGPIE_WM_DESTORYMAG
			// 可以在没有全屏窗口句柄的情况下关闭它
			_ = NativeMethods.BroadcastMessage(NativeMethods.MAGPIE_WM_DESTORYMAG);
		}

		private void HookCursorAtRuntime(IntPtr hwndSrc) {
			int pid = NativeMethods.GetWindowProcessId(hwndSrc);
			if (pid == 0 || pid == Process.GetCurrentProcess().Id) {
				// 不能 hook 本进程
				return;
			}

			string channelName = null;
#if DEBUG
			// DEBUG 时创建 IPC server
			RemoteHooking.IpcCreateServer<ServerInterface>(ref channelName, WellKnownObjectMode.Singleton);
#else
            channelName = "";
#endif

			// 获取 CursorHook.dll 的绝对路径
			string injectionLibrary = Path.Combine(
				Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
				"CursorHook.dll"
			);

			// 使用 EasyHook 注入
			try {
				RemoteHooking.Inject(
				pid,                // 要注入的进程的 ID
				injectionLibrary,   // 32 位 DLL
				injectionLibrary,   // 64 位 DLL
									// 下面为传递给注入 DLL 的参数
				channelName,
				hwndSrc
				);
			} catch (Exception e) {
				Console.WriteLine("CursorHook 注入失败：" + e.Message);
			}
		}

		public void HookCursorAtStartUp(string exePath) {

			string channelName = null;
#if DEBUG
			// DEBUG 时创建 IPC server
			RemoteHooking.IpcCreateServer<ServerInterface>(ref channelName, WellKnownObjectMode.Singleton);
#else
            channelName = "";
#endif

			// 获取 CursorHook.dll 的绝对路径
			string injectionLibrary = Path.Combine(
				Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
				"CursorHook.dll"
			);

			try {
				RemoteHooking.CreateAndInject(
					exePath,    // 可执行文件路径
					"",         // 命令行参数
					0,          // 传递给 CreateProcess 的标志
					injectionLibrary,   // 32 位 DLL
					injectionLibrary,   // 64 位 DLL
					out int _,  // 忽略进程 ID
								// 下面为传递给注入 DLL 的参数
					channelName
				);
			} catch (Exception e) {
				Console.WriteLine("CursorHook 注入失败：" + e.Message);
			}
		}
	}
}
