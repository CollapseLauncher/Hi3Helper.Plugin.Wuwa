using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Management.PresetConfig;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa;

public partial class Exports
{
	private const string EpicLaunchUri = "com.epicgames.launcher://apps/e885327ce4414509bff4c10757f88334%3Ab873e9e6a8bb4801b700dda4cc33078c%3Aa5faf668dbaf499c8dc2917bf1c346e5?action=launch&silent=true";
	private bool IsEpicLoading = false;
	private DateTime? EpicStartTime = null;
	private Process[] EpicProcesses = [];

	private async Task<bool> TryInitializeEpicLauncher(GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token)
	{
		if (context.PresetConfig is not WuwaEpicPresetConfig presetConfig)
		{
			return true;
		}

		IsEpicLoading = true;
		EpicStartTime = DateTime.Now;

		// Trigger launcher via Epic
		ProcessStartInfo psi = new()
		{
			FileName = EpicLaunchUri,
			UseShellExecute = true
		};
		Process.Start(psi);

		// Find main process for launcher
		int delay = 0;
		while (EpicProcesses.Length == 0 && delay < 15000)
		{
			EpicProcesses = Process.GetProcessesByName("launcher_main");

			await Task.Delay(200, token);
			delay += 200;
		}

		if (EpicProcesses.Length > 0)
		{
			Process p = EpicProcesses.First();
			while (p.MainWindowHandle == IntPtr.Zero)
			{
				p.Refresh();
				await Task.Delay(100, token);
			}

			// Minimize launcher window
			ShowWindow(p.MainWindowHandle, SW_MINIMIZE);
		}

		IsEpicLoading = false;

		return true;
	}

	private async Task TryKillEpicLauncher(GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token)
	{
		try
		{
			// Give some time for the launcher to init EOS for the game
			await Task.Delay(15000, CancellationToken.None);

			// Kill launcher
			foreach (var p in EpicProcesses)
			{
				p.Kill();
				p.Dispose();
			}
		}
		catch (Exception)
		{
			// Pass
		}
		finally
		{
			EpicProcesses = [];
		}
	}
}