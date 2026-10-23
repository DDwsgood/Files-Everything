// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Files.App.Helpers.ContextFlyouts
{
	/// <summary>
	/// Provides Windows 10 style mnemonic activation for menu flyouts: while a menu is open, pressing an item's
	/// access key runs that item without holding Alt, which WinUI's built-in access key mode requires.
	/// Keys are handled through single-key keyboard accelerators and through key handlers on the menu items and
	/// the menu presenter, because the presenter consumes key input itself before ordinary handlers see it.
	/// </summary>
	internal static class MenuFlyoutKeyboardHelper
	{
		private static object? lastActivatedTarget;
		private static long lastActivatedTick;

		private static readonly DependencyProperty IsAttachedProperty =
			DependencyProperty.RegisterAttached("IsAttached", typeof(bool), typeof(MenuFlyoutKeyboardHelper), new PropertyMetadata(false));

		private static readonly DependencyProperty IsFlyoutAttachedProperty =
			DependencyProperty.RegisterAttached("IsFlyoutAttached", typeof(bool), typeof(MenuFlyoutKeyboardHelper), new PropertyMetadata(false));

		private static readonly DependencyProperty AddedAcceleratorsProperty =
			DependencyProperty.RegisterAttached("AddedAccelerators", typeof(List<KeyboardAccelerator>), typeof(MenuFlyoutKeyboardHelper), new PropertyMetadata(null));

		/// <summary>
		/// Enables mnemonic activation on a single menu item. Items built from models are attached automatically.
		/// </summary>
		public static void Attach(MenuFlyoutItemBase? item)
		{
			if (item is null or MenuFlyoutSeparator || item.GetValue(IsAttachedProperty) is true)
				return;

			item.SetValue(IsAttachedProperty, true);
			item.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Item_KeyDown), true);
			item.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Item_KeyDown), true);
		}

		/// <summary>
		/// Enables mnemonic activation on a menu presenter, which catches keys while no item has focus.
		/// </summary>
		public static void Attach(MenuFlyoutPresenter? presenter)
		{
			if (presenter is null || presenter.GetValue(IsAttachedProperty) is true)
				return;

			presenter.SetValue(IsAttachedProperty, true);
			presenter.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Presenter_KeyDown), true);
			presenter.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Presenter_KeyDown), true);
		}

		/// <summary>
		/// Enables mnemonic activation on every item of a flyout, including items added to its submenus later.
		/// </summary>
		public static void Attach(MenuFlyout? flyout)
		{
			if (flyout is null || flyout.GetValue(IsFlyoutAttachedProperty) is true)
				return;

			flyout.SetValue(IsFlyoutAttachedProperty, true);
			flyout.Opening += Flyout_Opening;
			flyout.Closed += Flyout_Closed;
		}

		/// <summary>
		/// Runs the open-time initialization for a flyout that is attached while it is already opening.
		/// </summary>
		public static void PrepareOpen(MenuFlyout flyout)
		{
			App.LastOpenedFlyout = flyout;
			AttachItems(flyout.Items);
			ApplyAccelerators(flyout.Items);
		}

		/// <summary>
		/// Enables mnemonic activation on the given items and on the items of their submenus.
		/// </summary>
		public static void AttachItems(IEnumerable<MenuFlyoutItemBase> items)
		{
			foreach (var item in items)
			{
				Attach(item);

				// Submenu children live in their own popup and therefore need their own handlers.
				if (item is MenuFlyoutSubItem subItem)
					AttachItems(subItem.Items);
			}
		}

		/// <summary>
		/// Registers a single-key accelerator for every menu item of the given levels. Accelerators are
		/// dispatched by the input manager, so they also work while a menu item holds the focus.
		/// </summary>
		public static void ApplyAccelerators(IEnumerable<MenuFlyoutItemBase> items)
		{
			ClearAccelerators(items);
			ApplyLevelAccelerators(items);
		}

		/// <summary>
		/// Removes the accelerators this helper registered, so no single-key shortcut stays live while a menu is closed.
		/// </summary>
		public static void ClearAccelerators(IEnumerable<MenuFlyoutItemBase> items)
		{
			foreach (var item in items)
			{
				ClearElementAccelerators(item);

				// The primary command row hosts its commands as buttons inside the item.
				if (item is MenuFlyoutItem { Tag: Panel primaryRow })
					foreach (var button in primaryRow.Children.OfType<Button>())
						ClearElementAccelerators(button);

				if (item is MenuFlyoutSubItem subItem)
					ClearAccelerators(subItem.Items);
			}
		}

		private static void ClearElementAccelerators(UIElement element)
		{
			if (element.GetValue(AddedAcceleratorsProperty) is not List<KeyboardAccelerator> added)
				return;

			foreach (var accelerator in added)
				element.KeyboardAccelerators.Remove(accelerator);

			added.Clear();
		}

		private static void ApplyLevelAccelerators(IEnumerable<MenuFlyoutItemBase> items)
		{
			foreach (var entry in BuildLevelEntries(items))
			{
				var accelerator = new KeyboardAccelerator
				{
					Key = (VirtualKey)entry.Key,
					Modifiers = VirtualKeyModifiers.None,
				};

				var element = entry.Element;
				var key = entry.Key;
				var fallback = entry.Activate;
				accelerator.Invoked += (_, args) =>
				{
					// Stay inert while no menu is open, so a leftover accelerator cannot swallow a key.
					if (App.LastOpenedFlyout is not { IsOpen: true })
						return;

					args.Handled = true;
					Activate(element, key, fallback);
				};

				entry.Element.KeyboardAccelerators.Add(accelerator);
				entry.Element.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

				if (entry.Element.GetValue(AddedAcceleratorsProperty) is not List<KeyboardAccelerator> added)
				{
					added = [];
					entry.Element.SetValue(AddedAcceleratorsProperty, added);
				}

				added.Add(accelerator);
			}

			foreach (var item in items)
			{
				if (item is MenuFlyoutSubItem subItem)
					ApplyLevelAccelerators(subItem.Items);
			}
		}

		private static void Flyout_Opening(object? sender, object e)
		{
			if (sender is not MenuFlyout flyout)
				return;

			PrepareOpen(flyout);
		}

		private static void Flyout_Closed(object? sender, object e)
		{
			if (sender is not MenuFlyout flyout)
				return;

			ClearAccelerators(flyout.Items);
		}

		private static void Presenter_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			// The menu presenter handles key input itself and marks it handled, so the event state cannot be
			// used here. The activation guard keeps a key from running the same item twice.
			Item_KeyDown(sender, e);
		}

		private static void Item_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			// Preview and bubble handlers, and the item and presenter handlers, all receive the same key
			// event; the first one that acts marks it handled.
			if (e.Handled)
				return;

			if (sender is not UIElement source || !TryGetCharacter(e.Key, out char character))
				return;

			// Only act while one of our menus is open.
			if (App.LastOpenedFlyout is not { IsOpen: true })
				return;

			// Leave Alt, Ctrl and Win shortcuts alone. Alt is how WinUI's own access key mode works.
			if (IsModifierDown(VirtualKey.Menu) || IsModifierDown(VirtualKey.Control) ||
				IsModifierDown(VirtualKey.LeftWindows) || IsModifierDown(VirtualKey.RightWindows))
				return;

			if (GetMenuLevel(source) is not { } level || ResolveActivation(level, character) is not { } activation)
				return;

			e.Handled = true;
			activation();
		}

		/// <summary>
		/// Runs an accelerator against the menu level that currently has focus, so a submenu key wins over the
		/// same key of its parent menu. The entry that registered the accelerator is only used when the focused
		/// level cannot be determined at all.
		/// </summary>
		private static void Activate(UIElement source, char character, Action fallback)
		{
			if (App.LastOpenedFlyout is not { IsOpen: true })
				return;

			if (source.XamlRoot is { } xamlRoot &&
				FocusManager.GetFocusedElement(xamlRoot) is DependencyObject focused &&
				GetOwnerItemsControl(focused) is { } level)
			{
				ResolveActivation(level, character)?.Invoke();
				return;
			}

			fallback();
		}

		private static Action? ResolveActivation(ItemsControl level, char character)
		{
			var matches = BuildLevelEntries(level.Items.OfType<MenuFlyoutItemBase>())
				.Where(x => x.Key == character)
				.ToList();

			if (matches.Count is 0)
				return null;

			// Several items declare the same key; the first one wins.
			return matches[0].Activate;
		}

		private static bool IsModifierDown(VirtualKey key)
			=> Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
				.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

		private static bool TryGetCharacter(VirtualKey key, out char character)
		{
			character = default;

			var keyCode = (int)key;

			if (keyCode is >= (int)VirtualKey.A and <= (int)VirtualKey.Z)
				character = (char)('A' + keyCode - (int)VirtualKey.A);
			else if (keyCode is >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9)
				character = (char)('0' + keyCode - (int)VirtualKey.Number0);
			else if (keyCode is >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad9)
				character = (char)('0' + keyCode - (int)VirtualKey.NumberPad0);
			else
				return false;

			return true;
		}

		private static ItemsControl? GetMenuLevel(UIElement source)
		{
			if (source.XamlRoot is not { } xamlRoot)
				return null;

			var focused = FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;

			return GetOwnerItemsControl(focused) ?? GetOwnerItemsControl(source);
		}

		private static ItemsControl? GetOwnerItemsControl(DependencyObject? element)
		{
			for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
			{
				if (current is ItemsControl itemsControl)
					return itemsControl;
			}

			return null;
		}

		/// <summary>
		/// Assigns one key per actionable item of a menu level: declared access keys first, then the first free
		/// character of the label. Declared keys of the primary row buttons are reserved up front as well.
		/// </summary>
		private static List<(string Text, char Key, UIElement Element, Action Activate)> BuildLevelEntries(IEnumerable<MenuFlyoutItemBase> items)
		{
			var candidates = new List<(MenuFlyoutItemBase Element, string Text, char? ExplicitKey, List<Button> RowButtons)>();
			var usedKeys = new HashSet<char>();

			foreach (var item in items)
			{
				if (item is MenuFlyoutSeparator || item.Visibility is not Visibility.Visible || !item.IsEnabled)
					continue;

				if (GetText(item) is { Length: > 0 } text)
				{
					if (GetActivation(item) is null)
						continue;

					var explicitKey = GetExplicitKey(GetAccessKey(item));
					candidates.Add((item, text, explicitKey, []));

					if (explicitKey is char key)
						usedKeys.Add(key);

					continue;
				}

				// The primary command row hosts icon buttons inside an item without text of its own.
				if (item is MenuFlyoutItem { Tag: Panel primaryRow })
				{
					var rowButtons = primaryRow.Children
						.OfType<Button>()
						.Where(x => x.IsEnabled && GetExplicitKey(x.AccessKey) is not null)
						.ToList();

					if (rowButtons.Count is 0)
						continue;

					foreach (var button in rowButtons)
						usedKeys.Add(GetExplicitKey(button.AccessKey)!.Value);

					candidates.Add((item, string.Empty, null, rowButtons));
				}
			}

			var entries = new List<(string Text, char Key, UIElement Element, Action Activate)>();

			foreach (var candidate in candidates)
			{
				if (candidate.RowButtons.Count > 0)
				{
					foreach (var button in candidate.RowButtons)
					{
						if (GetExplicitKey(button.AccessKey) is not char buttonKey)
							continue;

						entries.Add((AutomationProperties.GetName(button), buttonKey, button, () => InvokeButton(button)));
					}
					continue;
				}

				if (candidate.ExplicitKey is char explicitKey)
				{
					entries.Add((candidate.Text, explicitKey, candidate.Element, GetActivation(candidate.Element)!));
					continue;
				}

				if (GetDerivedKey(candidate.Text, usedKeys) is not char derivedKey)
					continue;

				usedKeys.Add(derivedKey);
				entries.Add((candidate.Text, derivedKey, candidate.Element, GetActivation(candidate.Element)!));
			}

			return entries;
		}

		private static Action? GetActivation(MenuFlyoutItemBase item)
		{
			if (item is MenuFlyoutSubItem subItem)
			{
				// Menus cannot be opened programmatically, so at least move the selection onto the submenu.
				return () => subItem.Focus(FocusState.Keyboard);
			}

			if (item is MenuFlyoutItem menuItem && (menuItem.Command is not null || menuItem is ToggleMenuFlyoutItem))
				return () => InvokeItem(menuItem);

			return null;
		}

		private static void InvokeItem(MenuFlyoutItem menuItem)
		{
			if (menuItem.Command is { } command)
			{
				if (!command.CanExecute(menuItem.CommandParameter) || !TryBeginActivation(menuItem))
					return;

				// A click toggles the item before running its command; mirror that for the keyboard path.
				if (menuItem is ToggleMenuFlyoutItem toggleItem)
					toggleItem.IsChecked = !toggleItem.IsChecked;

				CloseFlyout();
				menuItem.DispatcherQueue.TryEnqueue(() =>
				{
					if (command.CanExecute(menuItem.CommandParameter))
						command.Execute(menuItem.CommandParameter);
				});
				return;
			}

			// Checkable items bound straight to their IsChecked state have no command.
			if (menuItem is ToggleMenuFlyoutItem boundToggle && TryBeginActivation(boundToggle))
			{
				boundToggle.IsChecked = !boundToggle.IsChecked;
				CloseFlyout();
			}
		}

		private static void InvokeButton(Button button)
		{
			if (button.Command is not { } command || !command.CanExecute(button.CommandParameter) || !TryBeginActivation(button))
				return;

			CloseFlyout();
			button.DispatcherQueue.TryEnqueue(() =>
			{
				if (command.CanExecute(button.CommandParameter))
					command.Execute(button.CommandParameter);
			});
		}

		private static bool TryBeginActivation(object target)
		{
			var now = Stopwatch.GetTimestamp();
			if (ReferenceEquals(lastActivatedTarget, target) && now - lastActivatedTick < Stopwatch.Frequency / 4)
				return false;

			lastActivatedTarget = target;
			lastActivatedTick = now;

			return true;
		}

		private static void CloseFlyout()
		{
			if (App.LastOpenedFlyout is { IsOpen: true } flyout)
				flyout.Hide();
		}

		private static char? GetDerivedKey(string text, HashSet<char> usedKeys)
		{
			foreach (var candidate in EnumerateKeyCandidates(text))
			{
				if (!usedKeys.Contains(candidate))
					return candidate;
			}

			return null;
		}

		private static IEnumerable<char> EnumerateKeyCandidates(string text)
		{
			char first = default;
			if (text.Length > 0 && TryGetKeyCandidate(text[0], out char firstKey))
			{
				first = firstKey;
				yield return first;
			}

			var atWordStart = false;
			foreach (var character in text)
			{
				if (char.IsWhiteSpace(character) || character is '(' or '-' or '_')
				{
					atWordStart = true;
					continue;
				}

				if (atWordStart && TryGetKeyCandidate(character, out char initial) && initial != first)
					yield return initial;

				atWordStart = false;
			}

			foreach (var character in text)
			{
				if (TryGetKeyCandidate(character, out char key) && key != first)
					yield return key;
			}
		}

		private static bool TryGetKeyCandidate(char character, out char key)
		{
			key = char.ToUpperInvariant(character);

			return key is (>= 'A' and <= 'Z') or (>= '0' and <= '9');
		}

		private static char? GetExplicitKey(string? accessKey)
		{
			if (accessKey is not { Length: > 0 })
				return null;

			var trimmedKey = accessKey.Trim();
			if (trimmedKey.Length is 0)
				return null;

			return TryGetKeyCandidate(trimmedKey[0], out char key) ? key : null;
		}

		private static string? GetText(MenuFlyoutItemBase item)
			=> item switch
			{
				MenuFlyoutItem menuItem => menuItem.Text,
				MenuFlyoutSubItem subItem => subItem.Text,
				_ => null,
			};

		private static string? GetAccessKey(MenuFlyoutItemBase item)
			=> item switch
			{
				MenuFlyoutItem menuItem => menuItem.AccessKey,
				MenuFlyoutSubItem subItem => subItem.AccessKey,
				_ => null,
			};
	}
}
