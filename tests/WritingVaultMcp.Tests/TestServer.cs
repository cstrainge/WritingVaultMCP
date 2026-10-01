namespace WritingVaultMcp.Tests;

internal static class TestServer
{
    public static string AssemblyPath
    {
        get
        {
            // A normal test output may contain the server DLL without all of
            // its runtime dependencies. Launch the complete server build in
            // that case. Isolated OutDir tests share their output with the
            // server and can safely launch the adjacent copy.
            var adjacent = Path.Combine(AppContext.BaseDirectory, "WritingVaultMcp.dll");
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? throw new InvalidOperationException("Could not determine the test build configuration.");
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WritingVaultMcp.csproj")))
                directory = directory.Parent;
            var root = directory?.FullName
                ?? throw new InvalidOperationException("Could not locate the Writing Vault server project.");
            if (configuration is "Debug" or "Release")
            {
                var completeBuild = Path.Combine(root, "bin", configuration,
                    "net10.0-windows", "WritingVaultMcp.dll");
                if (File.Exists(completeBuild)) return completeBuild;
            }
            if (File.Exists(adjacent)) return adjacent;
            throw new FileNotFoundException("The built Writing Vault server was not found.", adjacent);
        }
    }
}
