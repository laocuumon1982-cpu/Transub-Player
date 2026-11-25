namespace TransubPlayer.Services;

/// <summary>
/// App-owned folder of finished translated subtitles (pre-translate-before-play output).
/// Layout: <c>{root}/{parent folder of video}/{video stem}.{target}.srt</c> so same-named
/// episodes in different folders do not collide, and the folder stays readable by hand.
/// Portable: <c>{exe folder}/Subs</c>; installed: <c>{app data}/Subs</c>; override via <see cref="AppSettings.SubLibraryDir"/>.
/// </summary>
internal static class SubLibrary
{
    public static string Root(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SubLibraryDir))
            return settings.SubLibraryDir.Trim();
        return AppPaths.IsPortable
            ? Path.Combine(AppPaths.InstallRoot, "Subs")
            : Path.Combine(AppPaths.AppDataDir, "Subs");
    }

    public static string PathFor(AppSettings settings, string mediaPath)
    {
        var full = Path.GetFullPath(mediaPath);
        var parent = Path.GetFileName(Path.GetDirectoryName(full) ?? "") ?? "";
        parent = SafeName(string.IsNullOrWhiteSpace(parent) ? "_" : parent);
        var stem = Path.GetFileNameWithoutExtension(full);
        return Path.Combine(Root(settings), parent, $"{stem}.{TranslateTargets.FileSuffix(settings.TranslateTarget)}.srt");
    }

    /// <summary>Finished library subtitle for the current target, or null.</summary>
    public static string? Find(AppSettings settings, string mediaPath)
    {
        try
        {
            if (MediaSourceHelper.IsNonLocalMedia(mediaPath)) return null;
            var p = PathFor(settings, mediaPath);
            var info = new FileInfo(p);
            return info.Exists && info.Length > 40 ? p : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Copy a finished subtitle into the library; returns the saved path.</summary>
    public static string Save(AppSettings settings, string mediaPath, string finishedSrt)
    {
        var dest = PathFor(settings, mediaPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".tmp";
        File.Copy(finishedSrt, tmp, overwrite: true);
        File.Move(tmp, dest, overwrite: true);
        return dest;
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
