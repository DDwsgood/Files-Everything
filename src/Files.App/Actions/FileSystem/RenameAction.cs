// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class RenameAction : ObservableObject, IAction
	{
		internal sealed record Invocation(BaseLayoutPage Page, IReadOnlyList<ListedItem> Items);

		private readonly IContentPageContext context;

		public string Label
			=> Strings.Rename.GetLocalizedResource();

		public string Description
			=> Strings.RenameDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.FileSystem;

		public HotKey HotKey
			=> new(Keys.F2);

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.Rename");

		public string AutomationId
			=> "InnerNavigationToolbarRenameButton";

		public string AccessKey
			=> "M";

		public bool IsExecutable =>
			context.ShellPage is not null &&
			IsPageTypeValid() &&
			context.ShellPage.SlimContentPage is not null &&
			context.HasSelection;

		public RenameAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();

			context.PropertyChanged += Context_PropertyChanged;
		}

		public async Task ExecuteAsync(object? parameter = null)
		{
			var invocation = parameter as Invocation;
			var shellPage = invocation?.Page.ParentShellPageInstance ?? context.ShellPage;
			var page = invocation?.Page ?? shellPage?.SlimContentPage;
			IReadOnlyList<ListedItem> items = invocation?.Items ?? context.SelectedItems.ToArray();
			if (page is null || items.Count == 0)
				return;

			if (items.Count > 1)
			{
				var viewModel = new BulkRenameDialogViewModel(shellPage, items);
				var dialogService = Ioc.Default.GetRequiredService<IDialogService>();
				var result = await dialogService.ShowDialogAsync(viewModel);
			}
			else
			{
				// Keep the menu's target even if focus restoration changed the live selection.
				page.ItemManipulationModel.SetSelectedItem(items[0]);
				page.ItemManipulationModel.StartRenameItem();
			}
		}

		private bool IsPageTypeValid()
		{
			return
				context.PageType != ContentPageTypes.None &&
				context.PageType != ContentPageTypes.Home &&
				context.PageType != ContentPageTypes.RecycleBin &&
				context.PageType != ContentPageTypes.ZipFolder &&
				context.PageType != ContentPageTypes.ReleaseNotes &&
				context.PageType != ContentPageTypes.Settings;
		}

		private void Context_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(IContentPageContext.ShellPage):
				case nameof(IContentPageContext.PageType):
				case nameof(IContentPageContext.HasSelection):
				case nameof(IContentPageContext.SelectedItems):
					OnPropertyChanged(nameof(IsExecutable));
					break;
			}
		}
	}
}
