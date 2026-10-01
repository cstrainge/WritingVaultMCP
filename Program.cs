using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Schema;
using WritingVaultMcp.Infrastructure.Access.Integrity;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Infrastructure;

return await RunAsync(args);

static async Task<int> RunAsync(string[] commandLine)
{
    try
    {
        if (commandLine.Length == 0 || commandLine[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
        {
            var configured = WritingVaultConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            var serverOptions = configured with
            {
                DatabasePath = ReadOption(commandLine, "--database") ?? configured.DatabasePath,
                Provider = ReadOption(commandLine, "--provider") ?? configured.Provider,
                BackupRoot = ReadOption(commandLine, "--backup-root") ?? configured.BackupRoot,
                ReadOnly = configured.ReadOnly || commandLine.Contains("--read-only", StringComparer.OrdinalIgnoreCase),
                ToolSurface = NormalizeToolSurface(ReadOption(commandLine, "--tool-surface") ?? configured.ToolSurface)
            };
            RequireConfiguredPaths(serverOptions);
            await VaultProcessHost.RunAdapterAsync(
                serverOptions,
                ReadOption(commandLine, "--client-label") ?? "mcp-client");
            return 0;
        }

        if (commandLine[0].Equals("backend", StringComparison.OrdinalIgnoreCase))
        {
            var configured = WritingVaultConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            var backendOptions = configured with
            {
                DatabasePath = ReadOption(commandLine, "--database") ?? configured.DatabasePath,
                Provider = ReadOption(commandLine, "--provider") ?? configured.Provider,
                BackupRoot = ReadOption(commandLine, "--backup-root") ?? configured.BackupRoot,
                ReadOnly = false
            };
            RequireConfiguredPaths(backendOptions);
            await VaultProcessHost.RunBackendAsync(backendOptions);
            return 0;
        }

        if (commandLine[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        if (commandLine[0].Equals("contract", StringComparison.OrdinalIgnoreCase))
        {
            if (commandLine.Length < 2) throw new ArgumentException("contract requires export or image-probe.");
            var contractAction = commandLine[1].ToLowerInvariant();
            if (contractAction == "export")
            {
                var output = ReadOption(commandLine, "--output")
                    ?? Path.Combine(AppContext.BaseDirectory, "contracts", "v4");
                V4ContractCatalog.WriteSnapshots(output);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    surfaceVersion = V4ContractCatalog.SurfaceVersion,
                    toolCount = V4ContractCatalog.Tools.Count,
                    output = Path.GetFullPath(output)
                }, JsonOptions()));
                return 0;
            }
            if (contractAction == "image-probe")
            {
#if DEBUG
                await V4ImageIngressProbeHost.RunAsync(ReadOption(commandLine, "--evidence-output"));
                return 0;
#else
                throw new InvalidOperationException("The image ingress probe is available only in Debug builds.");
#endif
            }
            throw new ArgumentException($"Unknown contract action '{contractAction}'.");
        }

        if (commandLine.Length < 2)
        {
            Console.Error.WriteLine("Unknown command.");
            PrintUsage();
            return 64;
        }

        var options = WritingVaultConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var group = commandLine[0].ToLowerInvariant();
        var action = commandLine[1].ToLowerInvariant();
        // Command-line tokens are untrusted input and may contain local paths or secrets.
        VaultDiagnostics.Write("administrative.started", commandType: (group, action) switch
        {
            ("integrity", "status") => "integrity.status",
            ("backup", "create" or "verify" or "restore") => $"backup.{action}",
            ("purge", "preview" or "execute") => $"purge.{action}",
            ("schema", "status" or "migrate" or "init" or "range-review") => $"schema.{action}",
            _ => "unknown"
        });
        var databasePath = ReadOption(commandLine, "--database") ?? options.DatabasePath;
        var provider = ReadOption(commandLine, "--provider") ?? options.Provider;
        var backupRoot = ReadOption(commandLine, "--backup-root") ?? options.BackupRoot;
        if (string.IsNullOrWhiteSpace(databasePath) &&
            !(group == "backup" && (action is "verify" or "restore")))
            throw new ArgumentException("Set WritingVault.DatabasePath in appsettings.json or pass --database.");

        if (group == "integrity" && action == "status")
        {
            return await ShowIntegrityStatusAsync(databasePath, provider);
        }

        if (group == "backup")
        {
            var backups = new AccessBackupService(provider);
            if (action == "create")
            {
                var purposeText = ReadOption(commandLine, "--purpose") ?? "regular";
                var purpose = purposeText.Equals("pre-migration", StringComparison.OrdinalIgnoreCase)
                    ? VaultBackupPurpose.PreMigration
                    : purposeText.Equals("regular", StringComparison.OrdinalIgnoreCase)
                        ? VaultBackupPurpose.Regular
                        : throw new ArgumentException("--purpose must be regular or pre-migration.");
                var retention = ReadOption(commandLine, "--retention") is { } retentionText
                    ? int.Parse(retentionText, System.Globalization.CultureInfo.InvariantCulture)
                    : options.BackupRetentionCount;
                if (string.IsNullOrWhiteSpace(backupRoot)) throw new ArgumentException("Set WritingVault.BackupRoot in appsettings.json or pass --backup-root.");
                var result = await backups.CreateAsync(databasePath, backupRoot, retention, purpose: purpose);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions()));
                return 0;
            }
            if (action == "verify")
            {
                var manifest = ReadOption(commandLine, "--manifest") ?? throw new ArgumentException("--manifest is required.");
                var result = await backups.VerifyAsync(manifest,
                    requireCurrentSchema: !commandLine.Contains("--allow-incompatible-schema", StringComparer.OrdinalIgnoreCase));
                Console.WriteLine(JsonSerializer.Serialize(new { valid = true, result.CreatedAtUtc, result.BackupFileName, result.BackupSha256 }, JsonOptions()));
                return 0;
            }
            if (action == "restore")
            {
                var manifest = ReadOption(commandLine, "--manifest") ?? throw new ArgumentException("--manifest is required.");
                var target = ReadOption(commandLine, "--target") ?? throw new ArgumentException("--target is required.");
                await backups.RestoreToNewPathAsync(manifest, target, requireCurrentSchema: !commandLine.Contains("--allow-incompatible-schema", StringComparer.OrdinalIgnoreCase));
                Console.WriteLine(JsonSerializer.Serialize(new { restored = true, target = Path.GetFileName(target) }, JsonOptions()));
                return 0;
            }

            return UnknownSchemaAction(action);
        }

        if (group == "purge")
        {
            var purge = new AccessPurgeService(provider);
            if (action == "preview")
            {
                var entityId = int.Parse(ReadOption(commandLine, "--entity") ?? throw new ArgumentException("--entity is required."));
                var version = int.Parse(ReadOption(commandLine, "--expected-version") ?? throw new ArgumentException("--expected-version is required."));
                if (string.IsNullOrWhiteSpace(backupRoot)) throw new ArgumentException("Set WritingVault.BackupRoot in appsettings.json or pass --backup-root.");
                var result = await purge.PreviewAsync(databasePath, backupRoot, entityId, version);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions()));
                return result.CanPurge ? 0 : 4;
            }
            if (action == "execute")
            {
                var token = ReadOption(commandLine, "--token") ?? throw new ArgumentException("--token is required.");
                var manifest = ReadOption(commandLine, "--backup-manifest") ?? throw new ArgumentException("--backup-manifest is required.");
                if (string.IsNullOrWhiteSpace(backupRoot)) throw new ArgumentException("Set WritingVault.BackupRoot in appsettings.json or pass --backup-root.");
                await purge.ExecuteAsync(databasePath, backupRoot, token, manifest);
                Console.WriteLine(JsonSerializer.Serialize(new { purged = true }, JsonOptions()));
                return 0;
            }
            return UnknownSchemaAction(action);
        }

        if (group != "schema")
        {
            Console.Error.WriteLine("Unknown command group. Run --help for usage.");
            PrintUsage();
            return 64;
        }

        return action switch
        {
            "status" => await ShowSchemaStatusAsync(databasePath, provider),
            "range-review" => await ShowLegacyRangesAsync(databasePath, provider),
            "migrate" => await MigrateAsync(commandLine, databasePath, provider),
            "init" => await InitializeAsync(databasePath, provider),
            _ => UnknownSchemaAction(action)
        };
    }
    catch (OperationCanceledException)
    {
        VaultDiagnostics.Write("process.cancelled", "warning");
        Console.Error.WriteLine("Operation cancelled.");
        return 130;
    }
    catch (Exception exception)
    {
        VaultDiagnostics.Write("process.failed", "error", code: exception.GetType().Name);
        // Exception messages from IO, ACE, and JSON parsing can contain local paths or source text.
        // Keep persistent launcher logs path-free; the administrative status commands supply detail.
        Console.Error.WriteLine(exception switch
        {
            FileNotFoundException or DirectoryNotFoundException =>
                "A required local file was not found. Check the configured database and backup locations.",
            UnauthorizedAccessException =>
                "Access to a required local file was denied. Check the database and backup directory permissions.",
            System.Data.OleDb.OleDbException =>
                "The Access provider could not open the database. Check the 64-bit provider, then run schema and integrity status.",
            ArgumentException =>
                "A command or configuration value is invalid. Run --help and check the local configuration.",
            _ => "Writing Vault could not complete the operation. Run schema and integrity status for details."
        });
        return 1;
    }
}

static void RequireConfiguredPaths(WritingVaultOptions options)
{
    if (string.IsNullOrWhiteSpace(options.DatabasePath))
        throw new ArgumentException("Set WritingVault.DatabasePath in appsettings.json or pass --database.");
    if (string.IsNullOrWhiteSpace(options.BackupRoot))
        throw new ArgumentException("Set WritingVault.BackupRoot in appsettings.json or pass --backup-root.");
}

static async Task<int> ShowIntegrityStatusAsync(string databasePath, string provider)
{
    var verifier = new AccessIntegrityVerifier(new AccessConnectionFactory(databasePath, provider));
    var result = await verifier.VerifyAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { valid = result.IsValid, issues = result.Issues }, JsonOptions()));
    return result.IsValid ? 0 : 3;
}

static async Task<int> ShowSchemaStatusAsync(string databasePath, string provider)
{
    var verifier = new AccessSchemaVerifier(new AccessConnectionFactory(databasePath, provider));
    var result = await verifier.VerifyAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        valid = result.IsValid,
        migrationId = AccessSchemaDefinition.MigrationId,
        checksum = AccessSchemaDefinition.Checksum,
        issues = result.Issues
    }, JsonOptions()));
    return result.IsValid ? 0 : 2;
}

static async Task<int> ShowLegacyRangesAsync(string databasePath, string provider)
{
    var rows = await new AccessLegacyRangeReview(new AccessConnectionFactory(databasePath, provider))
        .ListAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { count = rows.Count, rows }, JsonOptions()));
    return 0;
}

static async Task<int> MigrateAsync(string[] commandLine, string databasePath, string provider)
{
    var allowRebuild = commandLine.Contains("--allow-empty-rebuild", StringComparer.OrdinalIgnoreCase);
    if (allowRebuild)
    {
        var manifestPath = ReadOption(commandLine, "--backup-manifest")
            ?? throw new ArgumentException("--backup-manifest is required with --allow-empty-rebuild.");
        await VerifyMigrationBackupAsync(databasePath, manifestPath, provider);
    }

    var migrator = new AccessSchemaMigrator(
        new AccessConnectionFactory(databasePath, provider),
        TimeProvider.System);
    var result = await migrator.MigrateAsync(allowRebuild);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        changed = result.Changed,
        migrationId = result.MigrationId,
        checksum = result.Checksum,
        valid = result.Verification.IsValid
    }, JsonOptions()));
    return 0;
}

static async Task VerifyMigrationBackupAsync(string databasePath, string manifestPath, string provider)
{
    using var document = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(manifestPath)));
    if (!document.RootElement.TryGetProperty("BackupFileName", out _))
    {
        BackupManifestVerifier.Verify(databasePath, manifestPath);
        return;
    }

    var manifest = await new AccessBackupService(provider).VerifyAsync(manifestPath, requireCurrentSchema: false);
    var database = Path.GetFullPath(databasePath);
    if (!string.Equals(manifest.SourceFileName, Path.GetFileName(database), StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(manifest.SourcePathSha256) ||
        !string.Equals(manifest.SourcePathSha256, AccessBackupService.HashPath(database), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The migration backup was not created from this database path.");
    using var stream = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read);
    var currentHash = Convert.ToHexString(SHA256.HashData(stream));
    if (!string.Equals(currentHash, manifest.SourceSha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The database changed after the migration backup was created.");
}

static async Task<int> InitializeAsync(string databasePath, string provider)
{
    AccessDatabaseFileInitializer.Create(databasePath, provider);
    var migrator = new AccessSchemaMigrator(
        new AccessConnectionFactory(databasePath, provider),
        TimeProvider.System);
    var result = await migrator.MigrateAsync(false);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        created = Path.GetFullPath(databasePath),
        migrationId = result.MigrationId,
        checksum = result.Checksum,
        valid = result.Verification.IsValid
    }, JsonOptions()));
    return 0;
}

static int UnknownSchemaAction(string action)
{
    Console.Error.WriteLine("Unknown command action. Run --help for usage.");
    PrintUsage();
    return 64;
}

static string? ReadOption(IReadOnlyList<string> arguments, string option)
{
    for (var index = 0; index < arguments.Count; index++)
    {
        if (!arguments[index].Equals(option, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return arguments[index + 1];
    }

    return null;
}

static string NormalizeToolSurface(string value) => value.Trim().ToLowerInvariant() switch
{
    "v3" or "3" or "3.0" => "v3",
    "v4" or "4" or "4.0" => "v4",
    _ => throw new ArgumentException("--tool-surface must be v3 or v4.")
};

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };

static void PrintUsage()
{
    Console.WriteLine("""
        WritingVaultMcp administrative commands

          serve [--database <path>] [--provider <provider>] [--backup-root <path>]
                [--client-label <label>] [--read-only] [--tool-surface v3|v4]
          schema status  [--database <path>] [--provider <provider>]
          schema range-review [--database <path>] [--provider <provider>]
          schema migrate [--database <path>] [--provider <provider>]
                         [--allow-empty-rebuild --backup-manifest <path>]
          schema init    --database <new-path> [--provider <provider>]
          integrity status [--database <path>] [--provider <provider>]
          backup create [--database <path>] [--provider <provider>] [--purpose regular|pre-migration] [--retention <count>=2+]
          backup verify --manifest <path> [--provider <provider>] [--allow-incompatible-schema]
          backup restore --manifest <path> --target <new-path> [--provider <provider>] [--allow-incompatible-schema]
          purge preview --entity <id> --expected-version <version>
          purge execute --token <preview-token> --backup-manifest <recent-manifest>
          contract export --output <directory>
          contract image-probe [--evidence-output <jsonl-path>]   (Debug only)
        """);
}

internal sealed record WritingVaultOptions(string DatabasePath, string Provider, string BackupRoot, bool ReadOnly, int BackupRetentionCount,
    string ToolSurface = WritingVaultConfiguration.DefaultToolSurface);

internal static class WritingVaultConfiguration
{
#if DEBUG
    internal const string DefaultToolSurface = "v3";
#else
    internal const string DefaultToolSurface = "v4";
#endif
    public static WritingVaultOptions Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return new WritingVaultOptions("", "Microsoft.ACE.OLEDB.12.0", "", false, 20);
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
        var section = document.RootElement.GetProperty("WritingVault");
        return new WritingVaultOptions(
            section.GetProperty("DatabasePath").GetString()
                ?? throw new InvalidDataException("WritingVault.DatabasePath is required."),
            section.GetProperty("Provider").GetString()
                ?? throw new InvalidDataException("WritingVault.Provider is required."),
            section.GetProperty("BackupRoot").GetString()
                ?? throw new InvalidDataException("WritingVault.BackupRoot is required."),
            section.TryGetProperty("ReadOnly", out var readOnly) && readOnly.GetBoolean(),
            section.TryGetProperty("BackupRetentionCount", out var retention) ? retention.GetInt32() : 20,
            section.TryGetProperty("ToolSurface", out var surface) ? NormalizeConfiguredSurface(surface.GetString()) : DefaultToolSurface);
    }

    private static string NormalizeConfiguredSurface(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "v4" or "4" or "4.0" => "v4",
        _ => "v3"
    };
}

internal sealed record BackupManifest(
    string Source,
    string SourceSha256,
    string Backup,
    string BackupSha256);

internal static class BackupManifestVerifier
{
    public static void Verify(string databasePath, string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            File.ReadAllText(Path.GetFullPath(manifestPath)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Backup manifest is invalid.");

        var fullDatabasePath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullDatabasePath))
        {
            throw new FileNotFoundException("Database to rebuild was not found.", fullDatabasePath);
        }

        if (!File.Exists(manifest.Backup))
        {
            throw new FileNotFoundException("Verified backup was not found.", manifest.Backup);
        }

        var sourceHash = HashFile(fullDatabasePath);
        var backupHash = HashFile(manifest.Backup);
        var isOriginalSourcePath = string.Equals(
            fullDatabasePath,
            Path.GetFullPath(manifest.Source),
            StringComparison.OrdinalIgnoreCase);
        if (!isOriginalSourcePath && !sourceHash.Equals(manifest.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A non-source database must match the source hash recorded in the backup manifest before rebuild.");
        }

        if (!backupHash.Equals(manifest.BackupSha256, StringComparison.OrdinalIgnoreCase) ||
            !backupHash.Equals(manifest.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The backup does not match the database selected for empty rebuild.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
