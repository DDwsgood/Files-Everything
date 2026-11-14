// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Controls;
using Files.App.UserControls;
using Files.App.UserControls.TabBar;
using Files.App.Utils.FileTags;
using Files.App.Views;
using Files.App.Views.Layouts;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices.Marshalling;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT;

namespace Files.App.Utils.Shell
{
	/// <summary>
	/// Provides classic Win32 OLE drag-and-drop (<c>RegisterDragDrop</c> and <c>DoDragDrop</c>)
	/// when the process runs elevated, bypassing <c>DataExchangeHost.exe</c>'s Medium-IL restriction.
	/// </summary>
	internal static partial class ElevatedDragDropManager
	{
		private const uint WM_COPYGLOBALDATA = 0x0049;
		private const MODIFIERKEYS_FLAGS MkAlt = (MODIFIERKEYS_FLAGS)0x0020;

		private static readonly HashSet<nint> _registeredHwnds = [];
		private static bool _oleInitialized;
		private static bool _isDraggingFromShelf;
		private static WindowDropTarget? _dropTarget;
		private static int _dragInProgress;
		private static DragOperation? _activeDrag;

		public static bool IsDragging => Volatile.Read(ref _dragInProgress) != 0;

		/// <summary>
		/// Ensures Win32 OLE drop targets are registered for the main window and its in-process WinUI child windows.
		/// </summary>
		public static unsafe void EnsureRegistered(nint mainWindowHandle)
		{
			if (mainWindowHandle == 0)
				return;

			var securityService = Ioc.Default.GetService<IWindowsSecurityService>();
			if (securityService is null || !securityService.IsAppElevated())
				return;

			EnsureOleInitialized();
			_dropTarget ??= new WindowDropTarget();

			HWND mainHwnd = new(mainWindowHandle);
			uint currentProcessId = (uint)Environment.ProcessId;
			var visited = new HashSet<nint>();
			var pending = new Queue<HWND>();
			pending.Enqueue(mainHwnd);

			// WinUI 3 hosts content inside DesktopChildSiteBridge -> InputSite.WindowClass and
			// InputNonClientPointerSource; register all in-process descendants so drops hit our IDropTarget.
			while (pending.Count > 0)
			{
				HWND parent = pending.Dequeue();
				if (!visited.Add((nint)parent.Value))
					continue;

				RegisterWindowHandle(parent);

				HWND child = HWND.Null;
				while (true)
				{
					child = PInvoke.FindWindowEx(parent, child, null, null);
					if (child.IsNull)
						break;

					if (IsCurrentProcessWindow(child, currentProcessId))
						pending.Enqueue(child);
				}
			}
		}

		/// <summary>
		/// Revokes registered Win32 OLE drop targets when the window closes.
		/// </summary>
		public static void Unregister()
		{
			_activeDrag?.Cancel();
			_dropTarget?.ResetTransientState();

			foreach (nint hwndValue in _registeredHwnds)
				PInvoke.RevokeDragDrop(new(hwndValue));

			_registeredHwnds.Clear();
			_dropTarget = null;
			_isDraggingFromShelf = false;
		}

		/// <summary>
		/// Starts Win32 OLE drag-and-drop on a dedicated STA without blocking the UI thread.
		/// </summary>
		public static Task<DROPEFFECT> StartDragDropFromPathsAsync(
			IReadOnlyList<string> paths,
			DROPEFFECT allowedEffects = DROPEFFECT.DROPEFFECT_COPY | DROPEFFECT.DROPEFFECT_MOVE | DROPEFFECT.DROPEFFECT_LINK,
			bool isFromShelf = false)
		{
			string[] validPaths = paths.Where(path => !string.IsNullOrEmpty(path)).ToArray();
			return QueueDragDrop(null, validPaths, allowedEffects, isFromShelf);
		}

		/// <summary>
		/// Copies Shell item identities so no Shell COM objects cross apartment boundaries.
		/// </summary>
		public static Task<DROPEFFECT> StartDragDropAsync(
			IReadOnlyList<ShellItem> shellItems,
			IReadOnlyList<string> paths,
			DROPEFFECT allowedEffects = DROPEFFECT.DROPEFFECT_COPY | DROPEFFECT.DROPEFFECT_MOVE | DROPEFFECT.DROPEFFECT_LINK,
			bool isFromShelf = false)
		{
			try
			{
				ShellPidl[] itemPidls = shellItems.Select(item => item.PIDL).ToArray();
				return QueueDragDrop(itemPidls, paths.ToArray(), allowedEffects, isFromShelf);
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, ex.Message);
				return Task.FromResult(DROPEFFECT.DROPEFFECT_NONE);
			}
		}

		private static Task<DROPEFFECT> QueueDragDrop(
			ShellPidl[]? itemPidls,
			string[] paths,
			DROPEFFECT allowedEffects,
			bool isFromShelf)
		{
			bool leftPressed = IsKeyDown(VirtualKey.LeftButton);
			bool rightPressed = IsKeyDown(VirtualKey.RightButton);
			if ((itemPidls?.Length ?? paths.Length) is 0 || leftPressed == rightPressed ||
				Interlocked.CompareExchange(ref _dragInProgress, 1, 0) != 0)
			{
				return Task.FromResult(DROPEFFECT.DROPEFFECT_NONE);
			}

			var window = MainWindow.Instance;
			var dispatcher = window.DispatcherQueue;
			var completion = new TaskCompletionSource<DROPEFFECT>(TaskCreationOptions.RunContinuationsAsynchronously);
			var operation = new DragOperation(leftPressed ? VirtualKey.LeftButton : VirtualKey.RightButton);
			_activeDrag = operation;

			void Complete(DROPEFFECT effect)
			{
				_activeDrag = null;
				_isDraggingFromShelf = false;
				window.SetCanWindowToFront(true);
				Interlocked.Exchange(ref _dragInProgress, 0);
				completion.TrySetResult(effect);
			}

			// Let XAML finish cancelling its gesture before OLE takes over input capture.
			if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
			{
				try
				{
					if (!operation.CanStart())
					{
						Complete(DROPEFFECT.DROPEFFECT_NONE);
						return;
					}

					EnsureRegistered(window.WindowHandle);
					_isDraggingFromShelf = isFromShelf;
					window.SetCanWindowToFront(false);
					var thread = new Thread(() =>
					{
						DROPEFFECT effect = operation.Run(itemPidls, paths, allowedEffects);
						if (!dispatcher.TryEnqueue(() => Complete(effect)))
						{
							// The window has shut down; there is no UI state left to restore.
							Interlocked.Exchange(ref _dragInProgress, 0);
							completion.TrySetResult(effect);
						}
					})
					{
						IsBackground = true,
						Name = "Elevated OLE drag-and-drop",
					};
					thread.SetApartmentState(ApartmentState.STA);
					thread.Start();
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, ex.Message);
					Complete(DROPEFFECT.DROPEFFECT_NONE);
				}
			}))
			{
				Complete(DROPEFFECT.DROPEFFECT_NONE);
			}

			return completion.Task;
		}

		private static bool IsKeyDown(VirtualKey key)
			=> (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;

		private static unsafe uint FindMouseInputThread()
		{
			HWND window = HWND.Null;
			while (true)
			{
				window = PInvoke.FindWindowEx(new HWND(-3), window, "MITMessageOnlyWindowClass", null);
				if (window.IsNull)
					return 0;

				uint processId = 0;
				uint threadId = PInvoke.GetWindowThreadProcessId(window, &processId);
				if (processId == (uint)Environment.ProcessId)
					return threadId;
			}
		}

		private sealed class DragOperation
		{
			private readonly object _inputLock = new();
			private readonly VirtualKey _button;
			private readonly VirtualKey _otherButton;
			private readonly MODIFIERKEYS_FLAGS _buttonFlag;
			private bool _cancelRequested;
			private bool _hasContinued;
			private bool _buttonReleased;
			private bool _running;
			private uint _threadId;

			public DragOperation(VirtualKey button)
			{
				_button = button;
				_otherButton = button == VirtualKey.LeftButton ? VirtualKey.RightButton : VirtualKey.LeftButton;
				bool swapped = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0;
				bool logicalRightButton = (button == VirtualKey.RightButton) != swapped;
				_buttonFlag = logicalRightButton ? MODIFIERKEYS_FLAGS.MK_RBUTTON : MODIFIERKEYS_FLAGS.MK_LBUTTON;
			}

			public void Cancel()
			{
				lock (_inputLock)
					_cancelRequested = true;
			}

			public bool CanStart()
			{
				lock (_inputLock)
					return !_cancelRequested && IsKeyDown(_button) && !IsKeyDown(_otherButton) && !IsKeyDown(VirtualKey.Escape);
			}

			public unsafe DROPEFFECT Run(ShellPidl[]? itemPidls, string[] paths, DROPEFFECT allowedEffects)
			{
				bool oleInitialized = false;
				bool inputAttached = false;
				uint inputThreadId = 0;
				var shellItems = new List<ShellItem>();
				IDataObject? dataObject = null;
				try
				{
					if (!CanStart())
						return DROPEFFECT.DROPEFFECT_NONE;

					PInvoke.OleInitialize(null).ThrowOnFailure();
					oleInitialized = true;
					PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
					_threadId = PInvoke.GetCurrentThreadId();
					inputThreadId = FindMouseInputThread();
					if (inputThreadId == 0 || !PInvoke.AttachThreadInput(_threadId, inputThreadId, true))
					{
						App.Logger.LogWarning("Cannot attach elevated OLE drag-and-drop to the WinUI mouse input thread.");
						return DROPEFFECT.DROPEFFECT_NONE;
					}
					inputAttached = true;

					if (itemPidls is not null)
					{
						foreach (var pidl in itemPidls)
							shellItems.Add(ShellItem.Open(pidl));
					}
					else
					{
						foreach (string path in paths)
							shellItems.Add(new ShellItem(path));
					}

					dataObject = ShellDataObject.CreateForDrag(shellItems, paths);
					if (!CanStart())
						return DROPEFFECT.DROPEFFECT_NONE;

					var dropSource = new OleDropSource(this);
					lock (_inputLock)
						_running = true;
					using var inputTimer = new Timer(UpdateInput, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(16));
					try
					{
						HRESULT result = PInvoke.DoDragDrop(dataObject, dropSource, allowedEffects, out DROPEFFECT finalEffect);
						result.ThrowOnFailure();
						return result == HRESULT.DRAGDROP_S_DROP ? finalEffect : DROPEFFECT.DROPEFFECT_NONE;
					}
					finally
					{
						lock (_inputLock)
							_running = false;
					}
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, ex.Message);
					return DROPEFFECT.DROPEFFECT_NONE;
				}
				finally
				{
					if ((object?)dataObject is ComObject comObject)
						comObject.FinalRelease();
					foreach (var item in shellItems)
						item.Dispose();
					if (inputAttached)
						PInvoke.AttachThreadInput(_threadId, inputThreadId, false);
					if (oleInitialized)
						PInvoke.OleUninitialize();
				}
			}

			public HRESULT QueryContinueDrag(bool escapePressed)
			{
				lock (_inputLock)
				{
					if (_cancelRequested || escapePressed || IsKeyDown(VirtualKey.Escape) || IsKeyDown(_otherButton))
						return HRESULT.DRAGDROP_S_CANCEL;

					if (_buttonReleased || !IsKeyDown(_button))
						return _hasContinued ? HRESULT.DRAGDROP_S_DROP : HRESULT.DRAGDROP_S_CANCEL;

					_hasContinued = true;
					return HRESULT.S_OK;
				}
			}

			private unsafe void UpdateInput(object? state)
			{
				lock (_inputLock)
				{
					if (!_running)
						return;

					_buttonReleased |= !IsKeyDown(_button);
					_cancelRequested |= IsKeyDown(VirtualKey.Escape) || IsKeyDown(_otherButton) || (_buttonReleased && !_hasContinued);
					if (_cancelRequested || !PInvoke.GetCursorPos(out var screenPoint))
					{
						_cancelRequested = true;
						// Init does not call IDropSource; Escape also wakes its filtered message loop.
						PInvoke.PostThreadMessage(_threadId, PInvoke.WM_KEYDOWN, new WPARAM((nuint)VirtualKey.Escape), default);
						return;
					}

					GUITHREADINFO threadInfo = new() { cbSize = (uint)sizeof(GUITHREADINFO) };
					if (!PInvoke.GetGUIThreadInfo(_threadId, &threadInfo) || threadInfo.hwndCapture.IsNull ||
						PInvoke.GetWindowThreadProcessId(threadInfo.hwndCapture, null) != _threadId)
					{
						return;
					}

					var clientPoint = screenPoint;
					if (!PInvoke.ScreenToClient(threadInfo.hwndCapture, ref clientPoint))
						return;

					MODIFIERKEYS_FLAGS keys = _buttonReleased ? 0 : _buttonFlag;
					if (IsKeyDown(VirtualKey.Control))
						keys |= MODIFIERKEYS_FLAGS.MK_CONTROL;
					if (IsKeyDown(VirtualKey.Shift))
						keys |= MODIFIERKEYS_FLAGS.MK_SHIFT;

					// Supplement WinUI's pointer-only stream without injecting global mouse input.
					int coordinates = unchecked((ushort)clientPoint.X | ((ushort)clientPoint.Y << 16));
					PInvoke.PostMessage(threadInfo.hwndCapture, PInvoke.WM_MOUSEMOVE, new WPARAM((nuint)keys), new LPARAM(coordinates));
				}
			}
		}

		private static unsafe bool IsCurrentProcessWindow(HWND hwnd, uint currentProcessId)
		{
			uint processId = 0;
			PInvoke.GetWindowThreadProcessId(hwnd, &processId);
			return processId == currentProcessId;
		}

		private static unsafe void EnsureOleInitialized()
		{
			if (_oleInitialized)
				return;

			if (PInvoke.OleInitialize(null).Succeeded)
				_oleInitialized = true;
		}

		private static unsafe void RegisterWindowHandle(HWND hwnd)
		{
			if (!_registeredHwnds.Add((nint)hwnd.Value))
				return;

			PInvoke.ChangeWindowMessageFilterEx(hwnd, PInvoke.WM_DROPFILES, WINDOW_MESSAGE_FILTER_ACTION.MSGFLT_ALLOW, null);
			PInvoke.ChangeWindowMessageFilterEx(hwnd, PInvoke.WM_COPYDATA, WINDOW_MESSAGE_FILTER_ACTION.MSGFLT_ALLOW, null);
			PInvoke.ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, WINDOW_MESSAGE_FILTER_ACTION.MSGFLT_ALLOW, null);

			HRESULT hr = PInvoke.RegisterDragDrop(hwnd, _dropTarget!);
			if (hr == HRESULT.DRAGDROP_E_ALREADYREGISTERED)
			{
				// Replace WinUI's built-in drop target, which fails cross-component/XAML drops when elevated.
				PInvoke.RevokeDragDrop(hwnd);
				hr = PInvoke.RegisterDragDrop(hwnd, _dropTarget!);
			}

			if (hr.Failed)
				_registeredHwnds.Remove((nint)hwnd.Value);
		}

		[GeneratedComClass]
		private sealed partial class OleDropSource : IDropSource
		{
			private readonly DragOperation _operation;

			public OleDropSource(DragOperation operation)
			{
				_operation = operation;
			}

			public HRESULT QueryContinueDrag(BOOL fEscapePressed, MODIFIERKEYS_FLAGS grfKeyState)
				=> _operation.QueryContinueDrag(fEscapePressed);

			public HRESULT GiveFeedback(DROPEFFECT dwEffect)
				=> HRESULT.DRAGDROP_S_USEDEFAULTCURSORS;
		}

		private enum DropTargetKind
		{
			None,
			Folder,
			Executable,
			Shelf,
			NewTab,
			SidebarPin,
			SidebarTag,
		}

		private readonly record struct ResolvedDropTarget(
			DropTargetKind Kind,
			string? TargetPath,
			IShellPage? ShellPage,
			ShelfPane? ShelfPane,
			FileTagItem? TagItem,
			bool IsExecutable,
			bool IsScriptFile);

		[GeneratedComClass]
		private sealed partial class WindowDropTarget : IDropTarget
		{
			private readonly IDropTargetHelper? _dropTargetHelper;
			private IReadOnlyList<IStorageItemWithPath> _draggedItems = [];
			private DispatcherQueueTimer? _tabHoverTimer;
			private TabBarItem? _hoveredTabItem;

			public unsafe WindowDropTarget()
			{
				try
				{
					if (PInvoke.CoCreateInstance(CLSID.CLSID_DragDropHelper, null, CLSCTX.CLSCTX_INPROC_SERVER, out IDropTargetHelper? helper).Succeeded)
						_dropTargetHelper = helper;
				}
				catch
				{
				}
			}

			public void ResetTransientState()
			{
				_draggedItems = [];
				ClearTabHover();
			}

			public unsafe HRESULT DragEnter(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
			{
				if (pdwEffect is null)
					return HRESULT.E_POINTER;

				_draggedItems = ExtractStorageItems(pDataObj);
				DROPEFFECT effect = ComputeDropEffect(grfKeyState, pt, *pdwEffect, _draggedItems, out _);
				*pdwEffect = effect;

				if (_dropTargetHelper is not null && pDataObj is not null)
				{
					Point screenPt = new(pt.x, pt.y);
					_dropTargetHelper.DragEnter(new(MainWindow.Instance.WindowHandle), pDataObj, &screenPt, effect);
				}

				return HRESULT.S_OK;
			}

			public unsafe HRESULT DragOver(MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
			{
				if (pdwEffect is null)
					return HRESULT.E_POINTER;

				DROPEFFECT effect = ComputeDropEffect(grfKeyState, pt, *pdwEffect, _draggedItems, out _);
				*pdwEffect = effect;

				if (_dropTargetHelper is not null)
				{
					Point screenPt = new(pt.x, pt.y);
					_dropTargetHelper.DragOver(&screenPt, effect);
				}

				return HRESULT.S_OK;
			}

			public HRESULT DragLeave()
			{
				ResetTransientState();
				_dropTargetHelper?.DragLeave();
				return HRESULT.S_OK;
			}

			public unsafe HRESULT Drop(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
			{
				if (pdwEffect is null)
					return HRESULT.E_POINTER;

				var items = pDataObj is not null ? ExtractStorageItems(pDataObj) : _draggedItems;
				_draggedItems = [];
				ClearTabHover();

				DROPEFFECT effect = ComputeDropEffect(grfKeyState, pt, *pdwEffect, items, out ResolvedDropTarget resolvedTarget);
				ClearTabHover();
				*pdwEffect = effect;

				if (_dropTargetHelper is not null && pDataObj is not null)
				{
					Point screenPt = new(pt.x, pt.y);
					_dropTargetHelper.Drop(pDataObj, &screenPt, effect);
				}

				if (effect != DROPEFFECT.DROPEFFECT_NONE && items.Count > 0)
					ScheduleDropExecution(resolvedTarget, effect, items);

				return HRESULT.S_OK;
			}

			private static void ScheduleDropExecution(
				ResolvedDropTarget resolvedTarget,
				DROPEFFECT effect,
				IReadOnlyList<IStorageItemWithPath> items)
			{
				MainWindow.Instance.DispatcherQueue.TryEnqueue(async () =>
				{
					await ExecuteDropAsync(resolvedTarget, effect, items);
				});
			}

			private static IReadOnlyList<IStorageItemWithPath> ExtractStorageItems(IDataObject? dataObject)
			{
				if (dataObject is null)
					return [];

				try
				{
					var paths = ShellDataObject.GetFileDropList(dataObject);
					if (paths.Count is 0)
						return [];

					var items = new List<IStorageItemWithPath>(paths.Count);
					foreach (var path in paths)
					{
						var isDirectory = Win32Helper.HasFileAttribute(path, System.IO.FileAttributes.Directory);
						items.Add(StorageHelpers.FromPathAndType(path, isDirectory ? FilesystemItemType.Directory : FilesystemItemType.File));
					}
					return items.DistinctBy(x => string.IsNullOrEmpty(x.Path) ? x.Name : x.Path).ToList();
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, ex.Message);
					return [];
				}
			}

			private DROPEFFECT ComputeDropEffect(
				MODIFIERKEYS_FLAGS grfKeyState,
				POINTL pt,
				DROPEFFECT allowedEffects,
				IReadOnlyList<IStorageItemWithPath> draggedItems,
				out ResolvedDropTarget resolvedTarget)
			{
				resolvedTarget = default;
				if (draggedItems.Count is 0)
				{
					ClearTabHover();
					return DROPEFFECT.DROPEFFECT_NONE;
				}

				ResolvedDropTarget target = ResolveDropTarget(pt);
				resolvedTarget = target;
				switch (target.Kind)
				{
					case DropTargetKind.None:
						return DROPEFFECT.DROPEFFECT_NONE;

					case DropTargetKind.Shelf:
					{
						if (_isDraggingFromShelf || target.ShelfPane is null)
							return DROPEFFECT.DROPEFFECT_NONE;

						var existingItems = target.ShelfPane.ItemsSource;
						if (existingItems is not null &&
							draggedItems.All(item => existingItems.Any(existing => existing.Inner.Id == item.Path)))
						{
							return DROPEFFECT.DROPEFFECT_NONE;
						}

						return ChooseAllowedEffect(DROPEFFECT.DROPEFFECT_LINK, allowedEffects, DROPEFFECT.DROPEFFECT_COPY);
					}

					case DropTargetKind.NewTab:
					{
						bool hasDirectories = draggedItems.Any(item => item.ItemType == FilesystemItemType.Directory);
						return hasDirectories
							? ChooseAllowedEffect(DROPEFFECT.DROPEFFECT_LINK, allowedEffects, DROPEFFECT.DROPEFFECT_COPY)
							: DROPEFFECT.DROPEFFECT_NONE;
					}

					case DropTargetKind.SidebarPin:
					{
						bool hasFoldersToPin = draggedItems.Any(item =>
							item.ItemType == FilesystemItemType.Directory &&
							!string.IsNullOrEmpty(item.Path) &&
							!App.QuickAccessManager.Model.PinnedFolders.Contains(item.Path));
						return hasFoldersToPin
							? ChooseAllowedEffect(DROPEFFECT.DROPEFFECT_MOVE, allowedEffects, DROPEFFECT.DROPEFFECT_LINK)
							: DROPEFFECT.DROPEFFECT_NONE;
					}

					case DropTargetKind.SidebarTag:
					{
						bool hasTaggedCandidates = draggedItems.Any(item => !string.IsNullOrEmpty(item.Path));
						return hasTaggedCandidates
							? ChooseAllowedEffect(DROPEFFECT.DROPEFFECT_LINK, allowedEffects, DROPEFFECT.DROPEFFECT_COPY)
							: DROPEFFECT.DROPEFFECT_NONE;
					}

					case DropTargetKind.Executable:
						if (draggedItems.Any(item => string.Equals(item.Path, target.TargetPath, StringComparison.OrdinalIgnoreCase)))
							return DROPEFFECT.DROPEFFECT_NONE;
						return ChooseAllowedEffect(DROPEFFECT.DROPEFFECT_LINK, allowedEffects, DROPEFFECT.DROPEFFECT_COPY);

					case DropTargetKind.Folder:
					{
						string targetPath = target.TargetPath!;
						if (IsSelfOrDescendantTarget(draggedItems, targetPath))
							return DROPEFFECT.DROPEFFECT_NONE;

						if (targetPath.StartsWith(Constants.UserEnvironmentPaths.RecycleBinPath, StringComparison.Ordinal))
						{
							var trashBinService = Ioc.Default.GetService<IStorageTrashBinService>();
							if (trashBinService is not null && draggedItems.All(item => trashBinService.IsUnderTrashBin(item.Path)))
								return DROPEFFECT.DROPEFFECT_NONE;

							return ChooseAllowedEffect(DROPEFFECT.DROPEFFECT_MOVE, allowedEffects, DROPEFFECT.DROPEFFECT_NONE);
						}

						bool isCtrl = (grfKeyState & MODIFIERKEYS_FLAGS.MK_CONTROL) != 0;
						bool isShift = (grfKeyState & MODIFIERKEYS_FLAGS.MK_SHIFT) != 0;
						bool isAlt = (grfKeyState & MkAlt) != 0;

						if (!isCtrl && !isAlt && draggedItems.AreItemsAlreadyInFolder(targetPath))
							return DROPEFFECT.DROPEFFECT_NONE;

						DROPEFFECT desired;
						if (isAlt || (isCtrl && isShift))
							desired = DROPEFFECT.DROPEFFECT_LINK;
						else if (isCtrl)
							desired = DROPEFFECT.DROPEFFECT_COPY;
						else if (isShift)
							desired = DROPEFFECT.DROPEFFECT_MOVE;
						else if (ZipStorageFolder.IsZipPath(targetPath) || draggedItems.Any(item => ZipStorageFolder.IsZipPath(item.Path)))
							desired = DROPEFFECT.DROPEFFECT_COPY;
						else if (draggedItems.AreItemsInSameDrive(targetPath))
							desired = DROPEFFECT.DROPEFFECT_MOVE;
						else
							desired = DROPEFFECT.DROPEFFECT_COPY;

						return ChooseAllowedEffect(desired, allowedEffects, DROPEFFECT.DROPEFFECT_COPY);
					}

					default:
						return DROPEFFECT.DROPEFFECT_NONE;
				}
			}

			private static bool IsSelfOrDescendantTarget(IReadOnlyList<IStorageItemWithPath> draggedItems, string targetPath)
			{
				string normalizedTarget = targetPath.TrimEnd('\\');
				foreach (var item in draggedItems)
				{
					string normalizedItem = item.Path.TrimEnd('\\');
					if (string.Equals(normalizedItem, normalizedTarget, StringComparison.OrdinalIgnoreCase))
						return true;

					if (item.ItemType == FilesystemItemType.Directory &&
						normalizedTarget.StartsWith(normalizedItem + '\\', StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}

				return false;
			}

			private static DROPEFFECT ChooseAllowedEffect(DROPEFFECT desired, DROPEFFECT allowed, DROPEFFECT fallback)
			{
				if ((allowed & desired) != 0)
					return desired;
				if ((allowed & fallback) != 0)
					return fallback;
				if ((allowed & DROPEFFECT.DROPEFFECT_COPY) != 0)
					return DROPEFFECT.DROPEFFECT_COPY;
				if ((allowed & DROPEFFECT.DROPEFFECT_MOVE) != 0)
					return DROPEFFECT.DROPEFFECT_MOVE;
				if ((allowed & DROPEFFECT.DROPEFFECT_LINK) != 0)
					return DROPEFFECT.DROPEFFECT_LINK;
				return DROPEFFECT.DROPEFFECT_NONE;
			}

			private ResolvedDropTarget ResolveDropTarget(POINTL screenPoint)
			{
				try
				{
					if (MainWindow.Instance.Content is not UIElement rootElement || rootElement.XamlRoot is not { } xamlRoot)
					{
						ClearTabHover();
						return default;
					}

					var clientOrigin = new Point(0, 0);
					PInvoke.ClientToScreen(new(MainWindow.Instance.WindowHandle), ref clientOrigin);

					double scale = xamlRoot.RasterizationScale <= 0 ? 1.0 : xamlRoot.RasterizationScale;
					double dipX = (screenPoint.x - clientOrigin.X) / scale;
					double dipY = (screenPoint.y - clientOrigin.Y) / scale;

					var bounds = xamlRoot.Size;
					if (dipX < 0 || dipY < 0 || dipX > bounds.Width || dipY > bounds.Height)
					{
						ClearTabHover();
						return default;
					}

					var dipPoint = new Windows.Foundation.Point(dipX, dipY);

					// Hit-test open popups first (e.g. Sidebar flyouts or modal ContentDialogs) so drops
					// inside sidebar flyouts resolve cleanly and drops over modal overlays are blocked.
					foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
					{
						if (popup.Child is null)
							continue;

						var popupElements = VisualTreeHelper.FindElementsInHostCoordinates(dipPoint, popup.Child);
						if (!popupElements.Any())
							continue;

						ClearTabHover();
						return ResolveFromElements(popupElements, allowLayoutFallback: false);
					}

					var elements = VisualTreeHelper.FindElementsInHostCoordinates(dipPoint, rootElement);
					var resolved = ResolveFromElements(elements, allowLayoutFallback: true, out TabBarItem? hoveredTabItem);
					UpdateTabHover(hoveredTabItem);
					return resolved;
				}
				catch (Exception ex)
				{
					ClearTabHover();
					App.Logger.LogWarning(ex, ex.Message);
					return default;
				}
			}

			private static ResolvedDropTarget ResolveFromElements(IEnumerable<UIElement> elements, bool allowLayoutFallback)
				=> ResolveFromElements(elements, allowLayoutFallback, out _);

			[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
			private static ResolvedDropTarget ResolveFromElements(
				IEnumerable<UIElement> elements,
				bool allowLayoutFallback,
				out TabBarItem? hoveredTabItem)
			{
				hoveredTabItem = null;
				var trashBinService = Ioc.Default.GetService<IStorageTrashBinService>();
				ListedItem? hoveredListedItem = null;

				foreach (var element in elements)
				{
					if (element is ShelfPane shelfPane)
						return new(DropTargetKind.Shelf, null, null, shelfPane, null, false, false);

					if (element is FrameworkElement fe)
					{
						if (string.Equals(fe.Name, "TabBarAddNewTabButton", StringComparison.Ordinal))
							return new(DropTargetKind.NewTab, null, null, null, null, false, false);

						object? model = (element as SidebarItem)?.Item
							?? (fe.DataContext as FlatSidebarItem)?.Item
							?? fe.DataContext;

						switch (model)
						{
							case ListedItem listedItem when hoveredListedItem is null:
								hoveredListedItem = listedItem;
								break;

							case TabBarItem tabBarItem:
								hoveredTabItem = tabBarItem;
								if (tabBarItem.AllowStorageItemDrop &&
									tabBarItem.TabItemContent is ShellPanesPage panes &&
									panes.ActivePane is { } tabShellPage &&
									tabShellPage.ShellViewModel?.WorkingDirectory is { Length: > 0 } tabWorkingDir &&
									!IsVirtualPagePath(tabWorkingDir))
								{
									return new(DropTargetKind.Folder, tabWorkingDir, tabShellPage, null, null, false, false);
								}
								return default;

							case PathBoxItem pathBoxItem when !string.IsNullOrEmpty(pathBoxItem.Path) && !IsVirtualPagePath(pathBoxItem.Path):
								return new(DropTargetKind.Folder, pathBoxItem.Path, GetActiveShellPage(), null, null, false, false);

							case LocationItem locationItem when string.IsNullOrEmpty(locationItem.Path) && locationItem.Section == SectionType.Pinned:
								return new(DropTargetKind.SidebarPin, null, GetActiveShellPage(), null, null, false, false);

							case LocationItem locationItem when !string.IsNullOrEmpty(locationItem.Path) && !IsVirtualPagePath(locationItem.Path):
								return new(DropTargetKind.Folder, locationItem.Path, GetActiveShellPage(), null, null, false, false);

							case DriveItem driveItem when !string.IsNullOrEmpty(driveItem.Path) &&
								!string.Equals(driveItem.SpaceText, Strings.Unknown.GetLocalizedResource(), StringComparison.OrdinalIgnoreCase):
								return new(DropTargetKind.Folder, driveItem.Path, GetActiveShellPage(), null, null, false, false);

							case FileTagItem { FileTag: not null } tagItem:
								return new(DropTargetKind.SidebarTag, null, GetActiveShellPage(), null, tagItem, false, false);
						}
					}

					if (allowLayoutFallback && element is BaseLayoutPage layoutPage)
					{
						var shellPage = layoutPage.ParentShellPageInstance ?? GetActiveShellPage();
						if (hoveredListedItem is not null)
						{
							var shortcutTarget = (hoveredListedItem as IShortcutItem)?.TargetPath;
							var itemTargetPath = !string.IsNullOrEmpty(shortcutTarget) ? shortcutTarget : hoveredListedItem.ItemPath;

							if (!string.IsNullOrEmpty(itemTargetPath))
							{
								if (hoveredListedItem.IsExecutable || hoveredListedItem.IsScriptFile)
									return new(DropTargetKind.Executable, itemTargetPath, shellPage, null, null, hoveredListedItem.IsExecutable, hoveredListedItem.IsScriptFile);

								if (hoveredListedItem.PrimaryItemAttribute == StorageItemTypes.Folder &&
									(trashBinService is null || !trashBinService.IsUnderTrashBin(itemTargetPath)))
								{
									return new(DropTargetKind.Folder, itemTargetPath, shellPage, null, null, false, false);
								}
							}
						}

						if (layoutPage.InstanceViewModel is { IsPageTypeSearchResults: false } &&
							shellPage?.ShellViewModel?.WorkingDirectory is { Length: > 0 } workingDir &&
							!IsVirtualPagePath(workingDir))
						{
							return new(DropTargetKind.Folder, workingDir, shellPage, null, null, false, false);
						}

						return default;
					}
				}

				return default;
			}

			private void UpdateTabHover(TabBarItem? tabBarItem)
			{
				if (tabBarItem is null)
				{
					ClearTabHover();
					return;
				}

				if (ReferenceEquals(_hoveredTabItem, tabBarItem))
					return;

				ClearTabHover();
				int targetIndex = MainPageViewModel.AppInstances.IndexOf(tabBarItem);
				if (targetIndex < 0 || targetIndex == App.AppModel.TabStripSelectedIndex)
					return;

				_hoveredTabItem = tabBarItem;
				_tabHoverTimer ??= CreateTabHoverTimer();
				_tabHoverTimer.Start();
			}

			private DispatcherQueueTimer CreateTabHoverTimer()
			{
				var timer = MainWindow.Instance.DispatcherQueue.CreateTimer();
				timer.Interval = TimeSpan.FromMilliseconds(Constants.DragAndDrop.HoverToOpenTimespan);
				timer.Tick += (_, _) =>
				{
					timer.Stop();
					if (_hoveredTabItem is null)
						return;

					int index = MainPageViewModel.AppInstances.IndexOf(_hoveredTabItem);
					if (index >= 0)
						App.AppModel.TabStripSelectedIndex = index;
				};
				return timer;
			}

			private void ClearTabHover()
			{
				_tabHoverTimer?.Stop();
				_hoveredTabItem = null;
			}

			private static IShellPage? GetActiveShellPage()
				=> Ioc.Default.GetService<IContentPageContext>()?.ShellPage;

			private static bool IsVirtualPagePath(string? path)
			{
				if (string.IsNullOrEmpty(path))
					return true;

				return path.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
					path.Equals("Settings", StringComparison.OrdinalIgnoreCase) ||
					path.Equals("ReleaseNotes", StringComparison.OrdinalIgnoreCase) ||
					path.StartsWith("tag:", StringComparison.OrdinalIgnoreCase);
			}

			private static async Task ExecuteDropAsync(
				ResolvedDropTarget resolvedTarget,
				DROPEFFECT effect,
				IReadOnlyList<IStorageItemWithPath> items)
			{
				try
				{
					switch (resolvedTarget.Kind)
					{
						case DropTargetKind.Shelf when resolvedTarget.ShelfPane is not null:
							await resolvedTarget.ShelfPane.AddItemsAsync(items);
							return;

						case DropTargetKind.NewTab:
							foreach (var item in items.Where(x => x.ItemType == FilesystemItemType.Directory && !string.IsNullOrEmpty(x.Path)))
								await NavigationHelpers.OpenPathInNewTab(item.Path, true);
							return;

						case DropTargetKind.SidebarPin:
						{
							var quickAccessService = Ioc.Default.GetRequiredService<IQuickAccessService>();
							string[] foldersToPin = items
								.Where(x => x.ItemType == FilesystemItemType.Directory &&
									!string.IsNullOrEmpty(x.Path) &&
									!App.QuickAccessManager.Model.PinnedFolders.Contains(x.Path))
								.Select(x => x.Path)
								.Distinct(StringComparer.OrdinalIgnoreCase)
								.ToArray();
							if (foldersToPin.Length > 0)
								await quickAccessService.PinToSidebarAsync(foldersToPin);
							return;
						}

						case DropTargetKind.SidebarTag when resolvedTarget.TagItem?.FileTag is { } fileTag:
						{
							var dbInstance = FileTagsHelper.GetDbInstance();
							var pathToTags = new Dictionary<string, string[]>();
							foreach (var item in items)
							{
								if (string.IsNullOrEmpty(item.Path))
									continue;

								string path = item.Path;
								string[] filesTags = FileTagsHelper.ReadFileTag(path);
								if (!filesTags.Contains(fileTag.Uid))
								{
									filesTags = [.. filesTags, fileTag.Uid];
									ulong? fileFRN = await FileTagsHelper.GetFileFRN(item.Item) ?? FileTagsHelper.GetFileFRN(path);
									dbInstance.SetTags(path, fileFRN, filesTags);
									await FileTagsHelper.WriteFileTagAsync(path, filesTags);
									pathToTags[path] = filesTags;
								}
							}

							var shellPageForTags = resolvedTarget.ShellPage ?? GetActiveShellPage();
							if (pathToTags.Count > 0 && shellPageForTags?.ShellViewModel is { } tagShellViewModel)
							{
								await tagShellViewModel.UpdateItemsTags(pathToTags);
								await tagShellViewModel.RefreshTagGroups();
							}
							return;
						}
					}

					if (string.IsNullOrEmpty(resolvedTarget.TargetPath))
						return;

					var shellPage = resolvedTarget.ShellPage ?? GetActiveShellPage();
					var filesystemHelpers = shellPage?.FilesystemHelpers;
					if (filesystemHelpers is null)
						return;

					var operation = effect switch
					{
						var e when e.HasFlag(DROPEFFECT.DROPEFFECT_LINK) => DataPackageOperation.Link,
						var e when e.HasFlag(DROPEFFECT.DROPEFFECT_MOVE) => DataPackageOperation.Move,
						_ => DataPackageOperation.Copy,
					};

					await filesystemHelpers.PerformOperationTypeAsync(
						operation,
						items,
						resolvedTarget.TargetPath,
						false,
						true,
						resolvedTarget.IsExecutable,
						resolvedTarget.IsScriptFile);

					await shellPage!.RefreshIfNoWatcherExistsAsync();
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, ex.Message);
				}
			}
		}
	}
}
