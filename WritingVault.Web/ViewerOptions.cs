namespace WritingVault.Web;

internal sealed record ViewerOptions(
    string ServerAssemblyPath,
    string DatabasePath,
    string BackupRoot,
    string Provider)
{
#if DEBUG
    public const int Port = 5285;
    public const string Address = "http://127.0.0.1:5285";
    public const string Configuration = "Debug";
#else
    public const int Port = 5284;
    public const string Address = "http://127.0.0.1:5284";
    public const string Configuration = "Release";
#endif

    public static ViewerOptions Parse(string[] args)
    {
        static string? Read(string[] values, string name)
        {
            for (var index = 0; index < values.Length - 1; index++)
                if (values[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return values[index + 1];
            return null;
        }

        var server = Read(args, "--server") ?? throw new ArgumentException("--server is required.");
        var database = Read(args, "--database") ?? throw new ArgumentException("--database is required.");
        var backup = Read(args, "--backup-root") ?? throw new ArgumentException("--backup-root is required.");
        var listen = Read(args, "--listen") ?? Address;
        if (!listen.Equals(Address, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The viewer address is fixed at {Address}; refusing non-loopback or alternate-port binding.");
        server = Path.GetFullPath(server);
        database = Path.GetFullPath(database);
        backup = Path.GetFullPath(backup);
        if (!File.Exists(server)) throw new FileNotFoundException("The Writing Vault server assembly was not found.");
        if (!File.Exists(database)) throw new FileNotFoundException("The Writing Vault database was not found.");
        if (!Path.GetExtension(database).Equals(".accdb", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The configured database must be an .accdb file.");
        Directory.CreateDirectory(backup);
        return new(server, database, backup, Read(args, "--provider") ?? "Microsoft.ACE.OLEDB.12.0");
    }
}
