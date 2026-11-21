using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// A fail-closed evaluator for the vocabulary emitted by these two SDK-generated output schemas.
// Unrecognized validation keywords fail instead of silently weakening the contract as schemas evolve.
// This is deliberately not a general-purpose JSON Schema implementation.
internal static class PublishedOutputSchema
{
    internal static bool Matches(JsonElement schema, JsonElement value, out string failure)
    {
        try { Validate(schema, value, "$", 0); failure = ""; return true; }
        catch (InvalidDataException error) { failure = error.Message; return false; }
    }

    private static void Validate(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (depth > 32) Fail(path, "schema depth exceeds fixture bound");
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False) Fail(path, "schema refuses every value");
        if (schema.ValueKind != JsonValueKind.Object) Fail(path, "invalid schema");
        foreach (var keyword in schema.EnumerateObject())
            if (keyword.Name is not ("type" or "properties" or "required" or "items" or "enum" or "const" or
                "additionalProperties" or "format" or "minItems" or "maxItems" or "minLength" or "maxLength" or
                "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "pattern" or
                "default" or "description" or "title" or "$schema" or "deprecated" or "examples" or "readOnly" or "writeOnly"))
                Fail(path, "unsupported schema keyword " + keyword.Name);
        if (schema.TryGetProperty("type", out var type))
        {
            bool matches = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Any(item => TypeMatches(item.GetString()!, value))
                : TypeMatches(type.GetString()!, value);
            if (!matches) Fail(path, "type " + type.GetRawText());
        }
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
            Fail(path, "enum");
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value)) Fail(path, "const");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    if (!value.TryGetProperty(name.GetString()!, out _)) Fail(path + "." + name.GetString(), "required");
            bool hasProperties = schema.TryGetProperty("properties", out var properties);
            foreach (var property in value.EnumerateObject())
            {
                if (hasProperties && properties.TryGetProperty(property.Name, out var child))
                    Validate(child, property.Value, path + "." + property.Name, depth + 1);
                else if (schema.TryGetProperty("additionalProperties", out var additional))
                    Validate(additional, property.Value, path + "." + property.Name, depth + 1);
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            Bound(schema, "minItems", value.GetArrayLength(), true, path);
            Bound(schema, "maxItems", value.GetArrayLength(), false, path);
            if (schema.TryGetProperty("items", out var itemSchema))
            {
                int index = 0;
                foreach (var item in value.EnumerateArray()) Validate(itemSchema, item, path + "[" + index++ + "]", depth + 1);
            }
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            int length = text.EnumerateRunes().Count();
            Bound(schema, "minLength", length, true, path);
            Bound(schema, "maxLength", length, false, path);
            if (schema.TryGetProperty("pattern", out var pattern) &&
                !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) Fail(path, "pattern");
            if (schema.TryGetProperty("format", out var format))
            {
                bool valid = format.GetString() switch
                {
                    "uuid" => Guid.TryParseExact(text, "D", out _),
                    "date-time" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
                        Regex.IsMatch(text, "^[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]+)?([Zz]|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant),
                    _ => throw new InvalidDataException(path + ": unsupported format " + format.GetString())
                };
                if (!valid) Fail(path, "format " + format.GetString());
            }
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDecimal(out decimal number)) Fail(path, "number exceeds fixture bound");
            Bound(schema, "minimum", number, true, path);
            Bound(schema, "maximum", number, false, path);
            if (schema.TryGetProperty("exclusiveMinimum", out var minimum) && number <= minimum.GetDecimal()) Fail(path, "exclusiveMinimum");
            if (schema.TryGetProperty("exclusiveMaximum", out var maximum) && number >= maximum.GetDecimal()) Fail(path, "exclusiveMaximum");
        }
    }

    private static bool TypeMatches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number) && number == decimal.Truncate(number),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new InvalidDataException("unsupported schema type " + type)
    };

    private static void Bound(JsonElement schema, string name, decimal value, bool minimum, string path)
    {
        if (schema.TryGetProperty(name, out var bound) && (minimum ? value < bound.GetDecimal() : value > bound.GetDecimal())) Fail(path, name);
    }

    private static void Fail(string path, string reason) => throw new InvalidDataException(path + ": " + reason);
}
