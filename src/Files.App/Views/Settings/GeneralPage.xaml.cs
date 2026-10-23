// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Files.App.Views.Settings
{
	public sealed partial class GeneralPage : Page
	{

		public GeneralPage()
		{
			InitializeComponent();
		}

		private void RemoveStartupPage(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PageOnStartupViewModel page })
				ViewModel.RemovePageCommand.Execute(page);
		}

		private void AddHiddenShellExtension_Click(object sender, RoutedEventArgs e)
		{
			ViewModel.AddHiddenShellExtension();
		}

		private void HiddenShellExtensionTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key is not VirtualKey.Enter)
				return;

			ViewModel.AddHiddenShellExtension();
			e.Handled = true;
		}

		private void RemoveHiddenShellExtension_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: string entry })
				ViewModel.RemoveHiddenShellExtension(entry);
		}

		private void ClearHiddenShellExtensions_Click(object sender, RoutedEventArgs e)
		{
			ViewModel.HiddenShellExtensions.Clear();
		}
	}
}
