using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;

namespace WritingVaultMcp.Tests;

public sealed class TransactionAndConcurrencyTests
{
    [Fact]
    public async Task ConflictingOwnershipWritesSerializeAndLeaveOneValidPeriod()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var obj = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "Artifact"))).ResourceKey!);
        var period = StoryDate.Year(2000);
        var writes = await Task.WhenAll(
            vault.Service.AddOwnershipPeriodAsync(new(Guid.NewGuid().ToString(), obj, OwnershipState.Unknown, period, [])),
            vault.Service.AddOwnershipPeriodAsync(new(Guid.NewGuid().ToString(), obj, OwnershipState.Unowned, period, [])));
        Assert.Single(writes, result => result.Success);
        Assert.Single(writes, result => result.Code == "interval.overlap");
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task ConsistentReadGateCannotInterleaveWithAWrite()
    {
        await using var vault = await TestVault.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = vault.Coordinator.ExecuteConsistentReadAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        }, CancellationToken.None);
        await entered.Task;
        var write = vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(), "Queued"));
        await Task.Delay(100);
        Assert.False(write.IsCompleted);
        release.SetResult();
        Assert.True(await read);
        Assert.True((await write).Success);
    }

    [Fact]
    public async Task FaultRollsBackDataOperationAndJournal()
    {
        await using var vault = await TestVault.CreateAsync();
        var operationId = Guid.NewGuid().ToString();
        var result = await vault.Coordinator.ExecuteAsync(
            operationId, "fault.test", new { value = "x" }, "fault_test", null,
            async (context, token) =>
            {
                using var insert = context.Command("INSERT INTO [Tags] ([Name],[NormalizedName],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Transient','TRANSIENT',Now(),Now())");
                await insert.ExecuteNonQueryAsync(token);
                throw new InvalidOperationException("injected");
#pragma warning disable CS0162
                return new VaultMutationOutcome("Tag", "0", 1, "create", null);
#pragma warning restore CS0162
            });
        Assert.False(result.Success);
        await using var connection = vault.Factory.Create(); await connection.OpenAsync();
        foreach (var table in new[] { "Tags", "ProcessedOperations", "ChangeLog" })
        {
            using var command = connection.CreateCommand(); command.CommandText = $"SELECT COUNT(*) FROM [{table}]";
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task ConcurrentCanonicalTagCreatesLeaveOneRow()
    {
        await using var vault = await TestVault.CreateAsync();
        var first = vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(), " Mystery "));
        var second = vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(), "MYSTERY"));
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result.Success);
        Assert.Single(results, result => result.Code == "constraint.duplicate");
        await using var connection = vault.Factory.Create(); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM [Tags]";
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ConcurrentIdenticalOperationIdsCommitOnceAndReplayOnce()
    {
        await using var vault = await TestVault.CreateAsync();
        var request = new CreateTagRequest(Guid.NewGuid().ToString(), "Exactly once");
        var results = await Task.WhenAll(
            vault.Service.CreateTagAsync(request),
            vault.Service.CreateTagAsync(request));

        Assert.All(results, result => Assert.True(result.Success, result.Message));
        Assert.Single(results, result => !result.Replayed);
        Assert.Single(results, result => result.Replayed);
        Assert.Equal(results[0].ResourceKey, results[1].ResourceKey);
        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var tagCount = connection.CreateCommand();
        tagCount.CommandText = "SELECT COUNT(*) FROM [Tags]";
        Assert.Equal(1, Convert.ToInt32(await tagCount.ExecuteScalarAsync()));
        using var operationCount = connection.CreateCommand();
        operationCount.CommandText = "SELECT COUNT(*) FROM [ProcessedOperations]";
        Assert.Equal(1, Convert.ToInt32(await operationCount.ExecuteScalarAsync()));
        using var historyCount = connection.CreateCommand();
        historyCount.CommandText = "SELECT COUNT(*) FROM [ChangeLog]";
        Assert.Equal(1, Convert.ToInt32(await historyCount.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task CancellationRollsBackAndReleasesTheDatabase()
    {
        await using var vault = await TestVault.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var operation = vault.Coordinator.ExecuteAsync(
            Guid.NewGuid().ToString(), "cancel.test", new { value = "x" }, "cancel_test", null,
            async (context, token) =>
            {
                using var insert = context.Command("INSERT INTO [Tags] ([Name],[NormalizedName],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Cancelled','CANCELLED',Now(),Now())");
                await insert.ExecuteNonQueryAsync(token);
                cancellation.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return new VaultMutationOutcome("Tag", "0", 1, "create", null);
            }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await using var connection = vault.Factory.Create(); await connection.OpenAsync();
        using var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM [Tags]";
        Assert.Equal(0, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ExclusiveLockContentionReturnsBoundedRetryableFailure()
    {
        await using var vault = await TestVault.CreateAsync();
        var connectionString = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={vault.DatabasePath};Mode=Share Exclusive;OLE DB Services=-4;";
        await using var exclusive = new OleDbConnection(connectionString);
        await exclusive.OpenAsync();
        var started = DateTime.UtcNow;
        var result = await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(), "Blocked"));
        Assert.False(result.Success);
        Assert.Equal("storage.failure", result.Code);
        Assert.True(result.Retryable);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(vault.DatabasePath, result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
