using System.Text.Json;

namespace Baymax.Core.Models;

internal static class JsonSchemaValidator
{
    private static readonly HashSet<string> SupportedKeywords =
    [
        "type", "properties", "required", "items", "enum", "additionalProperties",
        "minLength", "maxLength", "minItems", "maxItems", "minimum", "maximum",
        "description", "title"
    ];

    public static void Validate(JsonElement schema, JsonElement value)
    {
        EnsureSupportedSchema(schema, "$", 0);
        ValidateValue(schema, value, "$", 0);
    }

    public static JsonElement GetSchema(JsonElement responseFormat)
    {
        if (!responseFormat.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("ResponseFormat must include a string 'type'.");
        }

        if (type.GetString() == "json_object")
        {
            return default;
        }

        if (type.GetString() != "json_schema")
        {
            throw new ArgumentException("ResponseFormat type must be 'json_object' or 'json_schema'.");
        }

        if (!responseFormat.TryGetProperty("json_schema", out var jsonSchema)
            || jsonSchema.ValueKind != JsonValueKind.Object
            || !jsonSchema.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A json_schema response format must include an object schema.");
        }

        EnsureSupportedSchema(schema, "$", 0);
        return schema;
    }

    private static void EnsureSupportedSchema(JsonElement schema, string path, int depth)
    {
        if (depth > 32 || schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"JSON Schema at {path} must be an object with a maximum depth of 32.");
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (!SupportedKeywords.Contains(property.Name))
            {
                throw new ArgumentException(
                    $"JSON Schema keyword '{property.Name}' at {path} is not supported.");
            }
        }

        if (!schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() is not ("object" or "array" or "string" or "integer" or "number" or "boolean" or "null"))
        {
            throw new ArgumentException($"JSON Schema at {path} must declare a supported string type.");
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            if (type.GetString() != "object" || properties.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"JSON Schema properties at {path} require an object schema.");
            }

            foreach (var property in properties.EnumerateObject())
            {
                EnsureSupportedSchema(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            if (type.GetString() != "array")
            {
                throw new ArgumentException($"JSON Schema items at {path} require an array schema.");
            }

            EnsureSupportedSchema(items, $"{path}[]", depth + 1);
        }

        if (schema.TryGetProperty("required", out var required)
            && (type.GetString() != "object"
                || required.ValueKind != JsonValueKind.Array
                || required.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)))
        {
            throw new ArgumentException($"JSON Schema required at {path} must be an array of strings.");
        }

        if (schema.TryGetProperty("required", out required)
            && schema.TryGetProperty("properties", out properties))
        {
            foreach (var requiredProperty in required.EnumerateArray())
            {
                if (!properties.TryGetProperty(requiredProperty.GetString()!, out _))
                {
                    throw new ArgumentException(
                        $"JSON Schema required property '{requiredProperty.GetString()}' at {path} is not declared in properties.");
                }
            }
        }

        if (schema.TryGetProperty("enum", out var enumValues)
            && (enumValues.ValueKind != JsonValueKind.Array || enumValues.GetArrayLength() == 0))
        {
            throw new ArgumentException($"JSON Schema enum at {path} must be a non-empty array.");
        }

        if (schema.TryGetProperty("minLength", out _) || schema.TryGetProperty("maxLength", out _))
        {
            if (type.GetString() != "string")
            {
                throw new ArgumentException($"JSON Schema string length bounds at {path} require type 'string'.");
            }
        }

        if (schema.TryGetProperty("minItems", out _) || schema.TryGetProperty("maxItems", out _))
        {
            if (type.GetString() != "array")
            {
                throw new ArgumentException($"JSON Schema item bounds at {path} require type 'array'.");
            }
        }

        if (schema.TryGetProperty("minimum", out _) || schema.TryGetProperty("maximum", out _))
        {
            if (type.GetString() is not ("integer" or "number"))
            {
                throw new ArgumentException($"JSON Schema numeric bounds at {path} require a numeric type.");
            }
        }

        foreach (var keyword in new[] { "minLength", "maxLength", "minItems", "maxItems" })
        {
            if (schema.TryGetProperty(keyword, out var bound)
                && (bound.ValueKind != JsonValueKind.Number
                    || !bound.TryGetInt32(out var boundValue)
                    || boundValue < 0))
            {
                throw new ArgumentException($"JSON Schema {keyword} at {path} must be a non-negative integer.");
            }
        }

        foreach (var keyword in new[] { "minimum", "maximum" })
        {
            if (schema.TryGetProperty(keyword, out var bound)
                && (bound.ValueKind != JsonValueKind.Number || !bound.TryGetDouble(out _)))
            {
                throw new ArgumentException($"JSON Schema {keyword} at {path} must be a number.");
            }
        }

        if (schema.TryGetProperty("additionalProperties", out var additionalProperties)
            && additionalProperties.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"JSON Schema additionalProperties at {path} must be a boolean.");
        }

        if (schema.TryGetProperty("minLength", out var minLength)
            && schema.TryGetProperty("maxLength", out var maxLength)
            && minLength.GetInt32() > maxLength.GetInt32())
        {
            throw new ArgumentException($"JSON Schema minLength exceeds maxLength at {path}.");
        }

        if (schema.TryGetProperty("minItems", out var minItems)
            && schema.TryGetProperty("maxItems", out var maxItems)
            && minItems.GetInt32() > maxItems.GetInt32())
        {
            throw new ArgumentException($"JSON Schema minItems exceeds maxItems at {path}.");
        }

        if (schema.TryGetProperty("minimum", out var minimum)
            && schema.TryGetProperty("maximum", out var maximum)
            && minimum.GetDouble() > maximum.GetDouble())
        {
            throw new ArgumentException($"JSON Schema minimum exceeds maximum at {path}.");
        }
    }

    private static void ValidateValue(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (depth > 32)
        {
            throw new InvalidDataException($"Structured model output exceeds the maximum JSON depth at {path}.");
        }

        var type = schema.GetProperty("type").GetString();
        var validType = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };
        if (!validType)
        {
            throw new InvalidDataException($"Structured model output at {path} must be of type '{type}'.");
        }

        if (schema.TryGetProperty("enum", out var enumValues)
            && !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            throw new InvalidDataException($"Structured model output at {path} is not an allowed enum value.");
        }

        if (type == "object")
        {
            ValidateObject(schema, value, path, depth);
        }
        else if (type == "array")
        {
            ValidateArray(schema, value, path, depth);
        }
        else if (type == "string")
        {
            ValidateString(schema, value.GetString()!, path);
        }
        else if (type is "integer" or "number")
        {
            ValidateNumber(schema, value.GetDouble(), path);
        }
    }

    private static void ValidateObject(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (!schema.TryGetProperty("properties", out var properties))
        {
            properties = default;
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var requiredProperty in required.EnumerateArray())
            {
                var name = requiredProperty.GetString()!;
                if (!value.TryGetProperty(name, out _))
                {
                    throw new InvalidDataException($"Structured model output is missing required property '{path}.{name}'.");
                }
            }
        }

        var allowAdditional = !schema.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind != JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateValue(propertySchema, property.Value, $"{path}.{property.Name}", depth + 1);
            }
            else if (!allowAdditional)
            {
                throw new InvalidDataException($"Structured model output contains unexpected property '{path}.{property.Name}'.");
            }
        }
    }

    private static void ValidateArray(JsonElement schema, JsonElement value, string path, int depth)
    {
        var length = value.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var minimum)
            && length < minimum.GetInt32())
        {
            throw new InvalidDataException($"Structured model output at {path} has too few items.");
        }

        if (schema.TryGetProperty("maxItems", out var maximum)
            && length > maximum.GetInt32())
        {
            throw new InvalidDataException($"Structured model output at {path} has too many items.");
        }

        if (schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateValue(itemSchema, item, $"{path}[{index}]", depth + 1);
                index++;
            }
        }
    }

    private static void ValidateString(JsonElement schema, string value, string path)
    {
        if (schema.TryGetProperty("minLength", out var minimum)
            && value.Length < minimum.GetInt32())
        {
            throw new InvalidDataException($"Structured model output at {path} is shorter than allowed.");
        }

        if (schema.TryGetProperty("maxLength", out var maximum)
            && value.Length > maximum.GetInt32())
        {
            throw new InvalidDataException($"Structured model output at {path} is longer than allowed.");
        }
    }

    private static void ValidateNumber(JsonElement schema, double value, string path)
    {
        if (schema.TryGetProperty("minimum", out var minimum)
            && value < minimum.GetDouble())
        {
            throw new InvalidDataException($"Structured model output at {path} is below the minimum.");
        }

        if (schema.TryGetProperty("maximum", out var maximum)
            && value > maximum.GetDouble())
        {
            throw new InvalidDataException($"Structured model output at {path} exceeds the maximum.");
        }
    }
}
