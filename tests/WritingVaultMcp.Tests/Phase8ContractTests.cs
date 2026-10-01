using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using WritingVaultMcp.Mcp;

namespace WritingVaultMcp.Tests;

public sealed class Phase8ContractTests
{
    [Fact]
    public void EveryMutationToolUsesTheCommonRequestTokenAndResultContract()
    {
        var tools = typeof(SemanticVaultWriteTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => (Method: method, Tool: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(value => value.Tool is not null)
            .ToArray();
        Assert.Equal(48, tools.Length);
        Assert.Equal(48, tools.Select(value => value.Tool!.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (var (method, tool) in tools)
        {
            Assert.True(tool!.Idempotent, $"{tool.Name} must advertise idempotency.");
            Assert.Equal(typeof(Task<McpMutationResult>), method.ReturnType);
            var request = Assert.Single(method.GetParameters(), parameter => parameter.ParameterType != typeof(CancellationToken));
            var token = request.ParameterType.GetProperty("RequestToken", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(token);
            Assert.Equal(typeof(string), token!.PropertyType);
        }
    }

    [Fact]
    public void RegisteredToolParametersAndResultsContainNoStorageIdentityContract()
    {
        var types = new[] { typeof(SemanticVaultReadTools), typeof(SemanticVaultWriteTools) };
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "operationId", "continuityId", "entityId", "characterId", "sourceId", "tagId",
            "recordId", "relationshipId", "afterId"
        };
        foreach (var method in types.SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                     .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            foreach (var parameter in method.GetParameters().Where(parameter => parameter.ParameterType != typeof(CancellationToken)))
                Assert.DoesNotContain(parameter.Name!, forbidden);
            foreach (var property in FlattenPublicContract(method.ReturnType).Concat(
                         method.GetParameters().SelectMany(parameter => FlattenPublicContract(parameter.ParameterType))))
                Assert.DoesNotContain(property, forbidden);
        }
    }

    [Fact]
    public void MutationResultSerializationHasOneStableSuccessAndErrorShape()
    {
        var success = JsonSerializer.Serialize(new McpMutationResult(true, "ok", "character:ada~ABCDEFGHJK", "Character", 2));
        var error = JsonSerializer.Serialize(new McpMutationResult(false, "concurrency.conflict", Version: 3,
            Message: "The record changed after it was read."));
        foreach (var json in new[] { success, error })
        {
            using var document = JsonDocument.Parse(json);
            Assert.True(document.RootElement.TryGetProperty("Success", out _));
            Assert.True(document.RootElement.TryGetProperty("Code", out _));
            Assert.False(document.RootElement.TryGetProperty("OperationId", out _));
            Assert.False(document.RootElement.TryGetProperty("ResourceKey", out _));
        }
    }

    [Fact]
    public void VerificationSuiteContainsNoSkippedFacts()
    {
        var skipped = typeof(Phase8ContractTests).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(Phase8ContractTests).Namespace)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Select(method => (Method: method, Fact: method.GetCustomAttribute<FactAttribute>()))
            .Where(value => value.Fact?.Skip is not null)
            .Select(value => $"{value.Method.DeclaringType!.Name}.{value.Method.Name}")
            .ToArray();
        Assert.Empty(skipped);
    }

    private static IEnumerable<string> FlattenPublicContract(Type type)
    {
        if (type == typeof(string) || type == typeof(CancellationToken) || type.IsPrimitive || type.IsEnum) yield break;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
                foreach (var property in FlattenPublicContract(argument)) yield return property;
        }
        if (type.Namespace?.StartsWith("WritingVaultMcp.Mcp", StringComparison.Ordinal) != true) yield break;
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)) yield return property.Name;
    }
}
