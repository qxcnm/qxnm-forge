using System.Buffers.Binary;
using System.Text.Json;
using QxnmForge.Agent;
using QxnmForge.Domain;
using QxnmForge.Provider;
using QxnmForge.Session;
using QxnmForge.Tools;

namespace QxnmForge.Tests;

/// <summary>
/// 功能：仅在显式原生 X11 双门环境发现实机桌面测试。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class ExplicitX11FactAttribute : FactAttribute
{
    /// <summary>
    /// 功能：缺少原生 X11 正向宿主证明时在测试发现阶段明确跳过。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    public ExplicitX11FactAttribute()
    {
        if (!OperatingSystem.IsLinux() ||
            !DesktopComputer.IsHostEnvironmentEnabled(
                Environment.GetEnvironmentVariable(DesktopComputer.EnableEnvironmentName),
                Environment.GetEnvironmentVariable(DesktopComputer.ExperimentalEnableEnvironmentName),
                Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
                Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")) ||
            !DesktopComputer.IsLocalDisplay(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Skip =
                "requires Linux, both desktop gates, native X11 session and a local DISPLAY";
        }
    }
}

/// <summary>
/// 功能：验证显式桌面宿主门下的 computer 能力、参数边界、PNG 捕获与 Session 持久化。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
public sealed class DesktopComputerTests
{
    /// <summary>
    /// 功能：确认双门缺失或存在 Wayland/XWayland 证据时 fail closed。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="enableGate">稳定桌面门原始值。</param>
    /// <param name="experimentalGate">实验桌面门原始值。</param>
    /// <param name="waylandDisplay">WAYLAND_DISPLAY 原始值。</param>
    /// <param name="sessionType">XDG_SESSION_TYPE 原始值。</param>
    /// <param name="expected">期望是否允许继续 X11 探测。</param>
    [Theory]
    [InlineData("1", "1", null, null, false)]
    [InlineData("1", "1", null, "x11", true)]
    [InlineData("1", "1", null, " X11 ", true)]
    [InlineData("1", "1", null, "\u00A0x11\u00A0", false)]
    [InlineData("1", "1", null, "x1\uFF11", false)]
    [InlineData("1", "1", null, "", false)]
    [InlineData("1", "1", " ", "x11", false)]
    [InlineData(null, "1", null, "x11", false)]
    [InlineData("1", null, null, "x11", false)]
    [InlineData("true", "1", null, "x11", false)]
    [InlineData("1", "1", "wayland-0", "x11", false)]
    [InlineData("1", "1", null, "wayland", false)]
    [InlineData("1", "1", null, "XWayland", false)]
    public void HostEnvironmentRequiresBothGatesAndRejectsWayland(
        string? enableGate,
        string? experimentalGate,
        string? waylandDisplay,
        string? sessionType,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopComputer.IsHostEnvironmentEnabled(
                enableGate,
                experimentalGate,
                waylandDisplay,
                sessionType));
    }

    /// <summary>
    /// 功能：确认仅接受有界本机 Unix DISPLAY，并拒绝 hostname、TCP 与 SSH forwarding 形式。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">未经信任的 DISPLAY 原始值。</param>
    /// <param name="expected">是否属于允许的本机 Unix 形状。</param>
    [Theory]
    [InlineData(":0", true)]
    [InlineData(":0.0", true)]
    [InlineData(":99", true)]
    [InlineData(":65535.65535", true)]
    [InlineData("unix/:0", true)]
    [InlineData("unix/:99.1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("0", false)]
    [InlineData("unix:0", false)]
    [InlineData("localhost:10.0", false)]
    [InlineData("hostname:0", false)]
    [InlineData("127.0.0.1:0", false)]
    [InlineData("tcp/localhost:0", false)]
    [InlineData("tcp/:0", false)]
    [InlineData(":65536", false)]
    [InlineData(":0.65536", false)]
    [InlineData("unix/:", false)]
    [InlineData(":", false)]
    [InlineData(":0.", false)]
    [InlineData(":0.0.0", false)]
    [InlineData(":-1", false)]
    [InlineData(":０", false)]
    [InlineData(":0 ", false)]
    public void LocalDisplayRejectsRemoteAndAmbiguousTransports(string? display, bool expected)
    {
        Assert.Equal(expected, DesktopComputer.IsLocalDisplay(display));
    }

    /// <summary>
    /// 功能：确认所有允许输入都重建为显式 Unix transport，禁止 Xlib TCP fallback。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">允许或拒绝的 DISPLAY。</param>
    /// <param name="expected">期望的显式 Unix transport；null 表示拒绝。</param>
    [Theory]
    [InlineData(":0", "unix/:0")]
    [InlineData(":99.1", "unix/:99.1")]
    [InlineData("unix/:7", "unix/:7")]
    [InlineData("localhost:10.0", null)]
    [InlineData("unix:0", null)]
    public void LocalDisplayNormalizationForcesUnixTransport(string display, string? expected)
    {
        Assert.Equal(expected, DesktopComputer.NormalizeLocalDisplay(display));
    }

    /// <summary>
    /// 功能：确认 DISPLAY 总长 64 的纯本机形式可接受，而 65 字节立即失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void LocalDisplayEnforcesFrozenLengthBoundary()
    {
        var boundary = "unix/:" + new string('0', 58);
        var overLimit = "unix/:" + new string('0', 59);

        Assert.Equal(64, boundary.Length);
        Assert.NotNull(DesktopComputer.NormalizeLocalDisplay(boundary));
        Assert.Equal(65, overLimit.Length);
        Assert.Null(DesktopComputer.NormalizeLocalDisplay(overLimit));
    }

    /// <summary>
    /// 功能：确认 computer.interact 的 tagged union 要求动作专属字段并拒绝已移除的 text/单字符键。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void InteractionSchemaAndParserEnforceClosedActionSet()
    {
        var definition = Assert.Single(
            ToolRegistry.CreateDefinitions(new DesktopComputerCapability(InteractionAvailable: true)),
            static item => item.Name == "computer.interact");
        RestrictedJsonSchemaValidator.ValidateSchema(definition.InputSchema);

        using var move = JsonDocument.Parse("""{"action":"move","x":0,"y":16383}""");
        RestrictedJsonSchemaValidator.ValidateArguments(definition.InputSchema, move.RootElement);
        Assert.Equal(DesktopComputerActionKind.Move, DesktopComputer.ParseAction(move.RootElement).Kind);

        using var key = JsonDocument.Parse(
            """{"action":"key","key":"page_down","modifiers":["control"]}""");
        RestrictedJsonSchemaValidator.ValidateArguments(definition.InputSchema, key.RootElement);
        Assert.Equal(DesktopComputerActionKind.Key, DesktopComputer.ParseAction(key.RootElement).Kind);

        using var missingCoordinate = JsonDocument.Parse("""{"action":"move","x":1}""");
        Assert.Throws<ToolOperationException>(
            () => RestrictedJsonSchemaValidator.ValidateArguments(
                definition.InputSchema,
                missingCoordinate.RootElement));

        using var outOfBounds = JsonDocument.Parse("""{"action":"move","x":16384,"y":0}""");
        Assert.Throws<ToolOperationException>(
            () => RestrictedJsonSchemaValidator.ValidateArguments(
                definition.InputSchema,
                outOfBounds.RootElement));
        Assert.Throws<ToolOperationException>(() => DesktopComputer.ParseAction(outOfBounds.RootElement));

        using var removedText = JsonDocument.Parse("""{"action":"text","text":"abc\u0000"}""");
        Assert.Throws<ToolOperationException>(
            () => RestrictedJsonSchemaValidator.ValidateArguments(
                definition.InputSchema,
                removedText.RootElement));
        Assert.Throws<ToolOperationException>(() => DesktopComputer.ParseAction(removedText.RootElement));

        using var arbitraryKey = JsonDocument.Parse(
            """{"action":"key","key":"a","modifiers":[]}""");
        Assert.Throws<ToolOperationException>(
            () => RestrictedJsonSchemaValidator.ValidateArguments(
                definition.InputSchema,
                arbitraryKey.RootElement));
        Assert.Throws<ToolOperationException>(() => DesktopComputer.ParseAction(arbitraryKey.RootElement));

        using var zeroScroll = JsonDocument.Parse(
            """{"action":"scroll","deltaX":0,"deltaY":0}""");
        RestrictedJsonSchemaValidator.ValidateArguments(definition.InputSchema, zeroScroll.RootElement);
        Assert.Equal(
            DesktopComputerActionKind.Scroll,
            DesktopComputer.ParseAction(zeroScroll.RootElement).Kind);
    }

    /// <summary>
    /// 功能：确认捕获几何同时受维度、像素与 64 MiB 预计 raw 上限约束。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CaptureGeometryEnforcesDimensionPixelAndRawBounds()
    {
        Assert.True(DesktopComputer.IsCaptureGeometrySafe(4096, 4096));
        Assert.True(DesktopComputer.IsCaptureGeometrySafe(16_384, 1024));
        Assert.False(DesktopComputer.IsCaptureGeometrySafe(16_384, 1025));
        Assert.False(DesktopComputer.IsCaptureGeometrySafe(16_385, 1));
        Assert.False(DesktopComputer.IsCaptureGeometrySafe(0, 1024));
        Assert.False(DesktopComputer.IsCaptureGeometrySafe(int.MaxValue, int.MaxValue));
    }

    /// <summary>
    /// 功能：确认 XImage stride 按 pixmap pad 向上对齐，并冻结精确总长度边界。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CaptureLayoutCalculatesPaddedStrideAndFrozenLength()
    {
        Assert.Equal(
            new DesktopCaptureLayout(24, 24, 8, 3, 6),
            DesktopComputer.CalculateCaptureLayout(1, 2, 24, 24, 8));
        Assert.Equal(
            new DesktopCaptureLayout(24, 24, 32, 4, 8),
            DesktopComputer.CalculateCaptureLayout(1, 2, 24, 24, 32));
        Assert.Equal(
            new DesktopCaptureLayout(24, 24, 32, 12, 24),
            DesktopComputer.CalculateCaptureLayout(3, 2, 24, 24, 32));
        Assert.Equal(
            new DesktopCaptureLayout(24, 32, 32, 65_536, 67_108_864),
            DesktopComputer.CalculateCaptureLayout(16_384, 1024, 24, 32, 32));
    }

    /// <summary>
    /// 功能：确认 XImage 布局对非法几何、depth、像素位数与对齐全部失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="width">测试宽度。</param>
    /// <param name="height">测试高度。</param>
    /// <param name="depth">测试 depth。</param>
    /// <param name="bitsPerPixel">测试像素位数。</param>
    /// <param name="bitmapPad">测试扫描线对齐。</param>
    [Theory]
    [InlineData(0, 1, 24, 24, 8)]
    [InlineData(1, 0, 24, 24, 8)]
    [InlineData(16_384, 1025, 24, 32, 32)]
    [InlineData(1, 1, 0, 24, 8)]
    [InlineData(1, 1, 25, 24, 8)]
    [InlineData(1, 1, 24, 16, 8)]
    [InlineData(1, 1, 24, 24, 64)]
    public void CaptureLayoutRejectsInvalidInputs(
        int width,
        int height,
        int depth,
        int bitsPerPixel,
        int bitmapPad)
    {
        Assert.Throws<DesktopComputerException>(
            () => DesktopComputer.CalculateCaptureLayout(
                width,
                height,
                depth,
                bitsPerPixel,
                bitmapPad));
    }

    /// <summary>
    /// 功能：确认捕获 decoder 只接受互斥、连续且位于像素宽度内的 RGB mask。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CaptureColorMasksRequireSupportedTrueColorLayout()
    {
        Assert.True(DesktopComputer.AreColorMasksSafe(0xff0000, 0x00ff00, 0x0000ff, 24));
        Assert.True(DesktopComputer.AreColorMasksSafe(0xff0000, 0x00ff00, 0x0000ff, 32));
        Assert.False(DesktopComputer.AreColorMasksSafe(0, 0x00ff00, 0x0000ff, 32));
        Assert.False(DesktopComputer.AreColorMasksSafe(0xff0000, 0xff0000, 0x0000ff, 32));
        Assert.False(DesktopComputer.AreColorMasksSafe(0x00f500, 0xff0000, 0x0000ff, 32));
        Assert.False(DesktopComputer.AreColorMasksSafe(0xff000000, 0x00ff00, 0x0000ff, 24));
        Assert.False(DesktopComputer.AreColorMasksSafe(0xff0000, 0x00ff00, 0x0000ff, 16));
    }

    /// <summary>
    /// 功能：确认执行期 XImage 必须逐字段匹配冻结的 root format 与 visual masks。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void CapturedImageLayoutMustExactlyMatchFrozenRootVisual()
    {
        var layout = DesktopComputer.CalculateCaptureLayout(1, 2, 24, 24, 32) with
        {
            RedMask = 0xff0000,
            GreenMask = 0x00ff00,
            BlueMask = 0x0000ff,
        };
        Assert.True(DesktopComputer.IsCapturedImageLayoutExact(
            layout, 1, 2, 1, 2, 0, 2, 24, 32, 24, 4, 0xff0000, 0x00ff00, 0x0000ff));
        var mismatches = new (
            int Width,
            int Height,
            int XOffset,
            int Format,
            int Depth,
            int Pad,
            int Bits,
            int Stride,
            nuint Red,
            nuint Green,
            nuint Blue)[]
        {
            (2, 2, 0, 2, 24, 32, 24, 4, 0xff0000, 0x00ff00, 0x0000ff),
            (1, 1, 0, 2, 24, 32, 24, 4, 0xff0000, 0x00ff00, 0x0000ff),
            (1, 2, 1, 2, 24, 32, 24, 4, 0xff0000, 0x00ff00, 0x0000ff),
            (1, 2, 0, 1, 24, 32, 24, 4, 0xff0000, 0x00ff00, 0x0000ff),
            (1, 2, 0, 2, 32, 32, 24, 4, 0xff0000, 0x00ff00, 0x0000ff),
            (1, 2, 0, 2, 24, 16, 24, 4, 0xff0000, 0x00ff00, 0x0000ff),
            (1, 2, 0, 2, 24, 32, 32, 4, 0xff0000, 0x00ff00, 0x0000ff),
            (1, 2, 0, 2, 24, 32, 24, 8, 0xff0000, 0x00ff00, 0x0000ff),
            (1, 2, 0, 2, 24, 32, 24, 4, 0x0000ff, 0x00ff00, 0xff0000),
        };
        foreach (var mismatch in mismatches)
        {
            Assert.False(DesktopComputer.IsCapturedImageLayoutExact(
                layout,
                1,
                2,
                mismatch.Width,
                mismatch.Height,
                mismatch.XOffset,
                mismatch.Format,
                mismatch.Depth,
                mismatch.Pad,
                mismatch.Bits,
                mismatch.Stride,
                mismatch.Red,
                mismatch.Green,
                mismatch.Blue));
        }
    }

    /// <summary>
    /// 功能：确认 scroll/key 也要求有效 root 几何，而鼠标动作还要求坐标在界内。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void RuntimeGeometryAppliesToEveryActionKind()
    {
        var scroll = new DesktopComputerAction(DesktopComputerActionKind.Scroll, DeltaY: 1);
        var key = new DesktopComputerAction(
            DesktopComputerActionKind.Key,
            Key: "enter",
            Modifiers: []);
        var move = new DesktopComputerAction(DesktopComputerActionKind.Move, X: 1, Y: 0);
        var click = new DesktopComputerAction(
            DesktopComputerActionKind.Click,
            X: 0,
            Y: 1,
            Button: 1,
            Clicks: 1);

        Assert.False(DesktopComputer.IsRuntimeGeometrySafe(0, 1, scroll));
        Assert.False(DesktopComputer.IsRuntimeGeometrySafe(1, 0, key));
        Assert.False(DesktopComputer.IsRuntimeGeometrySafe(16_385, 1, scroll));
        Assert.True(DesktopComputer.IsRuntimeGeometrySafe(1, 1, scroll));
        Assert.True(DesktopComputer.IsRuntimeGeometrySafe(1, 1, key));
        Assert.False(DesktopComputer.IsRuntimeGeometrySafe(1, 1, move));
        Assert.False(DesktopComputer.IsRuntimeGeometrySafe(1, 1, click));
    }

    /// <summary>
    /// 功能：确认可移植 keysym 只接受当前 keycode 的 level 0 或 level 1。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void PortableKeysymMustBeAtLevelZeroOrOne()
    {
        Assert.Equal(0, DesktopComputer.PortableKeysymLevel(42, 42, 7));
        Assert.Equal(1, DesktopComputer.PortableKeysymLevel(42, 7, 42));
        Assert.Equal(-1, DesktopComputer.PortableKeysymLevel(42, 7, 8));
        Assert.Equal(-1, DesktopComputer.PortableKeysymLevel(0, 0, 0));
    }

    /// <summary>
    /// 功能：确认修饰键 keycode 不得重复、为零或与目标键冲突。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void KeycodesRequireDistinctTargetAndModifiers()
    {
        Assert.True(DesktopComputer.AreKeycodesDistinct(1, [2, 3, 4]));
        Assert.False(DesktopComputer.AreKeycodesDistinct(1, [2, 2]));
        Assert.False(DesktopComputer.AreKeycodesDistinct(1, [2, 1]));
        Assert.False(DesktopComputer.AreKeycodesDistinct(1, [0]));
        Assert.False(DesktopComputer.AreKeycodesDistinct(0, []));
    }

    /// <summary>
    /// 功能：确认 PNG IHDR 的长度、类型、全部字段与 CRC 均为精确规范值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void PngIhdrIsFullyInitializedAndHasExactCrc()
    {
        var png = DesktopComputer.EncodePng([0, 0, 0], 1, 1, CancellationToken.None);

        Assert.Equal(13U, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)));
        Assert.True(png.AsSpan(12, 4).SequenceEqual("IHDR"u8));
        Assert.True(png.AsSpan(16, 13).SequenceEqual(
            new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0 }));
        Assert.Equal(0x907753deU, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(29, 4)));
    }

    /// <summary>
    /// 功能：确认 PNG 压缩输出在写入与扩容前拒绝超过容器预算的字节。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void PngCompressionBufferRejectsBeforeExceedingLimit()
    {
        using var stream = new DesktopComputer.BoundedMemoryStream(4, 2);
        stream.Write([1, 2], 0, 2);
        stream.Write(new ReadOnlySpan<byte>([3]));
        stream.WriteByte(4);

        Assert.Equal(4, stream.Length);
        Assert.Equal(4, stream.Capacity);
        Assert.Throws<IOException>(() => stream.WriteByte(5));
        Assert.Equal(4, stream.Length);
        Assert.Equal(4, stream.Capacity);
    }

    /// <summary>
    /// 功能：确认滚动补偿只释放本动作实际使用的非零轴按钮。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void ScrollCompensationUsesOnlyActionButtons()
    {
        Assert.Empty(DesktopComputer.ScrollReleaseButtons(0, 0));
        Assert.Equal([4U], DesktopComputer.ScrollReleaseButtons(0, 1));
        Assert.Equal([5U], DesktopComputer.ScrollReleaseButtons(0, -1));
        Assert.Equal([6U], DesktopComputer.ScrollReleaseButtons(1, 0));
        Assert.Equal([7U], DesktopComputer.ScrollReleaseButtons(-1, 0));
        Assert.Equal([5U, 6U], DesktopComputer.ScrollReleaseButtons(1, -1));
    }

    /// <summary>
    /// 功能：确认缺少正向 X11 证明的冻结能力不会广告任何 computer 工具。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    [Fact]
    public void MissingPositiveX11ProofDoesNotAdvertiseComputerTools()
    {
        Assert.False(DesktopComputer.IsHostEnvironmentEnabled("1", "1", null, null));
        var names = ToolRegistry.CreateDefinitions(desktopComputer: null)
            .Select(static definition => definition.Name)
            .ToArray();
        Assert.DoesNotContain("computer.observe", names);
        Assert.DoesNotContain("computer.screenshot", names);
        Assert.DoesNotContain("computer.interact", names);
    }

    /// <summary>
    /// 功能：在显式原生 X11 双门环境捕获并 durable 发布真实 PNG。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    /// <remarks>未配置实机环境时由 xUnit 明确记为 skipped；测试从不执行鼠标或键盘动作。</remarks>
    [ExplicitX11Fact]
    public async Task ExplicitX11BackendCapturesDurablePngArtifact()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(Path.Combine(temporary.Path, "sessions"), workspace);
        var capability = DesktopComputer.Detect();
        Assert.NotNull(capability);
        var service = new AgentService(repository, new FauxProvider());
        await repository.ConfigureFauxAsync(
            "computer-session",
            new FauxScenario("0.1", "computer-test", 0, [new FauxTextStep("unused")]),
            CancellationToken.None);
        var run = await service.AcceptAsync(
            "computer-session",
            new InputMessage("user", [new TextContent("capture")]),
            new ProviderSelection("faux", "faux-v1"),
            CancellationToken.None);
        using var registry = new ToolRegistry(
            workspace,
            cliConformance: false,
            environmentConformance: false,
            repository);
        Assert.Contains("computer.observe", registry.Names);
        Assert.Contains("computer.screenshot", registry.Names);
        if (capability.InteractionAvailable)
        {
            Assert.Contains("computer.interact", registry.Names);
        }
        else
        {
            Assert.DoesNotContain("computer.interact", registry.Names);
        }

        using var screenshotDocument = JsonDocument.Parse("{}");
        var screenshot = registry.Prepare("computer.screenshot", screenshotDocument.RootElement);
        Assert.Equal(new ApprovalResource("other", "desktop:screen"), Assert.Single(screenshot.Resources));
        if (capability.InteractionAvailable)
        {
            using var moveDocument = JsonDocument.Parse("""{"action":"move","x":0,"y":0}""");
            var move = registry.Prepare("computer.interact", moveDocument.RootElement);
            Assert.Equal(new ApprovalResource("other", "desktop:move"), Assert.Single(move.Resources));
        }

        var result = await registry.ExecuteAsync(
            screenshot,
            "computer-session",
            run.RunId,
            CancellationToken.None);

        Assert.False(result.IsError);
        var image = Assert.Single(result.Content.OfType<ImageReferenceContent>());
        var artifact = Assert.IsType<ArtifactReference>(image.Artifact);
        var computer = artifact.Extensions!.Value
            .GetProperty("org.agentprotocol.computer");
        Assert.Equal("desktop_capture", computer.GetProperty("source").GetString());
        Assert.Equal(run.RunId, computer.GetProperty("runId").GetString());
        Assert.Equal("desktop_sensitive", computer.GetProperty("sensitivity").GetString());
        Assert.Equal("session_lifecycle", computer.GetProperty("retention").GetString());
        var bytes = await SessionArtifactStore.ReadImageAsync(
            run.Runtime.Journal.DirectoryPath,
            artifact,
            DesktopComputer.MaxPngBytes,
            CancellationToken.None);
        Assert.True(bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
    }

    /// <summary>
    /// 功能：确认 active-run 发布移除非法顶层 runId，并在引用与记录中写入统一敏感扩展。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ScreenshotPublicationBindsRunAndUsesNamespacedSensitiveExtensions()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(Path.Combine(temporary.Path, "sessions"), workspace);
        var service = new AgentService(repository, new FauxProvider());
        await repository.ConfigureFauxAsync(
            "artifact-session",
            new FauxScenario("0.1", "artifact-test", 0, [new FauxTextStep("unused")]),
            CancellationToken.None);
        var run = await service.AcceptAsync(
            "artifact-session",
            new InputMessage("user", [new TextContent("capture")]),
            new ProviderSelection("faux", "faux-v1"),
            CancellationToken.None);
        var png = Convert.FromBase64String("iVBORw0KGgpmaXh0dXJl");

        await Assert.ThrowsAsync<SessionMutationException>(
            () => repository.PublishToolScreenshotAsync(
                "artifact-session",
                "stale-run",
                png,
                CancellationToken.None));
        var reference = await repository.PublishToolScreenshotAsync(
            "artifact-session",
            run.RunId,
            png,
            CancellationToken.None);

        var referenceComputer = reference.Extensions!.Value
            .GetProperty("org.agentprotocol.computer");
        Assert.Equal("desktop_capture", referenceComputer.GetProperty("source").GetString());
        Assert.Equal(run.RunId, referenceComputer.GetProperty("runId").GetString());
        Assert.Equal("desktop_sensitive", referenceComputer.GetProperty("sensitivity").GetString());
        Assert.Equal("session_lifecycle", referenceComputer.GetProperty("retention").GetString());

        JsonElement? createdRecord = null;
        foreach (var line in (await File.ReadAllLinesAsync(
                     run.Runtime.Journal.JournalPath,
                     CancellationToken.None)).Skip(1))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("kind").GetString() == "artifact.created")
            {
                Assert.Null(createdRecord);
                createdRecord = document.RootElement.Clone();
            }
        }

        Assert.True(createdRecord.HasValue);
        var data = createdRecord.Value.GetProperty("data");
        Assert.False(data.TryGetProperty("runId", out _));
        var eventComputer = data
            .GetProperty("extensions")
            .GetProperty("org.agentprotocol.computer");
        Assert.Equal("desktop_capture", eventComputer.GetProperty("source").GetString());
        Assert.Equal(run.RunId, eventComputer.GetProperty("runId").GetString());
        Assert.Equal("desktop_sensitive", eventComputer.GetProperty("sensitivity").GetString());
        Assert.Equal("session_lifecycle", eventComputer.GetProperty("retention").GetString());
    }

    /// <summary>
    /// 功能：确认 durable 取消先在线性化 gate 内生效后不能再发布截图或 artifact.created。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>异步测试 Task。</returns>
    [Fact]
    public async Task ScreenshotPublicationRejectsDurablyCancelledActiveRun()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(temporary.Path, "workspace")).FullName;
        await using var repository = new SessionRepository(Path.Combine(temporary.Path, "sessions"), workspace);
        var service = new AgentService(repository, new FauxProvider());
        await repository.ConfigureFauxAsync(
            "cancelled-artifact-session",
            new FauxScenario("0.1", "cancelled-artifact-test", 0, [new FauxTextStep("unused")]),
            CancellationToken.None);
        var run = await service.AcceptAsync(
            "cancelled-artifact-session",
            new InputMessage("user", [new TextContent("capture")]),
            new ProviderSelection("faux", "faux-v1"),
            CancellationToken.None);
        Assert.Equal(
            "requested",
            await service.RequestCancellationAsync(
                "cancelled-artifact-session",
                run.RunId,
                CancellationToken.None));

        var png = Convert.FromBase64String("iVBORw0KGgpmaXh0dXJl");
        await Assert.ThrowsAsync<SessionMutationException>(
            () => repository.PublishToolScreenshotAsync(
                "cancelled-artifact-session",
                run.RunId,
                png,
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(
            run.Runtime.Journal.DirectoryPath,
            "artifacts")));
        var kinds = (await File.ReadAllLinesAsync(
                run.Runtime.Journal.JournalPath,
                CancellationToken.None))
            .Skip(1)
            .Select(static line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("kind").GetString();
            });
        Assert.DoesNotContain("artifact.created", kinds);
    }
}
