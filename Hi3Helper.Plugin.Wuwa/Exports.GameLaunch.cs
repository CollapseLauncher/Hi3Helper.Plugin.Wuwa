using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Management;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Hi3Helper.Plugin.Wuwa.Management.PresetConfig;

namespace Hi3Helper.Plugin.Wuwa;

public partial class Exports
{
	/// <inheritdoc/>
	protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(GameManagerExtension.RunGameFromGameManagerContext context, string? startArgument, bool isRunBoosted, ProcessPriorityClass processPriority, CancellationToken token)
	{
		return (true, Impl());

		async Task<bool> Impl()
		{
			if (!TryGetStartingProcessFromContext(context, startArgument, out Process? process))
			{
				return false;
			}

			if (!TryGetGameProcessFromContext(context, out Process? gameProcess))
			{
				return false;
			}

			using (process)
			{
				process.Start();

				try
				{
					process.PriorityBoostEnabled = isRunBoosted;
					process.PriorityClass = processPriority;
				}
				catch (Exception e)
				{
					InstanceLogger.LogError(e, "[Wuwa::LaunchGameFromGameManagerCoreAsync()] An error has occurred while trying to set process priority, Ignoring!");
				}

				CancellationTokenSource gameLogReaderCts = new();
				CancellationTokenSource coopCts = CancellationTokenSource.CreateLinkedTokenSource(token, gameLogReaderCts.Token);

				// Run game log reader (Create a new thread)
				_ = ReadGameLog(context, gameProcess, coopCts.Token);

				// ReSharper disable once PossiblyMistakenUseOfCancellationToken
				await process.WaitForExitAsync(token);
				await gameLogReaderCts.CancelAsync();

				return true;
			}
		}
	}

	/// <inheritdoc/>
	protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context, out bool isGameRunning, out DateTime gameStartTime)
	{
		isGameRunning = false;
		gameStartTime = default;

		if (!TryGetGameExecutablePath(context, out string? gameExecutablePath))
		{
			return true;
		}

		using Process? process = FindExecutableProcess(gameExecutablePath);
		isGameRunning = process != null;
		gameStartTime = process?.StartTime ?? default;

		return true;
	}

	/// <inheritdoc/>
	protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token)
	{
		return (true, Impl());

		async Task<bool> Impl()
		{
			if (!TryGetGameExecutablePath(context, out string? gameExecutablePath))
			{
				return false;
			}

			using Process? process = FindExecutableProcess(gameExecutablePath);
			if (process == null)
			{
				return true;
			}

			await process.WaitForExitAsync(token);
			return true;
		}
	}

	/// <inheritdoc/>
	protected override bool KillRunningGameCore(GameManagerExtension.RunGameFromGameManagerContext context, out bool wasGameRunning, out DateTime gameStartTime)
	{
		wasGameRunning = false;
		gameStartTime = default;

		if (!TryGetGameExecutablePath(context, out string? gameExecutablePath))
		{
			return true;
		}

		using Process? process = FindExecutableProcess(gameExecutablePath);
		if (process == null)
		{
			return true;
		}

		wasGameRunning = true;
		gameStartTime = process.StartTime;
		process.Kill();
		return true;
	}

	private static Process? FindExecutableProcess(string executablePath)
	{
		ReadOnlySpan<char> executableDirPath = Path.GetDirectoryName(executablePath.AsSpan());
		string executableName = Path.GetFileNameWithoutExtension(executablePath);

		Process[] processes = Process.GetProcessesByName(executableName);
		Process? returnProcess = null;

		foreach (Process process in processes)
		{
			if (process.MainModule?.FileName.StartsWith(executableDirPath, StringComparison.OrdinalIgnoreCase) ?? false)
			{
				returnProcess = process;
				break;
			}
		}

		try
		{
			return returnProcess;
		}
		finally
		{
			foreach (var process in processes.Where(x => x != returnProcess))
			{
				process.Dispose();
			}
		}
	}

	private static bool TryGetGameExecutablePath(GameManagerExtension.RunGameFromGameManagerContext context, [NotNullWhen(true)] out string? gameExecutablePath)
	{
		gameExecutablePath = null;
		if (context is not { GameManager: WuwaGameManager wuwaGameManager, PresetConfig: WuwaPresetConfig presetConfig })
		{
			return false;
		}

		wuwaGameManager.GetGamePath(out string? gamePath);
		string? executablePath = presetConfig.StartExecutableName;

		gamePath?.NormalizePathInplace();
		executablePath?.NormalizePathInplace();

		if (string.IsNullOrEmpty(gamePath) || string.IsNullOrEmpty(executablePath))
		{
			return false;
		}

		gameExecutablePath = Path.Combine(gamePath, executablePath);
		return File.Exists(gameExecutablePath);
	}

	private static bool TryGetGameProcessFromContext(GameManagerExtension.RunGameFromGameManagerContext context, [NotNullWhen(true)] out Process? process)
	{
		process = null;
		if (!TryGetGameExecutablePath(context, out string? gameExecutablePath))
		{
			return false;
		}

		ProcessStartInfo startInfo = new ProcessStartInfo(gameExecutablePath);

		process = new Process
		{
			StartInfo = startInfo
		};
		return true;
	}

	private static bool TryGetStartingExecutablePath(GameManagerExtension.RunGameFromGameManagerContext context, [NotNullWhen(true)] out string? startingExecutablePath)
	{
		startingExecutablePath = null;
		if (context is not { GameManager: WuwaGameManager wuwaGameManager, PresetConfig: WuwaPresetConfig presetConfig })
		{
			return false;
		}

		wuwaGameManager.GetGamePath(out string? gamePath);
		string? executablePath = presetConfig?.StartExecutableName;

		gamePath?.NormalizePathInplace();
		executablePath?.NormalizePathInplace();

		if (string.IsNullOrEmpty(gamePath)
			|| string.IsNullOrEmpty(executablePath))
		{
			return false;
		}

		startingExecutablePath = Path.Combine(gamePath, executablePath);
		return File.Exists(startingExecutablePath);
	}

	private static bool TryGetStartingProcessFromContext(GameManagerExtension.RunGameFromGameManagerContext context, string? startArgument, [NotNullWhen(true)] out Process? process)
	{
		process = null;
		if (!TryGetStartingExecutablePath(context, out string? startingExecutablePath))
		{
			return false;
		}

		ProcessStartInfo startInfo = string.IsNullOrEmpty(startArgument) ?
			new ProcessStartInfo(startingExecutablePath) :
			new ProcessStartInfo(startingExecutablePath, startArgument);

		process = new Process
		{
			StartInfo = startInfo
		};
		return true;
	}

	private static async Task ReadGameLog(GameManagerExtension.RunGameFromGameManagerContext context, Process process, CancellationToken token)
	{
		if (context is not { PresetConfig: PluginPresetConfigBase presetConfig })
		{
			return;
		}

		presetConfig.comGet_GameAppDataPath(out string gameAppDataPath);
		presetConfig.comGet_GameLogFileName(out string gameLogFileName);

		if (string.IsNullOrEmpty(gameAppDataPath) ||
			string.IsNullOrEmpty(gameLogFileName))
		{
			return;
		}

		string gameLogPath = Path.Combine(gameAppDataPath, gameLogFileName);
		InstanceLogger.LogDebug("Log: {Log}", gameLogPath);

		// Make artificial delay and read the game log if the window is already spawned.
		while (!token.IsCancellationRequested &&
			   process.MainWindowHandle == nint.Zero)
		{
			await Task.Delay(250, token);
		}

		int retry = 5;
		while (!File.Exists(gameLogPath) && retry >= 0)
		{
			// Delays for 5 seconds to wait the game log existence
			await Task.Delay(1000, token);
			--retry;
		}

		if (retry <= 0)
		{
			return;
		}

		GameManagerExtension.PrintGameLog? printCallback = context.PrintGameLogCallback;

		await using FileStream fileStream = File.Open(gameLogPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
		using StreamReader reader = new StreamReader(fileStream);

		fileStream.Position = 0;
		while (!token.IsCancellationRequested)
		{
			while (await reader.ReadLineAsync(token) is { } line)
			{
				PassStringLineToCallback(printCallback, line);
			}

			await Task.Delay(250, token);
		}

		return;

		static unsafe void PassStringLineToCallback(GameManagerExtension.PrintGameLog? invoke, string line)
		{
			char* lineP = line.GetPinnableStringPointer();
			int lineLen = line.Length;

			invoke?.Invoke(lineP, lineLen, 0);
		}
	}
}