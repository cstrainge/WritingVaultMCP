using System.Data.OleDb;

namespace WritingVaultMcp.Infrastructure.Access;

public interface IAccessConnectionFactory
{
    OleDbConnection Create();
}

public sealed class AccessConnectionFactory : IAccessConnectionFactory
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public AccessConnectionFactory(string databasePath, string provider = "Microsoft.ACE.OLEDB.12.0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var fullPath = Path.GetFullPath(databasePath);
        DatabasePath = fullPath;
        var builder = new OleDbConnectionStringBuilder
        {
            Provider = provider,
            DataSource = fullPath,
            PersistSecurityInfo = false
        };

        // ACE's default OLE DB resource pooling can keep the database lock file
        // alive after the last OleDbConnection is disposed. The vault is a
        // single-process owner, so deterministic release matters more here.
        builder["OLE DB Services"] = -4;

        _connectionString = builder.ConnectionString;
    }

    public OleDbConnection Create() => new(_connectionString);
}
