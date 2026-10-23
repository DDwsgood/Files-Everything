// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Items
{
	/// <summary>
	/// A shell extension menu entry that was seen in a context menu, remembered so it can be hidden or shown
	/// from the settings page.
	/// </summary>
	public sealed class ShellExtensionMenuItem
	{
		[JsonPropertyName("Verb")]
		public string Verb { get; set; } = string.Empty;

		[JsonPropertyName("Label")]
		public string Label { get; set; } = string.Empty;
	}
}
