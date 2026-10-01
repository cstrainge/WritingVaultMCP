using System.Text.Json;
using System.Text.Json.Serialization;

namespace WritingVaultMcp.Mcp.V4;

/// <summary>Accepts the compact exact-date string promised by the v4 contract as well as its rich object form.</summary>
public sealed class V4StoryDateInputJsonConverter : JsonConverter<V4StoryDateInput>
{
    public override V4StoryDateInput Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)
    {
        if(reader.TokenType==JsonTokenType.String)
            return new(V4StoryDateKind.ExactDate,reader.GetString());
        if(reader.TokenType!=JsonTokenType.StartObject)throw new JsonException("A story date must be an exact-date string or an object.");
        using var document=JsonDocument.ParseValue(ref reader);var root=document.RootElement;
        var allowed=new HashSet<string>(["kind","value","lower","upper","lowerInclusive","upperInclusive","originalText","calendarId"],StringComparer.Ordinal);
        foreach(var property in root.EnumerateObject())if(!allowed.Contains(property.Name))throw new JsonException($"Unknown story-date field '{property.Name}'.");
        V4StoryDateKind? kind=null;
        if(root.TryGetProperty("kind",out var kindValue)&&kindValue.ValueKind!=JsonValueKind.Null)
        {
            if(kindValue.ValueKind!=JsonValueKind.String||!Enum.TryParse<V4StoryDateKind>(kindValue.GetString(),true,out var parsed))throw new JsonException("The story-date kind is invalid.");
            kind=parsed;
        }
        return new(kind,Text(root,"value"),Text(root,"lower"),Text(root,"upper"),Boolean(root,"lowerInclusive"),Boolean(root,"upperInclusive"),Text(root,"originalText"),Text(root,"calendarId")??"Gregorian");
    }

    public override void Write(Utf8JsonWriter writer,V4StoryDateInput value,JsonSerializerOptions options)
    {
        writer.WriteStartObject();if(value.Kind is { } kind)writer.WriteString("kind",kind.ToString());
        Write(writer,"value",value.Value);Write(writer,"lower",value.Lower);Write(writer,"upper",value.Upper);
        if(value.LowerInclusive is { } lower)writer.WriteBoolean("lowerInclusive",lower);if(value.UpperInclusive is { } upper)writer.WriteBoolean("upperInclusive",upper);
        Write(writer,"originalText",value.OriginalText);Write(writer,"calendarId",value.CalendarId);writer.WriteEndObject();
    }

    private static string? Text(JsonElement root,string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind!=JsonValueKind.Null
        ?value.ValueKind==JsonValueKind.String?value.GetString():throw new JsonException($"{name} must be a string."):null;
    private static bool? Boolean(JsonElement root,string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind!=JsonValueKind.Null
        ?value.ValueKind is JsonValueKind.True or JsonValueKind.False?value.GetBoolean():throw new JsonException($"{name} must be a boolean."):null;
    private static void Write(Utf8JsonWriter writer,string name,string? value){if(value is not null)writer.WriteString(name,value);}
}
