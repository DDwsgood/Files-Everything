// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;

namespace Files.App.ViewModels.Settings
{
	/// <summary>
	/// A shell extension menu entry shown in the settings page. Toggling it hides or shows the entry in context menus.
	/// </summary>
	public sealed partial class ShellExtensionItemViewModel : ObservableObject
	{
		private readonly Action<ShellExtensionItemViewModel, bool> visibilityChanged;

		public string Label { get; }

		public string Verb { get; }

		public Visibility VerbVisibility
			=> string.IsNullOrWhiteSpace(Verb) ? Visibility.Collapsed : Visibility.Visible;

		private bool isVisible;
		public bool IsVisible
		{
			get => isVisible;
			set
			{
				if (SetProperty(ref isVisible, value))
					visibilityChanged(this, value);
			}
		}

		internal ShellExtensionItemViewModel(string label, string verb, bool isVisible, Action<ShellExtensionItemViewModel, bool> visibilityChanged)
		{
			Label = label;
			Verb = verb;

			this.isVisible = isVisible;
			this.visibilityChanged = visibilityChanged;
		}
	}
}
