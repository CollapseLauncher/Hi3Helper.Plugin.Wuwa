using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Management;
using Hi3Helper.Plugin.Wuwa.Management.PresetConfig;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa;

public partial class Exports
{
	/// <summary>
	/// Set to true while the game is being launched and the real game process may not
	/// have appeared yet.  Keeps <see cref="IsGameRunningCore"/> returning true so
	/// that the Collapse polling loop does not exit prematurely during the
	/// wrapper -> game-executable hand-off.
	/// </summary>
	private volatile bool _isGameLaunching;

	/// <inheritdoc/>
	protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(GameManagerExtension.RunGameFromGameManagerContext context, string? startArgument, bool isRunBoosted, ProcessPriorityClass processPriority, CancellationToken token)
	{
		return (true, Impl());

		async Task<bool> Impl()
		{
			_isGameLaunching = true;
			try
			{
				bool isEpic = context.PresetConfig is WuwaEpicPresetConfig;

				if (!await TryInitializeEpicLauncher(context, token))
				{
					return false;
				}

				if (!await TryInitializeSteamLauncher(context, token))
				{
					return false;
				}

				// Epic protocol URL already launches the game so starting the wrapper
				// ourselves would create a duplicate instance (black-screen /
				// "connection issues" dialog). Instead, we wait for the real game
				// process to appear and monitor it.
				if (isEpic)
				{
					return await WaitForEpicGameProcessAsync(context, isRunBoosted, processPriority, token);
				}

				if (!TryGetStartingProcessFromContext(context, startArgument, out Process? process))
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
					_ = ReadGameLog(context, coopCts.Token);

					await process.WaitForExitAsync(token);
					await gameLogReaderCts.CancelAsync();
					return true;
				}
			}
			finally
			{
				_isGameLaunching = false;
			}
		}
	}

	/// <inheritdoc/>
	protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context, out bool isGameRunning, out DateTime gameStartTime)
	{
		isGameRunning = false;
		gameStartTime = default;

		string? startingExecutablePath = null;
		string? gameExecutablePath = null;
		if (!TryGetStartingExecutablePath(context, out startingExecutablePath)
			&& !TryGetGameExecutablePath(context, out gameExecutablePath))
		{
			return true;
		}

		using Process? process = FindExecutableProcess(startingExecutablePath);
		using Process? gameProcess = FindExecutableProcess(gameExecutablePath);
		isGameRunning = process != null || gameProcess != null || IsEpicLoading || IsSteamLoading || _isGameLaunching;
		gameStartTime = process?.StartTime ?? gameProcess?.StartTime ?? EpicStartTime ?? SteamStartTime ?? default;

		return true;
	}

	/// <inheritdoc/>
	protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token)
	{
		return (true, Impl());

		async Task<bool> Impl()
		{
			while (IsEpicLoading)
			{
				await Task.Delay(200, token);
			}

			while(IsSteamLoading)
			{
				await Task.Delay(200, token);
			}

			string? startingExecutablePath = null;
			string? gameExecutablePath = null;
			if (!TryGetStartingExecutablePath(context, out startingExecutablePath)
				&& !TryGetGameExecutablePath(context, out gameExecutablePath))
			{
				return true;
			}

			// The launcher wrapper (Wuthering Waves.exe) typically exits quickly
			// after spawning Client-Win64-Shipping.exe. Poll for a few seconds
			// to give the real game process time to appear.
			const int maxRetryMs = 15000;
			const int retryIntervalMs = 500;
			int elapsed = 0;

			Process? process = FindExecutableProcess(startingExecutablePath);
			Process? gameProcess = FindExecutableProcess(gameExecutablePath);

			while (process == null && gameProcess == null && elapsed < maxRetryMs)
			{
				await Task.Delay(retryIntervalMs, token);
				elapsed += retryIntervalMs;
				process = FindExecutableProcess(startingExecutablePath);
				gameProcess = FindExecutableProcess(gameExecutablePath);
			}

			using (process)
			using (gameProcess)
			{
				if (gameProcess != null)
					await gameProcess.WaitForExitAsync(token);
				else if (process != null)
					await process.WaitForExitAsync(token);
			}

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

	private static Process? FindExecutableProcess(string? executablePath)
	{
		if (executablePath == null) return null;

		ReadOnlySpan<char> executableDirPath = Path.GetDirectoryName(executablePath.AsSpan());
		string executableName = Path.GetFileNameWithoutExtension(executablePath);

		Process[] processes = Process.GetProcessesByName(executableName);
		Process? returnProcess = null;

		foreach (Process process in processes)
		{
			try
			{
				if (process.MainModule?.FileName != null &&
				    process.MainModule.FileName.StartsWith(executableDirPath, StringComparison.OrdinalIgnoreCase))
				{
					returnProcess = process;
					break;
				}
			}
			catch
			{
				// Ignore
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
		if (context is not { GameManager: WuwaGameManager dnaGameManager, PresetConfig: PluginPresetConfigBase presetConfig })
		{
			return false;
		}

		dnaGameManager.GetGamePath(out string? gamePath);
		presetConfig.comGet_GameExecutableName(out string executablePath);

		gamePath?.NormalizePathInplace();
		executablePath.NormalizePathInplace();

		if (string.IsNullOrEmpty(gamePath))
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
			SharedStatic.InstanceLogger.LogError(
				"[Wuwa::TryGetGameProcessFromContext] Failed to get game executable path.");
			return false;
		}

		SharedStatic.InstanceLogger.LogInformation(
			"[Wuwa::TryGetGameProcessFromContext] Game executable path: {Path}", gameExecutablePath);

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
		if (context is not { GameManager: WuwaGameManager dnaGameManager, PresetConfig: WuwaPresetConfig presetConfig })
		{
			SharedStatic.InstanceLogger.LogError(
				"[Wuwa::TryGetStartingExecutablePath] Invalid context or missing GameManager/PresetConfig.");
			return false;
		}

		dnaGameManager.GetGamePath(out string? gamePath);
		string? executablePath = presetConfig?.StartExecutableName;

		gamePath?.NormalizePathInplace();
		executablePath?.NormalizePathInplace();

		if (string.IsNullOrEmpty(gamePath)
			|| string.IsNullOrEmpty(executablePath))
		{
			SharedStatic.InstanceLogger.LogError(
				"[Wuwa::TryGetStartingExecutablePath] GamePath or ExecutablePath is null/empty. GamePath: {GamePath}, ExecutablePath: {ExecPath}",
				gamePath ?? "<null>", executablePath ?? "<null>");
			return false;
		}

		startingExecutablePath = Path.Combine(gamePath, executablePath);
		
		if (!File.Exists(startingExecutablePath))
		{
			SharedStatic.InstanceLogger.LogError(
				"[Wuwa::TryGetStartingExecutablePath] Starting executable not found at: {Path}", startingExecutablePath);
			return false;
		}
		
		return true;
	}

	private static bool TryGetStartingProcessFromContext(GameManagerExtension.RunGameFromGameManagerContext context, string? startArgument, [NotNullWhen(true)] out Process? process)
	{
		process = null;
		if (!TryGetStartingExecutablePath(context, out string? startingExecutablePath))
		{
			SharedStatic.InstanceLogger.LogError(
				"[Wuwa::TryGetStartingProcessFromContext] Failed to get starting executable path. Game cannot be launched.");
			return false;
		}

		SharedStatic.InstanceLogger.LogInformation(
			"[Wuwa::TryGetStartingProcessFromContext] Starting executable path: {Path}", startingExecutablePath);

		ProcessStartInfo startInfo = string.IsNullOrEmpty(startArgument) ?
			new ProcessStartInfo(startingExecutablePath) :
			new ProcessStartInfo(startingExecutablePath, startArgument);

		process = new Process
		{
			StartInfo = startInfo
		};
		return true;
	}

	private static async Task ReadGameLog(GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token)
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
		await Task.Delay(250, token);

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