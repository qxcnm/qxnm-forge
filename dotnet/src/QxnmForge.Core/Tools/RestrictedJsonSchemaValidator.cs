using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QxnmForge.Tools;

/// <summary>
/// 功能：验证工具定义和调用参数使用 SPEC 允许的有界 JSON Schema 子集。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public static class RestrictedJsonSchemaValidator
{
    private const int MaxDepth = 32;
    private const int MaxNodes = 4096;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 功能：验证工具 inputSchema 本身只使用允许关键字、有限容器与锚定非回溯正则。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">待注册的 inputSchema。</param>
    /// <remarks>不变量：成功后 schema 不含远程引用、递归、格式解码或任意扩展关键字。</remarks>
    /// <exception cref="ArgumentException">schema 超界或不属于受限子集。</exception>
    public static void ValidateSchema(JsonElement schema)
    {
        var nodes = 0;
        ValidateSchemaNode(schema, 0, ref nodes, requireObjectRoot: true);
    }

    /// <summary>
    /// 功能：按已经验证的受限 schema 检查完整工具参数，拒绝缺字段、额外字段和越界值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">已注册的受限 inputSchema。</param>
    /// <param name="arguments">Provider 完整组装后的参数值。</param>
    /// <exception cref="ToolOperationException">参数不符合 schema；错误不回显参数正文。</exception>
    public static void ValidateArguments(JsonElement schema, JsonElement arguments)
    {
        try
        {
            var nodes = 0;
            ValidateValue(schema, arguments, 0, ref nodes);
        }
        catch (SchemaValueException exception)
        {
            throw new ToolOperationException(
                new QxnmForge.Domain.PortableError(
                    -32602,
                    "tool arguments did not satisfy the registered schema",
                    false,
                    new QxnmForge.Domain.ErrorDetails("tool_arguments_invalid", Field: exception.Field)),
                "validation_error");
        }
    }

    /// <summary>
    /// 功能：递归验证一个 schema node 的结构、关键字与有限约束。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">当前 schema node。</param>
    /// <param name="depth">当前递归深度。</param>
    /// <param name="nodes">已观察节点计数。</param>
    /// <param name="requireObjectRoot">是否要求根节点为 object schema。</param>
    private static void ValidateSchemaNode(
        JsonElement schema,
        int depth,
        ref int nodes,
        bool requireObjectRoot = false)
    {
        EnsureBudget(schema, depth, ref nodes, schemaMode: true);
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("tool schema node must be an object");
        }

        if (schema.TryGetProperty("oneOf", out var alternatives))
        {
            ValidateTaggedUnionSchema(schema, alternatives, depth, ref nodes);
            if (requireObjectRoot)
            {
                throw new ArgumentException("tool schema root must be an object schema");
            }

            return;
        }

        var type = RequireSchemaString(schema, "type");
        if (requireObjectRoot && type != "object")
        {
            throw new ArgumentException("tool schema root must have type object");
        }

        switch (type)
        {
            case "object":
                ValidateObjectSchema(schema, depth, ref nodes);
                break;
            case "array":
                ValidateArraySchema(schema, depth, ref nodes);
                break;
            case "string":
                ValidateStringSchema(schema);
                break;
            case "integer":
            case "number":
                ValidateNumberSchema(schema, type);
                break;
            case "boolean":
            case "null":
                EnsureOnlySchemaProperties(schema, "type", "title", "description", "const", "default");
                break;
            default:
                throw new ArgumentException("tool schema type is unsupported");
        }
    }

    /// <summary>
    /// 功能：验证 object schema 的 properties、required 与 additionalProperties:false。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">object schema。</param>
    /// <param name="depth">当前深度。</param>
    /// <param name="nodes">节点计数。</param>
    private static void ValidateObjectSchema(JsonElement schema, int depth, ref int nodes)
    {
        EnsureOnlySchemaProperties(
            schema,
            "type", "title", "description", "properties", "required", "additionalProperties",
            "minProperties", "maxProperties", "default");
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False ||
            properties.GetPropertyCount() > 256)
        {
            throw new ArgumentException("object schema must have bounded properties and additionalProperties false");
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (!IsPropertyName(property.Name))
            {
                throw new ArgumentException("tool schema property name is invalid");
            }

            ValidateSchemaNode(property.Value, depth + 1, ref nodes);
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredElement))
        {
            if (requiredElement.ValueKind != JsonValueKind.Array || requiredElement.GetArrayLength() > 256)
            {
                throw new ArgumentException("object schema required is invalid");
            }

            foreach (var item in requiredElement.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (name is null || !required.Add(name) || !properties.TryGetProperty(name, out _))
                {
                    throw new ArgumentException("object schema required is invalid");
                }
            }
        }

        ValidateNonNegativeBound(schema, "minProperties", 256);
        ValidateNonNegativeBound(schema, "maxProperties", 256);
    }

    /// <summary>
    /// 功能：验证 array schema 必须声明 items 与有限 maxItems。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">array schema。</param>
    /// <param name="depth">当前深度。</param>
    /// <param name="nodes">节点计数。</param>
    private static void ValidateArraySchema(JsonElement schema, int depth, ref int nodes)
    {
        EnsureOnlySchemaProperties(
            schema,
            "type", "title", "description", "items", "minItems", "maxItems", "uniqueItems", "default");
        if (!schema.TryGetProperty("items", out var items) ||
            !schema.TryGetProperty("maxItems", out var maximum) ||
            !maximum.TryGetInt32(out var maxItems) || maxItems is < 0 or > 10_000)
        {
            throw new ArgumentException("array schema must declare bounded maxItems");
        }

        ValidateNonNegativeBound(schema, "minItems", 10_000);
        if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException("array schema uniqueItems must be boolean");
        }

        ValidateSchemaNode(items, depth + 1, ref nodes);
    }

    /// <summary>
    /// 功能：验证 string schema 的长度、枚举、常量与锚定非回溯 pattern。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">string schema。</param>
    private static void ValidateStringSchema(JsonElement schema)
    {
        EnsureOnlySchemaProperties(
            schema,
            "type", "title", "description", "minLength", "maxLength", "pattern", "enum", "const", "default");
        ValidateNonNegativeBound(schema, "minLength", 1_048_576);
        ValidateNonNegativeBound(schema, "maxLength", 1_048_576);
        if (schema.TryGetProperty("pattern", out var pattern))
        {
            var value = pattern.ValueKind == JsonValueKind.String ? pattern.GetString() : null;
            if (value is null || value.Length > 4096 || !value.StartsWith('^') || !value.EndsWith('$'))
            {
                throw new ArgumentException("tool schema pattern must be bounded and anchored");
            }

            _ = CreateSafeRegex(value);
        }

        ValidateScalarEnum(schema, JsonValueKind.String);
    }

    /// <summary>
    /// 功能：验证 integer/number schema 的数值边界、multipleOf、枚举和常量类型。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">数值 schema。</param>
    /// <param name="type">integer 或 number。</param>
    private static void ValidateNumberSchema(JsonElement schema, string type)
    {
        EnsureOnlySchemaProperties(
            schema,
            "type", "title", "description", "minimum", "maximum", "exclusiveMinimum",
            "exclusiveMaximum", "multipleOf", "enum", "const", "default");
        var kind = type == "integer" ? JsonValueKind.Number : JsonValueKind.Number;
        ValidateScalarEnum(schema, kind);
        if (schema.TryGetProperty("multipleOf", out var multiple) &&
            (!multiple.TryGetDecimal(out var value) || value <= 0))
        {
            throw new ArgumentException("tool schema multipleOf must be positive");
        }
    }

    /// <summary>
    /// 功能：验证 oneOf 仅作为带必需字符串 discriminator 的对象联合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">tagged union schema。</param>
    /// <param name="alternatives">oneOf 数组。</param>
    /// <param name="depth">当前深度。</param>
    /// <param name="nodes">节点计数。</param>
    private static void ValidateTaggedUnionSchema(
        JsonElement schema,
        JsonElement alternatives,
        int depth,
        ref int nodes)
    {
        EnsureOnlySchemaProperties(schema, "title", "description", "oneOf", "x-discriminator");
        var discriminator = RequireSchemaString(schema, "x-discriminator");
        if (!IsPropertyName(discriminator) || alternatives.ValueKind != JsonValueKind.Array ||
            alternatives.GetArrayLength() is < 2 or > 32)
        {
            throw new ArgumentException("tagged union schema is invalid");
        }

        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alternative in alternatives.EnumerateArray())
        {
            ValidateSchemaNode(alternative, depth + 1, ref nodes);
            var properties = alternative.GetProperty("properties");
            if (!properties.TryGetProperty(discriminator, out var discriminatorSchema) ||
                !discriminatorSchema.TryGetProperty("const", out var tag) ||
                tag.ValueKind != JsonValueKind.String ||
                !tags.Add(tag.GetString()!) ||
                !alternative.TryGetProperty("required", out var required) ||
                !required.EnumerateArray().Any(item => item.GetString() == discriminator))
            {
                throw new ArgumentException("tagged union discriminator is invalid");
            }
        }
    }

    /// <summary>
    /// 功能：递归验证一个参数值与受限 schema node 的类型和约束。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">当前 schema。</param>
    /// <param name="value">当前参数值。</param>
    /// <param name="depth">当前深度。</param>
    /// <param name="nodes">参数节点计数。</param>
    private static void ValidateValue(JsonElement schema, JsonElement value, int depth, ref int nodes)
    {
        EnsureBudget(value, depth, ref nodes, schemaMode: false);
        if (schema.TryGetProperty("oneOf", out var alternatives))
        {
            var discriminator = schema.GetProperty("x-discriminator").GetString()!;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(discriminator, out var tag) || tag.ValueKind != JsonValueKind.String)
            {
                throw new SchemaValueException(discriminator);
            }

            var matches = 0;
            foreach (var alternative in alternatives.EnumerateArray())
            {
                var tagSchema = alternative.GetProperty("properties").GetProperty(discriminator);
                if (JsonElement.DeepEquals(tagSchema.GetProperty("const"), tag))
                {
                    ValidateValue(alternative, value, depth + 1, ref nodes);
                    matches++;
                }
            }

            if (matches != 1)
            {
                throw new SchemaValueException(discriminator);
            }

            return;
        }

        var type = schema.GetProperty("type").GetString();
        switch (type)
        {
            case "object":
                ValidateObjectValue(schema, value, depth, ref nodes);
                break;
            case "array":
                ValidateArrayValue(schema, value, depth, ref nodes);
                break;
            case "string":
                ValidateStringValue(schema, value);
                break;
            case "integer":
                ValidateNumericValue(schema, value, integer: true);
                break;
            case "number":
                ValidateNumericValue(schema, value, integer: false);
                break;
            case "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            case "null" when value.ValueKind == JsonValueKind.Null:
                ValidateConst(schema, value);
                break;
            default:
                throw new SchemaValueException("arguments");
        }
    }

    /// <summary>
    /// 功能：验证 object 参数的属性数、required、未知字段和每个子值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">object schema。</param>
    /// <param name="value">object 参数。</param>
    /// <param name="depth">当前深度。</param>
    /// <param name="nodes">节点计数。</param>
    private static void ValidateObjectValue(JsonElement schema, JsonElement value, int depth, ref int nodes)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new SchemaValueException("arguments");
        }

        var count = value.GetPropertyCount();
        ValidateCountBounds(schema, count, "minProperties", "maxProperties", "arguments");
        var properties = schema.GetProperty("properties");
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString()!;
                if (!value.TryGetProperty(name, out _))
                {
                    throw new SchemaValueException(name);
                }
            }
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out var childSchema))
            {
                throw new SchemaValueException(property.Name);
            }

            ValidateValue(childSchema, property.Value, depth + 1, ref nodes);
        }
    }

    /// <summary>
    /// 功能：验证 array 参数的长度、唯一性和逐项 schema。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">array schema。</param>
    /// <param name="value">array 参数。</param>
    /// <param name="depth">当前深度。</param>
    /// <param name="nodes">节点计数。</param>
    private static void ValidateArrayValue(JsonElement schema, JsonElement value, int depth, ref int nodes)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new SchemaValueException("arguments");
        }

        var count = value.GetArrayLength();
        ValidateCountBounds(schema, count, "minItems", "maxItems", "arguments");
        var unique = schema.TryGetProperty("uniqueItems", out var uniqueElement) && uniqueElement.GetBoolean()
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        foreach (var item in value.EnumerateArray())
        {
            if (unique is not null && !unique.Add(Convert.ToHexString(CanonicalJson.Serialize(item))))
            {
                throw new SchemaValueException("arguments");
            }

            ValidateValue(schema.GetProperty("items"), item, depth + 1, ref nodes);
        }
    }

    /// <summary>
    /// 功能：验证 string 参数的 Unicode 长度、pattern、enum 和 const。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">string schema。</param>
    /// <param name="value">string 参数。</param>
    private static void ValidateStringValue(JsonElement schema, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new SchemaValueException("arguments");
        }

        var text = value.GetString()!;
        var length = CountRunes(text);
        ValidateCountBounds(schema, length, "minLength", "maxLength", "arguments");
        if (schema.TryGetProperty("pattern", out var pattern) && !CreateSafeRegex(pattern.GetString()!).IsMatch(text))
        {
            throw new SchemaValueException("arguments");
        }

        ValidateEnumAndConst(schema, value);
    }

    /// <summary>
    /// 功能：验证 integer/number 参数的边界、multipleOf、enum 和 const。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">数值 schema。</param>
    /// <param name="value">数值参数。</param>
    /// <param name="integer">是否要求整数。</param>
    private static void ValidateNumericValue(JsonElement schema, JsonElement value, bool integer)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            (integer && !value.TryGetInt64(out _)) ||
            !value.TryGetDecimal(out var number))
        {
            throw new SchemaValueException("arguments");
        }

        if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDecimal() ||
            schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDecimal() ||
            schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum) && number <= exclusiveMinimum.GetDecimal() ||
            schema.TryGetProperty("exclusiveMaximum", out var exclusiveMaximum) && number >= exclusiveMaximum.GetDecimal() ||
            schema.TryGetProperty("multipleOf", out var multiple) && number % multiple.GetDecimal() != 0)
        {
            throw new SchemaValueException("arguments");
        }

        ValidateEnumAndConst(schema, value);
    }

    /// <summary>
    /// 功能：验证可选 enum 与 const 使用指定 JSON 标量类型且 enum 唯一有界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">标量 schema。</param>
    /// <param name="kind">预期 JSON 类型。</param>
    private static void ValidateScalarEnum(JsonElement schema, JsonValueKind kind)
    {
        if (schema.TryGetProperty("enum", out var values))
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 256)
            {
                throw new ArgumentException("tool schema enum is invalid");
            }

            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != kind || !unique.Add(value.GetRawText()))
                {
                    throw new ArgumentException("tool schema enum is invalid");
                }
            }
        }

        if (schema.TryGetProperty("const", out var constant) && constant.ValueKind != kind)
        {
            throw new ArgumentException("tool schema const type is invalid");
        }
    }

    /// <summary>
    /// 功能：检查参数是否属于 enum 且等于 const。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">标量 schema。</param>
    /// <param name="value">参数值。</param>
    private static void ValidateEnumAndConst(JsonElement schema, JsonElement value)
    {
        if (schema.TryGetProperty("enum", out var values) &&
            !values.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
        {
            throw new SchemaValueException("arguments");
        }

        ValidateConst(schema, value);
    }

    /// <summary>
    /// 功能：检查可选 const 与参数完全相等。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">任意标量 schema。</param>
    /// <param name="value">参数值。</param>
    private static void ValidateConst(JsonElement schema, JsonElement value)
    {
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
        {
            throw new SchemaValueException("arguments");
        }
    }

    /// <summary>
    /// 功能：验证可选最小/最大计数边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">含边界的 schema。</param>
    /// <param name="count">实际计数。</param>
    /// <param name="minimumName">最小关键字。</param>
    /// <param name="maximumName">最大关键字。</param>
    /// <param name="field">失败字段。</param>
    private static void ValidateCountBounds(
        JsonElement schema,
        int count,
        string minimumName,
        string maximumName,
        string field)
    {
        if (schema.TryGetProperty(minimumName, out var minimum) && count < minimum.GetInt32() ||
            schema.TryGetProperty(maximumName, out var maximum) && count > maximum.GetInt32())
        {
            throw new SchemaValueException(field);
        }
    }

    /// <summary>
    /// 功能：验证 schema 的非负整数边界值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">父 schema。</param>
    /// <param name="name">边界关键字。</param>
    /// <param name="maximum">允许最大值。</param>
    private static void ValidateNonNegativeBound(JsonElement schema, string name, int maximum)
    {
        if (schema.TryGetProperty(name, out var value) &&
            (!value.TryGetInt32(out var parsed) || parsed is < 0 || parsed > maximum))
        {
            throw new ArgumentException("tool schema bound is invalid");
        }
    }

    /// <summary>
    /// 功能：拒绝当前 schema 类型未列出的关键字。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">schema object。</param>
    /// <param name="allowed">允许关键字。</param>
    private static void EnsureOnlySchemaProperties(JsonElement schema, params string[] allowed)
    {
        foreach (var property in schema.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new ArgumentException("tool schema contains an unsupported keyword");
            }
        }
    }

    /// <summary>
    /// 功能：读取 schema 必需的非空字符串关键字。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="schema">schema object。</param>
    /// <param name="name">关键字。</param>
    /// <returns>非空字符串值。</returns>
    private static string RequireSchemaString(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()))
        {
            throw new ArgumentException("tool schema is missing a required string keyword");
        }

        return value.GetString()!;
    }

    /// <summary>
    /// 功能：限制 schema/参数递归深度和总节点数以抵抗资源耗尽。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="element">当前节点。</param>
    /// <param name="depth">当前深度。</param>
    /// <param name="nodes">节点计数。</param>
    /// <param name="schemaMode">失败时是否抛 schema 配置异常。</param>
    private static void EnsureBudget(JsonElement element, int depth, ref int nodes, bool schemaMode)
    {
        _ = element;
        nodes++;
        if (depth > MaxDepth || nodes > MaxNodes)
        {
            if (schemaMode)
            {
                throw new ArgumentException("tool schema exceeds complexity limits");
            }

            throw new SchemaValueException("arguments");
        }
    }

    /// <summary>
    /// 功能：验证 JSON Schema 属性名使用可移植受限字符集。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="name">属性名。</param>
    /// <returns>长度与字符均有效时为 true。</returns>
    private static bool IsPropertyName(string name)
    {
        return name.Length is >= 1 and <= 128 &&
            (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
            name.Skip(1).All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
    }

    /// <summary>
    /// 功能：创建带超时、文化无关且禁止回溯的正则表达式。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="pattern">已要求锚定的 schema pattern。</param>
    /// <returns>安全 Regex。</returns>
    private static Regex CreateSafeRegex(string pattern)
    {
        try
        {
            return new Regex(
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                PatternTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("tool schema pattern is not supported", exception);
        }
    }

    /// <summary>
    /// 功能：按 Unicode scalar value 统计字符串长度。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待统计字符串。</param>
    /// <returns>Unicode rune 数。</returns>
    private static int CountRunes(string value)
    {
        var count = 0;
        foreach (var unused in value.EnumerateRunes())
        {
            _ = unused;
            count++;
        }

        return count;
    }

    /// <summary>
    /// 功能：表示参数值违反已注册 schema，且只保留安全字段名。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    private sealed class SchemaValueException : Exception
    {
        /// <summary>
        /// 功能：创建仅携带 schema 字段名的内部验证异常。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="field">安全字段名。</param>
        internal SchemaValueException(string field)
        {
            Field = field;
        }

        /// <summary>
        /// 功能：取得可进入 ErrorDetails.field 的 schema 字段名。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        internal string Field { get; }
    }
}
