using System.Net.Http.Headers;

namespace MingSim.Agents.Providers;

/// <summary>
/// 模型 API 密钥来源：只从环境变量读取，创建已带 Bearer 认证头的 HttpClient 后即丢弃密钥。
/// </summary>
/// <remarks>
/// 密钥红线（doc 07 §15）：密钥只存在于调用栈的本地变量里，本类不保存、
/// 不提供读回、不进入日志/审计/存档/快照；认证头由 HttpClient 持有，
/// 它不会被序列化进任何游戏状态。组合根负责持有并释放返回的 HttpClient。
/// </remarks>
public sealed class ModelKeySource
{
    /// <summary>读取密钥的环境变量名；密钥只允许来自这个来源。</summary>
    public const string EnvironmentVariableName = "MINGSIM_LLM_API_KEY";

    private readonly Func<string?> _reader;

    /// <summary>可注入环境变量读取器（默认读进程环境变量），便于测试且不依赖真实环境。</summary>
    public ModelKeySource(Func<string?>? environmentVariableReader = null)
    {
        _reader = environmentVariableReader ?? (() => Environment.GetEnvironmentVariable(EnvironmentVariableName));
    }

    /// <summary>
    /// 从环境变量读取密钥并创建已配置 Bearer 认证头的 HttpClient。
    /// </summary>
    /// <exception cref="InvalidOperationException">环境变量未配置或为空时抛出。</exception>
    /// <exception cref="ArgumentException">baseAddress 不是安全的绝对 HTTP(S) URI 时抛出。</exception>
    public HttpClient CreateKeyedHttpClient(string baseAddress)
    {
        var key = _reader();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"模型密钥未配置：环境变量 {EnvironmentVariableName} 为空。请先设置环境变量再启动游戏。");
        }

        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "baseAddress 必须是绝对 HTTP/HTTPS URI，且不含 userinfo/query/fragment。",
                nameof(baseAddress));
        }

        var client = new HttpClient { BaseAddress = uri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }
}
