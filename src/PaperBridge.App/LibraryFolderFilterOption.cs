namespace PaperBridge.App;

public enum LibraryFolderFilterKind
{
    All,
    Unfiled,
    Folder
}

public sealed record LibraryFolderFilterOption(
    LibraryFolderFilterKind Kind,
    string DisplayName,
    Guid? FolderId = null);

public sealed record LibraryTagFilterOption(string DisplayName, string? Tag = null);
