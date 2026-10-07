using System.Linq;

namespace ProoveGaming.App;

public static class Constants
{
    /// <summary>Supported avatar file extensions in dot form (e.g. ".jpg").</summary>
    public static readonly string[] AllowedAvatarExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    /// <summary>Supported avatar file extensions as glob patterns (e.g. "*.jpg"),
    /// suitable for <see cref="Avalonia.Platform.Storage.FilePickerFileType.Patterns"/>.</summary>
    public static readonly string[] AllowedExtensions =
        AllowedAvatarExtensions.Select(ext => "*" + ext).ToArray();
}
