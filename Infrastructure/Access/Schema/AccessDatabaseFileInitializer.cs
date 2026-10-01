using System.Runtime.InteropServices;

namespace WritingVaultMcp.Infrastructure.Access.Schema;

public static class AccessDatabaseFileInitializer
{
    public static void Create(string databasePath, string provider = "Microsoft.ACE.OLEDB.12.0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var fullPath = Path.GetFullPath(databasePath);
        if (File.Exists(fullPath))
        {
            throw new IOException($"Database already exists: {fullPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var catalogType = Type.GetTypeFromProgID("ADOX.Catalog")
            ?? throw new PlatformNotSupportedException("ADOX.Catalog is not registered. Install the matching Access Database Engine.");
        object? catalog = null;
        try
        {
            catalog = Activator.CreateInstance(catalogType)
                ?? throw new InvalidOperationException("ADOX.Catalog could not be created.");
            dynamic dynamicCatalog = catalog;
            dynamicCatalog.Create($"Provider={provider};Data Source={fullPath};Jet OLEDB:Engine Type=5;");

            // Catalog.Create leaves an ADODB.Connection attached to the COM
            // catalog. Close and detach it explicitly; releasing only Catalog
            // can leave a lock file until a later provider call or process exit.
            object? activeConnection = dynamicCatalog.ActiveConnection;
            if (activeConnection is not null)
            {
                try
                {
                    dynamic dynamicConnection = activeConnection;
                    dynamicConnection.Close();
                }
                finally
                {
                    if (Marshal.IsComObject(activeConnection))
                    {
                        Marshal.FinalReleaseComObject(activeConnection);
                    }
                }
            }
        }
        catch
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            throw;
        }
        finally
        {
            if (catalog is not null && Marshal.IsComObject(catalog))
            {
                Marshal.FinalReleaseComObject(catalog);
            }

            // ADOX creates through an internal OLE DB connection. Release its
            // provider pool so the file and lock are immediately released.
            System.Data.OleDb.OleDbConnection.ReleaseObjectPool();
        }
    }
}

public static class AccessDatabaseUseGuard
{
    public static void ThrowIfLockFilePresent(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Database path has no parent directory.");
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ownerPath = Path.Combine(directory, stem + ".writingvault.owner.lock");
        if (File.Exists(ownerPath))
        {
            try
            {
                using var stale = new FileStream(ownerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                stale.Close();
                File.Delete(ownerPath);
            }
            catch (IOException)
            {
                throw new IOException("The database is owned by a running Writing Vault server process.");
            }
        }
        foreach (var extension in new[] { ".laccdb", ".ldb" })
        {
            if (File.Exists(Path.Combine(directory, stem + extension)))
            {
                throw new IOException(
                    $"The database is in use or has a stale Access lock file. Close all users and resolve the '{extension}' lock before migration.");
            }
        }
    }
}

public sealed class AccessOwnerLease : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;
    private bool _disposed;

    private AccessOwnerLease(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    public static AccessOwnerLease Acquire(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var path = Path.Combine(Path.GetDirectoryName(fullPath)!, Path.GetFileNameWithoutExtension(fullPath) + ".writingvault.owner.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write($"pid={Environment.ProcessId};startedUtc={DateTime.UtcNow:O}");
                writer.Flush();
            }
            return new AccessOwnerLease(stream, path);
        }
        catch (IOException exception)
        {
            throw new IOException("Another Writing Vault server already owns the configured database.", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
