// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// Retrieves search results from Everything (voidtools) over the Everything SDK IPC.
	/// Returns <see langword="null"/> when Everything is unavailable so callers can fall
	/// back to the native search.
	/// </summary>
	internal static partial class EverythingApi
	{
#if EVERYTHING_X86
		private const string DllName = "Everything32.dll";
#elif EVERYTHING_ARM64
		private const string DllName = "EverythingARM64.dll";
#else
		private const string DllName = "Everything64.dll";
#endif

		private const uint EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME = 0x00000004;
		private static readonly TimeSpan WakeRetryInterval = TimeSpan.FromSeconds(30);
		private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(5);

		// The SDK stores its query state globally; serialize the whole set/query sequence.
		private static readonly object Gate = new();
		private static DateTimeOffset? lastWakeAttemptUtc;

		[LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
		private static partial void Everything_SetSearchW(string lpSearchString);

		[LibraryImport(DllName)]
		private static partial void Everything_SetRequestFlags(uint dwRequestFlags);

		[LibraryImport(DllName)]
		private static partial void Everything_SetMax(uint dwMax);

		[LibraryImport(DllName)]
		private static partial void Everything_SetOffset(uint dwOffset);

		[LibraryImport(DllName)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool Everything_QueryW([MarshalAs(UnmanagedType.Bool)] bool bWait);

		[LibraryImport(DllName)]
		private static partial uint Everything_GetNumResults();

		[LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
		private static partial void Everything_GetResultFullPathNameW(uint nIndex, StringBuilder lpString, uint nMaxCount);

		[LibraryImport(DllName)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool Everything_IsDBLoaded();

		/// <summary>
		/// Queries Everything for paths matching <paramref name="search"/>. Returns
		/// <see langword="null"/> when Everything is not running and cannot be woken.
		/// </summary>
		public static IReadOnlyList<string>? Search(string search, uint maxResults)
		{
			lock (Gate)
			{
				if (TrySearch(search, maxResults, out var results))
					return results;
			}

			if (!TryWakeEverything())
				return null;

			lock (Gate)
			{
				if (TrySearch(search, maxResults, out results))
					return results;
			}

			return null;
		}

		private static bool TrySearch(string search, uint maxResults, out List<string> results)
		{
			results = [];

			try
			{
				Everything_SetSearchW(search);
				Everything_SetRequestFlags(EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME);
				Everything_SetMax(maxResults);
				Everything_SetOffset(0);

				if (!Everything_QueryW(true))
				{
					// The query failed (typically EVERYTHING_ERROR_IPC when Everything is
					// not running); the caller may wake Everything and retry.
					return false;
				}
			}
			catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
			{
				// The bundled SDK dll is missing or blocked; use the native search.
				return false;
			}

			uint numResults = Everything_GetNumResults();

			var sb = new StringBuilder(32768);
			for (uint i = 0; i < numResults; i++)
			{
				sb.Clear();
				Everything_GetResultFullPathNameW(i, sb, (uint)sb.Capacity);
				if (sb.Length > 0)
					results.Add(sb.ToString());
			}

			return true;
		}

		private static bool TryWakeEverything()
		{
			lock (Gate)
			{
				if (lastWakeAttemptUtc is { } last && DateTimeOffset.UtcNow - last < WakeRetryInterval)
					return false;
				lastWakeAttemptUtc = DateTimeOffset.UtcNow;
			}

			var everythingPath = FindEverythingExecutable();
			if (everythingPath is null)
				return false;

			try
			{
				Process.Start(new ProcessStartInfo(everythingPath, "-startup") { UseShellExecute = false });
			}
			catch
			{
				return false;
			}

			// Wait for Everything to come up and finish loading its database.
			var sw = Stopwatch.StartNew();
			while (sw.Elapsed < WakeTimeout)
			{
				if (Everything_IsDBLoaded())
					return true;
				Thread.Sleep(250);
			}

			return false;
		}

		private static string? FindEverythingExecutable()
		{
			string[] knownLocations =
			[
				Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Everything\Everything.exe"),
				Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Everything\Everything.exe"),
				Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Everything\Everything.exe"),
			];

			foreach (var location in knownLocations)
			{
				if (File.Exists(location))
					return location;
			}

			foreach (var installLocation in GetRegistryInstallLocations())
			{
				var path = Path.Combine(installLocation, "Everything.exe");
				if (File.Exists(path))
					return path;
			}

			return null;
		}

		private static IEnumerable<string> GetRegistryInstallLocations()
		{
			const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

			var roots = new[]
			{
				Microsoft.Win32.Registry.LocalMachine,
				Microsoft.Win32.Registry.CurrentUser,
			};

			foreach (var root in roots)
			{
				using var key = root.OpenSubKey(uninstallKey);
				if (key is null)
					continue;

				foreach (var subKeyName in key.GetSubKeyNames())
				{
					using var subKey = key.OpenSubKey(subKeyName);
					if (subKey?.GetValue("DisplayName") is not string displayName ||
						!displayName.StartsWith("Everything", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					if (subKey.GetValue("InstallLocation") is string installLocation && !string.IsNullOrEmpty(installLocation))
						yield return installLocation;
				}
			}
		}
	}
}