// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Storage;

namespace Files.App.ViewModels.Dialogs
{
	public sealed partial class BulkRenameDialogViewModel : ObservableObject
	{
		private IContentPageContext context { get; } = Ioc.Default.GetRequiredService<IContentPageContext>();

		// Properties

		public bool IsNameValid =>
			FilesystemHelpers.IsValidForFilename(_FileName) && !_FileName.Contains('.');

		public bool ShowNameWarning =>
			!string.IsNullOrEmpty(_FileName) && !IsNameValid;

		private string _FileName = string.Empty;
		public string FileName
		{
			get => _FileName;
			set
			{
				if (SetProperty(ref _FileName, value))
				{
					OnPropertyChanged(nameof(IsNameValid));
					OnPropertyChanged(nameof(ShowNameWarning));
				}
			}
		}

		// Commands

		public IAsyncRelayCommand CommitRenameCommand { get; private set; }

		private readonly IShellPage? targetShellPage;
		private readonly IReadOnlyList<ListedItem> targetItems;

		public BulkRenameDialogViewModel(IShellPage? shellPage = null, IReadOnlyList<ListedItem>? items = null)
		{
			targetShellPage = shellPage ?? context.ShellPage;
			targetItems = items ?? context.SelectedItems.ToArray();
			CommitRenameCommand = new AsyncRelayCommand(DoCommitRenameAsync);
		}

		private async Task DoCommitRenameAsync()
		{
			if (targetShellPage is null)
				return;

			foreach (ListedItem item in targetItems)
			{
				var itemType = item.PrimaryItemAttribute == StorageItemTypes.Folder ? FilesystemItemType.Directory : FilesystemItemType.File;
				await targetShellPage.FilesystemHelpers.RenameAsync(
					StorageHelpers.FromPathAndType(item.GetRequiredPath(), itemType),
					FileName + item.FileExtension,
					NameCollisionOption.GenerateUniqueName,
					true,
					false
				);
			}
			;
		}

	}
}
