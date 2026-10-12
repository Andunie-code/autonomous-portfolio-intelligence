namespace PortfolioIntelligence.Architecture.Tests;

internal static class RepositoryRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "PortfolioIntelligence.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root (PortfolioIntelligence.slnx) not found.");
    }
}
