namespace PortfolioIntelligence.Kernel.Storage;

/// <summary>
/// Placement rule for local caches of downloaded content (docs/m1-data-policy.md §3 item 7, m7-protocol §2.4): raw
/// downloads must never live in the repository or in a OneDrive-synchronised folder. Shared by the archive cache (M1)
/// and the source cache (M7) so both apply the same rule.
/// </summary>
public static class LocalCacheRoot
{
    /// <summary>Returns the full path of <paramref name="root"/>, or throws if it is inside the repository or OneDrive.</summary>
    public static string Validate(string root, string repositoryRoot, string what, string parameterName)
    {
        var full = Path.GetFullPath(root);
        if (IsInside(full, Path.GetFullPath(repositoryRoot)))
        {
            throw new ArgumentException($"The {what} must be outside the repository.", parameterName);
        }

        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var oneDrive = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(oneDrive) && IsInside(full, Path.GetFullPath(oneDrive)))
            {
                throw new ArgumentException($"The {what} must be outside OneDrive.", parameterName);
            }
        }

        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"The {what} must be outside OneDrive.", parameterName);
        }

        return full;
    }

    public static bool IsInside(string path, string parent)
    {
        var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (path + Path.DirectorySeparatorChar).StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }
}
