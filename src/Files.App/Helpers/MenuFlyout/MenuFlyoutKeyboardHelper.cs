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
	/// </summary>
	internal static class MenuFlyoutKeyboardHelper
	{
		private static readonly DependencyProperty IsAttachedProperty =
			DependencyProperty.RegisterAttached("IsAttached", typeof(bool), typeof(MenuFlyoutKeyboardHelper), new PropertyMetadata(false));

		private static readonly DependencyProperty IsFlyoutAttachedProperty =
			DependencyProperty.RegisterAttached("IsFlyoutAttached", typeof(bool), typeof(MenuFlyoutKeyboardHelper), new PropertyMetadata(false));

		/// <summary>
		/// Enables mnemonic activation on a single menu item. Items built from models are attached automatically.
		/// </summary>
		public static void Attach(MenuFlyoutItemBase? item)
		{
			if (item is null or MenuFlyoutSeparator || item.GetValue(IsAttachedProperty) is true)
				return;

			item.SetValue(IsAttachedProperty, true);
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
			App.LastOpenedFlyout = flyout;
			AttachItems(flyout.Items);
			flyout.Opening += Flyout_Opening;
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

		private static void Flyout_Opening(object? sender, object e)
		{
			if (sender is not MenuFlyout flyout)
				return;

			App.LastOpenedFlyout = flyout;
			AttachItems(flyout.Items);
		}

		private static void Presenter_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			// Item handlers run first and mark the key as handled; only act when nothing else did.
			if (!e.Handled)
				Item_KeyDown(sender, e);
		}

		private static void Item_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (sender is not UIElement source || !TryGetCharacter(e.Key, out char character))
				return;

			// Leave Alt, Ctrl and Win shortcuts alone. Alt is how WinUI's own access key mode works.
			if (IsModifierDown(VirtualKey.Menu) || IsModifierDown(VirtualKey.Control) ||
				IsModifierDown(VirtualKey.LeftWindows) || IsModifierDown(VirtualKey.RightWindows))
				return;

			var level = GetMenuLevel(source);
			if (level is null || !TryFindItem(level, character, out Action? activate) || activate is null)
				return;

			e.Handled = true;
			activate();
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

		private static bool TryFindItem(ItemsControl level, char character, out Action? activate)
		{
			activate = null;

			var entries = new List<(string Text, char? ExplicitKey, Action Activate)>();
			var usedKeys = new HashSet<char>();

			foreach (var element in level.Items)
			{
				if (element is not MenuFlyoutItemBase item || item is MenuFlyoutSeparator ||
					item.Visibility is not Visibility.Visible || !item.IsEnabled)
					continue;

				if (GetText(item) is { Length: > 0 } text)
				{
					if (GetActivation(item) is not { } itemActivation)
						continue;

					var explicitKey = GetExplicitKey(GetAccessKey(item));
					entries.Add((text, explicitKey, itemActivation));

					if (explicitKey is char key)
						usedKeys.Add(key);

					continue;
				}

				// The primary command row hosts icon buttons inside an item without text of its own.
				if (item is MenuFlyoutItem { Tag: Panel primaryRow })
					AddPrimaryRowEntries(primaryRow, entries, usedKeys);
			}

			// Declared access keys win; the remaining items get the first free character of their label.
			foreach (var entry in entries)
			{
				if (entry.ExplicitKey is char explicitKey)
				{
					if (explicitKey != character)
						continue;

					activate = entry.Activate;
					return true;
				}

				var derivedKey = GetDerivedKey(entry.Text, usedKeys);
				if (derivedKey is not char derived)
					continue;

				usedKeys.Add(derived);
				if (derived == character)
				{
					activate = entry.Activate;
					return true;
				}
			}

			return false;
		}

		private static void AddPrimaryRowEntries(Panel primaryRow, List<(string Text, char? ExplicitKey, Action Activate)> entries, HashSet<char> usedKeys)
		{
			foreach (var child in primaryRow.Children)
			{
				if (child is not Button { IsEnabled: true } button)
					continue;

				var explicitKey = GetExplicitKey(button.AccessKey);
				if (explicitKey is not char key)
					continue;

				var text = AutomationProperties.GetName(button);
				entries.Add((text ?? string.Empty, key, () => InvokeButton(button)));
				usedKeys.Add(key);
			}
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
				if (!command.CanExecute(menuItem.CommandParameter))
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
			if (menuItem is ToggleMenuFlyoutItem boundToggle)
			{
				boundToggle.IsChecked = !boundToggle.IsChecked;
				CloseFlyout();
			}
		}

		private static void InvokeButton(Button button)
		{
			if (button.Command is not { } command || !command.CanExecute(button.CommandParameter))
				return;

			CloseFlyout();
			button.DispatcherQueue.TryEnqueue(() =>
			{
				if (command.CanExecute(button.CommandParameter))
					command.Execute(button.CommandParameter);
			});
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
			// Matches how explorer assigns mnemonics: the first character, then the initials of the
			// remaining words, then any other unused character of the label.
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
