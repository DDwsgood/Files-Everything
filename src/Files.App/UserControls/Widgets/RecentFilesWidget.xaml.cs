// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Files.App.UserControls.Widgets
{
	/// <summary>
	/// Represents a control that displays a list of recent files with <see cref="WidgetFolderCardItem"/>.
	/// </summary>
	public sealed partial class RecentFilesWidget : UserControl
	{
		public RecentFilesWidgetViewModel ViewModel { get; set; } = Ioc.Default.GetRequiredService<RecentFilesWidgetViewModel>();

		public RecentFilesWidget()
		{
			InitializeComponent();
		}

		private void RecentFilesListView_ItemClick(object sender, ItemClickEventArgs e)
		{
			if (e.ClickedItem is not RecentItem { Path: { Length: > 0 } path })
				return;

			ViewModel.NavigateToPath(path);
		}

		private void RecentFilesListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
		{
			ViewModel.BuildItemContextMenu(e.OriginalSource, e);
		}

		private async void RecentFilesListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
		{
			var item = e.Items.OfType<RecentItem>().FirstOrDefault();
			if (item is null || string.IsNullOrEmpty(item.Path))
			{
				e.Cancel = true;
				return;
			}

			if (Ioc.Default.GetRequiredService<IWindowsSecurityService>().IsAppElevated())
			{
				e.Cancel = true;
				ElevatedDragDropManager.StartDragDropFromPaths([item.Path]);
				return;
			}

			var storageItems = new List<IStorageItem>();

			try
			{
				var file = await StorageFile.GetFileFromPathAsync(item.Path);
				if (file != null)
					storageItems.Add(file);
			}
			catch
			{
				e.Cancel = true;
			}

			if (storageItems.Count > 0)
			{
				// Create a new data package and set the storage items
				DataPackage dataPackage = new DataPackage();
				dataPackage.SetStorageItems(storageItems);
				e.Data.SetDataProvider(StandardDataFormats.StorageItems, request => request.SetData(storageItems));

				e.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy | DataPackageOperation.Link;
			}
		}
	}
}
