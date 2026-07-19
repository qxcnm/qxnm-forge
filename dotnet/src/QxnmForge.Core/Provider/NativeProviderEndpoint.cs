using System.Text;

namespace QxnmForge.Provider;

/// <summary>
/// 功能：为原生 Provider family 构造同源、有界且不可路径逃逸的 request-target。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class NativeProviderEndpoint
{
    /// <summary>
    /// 功能：在已验证 base path 后追加原生后缀和经编码的唯一查询参数。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="baseEndpoint">无 user-info、query 或 fragment 的绝对 base URI。</param>
    /// <param name="suffix">以单斜杠开头且不含转义/穿越控制字符的 family 路径。</param>
    /// <param name="query">可选、键唯一的固定查询参数。</param>
    /// <returns>保持 base scheme、host 和 port 的新 URI。</returns>
    /// <remarks>不变量：本方法不修改输入 URI，模型或版本不能改变 authority。</remarks>
    /// <exception cref="ArgumentException">base、后缀、query 或结果 origin 不符合安全边界。</exception>
    internal static Uri Append(
        Uri baseEndpoint,
        string suffix,
        IReadOnlyDictionary<string, string>? query = null)
    {
        ArgumentNullException.ThrowIfNull(baseEndpoint);
        if (!baseEndpoint.IsAbsoluteUri ||
            !string.IsNullOrEmpty(baseEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(baseEndpoint.Query) ||
            !string.IsNullOrEmpty(baseEndpoint.Fragment) ||
            suffix.Length == 0 ||
            suffix[0] != '/' ||
            (suffix.Length > 1 && suffix[1] == '/') ||
            suffix.Contains("..", StringComparison.Ordinal) ||
            suffix.Contains('%') ||
            suffix.Contains('\\') ||
            suffix.Contains('?') ||
            suffix.Contains('#') ||
            suffix.Contains('\0'))
        {
            throw new ArgumentException("provider native endpoint is invalid");
        }

        var builder = new UriBuilder(baseEndpoint)
        {
            Path = baseEndpoint.AbsolutePath.TrimEnd('/') + suffix,
            Query = EncodeQuery(query),
            Fragment = string.Empty,
        };
        var result = builder.Uri;
        if (!result.Scheme.Equals(baseEndpoint.Scheme, StringComparison.Ordinal) ||
            !result.IdnHost.Equals(baseEndpoint.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            result.Port != baseEndpoint.Port ||
            !string.IsNullOrEmpty(result.UserInfo))
        {
            throw new ArgumentException("provider native endpoint changed origin");
        }

        return result;
    }

    /// <summary>
    /// 功能：验证 Google 模型 ID 可安全作为单一路径 segment。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">调用方选择的 Google 模型 ID。</param>
    /// <returns>长度 1..256 且只含 ASCII 字母、数字、点、下划线或连字符时为 true。</returns>
    /// <remarks>不变量：合法值不能引入 path、query、fragment 或 authority。</remarks>
    internal static bool IsSafeModelSegment(string value)
    {
        return IsSafeResourceSegment(value, 256);
    }

    /// <summary>
    /// 功能：验证云 Provider 项目、location 或模型值可安全作为单一路径段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待验证的资源标识。</param>
    /// <returns>长度 1..128 且只含 ASCII 字母、数字、点、下划线或连字符时为 true。</returns>
    /// <remarks>不变量：合法值不能引入 path/query/fragment/authority 边界。</remarks>
    internal static bool IsSafeResourceSegment(string value)
    {
        return IsSafeResourceSegment(value, 128);
    }

    /// <summary>
    /// 功能：以显式最大长度校验一个 ASCII 云资源路径段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">待验证值。</param>
    /// <param name="maximumLength">允许的闭区间最大长度。</param>
    /// <returns>完全符合 allowlist 时为 true。</returns>
    private static bool IsSafeResourceSegment(string value, int maximumLength)
    {
        return value.Length >= 1 && value.Length <= maximumLength && value.All(static character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '.' or '_' or '-');
    }

    /// <summary>
    /// 功能：验证 Azure api-version 可安全进入单一 query 值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">运行时显式版本或默认 v1。</param>
    /// <returns>长度 1..64 且只含 ASCII 字母、数字、点或连字符时为 true。</returns>
    /// <remarks>不变量：合法值不能追加查询键或 fragment。</remarks>
    internal static bool IsSafeApiVersion(string value)
    {
        return value.Length is >= 1 and <= 64 && value.All(static character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '.' or '-');
    }

    /// <summary>
    /// 功能：按 ordinal 键顺序把固定查询参数编码为 RFC 3986 兼容文本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="query">可选查询参数；键和值均不得为空或包含控制字符。</param>
    /// <returns>不含前导问号的查询文本。</returns>
    /// <exception cref="ArgumentException">发现空键值或 CR/LF/NUL。</exception>
    private static string EncodeQuery(IReadOnlyDictionary<string, string>? query)
    {
        if (query is null || query.Count == 0)
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        foreach (var pair in query.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(pair.Key) ||
                string.IsNullOrEmpty(pair.Value) ||
                pair.Key.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
                pair.Value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                throw new ArgumentException("provider native query is invalid");
            }

            if (result.Length > 0)
            {
                result.Append('&');
            }

            result.Append(Uri.EscapeDataString(pair.Key));
            result.Append('=');
            result.Append(Uri.EscapeDataString(pair.Value));
        }

        return result.ToString();
    }
}
