using System.Data.OleDb;
using System.Diagnostics;

if (args.Length != 2 || !int.TryParse(args[0], out var count) || count is < 1 or > 50_000)
    throw new ArgumentException("Usage: dotnet run --project tools/TimelineScaleFixture --configuration Debug -- 50000 artifacts/phase10-scale/WritingVault.Phase10Scale.accdb");

var directory = new DirectoryInfo(AppContext.BaseDirectory);
while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WritingVaultMcp.csproj")))
    directory = directory.Parent;
var root = directory?.FullName ?? throw new InvalidOperationException("Could not locate the Writing Vault root.");
var output = Path.GetFullPath(args[1]);
var artifacts = Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar;
if (!output.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) ||
    !output.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Scale fixtures must be new .accdb files under this checkout's artifacts directory.");
if (File.Exists(output)) throw new IOException("The scale fixture already exists. Choose a new output path.");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

var server = Path.Combine(root, "bin", "Debug", "net10.0-windows", "WritingVaultMcp.dll");
if (!File.Exists(server)) throw new FileNotFoundException("Build the server in Debug first.", server);
var start = new ProcessStartInfo("dotnet")
{
    WorkingDirectory = root, UseShellExecute = false,
    RedirectStandardOutput = true, RedirectStandardError = true,
    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
};
foreach (var argument in new[] { server, "schema", "init", "--database", output })
    start.ArgumentList.Add(argument);
using (var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start schema init."))
{
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException(
        $"Schema init failed: {await stdout}\n{await stderr}");
}

var connectionString = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={output};Persist Security Info=False;";
await using var connection = new OleDbConnection(connectionString);
await connection.OpenAsync();
var now = DateTime.UtcNow;
using (var continuity = new OleDbCommand(
           "INSERT INTO [Continuities] ([Name],[NormalizedName],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc],[Version],[IsDeleted]) VALUES (?,?,?,?,?,?,?)", connection))
{
    continuity.Parameters.Add("name", OleDbType.VarWChar).Value = "Scale timeline world";
    continuity.Parameters.Add("normalized", OleDbType.VarWChar).Value = "SCALE TIMELINE WORLD";
    continuity.Parameters.Add("zone", OleDbType.VarWChar).Value = "UTC";
    continuity.Parameters.Add("created", OleDbType.Date).Value = now;
    continuity.Parameters.Add("updated", OleDbType.Date).Value = now;
    continuity.Parameters.Add("version", OleDbType.Integer).Value = 1;
    continuity.Parameters.Add("deleted", OleDbType.Boolean).Value = false;
    await continuity.ExecuteNonQueryAsync();
}
int continuityId;
using (var identity = new OleDbCommand("SELECT @@IDENTITY", connection))
    continuityId = Convert.ToInt32(await identity.ExecuteScalarAsync());
using (var clock = new OleDbCommand(
           "INSERT INTO [ContinuityClocks] ([ContinuityId],[UpdatedAtUtc],[Version]) VALUES (?,?,?)", connection))
{
    clock.Parameters.Add("continuity", OleDbType.Integer).Value = continuityId;
    clock.Parameters.Add("updated", OleDbType.Date).Value = now;
    clock.Parameters.Add("version", OleDbType.Integer).Value = 1;
    await clock.ExecuteNonQueryAsync();
}

using (var canon = new OleDbCommand(
           "INSERT INTO [CanonEntities] ([ContinuityId],[EntityType],[CreatedAtUtc],[UpdatedAtUtc],[Version],[IsDeleted]) VALUES (?,?,?,?,?,?)", connection))
{
    canon.Parameters.Add("continuity", OleDbType.Integer).Value = continuityId;
    canon.Parameters.Add("type", OleDbType.VarWChar).Value = "WorldEvent";
    canon.Parameters.Add("created", OleDbType.Date).Value = now;
    canon.Parameters.Add("updated", OleDbType.Date).Value = now;
    canon.Parameters.Add("version", OleDbType.Integer).Value = 1;
    canon.Parameters.Add("deleted", OleDbType.Boolean).Value = false;
    for (var index = 0; index < count; index++)
    {
        await canon.ExecuteNonQueryAsync();
        if ((index + 1) % 5_000 == 0) Console.WriteLine($"Canon rows: {index + 1}/{count}");
    }
}

var ids = new List<int>(count);
using (var list = new OleDbCommand(
           "SELECT [Id] FROM [CanonEntities] WHERE [ContinuityId]=? ORDER BY [Id]", connection))
{
    list.Parameters.Add("continuity", OleDbType.Integer).Value = continuityId;
    using var reader = await list.ExecuteReaderAsync();
    while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
}
if (ids.Count != count) throw new InvalidOperationException("Canon row count changed during seeding.");
using (var events = new OleDbCommand(
           "INSERT INTO [WorldEvents] ([EntityId],[Title],[EventKind],[EventLowerBound],[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive],[EventCalendarId]) VALUES (?,?,?,?,?,?,?,?)", connection))
{
    events.Parameters.Add("id", OleDbType.Integer);
    events.Parameters.Add("title", OleDbType.VarWChar);
    events.Parameters.Add("kind", OleDbType.VarWChar).Value = "ExactDate";
    events.Parameters.Add("lower", OleDbType.Date);
    events.Parameters.Add("upper", OleDbType.Date);
    events.Parameters.Add("lowerInclusive", OleDbType.Boolean).Value = true;
    events.Parameters.Add("upperInclusive", OleDbType.Boolean).Value = false;
    events.Parameters.Add("calendar", OleDbType.VarWChar).Value = "Gregorian";
    for (var index = 0; index < count; index++)
    {
        var date = new DateTime(1900, 1, 1).AddDays(index);
        events.Parameters[0].Value = ids[index];
        events.Parameters[1].Value = $"Scale entry {index:00000}";
        events.Parameters[3].Value = date;
        events.Parameters[4].Value = date.AddDays(1);
        await events.ExecuteNonQueryAsync();
        if ((index + 1) % 5_000 == 0) Console.WriteLine($"World events: {index + 1}/{count}");
    }
}

Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(output)!, "backup"));
Console.WriteLine($"Created {count} disposable timeline entries in {output}");
