// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Windows.AppLifecycle;
using System.IO;
using Windows.ApplicationModel.Activation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Files.App.Helpers
{
	/// <summary>
	/// Hands an elevated launch over to an instance that runs with the desktop shell's own token.
	/// </summary>
	/// <remarks>
	/// UIPI blocks drag and drop between the desktop and an elevated window, so the UI runs unelevated and
	/// file operations keep requesting elevation on demand through the existing operation helpers.
	/// </remarks>
	internal static unsafe class UnelevatedRelaunchHelper
	{
		// CI replaces "files-dev" with the alias of the current release channel.
		private const string ExecutionAlias = "files-dev.exe";

		/// <summary>
		/// Starts the unelevated instance when this process is a UAC-elevated launch that can be forwarded.
		/// </summary>
		/// <returns>True if the unelevated instance was started and this process should exit.</returns>
		public static bool TryRelaunch()
		{
			HANDLE currentToken = default;
			HANDLE shellProcess = default;
			HANDLE shellToken = default;
			try
			{
				if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), TOKEN_ACCESS_MASK.TOKEN_QUERY, &currentToken) ||
					GetElevationType(currentToken) is not TOKEN_ELEVATION_TYPE.TokenElevationTypeFull)
					return false;

				if (!TryGetForwardedArguments(AppInstance.GetCurrent().GetActivatedEventArgs(), out string arguments))
					return false;

				string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", ExecutionAlias);
				if (!File.Exists(alias))
					return false;

				HWND shellWindow = PInvoke.GetShellWindow();
				if (shellWindow.IsNull || PInvoke.GetWindowThreadProcessId(shellWindow, out uint shellProcessId) == 0 || shellProcessId == 0)
					return false;

				shellProcess = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, shellProcessId);
				if (shellProcess.IsNull ||
					!PInvoke.OpenProcessToken(shellProcess, TOKEN_ACCESS_MASK.TOKEN_QUERY, &shellToken))
					return false;

				// Over-the-shoulder elevation runs as another account, and a shell without UAC filtering is elevated itself.
				if (GetElevationType(shellToken) is not TOKEN_ELEVATION_TYPE.TokenElevationTypeLimited || !HaveSameUser(currentToken, shellToken))
					return false;

				// Explorer performs a regular package activation of the alias, with its own token and the forwarded arguments.
				PInvoke.AllowSetForegroundWindow(shellProcessId);
				using var shellWindows = new ShellWindowsAutomation();
				return shellWindows.TryShellExecuteFromDesktop(alias, arguments, Environment.CurrentDirectory);
			}
			catch (Exception)
			{
				// Any failure keeps the elevated instance, which behaves as before this handoff existed.
				return false;
			}
			finally
			{
				CloseIfValid(shellToken);
				CloseIfValid(shellProcess);
				CloseIfValid(currentToken);
			}
		}

		private static bool TryGetForwardedArguments(AppActivationArguments activatedArgs, out string arguments)
		{
			arguments = string.Empty;

			// Other activation kinds carry data that cannot be replayed through the alias.
			if (activatedArgs.Kind is not ExtendedActivationKind.Launch || activatedArgs.Data is not ILaunchActivatedEventArgs launchArgs)
				return false;

			string raw = launchArgs.Arguments?.Trim() ?? string.Empty;
			if (raw.Length == 0)
				return true;

			// Alias launches report the alias as the first token, followed by the original command line.
			string first = CommandLineParser.SplitArguments(raw, true).FirstOrDefault() ?? string.Empty;
			if (!first.EndsWith(ExecutionAlias, StringComparison.OrdinalIgnoreCase) &&
				!first.EndsWith(Path.GetFileNameWithoutExtension(ExecutionAlias), StringComparison.OrdinalIgnoreCase))
				return false;

			int end = raw[0] == '"' ? raw.IndexOf('"', 1) + 1 : raw.IndexOfAny([' ', '\t']);
			arguments = end <= 0 || end >= raw.Length ? string.Empty : raw[end..].TrimStart();
			return true;
		}

		private static TOKEN_ELEVATION_TYPE GetElevationType(HANDLE token)
		{
			TOKEN_ELEVATION_TYPE type = default;
			uint length = 0;
			return PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevationType, &type, (uint)sizeof(TOKEN_ELEVATION_TYPE), &length)
				? type
				: default;
		}

		private static bool HaveSameUser(HANDLE first, HANDLE second)
		{
			byte[]? firstUser = GetTokenUser(first);
			byte[]? secondUser = GetTokenUser(second);
			if (firstUser is null || secondUser is null)
				return false;

			fixed (byte* firstPointer = firstUser)
			fixed (byte* secondPointer = secondUser)
				return PInvoke.EqualSid(((TOKEN_USER*)firstPointer)->User.Sid, ((TOKEN_USER*)secondPointer)->User.Sid);
		}

		private static byte[]? GetTokenUser(HANDLE token)
		{
			uint length = 0;
			PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, &length);
			if (length == 0)
				return null;

			var buffer = new byte[length];
			fixed (byte* pointer = buffer)
				return PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, pointer, length, &length) ? buffer : null;
		}

		private static void CloseIfValid(HANDLE handle)
		{
			if (!handle.IsNull)
				PInvoke.CloseHandle(handle);
		}
	}
}
