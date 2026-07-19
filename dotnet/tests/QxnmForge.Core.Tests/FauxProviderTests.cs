using QxnmForge.Domain;
using QxnmForge.Provider;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：验证 faux Provider 的确定性原生异步流。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class FauxProviderTests
{
    /// <summary>
    /// 功能：确认多个文本步骤按源顺序输出，并在末尾报告场景用量且不访问网络。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task StreamAsyncEmitsTextInScenarioOrder()
    {
        var provider = new FauxProvider();
        var scenario = new FauxScenario(
            "0.1",
            "ordered",
            7,
            [new FauxTextStep("你"), new FauxTextStep("好")],
            new Usage(1, 2, 3));

        var observed = new List<string>();
        Usage? observedUsage = null;
        await foreach (var signal in provider.StreamAsync(scenario, CancellationToken.None))
        {
            if (signal is ProviderTextSignal text)
            {
                observed.Add(text.Text);
            }
            else
            {
                observedUsage = Assert.IsType<ProviderUsageSignal>(signal).Usage;
            }
        }

        Assert.Equal(["你", "好"], observed);
        Assert.Equal(new Usage(1, 2, 3), observedUsage);
    }

    /// <summary>
    /// 功能：确认 error 步骤保留 portable error，而非要求客户端解析异常文本。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task StreamAsyncThrowsStructuredScenarioError()
    {
        var expected = new PortableError(
            -32005,
            "synthetic provider failure",
            true,
            new ErrorDetails("provider_unavailable"));
        var scenario = new FauxScenario("0.1", "failure", 0, [new FauxErrorStep(expected)]);
        var provider = new FauxProvider();

        var exception = await Assert.ThrowsAsync<FauxProviderException>(async () =>
        {
            await foreach (var unused in provider.StreamAsync(scenario, CancellationToken.None))
            {
                _ = unused;
            }
        });

        Assert.Equal(expected, exception.Error);
    }
}
