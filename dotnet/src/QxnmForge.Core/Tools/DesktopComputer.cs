using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QxnmForge.Tools;

/// <summary>
/// 功能：表示启动期已探测成功的 Linux X11 桌面能力。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="InteractionAvailable">XTEST 是否支持受审批的鼠标和键盘注入。</param>
internal sealed record DesktopComputerCapability(bool InteractionAvailable);

/// <summary>
/// 功能：承载一次完整 PNG 桌面捕获及同一 root 的几何信息。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Png">完整 PNG 字节。</param>
/// <param name="Width">root 像素宽度。</param>
/// <param name="Height">root 像素高度。</param>
/// <param name="PointerX">指针 root X 坐标。</param>
/// <param name="PointerY">指针 root Y 坐标。</param>
internal sealed record DesktopCapture(byte[] Png, int Width, int Height, int PointerX, int PointerY);

/// <summary>
/// 功能：冻结一次 default root 捕获所允许的 XImage 行布局。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Depth">default root depth。</param>
/// <param name="BitsPerPixel">pixmap format 的像素位数。</param>
/// <param name="BitmapPad">pixmap format 的扫描线对齐位数。</param>
/// <param name="BytesPerLine">按宽度与对齐精确计算的每行字节数。</param>
/// <param name="SourceLength">按高度精确计算且不超过 raw 上限的总字节数。</param>
internal readonly record struct DesktopCaptureLayout(
    int Depth,
    int BitsPerPixel,
    int BitmapPad,
    int BytesPerLine,
    int SourceLength)
{
    /// <summary>
    /// 功能：取得审批前 default visual 的红色 mask。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal nuint RedMask { get; init; }

    /// <summary>
    /// 功能：取得审批前 default visual 的绿色 mask。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal nuint GreenMask { get; init; }

    /// <summary>
    /// 功能：取得审批前 default visual 的蓝色 mask。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal nuint BlueMask { get; init; }
}

/// <summary>
/// 功能：定义 computer.interact 支持的封闭动作类型。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal enum DesktopComputerActionKind
{
    Move,
    Click,
    Scroll,
    Key,
}

/// <summary>
/// 功能：保存已经过 action-specific 预检的桌面交互参数。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Kind">封闭动作类型。</param>
/// <param name="X">可选绝对 X 坐标。</param>
/// <param name="Y">可选绝对 Y 坐标。</param>
/// <param name="Button">X11 按钮编号。</param>
/// <param name="Clicks">点击次数。</param>
/// <param name="DeltaX">水平滚动格数。</param>
/// <param name="DeltaY">垂直滚动格数。</param>
/// <param name="Key">可移植键名。</param>
/// <param name="Modifiers">唯一修饰键集合。</param>
internal sealed record DesktopComputerAction(
    DesktopComputerActionKind Kind,
    int X = 0,
    int Y = 0,
    uint Button = 0,
    int Clicks = 0,
    int DeltaX = 0,
    int DeltaY = 0,
    string? Key = null,
    IReadOnlyList<string>? Modifiers = null);

/// <summary>
/// 功能：标识不应向协议公开原生库、DISPLAY 或桌面内容的 backend 失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal sealed class DesktopComputerException : Exception
{
    /// <summary>
    /// 功能：创建固定脱敏消息的 desktop backend 异常。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal DesktopComputerException()
        : base("desktop computer backend is unavailable")
    {
    }
}

/// <summary>
/// 功能：通过 Linux X11 和 XTEST 原生实现屏幕捕获与逐次审批交互。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class DesktopComputer
{
    internal const string EnableEnvironmentName = "AGENT_CLIENT_DESKTOP_COMPUTER";
    internal const string ExperimentalEnableEnvironmentName =
        "AGENT_CLIENT_EXPERIMENTAL_DESKTOP_COMPUTER";
    internal const int MaxDimension = 16_384;
    internal const long MaxPixels = 16_777_216;
    internal const long MaxRawBytes = 64L * 1024 * 1024;
    internal const int MaxPngBytes = 32 * 1024 * 1024;
    private const int PngContainerOverhead = 8 + 12 + 13 + 12 + 12;
    private const int MaxCompressedPngBytes = MaxPngBytes - PngContainerOverhead;
    private const int InitialCompressedBufferBytes = 64 * 1024;
    private const int ZPixmap = 2;
    private static readonly SemaphoreSlim XlibGate = new(1, 1);
    private static readonly XErrorHandler ManagedXErrorHandler = HandleXError;
    private static readonly nint ManagedXErrorHandlerPointer =
        Marshal.GetFunctionPointerForDelegate(ManagedXErrorHandler);
    private static int xProtocolErrorObserved;

    /// <summary>
    /// 功能：定义不读取 XErrorEvent 内容的 Xlib 协议错误回调签名。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">触发错误的 display；不得记录。</param>
    /// <param name="errorEvent">原生 XErrorEvent 指针；不得解引用或记录。</param>
    /// <returns>固定返回 0。</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(nint display, nint errorEvent);

    /// <summary>
    /// 功能：判断品牌中立双门均已开启且当前进程明确处于原生 X11 会话。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="enableGate">稳定桌面能力门原始值。</param>
    /// <param name="experimentalGate">实验桌面能力门原始值。</param>
    /// <param name="waylandDisplay">WAYLAND_DISPLAY 原始值。</param>
    /// <param name="sessionType">XDG_SESSION_TYPE 原始值。</param>
    /// <returns>两个门精确为 1、WAYLAND_DISPLAY 缺失/空且 Session 类型明确为 X11 时为 true。</returns>
    /// <remarks>不变量：单门、未知/空 Session 类型、任何非空 WAYLAND_DISPLAY（包括空白）或任何非 X11 类型均 fail closed。</remarks>
    internal static bool IsHostEnvironmentEnabled(
        string? enableGate,
        string? experimentalGate,
        string? waylandDisplay,
        string? sessionType)
    {
        var normalizedSessionType = sessionType?.Trim(' ', '\t', '\r', '\n', '\f', '\v');
        return string.Equals(enableGate, "1", StringComparison.Ordinal) &&
            string.Equals(experimentalGate, "1", StringComparison.Ordinal) &&
            string.IsNullOrEmpty(waylandDisplay) &&
            normalizedSessionType is not null &&
            normalizedSessionType.All(static value => value <= '\u007f') &&
            string.Equals(normalizedSessionType, "x11", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 功能：保守判定 DISPLAY 只指向本机 Unix X11 transport。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">未经信任的 DISPLAY 原始值。</param>
    /// <returns>仅 `:N[.S]` 或 `unix/:N[.S]` 且 N/S 不超过 65535 时为 true。</returns>
    /// <remarks>不变量：hostname、IP、TCP protocol、SSH forwarding 常见形式、空白和额外分隔符均失败关闭。</remarks>
    internal static bool IsLocalDisplay(string? display)
    {
        return NormalizeLocalDisplay(display) is not null;
    }

    /// <summary>
    /// 功能：验证并把两种允许的 DISPLAY 形状规范成显式 Unix transport 名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">未经信任的 DISPLAY 原始值。</param>
    /// <returns>新构造的 `unix/:N[.S]`，或非法/远程/含糊输入对应的 null。</returns>
    /// <remarks>不变量：返回值不允许 Xlib 从 Unix socket 回退 TCP；调用方不得记录原值或规范值。</remarks>
    internal static string? NormalizeLocalDisplay(string? display)
    {
        if (string.IsNullOrEmpty(display) || display.Length > 64)
        {
            return null;
        }

        ReadOnlySpan<char> suffix;
        if (display[0] == ':')
        {
            suffix = display.AsSpan(1);
        }
        else if (display.StartsWith("unix/:", StringComparison.Ordinal))
        {
            suffix = display.AsSpan(6);
        }
        else
        {
            return null;
        }

        var separatorSeen = false;
        var digitSeen = false;
        var numericValue = 0;
        foreach (var value in suffix)
        {
            if (value is >= '0' and <= '9')
            {
                var digit = value - '0';
                if (numericValue > (65_535 - digit) / 10)
                {
                    return null;
                }

                numericValue = (numericValue * 10) + digit;
                digitSeen = true;
                continue;
            }

            if (value == '.' && !separatorSeen && digitSeen)
            {
                separatorSeen = true;
                digitSeen = false;
                numericValue = 0;
                continue;
            }

            return null;
        }

        return digitSeen ? string.Concat("unix/:", suffix) : null;
    }

    /// <summary>
    /// 功能：在每次 X11 连接前重新验证双门、原生 Session，并取得强制 Unix transport 名称。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>当前环境安全时为新构造的 `unix/:N[.S]`，否则为 null。</returns>
    /// <remarks>不变量：启动期探测结果不能授权环境变化后的远程或未知 display。</remarks>
    private static string? GetCurrentLocalDisplay()
    {
        if (!IsHostEnvironmentEnabled(
            Environment.GetEnvironmentVariable(EnableEnvironmentName),
            Environment.GetEnvironmentVariable(ExperimentalEnableEnvironmentName),
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")))
        {
            return null;
        }

        return NormalizeLocalDisplay(Environment.GetEnvironmentVariable("DISPLAY"));
    }

    /// <summary>
    /// 功能：仅在受信任桌面宿主双门精确启用且 Session 明确为 X11 时探测 root 与 XTEST。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>观察不可用为 null，否则返回交互可用性。</returns>
    /// <remarks>不变量：探测不读取屏幕像素、不注入输入、不回显 DISPLAY；全部 Xlib 调用由进程级 gate 串行。</remarks>
    internal static DesktopComputerCapability? Detect()
    {
        var displayName = GetCurrentLocalDisplay();
        if (!OperatingSystem.IsLinux() || displayName is null)
        {
            return null;
        }

        XlibGate.Wait();
        nint display = 0;
        nint previousErrorHandler = 0;
        var errorHandlerInstalled = false;
        try
        {
            previousErrorHandler = BeginXErrorCapture();
            errorHandlerInstalled = true;
            display = XOpenDisplay(displayName);
            if (display == 0)
            {
                return null;
            }

            var screen = XDefaultScreen(display);
            var root = XRootWindow(display, screen);
            var width = XDisplayWidth(display, screen);
            var height = XDisplayHeight(display, screen);
            if (root == 0 ||
                !IsCaptureGeometrySafe(width, height) ||
                XQueryPointer(
                    display,
                    root,
                    out _,
                    out _,
                    out var pointerX,
                    out var pointerY,
                    out _,
                    out _,
                    out _) == 0 ||
                pointerX < 0 || pointerX >= width || pointerY < 0 || pointerY >= height)
            {
                return null;
            }

            _ = ValidateCaptureVisualAndFormat(display, screen, width, height);
            var interaction = DetectInteraction(display);
            if (!SynchronizeAndCheck(display))
            {
                return null;
            }

            return new DesktopComputerCapability(interaction);
        }
        catch (DesktopComputerException)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
        finally
        {
            try
            {
                CloseDisplayAndRestoreErrorHandler(
                    display,
                    previousErrorHandler,
                    errorHandlerInstalled);
            }
            finally
            {
                XlibGate.Release();
            }
        }
    }

    /// <summary>
    /// 功能：在不影响屏幕观察能力的前提下探测可选 XTEST 交互扩展。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已在全局 gate 内打开的 X11 display。</param>
    /// <returns>XTEST 库、入口与扩展均可用时为 true，否则为 false。</returns>
    /// <remarks>不变量：libXtst 缺失只收窄交互能力，不能关闭 screenshot/observe。</remarks>
    private static bool DetectInteraction(nint display)
    {
        try
        {
            return XTestQueryExtension(display, out _, out _, out _, out _) != 0;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// 功能：在调用 XGetImage 前验证桌面几何的维度、像素与最坏 raw 上限。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="width">root 像素宽度。</param>
    /// <param name="height">root 像素高度。</param>
    /// <returns>每维不超过 16384、像素不超过 16777216 且预计 BGRA 不超过 64 MiB 时为 true。</returns>
    /// <remarks>不变量：使用 long 计算，不会因攻击性 X server 尺寸发生整数溢出。</remarks>
    internal static bool IsCaptureGeometrySafe(int width, int height)
    {
        if (width is <= 0 or > MaxDimension || height is <= 0 or > MaxDimension)
        {
            return false;
        }

        var pixels = (long)width * height;
        return pixels <= MaxPixels && pixels * 4 <= MaxRawBytes;
    }

    /// <summary>
    /// 功能：用 checked 算术冻结指定 X11 pixmap format 的精确行布局。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="width">受捕获上限约束的像素宽度。</param>
    /// <param name="height">受捕获上限约束的像素高度。</param>
    /// <param name="depth">default root depth。</param>
    /// <param name="bitsPerPixel">pixmap format 的像素位数，只允许 24 或 32。</param>
    /// <param name="bitmapPad">扫描线对齐位数，只允许 8、16 或 32。</param>
    /// <returns>包含精确 stride 与总长度的冻结布局。</returns>
    /// <remarks>不变量：总长度不超过 64 MiB，且每行位数始终向上对齐到 bitmapPad。</remarks>
    /// <exception cref="DesktopComputerException">几何、depth、format、对齐或计算结果不受支持。</exception>
    internal static DesktopCaptureLayout CalculateCaptureLayout(
        int width,
        int height,
        int depth,
        int bitsPerPixel,
        int bitmapPad)
    {
        if (!IsCaptureGeometrySafe(width, height) ||
            depth <= 0 || depth > bitsPerPixel ||
            bitsPerPixel is not (24 or 32) ||
            bitmapPad is not (8 or 16 or 32))
        {
            throw new DesktopComputerException();
        }

        try
        {
            var rowBits = checked((long)width * bitsPerPixel);
            var paddedRowBits = checked(
                ((rowBits + bitmapPad - 1L) / bitmapPad) * bitmapPad);
            var bytesPerLine = paddedRowBits / 8;
            var sourceLength = checked(bytesPerLine * height);
            if (bytesPerLine <= 0 || bytesPerLine > int.MaxValue ||
                sourceLength <= 0 || sourceLength > MaxRawBytes ||
                sourceLength > int.MaxValue)
            {
                throw new DesktopComputerException();
            }

            return new DesktopCaptureLayout(
                depth,
                bitsPerPixel,
                bitmapPad,
                checked((int)bytesPerLine),
                checked((int)sourceLength));
        }
        catch (OverflowException)
        {
            throw new DesktopComputerException();
        }
    }

    /// <summary>
    /// 功能：验证 RGB visual mask 可由当前 TrueColor decoder 安全解释。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="redMask">红色分量 mask。</param>
    /// <param name="greenMask">绿色分量 mask。</param>
    /// <param name="blueMask">蓝色分量 mask。</param>
    /// <param name="bitsPerPixel">当前 pixmap/XImage 像素位数。</param>
    /// <returns>仅三个非零、互斥、连续且位于 24/32 bit 像素内的 mask 为 true。</returns>
    internal static bool AreColorMasksSafe(
        nuint redMask,
        nuint greenMask,
        nuint blueMask,
        int bitsPerPixel)
    {
        if (bitsPerPixel is not (24 or 32))
        {
            return false;
        }

        var red = (ulong)redMask;
        var green = (ulong)greenMask;
        var blue = (ulong)blueMask;
        var combined = red | green | blue;
        return red != 0 && green != 0 && blue != 0 &&
            (red & green) == 0 && (red & blue) == 0 && (green & blue) == 0 &&
            (combined >> bitsPerPixel) == 0 &&
            IsContiguousMask(red) && IsContiguousMask(green) && IsContiguousMask(blue);
    }

    /// <summary>
    /// 功能：判断非零整数在去除低位零后是否只包含连续的一段 1。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="mask">待验证的非零颜色 mask。</param>
    /// <returns>mask 连续时为 true。</returns>
    private static bool IsContiguousMask(ulong mask)
    {
        if (mask == 0)
        {
            return false;
        }

        while ((mask & 1) == 0)
        {
            mask >>= 1;
        }

        return (mask & (mask + 1)) == 0;
    }

    /// <summary>
    /// 功能：只读验证 default root 的 depth、pixmap format 与 TrueColor visual mask。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已在全局 gate 内打开的 X11 display。</param>
    /// <param name="screen">当前 default screen。</param>
    /// <param name="width">已读取的 default root 宽度。</param>
    /// <param name="height">已读取的 default root 高度。</param>
    /// <returns>与 default depth、pixmap format 和捕获几何绑定的冻结布局。</returns>
    /// <remarks>不变量：不请求图像或像素；只接受 decoder 已实现的 24/32 bit TrueColor 布局。</remarks>
    /// <exception cref="DesktopComputerException">depth、format、visual class 或 mask 不受支持。</exception>
    private static DesktopCaptureLayout ValidateCaptureVisualAndFormat(
        nint display,
        int screen,
        int width,
        int height)
    {
        var depth = XDefaultDepth(display, screen);
        var visualPointer = XDefaultVisual(display, screen);
        if (depth <= 0 || visualPointer == 0)
        {
            throw new DesktopComputerException();
        }

        var visual = Marshal.PtrToStructure<XVisualHeader>(visualPointer);
        nint formats = 0;
        try
        {
            formats = XListPixmapFormats(display, out var count);
            if (formats == 0 || count is <= 0 or > 256)
            {
                throw new DesktopComputerException();
            }

            var itemSize = Marshal.SizeOf<XPixmapFormatValues>();
            DesktopCaptureLayout? layout = null;
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<XPixmapFormatValues>(
                    IntPtr.Add(formats, checked(index * itemSize)));
                if (item.Depth == depth)
                {
                    if (layout.HasValue)
                    {
                        throw new DesktopComputerException();
                    }

                    layout = CalculateCaptureLayout(
                        width,
                        height,
                        depth,
                        item.BitsPerPixel,
                        item.ScanlinePad);
                }
            }

            if (!layout.HasValue || visual.Class != 4 ||
                !AreColorMasksSafe(
                    visual.RedMask,
                    visual.GreenMask,
                    visual.BlueMask,
                    layout.Value.BitsPerPixel))
            {
                throw new DesktopComputerException();
            }

            return layout.Value with
            {
                RedMask = visual.RedMask,
                GreenMask = visual.GreenMask,
                BlueMask = visual.BlueMask,
            };
        }
        finally
        {
            if (formats != 0)
            {
                _ = XFree(formats);
            }
        }
    }

    /// <summary>
    /// 功能：在审批前只读重连并验证截图 backend、捕获几何与当前 pointer。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <remarks>不变量：不调用 XGetImage、不读取像素、不注入输入；执行边界仍完整复核。</remarks>
    /// <exception cref="DesktopComputerException">环境、连接、几何、pointer 或 X11 协议检查失败。</exception>
    internal static void PreflightObserve()
    {
        var displayName = GetCurrentLocalDisplay();
        if (!OperatingSystem.IsLinux() || displayName is null)
        {
            throw new DesktopComputerException();
        }

        XlibGate.Wait();
        nint display = 0;
        nint previousErrorHandler = 0;
        var errorHandlerInstalled = false;
        try
        {
            previousErrorHandler = BeginXErrorCapture();
            errorHandlerInstalled = true;
            display = XOpenDisplay(displayName);
            if (display == 0)
            {
                throw new DesktopComputerException();
            }

            var screen = XDefaultScreen(display);
            var root = XRootWindow(display, screen);
            var width = XDisplayWidth(display, screen);
            var height = XDisplayHeight(display, screen);
            _ = ValidateCaptureVisualAndFormat(display, screen, width, height);
            if (root == 0 || !IsCaptureGeometrySafe(width, height) ||
                XQueryPointer(
                    display,
                    root,
                    out _,
                    out _,
                    out var pointerX,
                    out var pointerY,
                    out _,
                    out _,
                    out _) == 0 ||
                pointerX < 0 || pointerX >= width || pointerY < 0 || pointerY >= height ||
                !SynchronizeAndCheck(display))
            {
                throw new DesktopComputerException();
            }
        }
        catch (DesktopComputerException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new DesktopComputerException();
        }
        finally
        {
            try
            {
                CloseDisplayAndRestoreErrorHandler(
                    display,
                    previousErrorHandler,
                    errorHandlerInstalled);
            }
            finally
            {
                XlibGate.Release();
            }
        }
    }

    /// <summary>
    /// 功能：在审批前只读重连并验证 XTEST、root 与本次动作坐标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="action">已通过 action-specific schema 与 parser 的动作。</param>
    /// <remarks>不变量：只查询扩展与几何，不发送任何 XTEST fake input；执行边界仍再次复核。</remarks>
    /// <exception cref="DesktopComputerException">环境、连接、XTEST、坐标或 X11 协议检查失败。</exception>
    internal static void PreflightInteract(DesktopComputerAction action)
    {
        var displayName = GetCurrentLocalDisplay();
        if (!OperatingSystem.IsLinux() || displayName is null)
        {
            throw new DesktopComputerException();
        }

        XlibGate.Wait();
        nint display = 0;
        nint previousErrorHandler = 0;
        var errorHandlerInstalled = false;
        try
        {
            previousErrorHandler = BeginXErrorCapture();
            errorHandlerInstalled = true;
            display = XOpenDisplay(displayName);
            if (display == 0 || XTestQueryExtension(display, out _, out _, out _, out _) == 0)
            {
                throw new DesktopComputerException();
            }

            var screen = XDefaultScreen(display);
            if (XRootWindow(display, screen) == 0)
            {
                throw new DesktopComputerException();
            }

            ValidateRuntimeCoordinates(display, screen, action);
            if (action.Kind == DesktopComputerActionKind.Key)
            {
                _ = ResolveKeycodes(
                    display,
                    action.Key!,
                    action.Modifiers ?? Array.Empty<string>());
            }

            if (!SynchronizeAndCheck(display))
            {
                throw new DesktopComputerException();
            }
        }
        catch (DesktopComputerException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new DesktopComputerException();
        }
        finally
        {
            try
            {
                CloseDisplayAndRestoreErrorHandler(
                    display,
                    previousErrorHandler,
                    errorHandlerInstalled);
            }
            finally
            {
                XlibGate.Release();
            }
        }
    }

    /// <summary>
    /// 功能：捕获 X11 root、转换为 RGB8 并编码为有界 PNG。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">等待全局 Xlib gate、像素转换和 PNG 扫描线编码的取消信号。</param>
    /// <returns>不超过 32 MiB 的 PNG、尺寸与指针位置。</returns>
    /// <remarks>不变量：XGetImage 前已限制几何和预计 raw；全部原生调用在进程级 gate 内串行。</remarks>
    /// <exception cref="OperationCanceledException">等待或逐行处理期间取消。</exception>
    /// <exception cref="DesktopComputerException">连接、像素布局、长度或编码失败。</exception>
    internal static DesktopCapture Capture(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var displayName = GetCurrentLocalDisplay();
        if (!OperatingSystem.IsLinux() || displayName is null)
        {
            throw new DesktopComputerException();
        }

        XlibGate.Wait(cancellationToken);
        nint display = 0;
        nint image = 0;
        nint previousErrorHandler = 0;
        var errorHandlerInstalled = false;
        try
        {
            previousErrorHandler = BeginXErrorCapture();
            errorHandlerInstalled = true;
            display = XOpenDisplay(displayName);
            if (display == 0)
            {
                throw new DesktopComputerException();
            }

            var screen = XDefaultScreen(display);
            var root = XRootWindow(display, screen);
            var width = XDisplayWidth(display, screen);
            var height = XDisplayHeight(display, screen);
            var layout = ValidateCaptureVisualAndFormat(display, screen, width, height);
            if (root == 0 || !IsCaptureGeometrySafe(width, height) ||
                XQueryPointer(
                    display,
                    root,
                    out _,
                    out _,
                    out var pointerX,
                    out var pointerY,
                    out _,
                    out _,
                    out _) == 0)
            {
                throw new DesktopComputerException();
            }

            if (pointerX < 0 || pointerX >= width || pointerY < 0 || pointerY >= height)
            {
                throw new DesktopComputerException();
            }

            cancellationToken.ThrowIfCancellationRequested();
            image = XGetImage(display, root, 0, 0, (uint)width, (uint)height, nuint.MaxValue, ZPixmap);
            if (image == 0 || !SynchronizeAndCheck(display))
            {
                throw new DesktopComputerException();
            }

            var header = Marshal.PtrToStructure<XImageHeader>(image);
            if (header.Data == 0 || header.ByteOrder is not (0 or 1) ||
                !IsCapturedImageLayoutExact(
                    layout,
                    width,
                    height,
                    header.Width,
                    header.Height,
                    header.XOffset,
                    header.Format,
                    header.Depth,
                    header.BitmapPad,
                    header.BitsPerPixel,
                    header.BytesPerLine,
                    header.RedMask,
                    header.GreenMask,
                    header.BlueMask))
            {
                throw new DesktopComputerException();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var source = new byte[layout.SourceLength];
            Marshal.Copy(header.Data, source, 0, source.Length);
            cancellationToken.ThrowIfCancellationRequested();
            var rgb = DecodeRgb(source, header, cancellationToken);
            var png = EncodePng(rgb, width, height, cancellationToken);
            if (png.Length == 0 || png.Length > MaxPngBytes)
            {
                throw new DesktopComputerException();
            }

            return new DesktopCapture(png, width, height, pointerX, pointerY);
        }
        catch (DesktopComputerException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is OverflowException or OutOfMemoryException or IOException or
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new DesktopComputerException();
        }
        finally
        {
            try
            {
                if (image != 0)
                {
                    _ = XDestroyImage(image);
                }
            }
            finally
            {
                try
                {
                    CloseDisplayAndRestoreErrorHandler(
                        display,
                        previousErrorHandler,
                        errorHandlerInstalled);
                }
                finally
                {
                    XlibGate.Release();
                }
            }
        }
    }

    /// <summary>
    /// 功能：解析 computer.interact 的 action-specific 参数并拒绝含糊或缺失动作。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">已通过受限 JSON Schema 基础验证的参数。</param>
    /// <returns>封闭且有界的动作。</returns>
    /// <exception cref="ToolOperationException">字段集合、类型、范围、命名键或修饰键非法。</exception>
    internal static DesktopComputerAction ParseAction(JsonElement arguments)
    {
        var action = RequireString(arguments, "action");
        return action switch
        {
            "move" => ParseMove(arguments),
            "click" => ParseClick(arguments),
            "scroll" => ParseScroll(arguments),
            "key" => ParseKey(arguments),
            _ => throw InvalidArguments(),
        };
    }

    /// <summary>
    /// 功能：通过 XTEST 执行一个已完成策略和逐次审批的桌面动作。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="action">已预检动作。</param>
    /// <param name="cancellationToken">等待全局 Xlib gate 与多步事件注入的取消信号。</param>
    /// <remarks>不变量：运行期坐标必须仍位于当前 root；所有 Xlib/XTEST 调用全局串行，已按下输入会在失败时尽力释放。</remarks>
    /// <exception cref="OperationCanceledException">等待或多步动作期间取消。</exception>
    /// <exception cref="DesktopComputerException">连接、扩展、坐标、键位映射或事件注入失败。</exception>
    internal static void Interact(DesktopComputerAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var displayName = GetCurrentLocalDisplay();
        if (!OperatingSystem.IsLinux() || displayName is null)
        {
            throw new DesktopComputerException();
        }

        XlibGate.Wait(cancellationToken);
        nint display = 0;
        nint previousErrorHandler = 0;
        var errorHandlerInstalled = false;
        var inputSequenceCompleted = false;
        try
        {
            previousErrorHandler = BeginXErrorCapture();
            errorHandlerInstalled = true;
            display = XOpenDisplay(displayName);
            if (display == 0 || XTestQueryExtension(display, out _, out _, out _, out _) == 0)
            {
                throw new DesktopComputerException();
            }

            var screen = XDefaultScreen(display);
            if (XRootWindow(display, screen) == 0)
            {
                throw new DesktopComputerException();
            }

            ValidateRuntimeCoordinates(display, screen, action);
            switch (action.Kind)
            {
                case DesktopComputerActionKind.Move:
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureNativeSuccess(XTestFakeMotionEvent(display, screen, action.X, action.Y, 0));
                    break;
                case DesktopComputerActionKind.Click:
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureNativeSuccess(XTestFakeMotionEvent(display, screen, action.X, action.Y, 0));
                    for (var index = 0; index < action.Clicks; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        SendButton(display, action.Button, cancellationToken);
                    }

                    break;
                case DesktopComputerActionKind.Scroll:
                    SendScroll(display, action.DeltaY, 4, 5, cancellationToken);
                    SendScroll(display, action.DeltaX, 6, 7, cancellationToken);
                    break;
                case DesktopComputerActionKind.Key:
                    SendKey(
                        display,
                        action.Key!,
                        action.Modifiers ?? Array.Empty<string>(),
                        cancellationToken);
                    break;
                default:
                    throw new DesktopComputerException();
            }

            inputSequenceCompleted = true;
            if (!SynchronizeAndCheck(display))
            {
                throw new DesktopComputerException();
            }
        }
        catch (OperationCanceledException)
        {
            if (inputSequenceCompleted)
            {
                BestEffortReleaseInputs(display, action);
            }

            throw;
        }
        catch (DesktopComputerException)
        {
            if (inputSequenceCompleted)
            {
                BestEffortReleaseInputs(display, action);
            }

            throw;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (inputSequenceCompleted)
            {
                BestEffortReleaseInputs(display, action);
            }

            throw new DesktopComputerException();
        }
        finally
        {
            try
            {
                CloseDisplayAndRestoreErrorHandler(
                    display,
                    previousErrorHandler,
                    errorHandlerInstalled);
            }
            finally
            {
                XlibGate.Release();
            }
        }
    }

    /// <summary>
    /// 功能：在完整输入序列已排队但 flush 失败后尽力反向释放相关原生输入。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">可能已打开的 X11 display。</param>
    /// <param name="action">已完成 press/release 排队但 flush 失败的动作。</param>
    /// <remarks>不变量：本方法吞掉自身全部失败，绝不覆盖原始取消或 backend 异常；不注入新的 press。</remarks>
    private static void BestEffortReleaseInputs(nint display, DesktopComputerAction action)
    {
        if (display == 0)
        {
            return;
        }

        try
        {
            switch (action.Kind)
            {
                case DesktopComputerActionKind.Click:
                    _ = XTestFakeButtonEvent(display, action.Button, false, 0);
                    break;
                case DesktopComputerActionKind.Scroll:
                    foreach (var button in ScrollReleaseButtons(action.DeltaX, action.DeltaY))
                    {
                        _ = XTestFakeButtonEvent(display, button, false, 0);
                    }

                    break;
                case DesktopComputerActionKind.Key:
                    var (keycode, keyModifiers) = ResolveKeycodes(
                        display,
                        action.Key!,
                        action.Modifiers ?? Array.Empty<string>());
                    _ = XTestFakeKeyEvent(display, keycode, false, 0);
                    for (var index = keyModifiers.Count - 1; index >= 0; index--)
                    {
                        _ = XTestFakeKeyEvent(display, keyModifiers[index], false, 0);
                    }

                    break;
            }

            _ = XSync(display, discard: false);
        }
        catch
        {
            // best effort cleanup must preserve the primary failure
        }
    }

    /// <summary>
    /// 功能：计算滚动失败补偿中仅属于本次非零轴动作的按钮集合。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="deltaX">水平滚动格数。</param>
    /// <param name="deltaY">垂直滚动格数。</param>
    /// <returns>按垂直、水平顺序排列且至多两个的 X11 button release 编号。</returns>
    /// <remarks>不变量：零轴不产生 release，避免干扰本动作未使用的物理按钮。</remarks>
    internal static IReadOnlyList<uint> ScrollReleaseButtons(int deltaX, int deltaY)
    {
        var buttons = new List<uint>(2);
        if (deltaY != 0)
        {
            buttons.Add(deltaY > 0 ? 4U : 5U);
        }

        if (deltaX != 0)
        {
            buttons.Add(deltaX > 0 ? 6U : 7U);
        }

        return buttons;
    }

    /// <summary>
    /// 功能：验证所有动作共享的 root 几何，并为鼠标动作额外验证坐标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="width">当前 root 像素宽度。</param>
    /// <param name="height">当前 root 像素高度。</param>
    /// <param name="action">已预检动作。</param>
    /// <returns>root 几何安全，且鼠标动作坐标位于 root 内时为 true。</returns>
    /// <remarks>不变量：scroll/key 也必须先通过非零、有界 root 几何验证。</remarks>
    internal static bool IsRuntimeGeometrySafe(
        int width,
        int height,
        DesktopComputerAction action)
    {
        return IsCaptureGeometrySafe(width, height) &&
            (action.Kind is not (DesktopComputerActionKind.Move or DesktopComputerActionKind.Click) ||
                (action.X >= 0 && action.X < width && action.Y >= 0 && action.Y < height));
    }

    /// <summary>
    /// 功能：纯函数验证执行期 XImage 与审批前冻结的 root visual/format 完全一致。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="expected">审批前冻结的 depth、format、stride、长度和 visual masks。</param>
    /// <param name="expectedWidth">本次请求的 root 宽度。</param>
    /// <param name="expectedHeight">本次请求的 root 高度。</param>
    /// <param name="actualWidth">XImage 返回宽度。</param>
    /// <param name="actualHeight">XImage 返回高度。</param>
    /// <param name="xOffset">XImage 返回水平偏移。</param>
    /// <param name="format">XImage 返回格式。</param>
    /// <param name="depth">XImage 返回 depth。</param>
    /// <param name="bitmapPad">XImage 返回扫描线对齐。</param>
    /// <param name="bitsPerPixel">XImage 返回像素位数。</param>
    /// <param name="bytesPerLine">XImage 返回行跨度。</param>
    /// <param name="redMask">XImage 返回红色 mask。</param>
    /// <param name="greenMask">XImage 返回绿色 mask。</param>
    /// <param name="blueMask">XImage 返回蓝色 mask。</param>
    /// <returns>全部字段与冻结值精确一致时为 true。</returns>
    /// <remarks>不变量：只接受 ZPixmap、零偏移和已验证安全且未变化的 default visual masks；不读取像素。</remarks>
    internal static bool IsCapturedImageLayoutExact(
        DesktopCaptureLayout expected,
        int expectedWidth,
        int expectedHeight,
        int actualWidth,
        int actualHeight,
        int xOffset,
        int format,
        int depth,
        int bitmapPad,
        int bitsPerPixel,
        int bytesPerLine,
        nuint redMask,
        nuint greenMask,
        nuint blueMask)
    {
        return expectedWidth == actualWidth &&
            expectedHeight == actualHeight &&
            xOffset == 0 &&
            format == ZPixmap &&
            depth == expected.Depth &&
            bitmapPad == expected.BitmapPad &&
            bitsPerPixel == expected.BitsPerPixel &&
            bytesPerLine == expected.BytesPerLine &&
            redMask == expected.RedMask &&
            greenMask == expected.GreenMask &&
            blueMask == expected.BlueMask &&
            AreColorMasksSafe(redMask, greenMask, blueMask, bitsPerPixel);
    }

    /// <summary>
    /// 功能：在注入任何动作前复验当前 X11 root 几何与动作坐标。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已在全局 gate 内打开的 X11 display。</param>
    /// <param name="screen">当前默认 screen。</param>
    /// <param name="action">已预检动作。</param>
    /// <remarks>不变量：所有动作都读取几何；屏幕无效、变化或鼠标坐标越界时不会调用 XTEST。</remarks>
    /// <exception cref="DesktopComputerException">屏幕几何或鼠标坐标无效。</exception>
    private static void ValidateRuntimeCoordinates(
        nint display,
        int screen,
        DesktopComputerAction action)
    {
        var width = XDisplayWidth(display, screen);
        var height = XDisplayHeight(display, screen);
        if (!IsRuntimeGeometrySafe(width, height, action))
        {
            throw new DesktopComputerException();
        }
    }

    /// <summary>
    /// 功能：解析绝对移动动作。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">仅允许 action、x、y 的 JSON 对象。</param>
    /// <returns>坐标已限制在协议维度内的 move 动作。</returns>
    /// <remarks>不变量：缺失、额外、非整数或越界字段均不会进入原生执行边界。</remarks>
    /// <exception cref="ToolOperationException">JSON 形状或坐标无效。</exception>
    private static DesktopComputerAction ParseMove(JsonElement arguments)
    {
        RequireOnly(arguments, "action", "x", "y");
        return new DesktopComputerAction(
            DesktopComputerActionKind.Move,
            X: RequireInteger(arguments, "x", 0, MaxDimension - 1),
            Y: RequireInteger(arguments, "y", 0, MaxDimension - 1));
    }

    /// <summary>
    /// 功能：解析有界鼠标点击动作。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">仅允许 click 动作字段的 JSON 对象。</param>
    /// <returns>按钮、次数与坐标均已封闭验证的 click 动作。</returns>
    /// <remarks>不变量：只接受 left/middle/right 与 1..3 次点击。</remarks>
    /// <exception cref="ToolOperationException">JSON 形状、坐标、按钮或次数无效。</exception>
    private static DesktopComputerAction ParseClick(JsonElement arguments)
    {
        RequireOnly(arguments, "action", "x", "y", "button", "clicks");
        var button = RequireString(arguments, "button") switch
        {
            "left" => 1U,
            "middle" => 2U,
            "right" => 3U,
            _ => throw InvalidArguments(),
        };
        return new DesktopComputerAction(
            DesktopComputerActionKind.Click,
            X: RequireInteger(arguments, "x", 0, MaxDimension - 1),
            Y: RequireInteger(arguments, "y", 0, MaxDimension - 1),
            Button: button,
            Clicks: RequireInteger(arguments, "clicks", 1, 3));
    }

    /// <summary>
    /// 功能：解析有界双轴滚动动作，并把双零保留为安全 no-op。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">仅允许 action、deltaX、deltaY 的 JSON 对象。</param>
    /// <returns>两轴均位于 -20..20 的 scroll 动作。</returns>
    /// <remarks>不变量：双零不注入按钮，非零轴分别独立执行。</remarks>
    /// <exception cref="ToolOperationException">JSON 形状或任一滚动量无效。</exception>
    private static DesktopComputerAction ParseScroll(JsonElement arguments)
    {
        RequireOnly(arguments, "action", "deltaX", "deltaY");
        var deltaX = RequireInteger(arguments, "deltaX", -20, 20);
        var deltaY = RequireInteger(arguments, "deltaY", -20, 20);
        return new DesktopComputerAction(
            DesktopComputerActionKind.Scroll,
            DeltaX: deltaX,
            DeltaY: deltaY);
    }

    /// <summary>
    /// 功能：解析可移植键名与唯一修饰键。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">仅允许 action、key、modifiers 的 JSON 对象。</param>
    /// <returns>键名受白名单约束且修饰键唯一的 key 动作。</returns>
    /// <remarks>不变量：不接受文本输入、任意字符、重复修饰键或未知修饰键。</remarks>
    /// <exception cref="ToolOperationException">JSON 形状、键名或修饰键集合无效。</exception>
    private static DesktopComputerAction ParseKey(JsonElement arguments)
    {
        RequireOnly(arguments, "action", "key", "modifiers");
        var key = RequireString(arguments, "key");
        if (key is not (
            "enter" or "tab" or "escape" or "backspace" or "delete" or "home" or
            "left" or "up" or "right" or "down" or "page_up" or "page_down" or
            "end" or "space"))
        {
            throw InvalidArguments();
        }

        if (!arguments.TryGetProperty("modifiers", out var modifiersElement) ||
            modifiersElement.ValueKind != JsonValueKind.Array ||
            modifiersElement.GetArrayLength() > 4)
        {
            throw InvalidArguments();
        }

        var modifiers = new List<string>(modifiersElement.GetArrayLength());
        foreach (var item in modifiersElement.EnumerateArray())
        {
            var modifier = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (modifier is not ("shift" or "control" or "alt" or "meta") ||
                modifiers.Contains(modifier, StringComparer.Ordinal))
            {
                throw InvalidArguments();
            }

            modifiers.Add(modifier);
        }

        return new DesktopComputerAction(DesktopComputerActionKind.Key, Key: key, Modifiers: modifiers);
    }

    /// <summary>
    /// 功能：把 XImage 24/32 bit TrueColor 数据转换为紧凑 RGB8。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="source">受 64 MiB 上限约束的 XImage 副本。</param>
    /// <param name="image">已验证的 XImage 前缀布局。</param>
    /// <param name="cancellationToken">逐扫描线取消信号。</param>
    /// <returns>紧凑 RGB8 字节。</returns>
    /// <exception cref="OperationCanceledException">扫描线转换期间取消。</exception>
    /// <exception cref="DesktopComputerException">像素偏移或 mask 无效。</exception>
    private static byte[] DecodeRgb(
        byte[] source,
        XImageHeader image,
        CancellationToken cancellationToken)
    {
        var bytesPerPixel = image.BitsPerPixel / 8;
        var output = new byte[checked(image.Width * image.Height * 3)];
        var outputOffset = 0;
        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowOffset = checked(y * image.BytesPerLine);
            for (var x = 0; x < image.Width; x++)
            {
                var sourceOffset = checked(rowOffset + (x * bytesPerPixel));
                if (sourceOffset < 0 || sourceOffset + bytesPerPixel > source.Length)
                {
                    throw new DesktopComputerException();
                }

                ulong pixel = 0;
                if (image.ByteOrder == 0)
                {
                    for (var index = 0; index < bytesPerPixel; index++)
                    {
                        pixel |= (ulong)source[sourceOffset + index] << (index * 8);
                    }
                }
                else
                {
                    for (var index = 0; index < bytesPerPixel; index++)
                    {
                        pixel = (pixel << 8) | source[sourceOffset + index];
                    }
                }

                output[outputOffset++] = MaskComponent(pixel, image.RedMask);
                output[outputOffset++] = MaskComponent(pixel, image.GreenMask);
                output[outputOffset++] = MaskComponent(pixel, image.BlueMask);
            }
        }

        return output;
    }

    /// <summary>
    /// 功能：按连续 visual mask 将像素分量缩放到 0..255。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="pixel">已按 XImage byte order 组装的像素值。</param>
    /// <param name="nativeMask">预先验证为非零连续位段的颜色 mask。</param>
    /// <returns>按 mask 最大值线性缩放的 8-bit 分量。</returns>
    /// <remarks>不变量：调用方只传入 AreColorMasksSafe 已接受的 mask。</remarks>
    /// <exception cref="DesktopComputerException">mask 为零。</exception>
    private static byte MaskComponent(ulong pixel, nuint nativeMask)
    {
        var mask = (ulong)nativeMask;
        if (mask == 0)
        {
            throw new DesktopComputerException();
        }

        var shift = 0;
        while (((mask >> shift) & 1UL) == 0)
        {
            shift++;
        }

        var maximum = mask >> shift;
        var value = (pixel & mask) >> shift;
        return checked((byte)((value * 255) / maximum));
    }

    /// <summary>
    /// 功能：把 RGB8 扫描线编码为单 IDAT 的标准 PNG。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="rgb">已通过几何上限约束的 RGB8 字节。</param>
    /// <param name="width">PNG 宽度。</param>
    /// <param name="height">PNG 高度。</param>
    /// <param name="cancellationToken">逐扫描线取消信号。</param>
    /// <returns>不超过 32 MiB 的完整 PNG。</returns>
    /// <remarks>不变量：IHDR 的 13 字节在写字段前全部清零，压缩输出在扩容前受硬上限约束。</remarks>
    /// <exception cref="OperationCanceledException">编码期间取消。</exception>
    /// <exception cref="IOException">压缩数据无法在合法 PNG 预算内完成。</exception>
    /// <exception cref="DesktopComputerException">冻结输出缓冲或最终容器长度不一致。</exception>
    internal static byte[] EncodePng(
        byte[] rgb,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        using var compressed = new BoundedMemoryStream(
            MaxCompressedPngBytes,
            InitialCompressedBufferBytes);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var rowBytes = checked(width * 3);
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                zlib.WriteByte(0);
                zlib.Write(rgb, checked(y * rowBytes), rowBytes);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var compressedLength = checked((int)compressed.Length);
        if (!compressed.TryGetBuffer(out var compressedBuffer))
        {
            throw new DesktopComputerException();
        }

        var result = new byte[checked(PngContainerOverhead + compressedLength)];
        using var png = new MemoryStream(result, writable: true);
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(png, "IHDR", header, cancellationToken);
        WriteChunk(
            png,
            "IDAT",
            compressedBuffer.AsSpan(0, compressedLength),
            cancellationToken);
        WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty, cancellationToken);
        if (png.Position != result.Length)
        {
            throw new DesktopComputerException();
        }

        return result;
    }

    /// <summary>
    /// 功能：写入带大端长度与 CRC-32 的 PNG chunk。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="output">容量已冻结的完整 PNG 输出。</param>
    /// <param name="type">四字节 ASCII chunk 类型。</param>
    /// <param name="data">chunk payload。</param>
    /// <param name="cancellationToken">CRC 计算取消信号。</param>
    /// <exception cref="OperationCanceledException">CRC 计算期间取消。</exception>
    private static void WriteChunk(
        Stream output,
        string type,
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            crc,
            ComputeCrc32(typeBytes, data, cancellationToken));
        output.Write(crc);
    }

    /// <summary>
    /// 功能：计算 PNG chunk 使用的 IEEE CRC-32。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="type">四字节 ASCII chunk 类型。</param>
    /// <param name="data">chunk payload。</param>
    /// <param name="cancellationToken">分段计算取消信号。</param>
    /// <returns>PNG 使用的 CRC-32。</returns>
    /// <exception cref="OperationCanceledException">CRC 计算期间取消。</exception>
    private static uint ComputeCrc32(
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken)
    {
        var crc = uint.MaxValue;
        UpdateCrc32(ref crc, type, cancellationToken);
        UpdateCrc32(ref crc, data, cancellationToken);
        return ~crc;
    }

    /// <summary>
    /// 功能：把一段字节累计进 CRC-32 状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="crc">待原位更新的 CRC 状态。</param>
    /// <param name="data">本次累计的字节。</param>
    /// <param name="cancellationToken">每 64 KiB 检查的取消信号。</param>
    /// <exception cref="OperationCanceledException">累计期间取消。</exception>
    private static void UpdateCrc32(
        ref uint crc,
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < data.Length; index++)
        {
            if ((index & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var value = data[index];
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320U & (uint)-(int)(crc & 1));
            }
        }
    }

    /// <summary>
    /// 功能：为 zlib 提供写入前校验且底层容量绝不超过上限的内存输出。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    internal sealed class BoundedMemoryStream : MemoryStream
    {
        private readonly int maximumLength;

        /// <summary>
        /// 功能：创建按需增长但绝不超过指定字节数的内存流。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="maximumLength">允许写入的最大总字节数。</param>
        /// <param name="initialCapacity">不超过上限的初始容量。</param>
        internal BoundedMemoryStream(int maximumLength, int initialCapacity)
            : base(Math.Min(maximumLength, initialCapacity))
        {
            if (maximumLength <= 0 || initialCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumLength));
            }

            this.maximumLength = maximumLength;
        }

        /// <summary>
        /// 功能：在任何分配和写入前验证数组片段仍位于硬上限内。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="buffer">待写入数组。</param>
        /// <param name="offset">数组起始偏移。</param>
        /// <param name="count">待写入字节数。</param>
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWriteCapacity(count);
            base.Write(buffer, offset, count);
        }

        /// <summary>
        /// 功能：在任何分配和写入前验证 span 仍位于硬上限内。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="buffer">待写入字节。</param>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWriteCapacity(buffer.Length);
            base.Write(buffer);
        }

        /// <summary>
        /// 功能：在任何分配和写入前验证单字节仍位于硬上限内。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="value">待写入字节。</param>
        public override void WriteByte(byte value)
        {
            EnsureWriteCapacity(1);
            base.WriteByte(value);
        }

        /// <summary>
        /// 功能：拒绝超限写入，并用封顶倍增策略避免 MemoryStream 过量扩容。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        /// </summary>
        /// <param name="count">即将写入的字节数。</param>
        /// <exception cref="IOException">写入终点超过硬上限。</exception>
        private void EnsureWriteCapacity(int count)
        {
            var requiredLength = checked(Position + count);
            if (requiredLength > maximumLength)
            {
                throw new IOException("bounded desktop output exceeded");
            }

            if (requiredLength <= Capacity)
            {
                return;
            }

            var doubledCapacity = Math.Max(256L, (long)Capacity * 2);
            Capacity = checked((int)Math.Min(
                maximumLength,
                Math.Max(requiredLength, doubledCapacity)));
        }
    }

    /// <summary>
    /// 功能：发送一次按钮按下与释放，并在按下成功后尽力保证释放。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已在全局 gate 内打开的 X11 display。</param>
    /// <param name="button">X11 按钮编号。</param>
    /// <param name="cancellationToken">按下前取消信号；按下后始终先释放再观察后续取消。</param>
    /// <exception cref="OperationCanceledException">按钮尚未按下前取消。</exception>
    /// <exception cref="DesktopComputerException">按钮事件无法排队。</exception>
    private static void SendButton(
        nint display,
        uint button,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNativeSuccess(XTestFakeButtonEvent(display, button, true, 0));
        try
        {
            EnsureNativeSuccess(XTestFakeButtonEvent(display, button, false, 0));
        }
        catch
        {
            _ = XTestFakeButtonEvent(display, button, false, 0);
            throw;
        }
    }

    /// <summary>
    /// 功能：把有符号滚动格数映射为 X11 滚轮按钮。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已在全局 gate 内打开的 X11 display。</param>
    /// <param name="delta">有符号滚动格数。</param>
    /// <param name="positiveButton">正向 X11 按钮。</param>
    /// <param name="negativeButton">负向 X11 按钮。</param>
    /// <param name="cancellationToken">每格注入前取消信号。</param>
    /// <exception cref="OperationCanceledException">下一格尚未注入前取消。</exception>
    /// <exception cref="DesktopComputerException">按钮事件无法排队。</exception>
    private static void SendScroll(
        nint display,
        int delta,
        uint positiveButton,
        uint negativeButton,
        CancellationToken cancellationToken)
    {
        var button = delta >= 0 ? positiveButton : negativeButton;
        for (var index = 0; index < Math.Abs(delta); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendButton(display, button, cancellationToken);
        }
    }

    /// <summary>
    /// 功能：发送带显式修饰键的单个可移植按键。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已在全局 gate 内打开的 X11 display。</param>
    /// <param name="key">有限可移植命名键。</param>
    /// <param name="modifiers">唯一修饰键集合。</param>
    /// <param name="cancellationToken">修饰键与目标键按下前取消信号。</param>
    /// <exception cref="OperationCanceledException">尚未完成组合键前取消；已按下键仍会尽力释放。</exception>
    /// <exception cref="DesktopComputerException">键位映射或事件注入失败。</exception>
    private static void SendKey(
        nint display,
        string key,
        IReadOnlyList<string> modifiers,
        CancellationToken cancellationToken)
    {
        var (keycode, modifierCodes) = ResolveKeycodes(display, key, modifiers);
        SendKeycode(display, keycode, modifierCodes, cancellationToken);
    }

    /// <summary>
    /// 功能：解析目标键、显式修饰键及当前布局要求的隐式 Shift keycode。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已在全局 gate 内打开的 X11 display。</param>
    /// <param name="key">有限可移植命名键。</param>
    /// <param name="modifiers">唯一显式修饰键集合。</param>
    /// <returns>非零目标 keycode 与完整修饰键 keycode 列表。</returns>
    /// <remarks>不变量：目标仅位于 level 0/1；修饰键位于 level 0，且所有 keycode 互不冲突。</remarks>
    /// <exception cref="DesktopComputerException">目标键或修饰键无法映射。</exception>
    private static (uint Keycode, List<uint> Modifiers) ResolveKeycodes(
        nint display,
        string key,
        IReadOnlyList<string> modifiers)
    {
        var keysym = KeySymForPortableKey(key);
        var keycode = RequireKeycode(display, keysym);
        var keysymLevel = PortableKeysymLevel(
            keysym,
            XKeycodeToKeysym(display, keycode, 0),
            XKeycodeToKeysym(display, keycode, 1));
        if (keysymLevel < 0)
        {
            throw new DesktopComputerException();
        }

        var modifierCodes = new List<uint>(modifiers.Count + 1);
        foreach (var modifier in modifiers)
        {
            var modifierKeysym = ModifierKeysym(modifier);
            var modifierKeycode = RequireKeycode(display, modifierKeysym);
            if (XKeycodeToKeysym(display, modifierKeycode, 0) != modifierKeysym)
            {
                throw new DesktopComputerException();
            }

            modifierCodes.Add(modifierKeycode);
        }

        if (keysymLevel == 1 && !modifiers.Contains("shift", StringComparer.Ordinal))
        {
            var shiftKeysym = XStringToKeysym("Shift_L");
            var shiftKeycode = RequireKeycode(display, shiftKeysym);
            if (XKeycodeToKeysym(display, shiftKeycode, 0) != shiftKeysym)
            {
                throw new DesktopComputerException();
            }

            modifierCodes.Add(shiftKeycode);
        }

        if (!AreKeycodesDistinct(keycode, modifierCodes))
        {
            throw new DesktopComputerException();
        }

        return (keycode, modifierCodes);
    }

    /// <summary>
    /// 功能：确认目标 keysym 精确位于 keycode 的 level 0 或 level 1。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="expected">目标非零 keysym。</param>
    /// <param name="level0">当前 keycode 的 level 0 keysym。</param>
    /// <param name="level1">当前 keycode 的 level 1 keysym。</param>
    /// <returns>匹配 level 0 时为 0，匹配 level 1 时为 1，否则为 -1。</returns>
    /// <remarks>不变量：零目标与仅位于 level 2+ 的目标均失败关闭。</remarks>
    internal static int PortableKeysymLevel(nuint expected, nuint level0, nuint level1)
    {
        if (expected == 0)
        {
            return -1;
        }

        if (expected == level0)
        {
            return 0;
        }

        return expected == level1 ? 1 : -1;
    }

    /// <summary>
    /// 功能：验证目标与全部修饰键 keycode 均非零且互不重复。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="target">目标键 keycode。</param>
    /// <param name="modifiers">显式及隐式修饰键 keycode。</param>
    /// <returns>所有 keycode 非零、修饰键不重复且不与目标冲突时为 true。</returns>
    /// <remarks>不变量：任何别名或失败映射都在输入注入前失败关闭。</remarks>
    internal static bool AreKeycodesDistinct(uint target, IReadOnlyList<uint> modifiers)
    {
        if (target == 0)
        {
            return false;
        }

        var seen = new HashSet<uint> { target };
        foreach (var modifier in modifiers)
        {
            if (modifier == 0 || !seen.Add(modifier))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 功能：发送修饰键、目标键及反向释放序列。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已在全局 gate 内打开的 X11 display。</param>
    /// <param name="keycode">非零目标 keycode。</param>
    /// <param name="modifiers">已经映射的修饰键 keycode。</param>
    /// <param name="cancellationToken">完整组合键 press/release 单元开始前的取消信号。</param>
    /// <exception cref="OperationCanceledException">组合键尚未开始前取消。</exception>
    /// <exception cref="DesktopComputerException">键盘事件无法排队。</exception>
    private static void SendKeycode(
        nint display,
        uint keycode,
        List<uint> modifiers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pressedModifiers = new List<uint>(modifiers.Count);
        var keyPressed = false;
        var releaseFailed = false;
        try
        {
            foreach (var modifier in modifiers)
            {
                EnsureNativeSuccess(XTestFakeKeyEvent(display, modifier, true, 0));
                pressedModifiers.Add(modifier);
            }

            EnsureNativeSuccess(XTestFakeKeyEvent(display, keycode, true, 0));
            keyPressed = true;
            EnsureNativeSuccess(XTestFakeKeyEvent(display, keycode, false, 0));
            keyPressed = false;
        }
        finally
        {
            if (keyPressed)
            {
                releaseFailed |= XTestFakeKeyEvent(display, keycode, false, 0) == 0;
            }

            for (var index = pressedModifiers.Count - 1; index >= 0; index--)
            {
                releaseFailed |= XTestFakeKeyEvent(display, pressedModifiers[index], false, 0) == 0;
            }
        }

        if (releaseFailed)
        {
            throw new DesktopComputerException();
        }
    }

    /// <summary>
    /// 功能：把受限可移植键名转换为 X11 keysym。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="key">parser 已接受的有限可移植键名。</param>
    /// <returns>对应的非零 X11 keysym。</returns>
    /// <remarks>不变量：不把任意用户文本交给 XStringToKeysym。</remarks>
    /// <exception cref="DesktopComputerException">键名不在白名单或 Xlib 无法解析固定名称。</exception>
    private static nuint KeySymForPortableKey(string key)
    {
        var nativeName = key switch
        {
            "enter" => "Return",
            "tab" => "Tab",
            "escape" => "Escape",
            "backspace" => "BackSpace",
            "delete" => "Delete",
            "home" => "Home",
            "left" => "Left",
            "up" => "Up",
            "right" => "Right",
            "down" => "Down",
            "page_up" => "Page_Up",
            "page_down" => "Page_Down",
            "end" => "End",
            "space" => "space",
            _ => string.Empty,
        };
        var keysym = nativeName.Length == 0 ? 0 : XStringToKeysym(nativeName);
        return keysym != 0 ? keysym : throw new DesktopComputerException();
    }

    /// <summary>
    /// 功能：把受限修饰键名映射为左侧 X11 keysym。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="modifier">parser 已接受的有限修饰键名。</param>
    /// <returns>对应左侧修饰键的非零 X11 keysym。</returns>
    /// <remarks>不变量：只映射 shift/control/alt/meta，不接受任意 X11 名称。</remarks>
    /// <exception cref="DesktopComputerException">修饰键未知或 Xlib 无法解析固定名称。</exception>
    private static nuint ModifierKeysym(string modifier)
    {
        var nativeName = modifier switch
        {
            "shift" => "Shift_L",
            "control" => "Control_L",
            "alt" => "Alt_L",
            "meta" => "Super_L",
            _ => string.Empty,
        };
        var keysym = nativeName.Length == 0 ? 0 : XStringToKeysym(nativeName);
        return keysym != 0 ? keysym : throw new DesktopComputerException();
    }

    /// <summary>
    /// 功能：把非零 keysym 转为当前布局的非零 keycode。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已在全局 gate 内打开的 X11 display。</param>
    /// <param name="keysym">待映射的非零 keysym。</param>
    /// <returns>当前布局中的非零 keycode。</returns>
    /// <remarks>不变量：零映射不会进入 XTEST 注入。</remarks>
    /// <exception cref="DesktopComputerException">keysym 为零或当前布局没有映射。</exception>
    private static uint RequireKeycode(nint display, nuint keysym)
    {
        var keycode = keysym == 0 ? 0 : XKeysymToKeycode(display, keysym);
        return keycode != 0 ? keycode : throw new DesktopComputerException();
    }

    /// <summary>
    /// 功能：在全局 Xlib gate 内安装临时协议错误捕获并清零本次状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>必须在关闭 display 后恢复的前一个进程级 Xlib handler 指针。</returns>
    /// <remarks>不变量：调用方持有 XlibGate；回调不读取、不记录 display 或 XErrorEvent。</remarks>
    private static nint BeginXErrorCapture()
    {
        Volatile.Write(ref xProtocolErrorObserved, 0);
        return XSetErrorHandler(ManagedXErrorHandlerPointer);
    }

    /// <summary>
    /// 功能：与 X server round-trip，并确认本次临时 handler 未观察到异步协议错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已安装临时 handler 的 X11 display。</param>
    /// <returns>XSync 成功且没有协议错误时为 true。</returns>
    private static bool SynchronizeAndCheck(nint display)
    {
        return XSync(display, discard: false) != 0 &&
            Volatile.Read(ref xProtocolErrorObserved) == 0;
    }

    /// <summary>
    /// 功能：关闭 display 前尽力 drain 错误，并恢复进入本次调用前的进程级 Xlib handler。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">可能为零的当前 display。</param>
    /// <param name="previousHandler">BeginXErrorCapture 返回的原 handler。</param>
    /// <param name="handlerInstalled">临时 handler 是否已经安装成功。</param>
    /// <remarks>不变量：cleanup 失败绝不覆盖主操作结果；恢复发生在释放全局 gate 之前。</remarks>
    private static void CloseDisplayAndRestoreErrorHandler(
        nint display,
        nint previousHandler,
        bool handlerInstalled)
    {
        try
        {
            if (handlerInstalled && display != 0)
            {
                _ = XSync(display, discard: false);
            }

            if (display != 0)
            {
                _ = XCloseDisplay(display);
            }
        }
        catch
        {
            // cleanup must not replace the primary result
        }
        finally
        {
            if (handlerInstalled)
            {
                try
                {
                    _ = XSetErrorHandler(previousHandler);
                }
                catch
                {
                    // cleanup must not replace the primary result
                }
            }
        }
    }

    /// <summary>
    /// 功能：记录一次脱敏 Xlib 协议错误，不读取或公开任何原生错误内容。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">忽略的 display 指针。</param>
    /// <param name="errorEvent">忽略的 XErrorEvent 指针。</param>
    /// <returns>固定返回 0 交还 Xlib。</returns>
    private static int HandleXError(nint display, nint errorEvent)
    {
        _ = display;
        _ = errorEvent;
        Volatile.Write(ref xProtocolErrorObserved, 1);
        return 0;
    }

    /// <summary>
    /// 功能：要求原生 X11/XTEST 调用返回非零成功值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">原生调用返回的 X11 布尔成功值。</param>
    /// <remarks>不变量：任何零返回值在后续原生调用前失败关闭。</remarks>
    /// <exception cref="DesktopComputerException">原生调用返回零。</exception>
    private static void EnsureNativeSuccess(int value)
    {
        if (value == 0)
        {
            throw new DesktopComputerException();
        }
    }

    /// <summary>
    /// 功能：要求 JSON 字符串属性存在且非空。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">未经信任的 computer.interact 参数对象。</param>
    /// <param name="name">必须存在的精确属性名。</param>
    /// <returns>非空字符串值。</returns>
    /// <remarks>不变量：不执行类型转换或空值默认化。</remarks>
    /// <exception cref="ToolOperationException">属性缺失、类型错误或字符串为空。</exception>
    private static string RequireString(JsonElement arguments, string name)
    {
        return arguments.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(value.GetString())
                ? value.GetString()!
                : throw InvalidArguments();
    }

    /// <summary>
    /// 功能：要求 JSON 整数属性位于闭区间。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">未经信任的 computer.interact 参数对象。</param>
    /// <param name="name">必须存在的精确属性名。</param>
    /// <param name="minimum">允许的闭区间下界。</param>
    /// <param name="maximum">允许的闭区间上界。</param>
    /// <returns>位于闭区间内的 Int32。</returns>
    /// <remarks>不变量：拒绝浮点数、超出 Int32 的数值与区间外数值。</remarks>
    /// <exception cref="ToolOperationException">属性缺失、类型错误或数值越界。</exception>
    private static int RequireInteger(JsonElement arguments, string name, int minimum, int maximum)
    {
        return arguments.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var result) &&
            result >= minimum && result <= maximum
                ? result
                : throw InvalidArguments();
    }

    /// <summary>
    /// 功能：拒绝 action 未显式允许的任何额外字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">未经信任的 computer.interact 参数。</param>
    /// <param name="allowed">当前动作唯一允许的精确属性名集合。</param>
    /// <remarks>不变量：参数必须是对象，且属性名按 Ordinal 精确匹配白名单。</remarks>
    /// <exception cref="ToolOperationException">参数不是对象或包含额外字段。</exception>
    private static void RequireOnly(JsonElement arguments, params string[] allowed)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw InvalidArguments();
        }
    }

    /// <summary>
    /// 功能：创建不回显 computer 参数的标准验证错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>固定错误码、原因与工具名的脱敏异常。</returns>
    /// <remarks>不变量：错误不包含输入字段、DISPLAY、桌面内容或原生错误。</remarks>
    private static ToolOperationException InvalidArguments()
    {
        return new ToolOperationException(
            new QxnmForge.Domain.PortableError(
                -32602,
                "computer tool arguments are invalid",
                false,
                new QxnmForge.Domain.ErrorDetails("tool_arguments_invalid", ToolName: "computer.interact")),
            "validation_error");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XImageHeader
    {
        internal int Width;
        internal int Height;
        internal int XOffset;
        internal int Format;
        internal nint Data;
        internal int ByteOrder;
        internal int BitmapUnit;
        internal int BitmapBitOrder;
        internal int BitmapPad;
        internal int Depth;
        internal int BytesPerLine;
        internal int BitsPerPixel;
        internal nuint RedMask;
        internal nuint GreenMask;
        internal nuint BlueMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XVisualHeader
    {
        internal nint ExtensionData;
        internal nuint VisualId;
        internal int Class;
        internal nuint RedMask;
        internal nuint GreenMask;
        internal nuint BlueMask;
        internal int BitsPerRgb;
        internal int MapEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XPixmapFormatValues
    {
        internal int Depth;
        internal int BitsPerPixel;
        internal int ScanlinePad;
    }

    /// <summary>
    /// 功能：打开当前默认 X11 display。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="displayName">已规范化为显式 Unix transport 且不得记录的 DISPLAY。</param>
    /// <returns>成功时为非零 display 指针，失败时为零。</returns>
    /// <remarks>不变量：调用方只传入 NormalizeLocalDisplay 返回的本机名称，并在全局 gate 内关闭结果。</remarks>
    [DllImport(
        "libX11.so.6",
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern nint XOpenDisplay(
        [MarshalAs(UnmanagedType.LPStr)] string displayName);

    /// <summary>
    /// 功能：关闭由当前调用打开的 X11 display。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">由当前调用打开且尚未关闭的非零 display。</param>
    /// <returns>Xlib 状态值；cleanup 路径不得据此覆盖主结果。</returns>
    /// <remarks>不变量：仅在持有全局 gate 时调用一次。</remarks>
    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    /// <summary>
    /// 功能：取得默认 X11 screen 编号。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开的非零 display。</param>
    /// <returns>default screen 编号。</returns>
    /// <remarks>不变量：返回值只用于同一 display 的后续查询。</remarks>
    [DllImport("libX11.so.6")]
    private static extern int XDefaultScreen(nint display);

    /// <summary>
    /// 功能：取得指定 screen 的 root window。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开的非零 display。</param>
    /// <param name="screenNumber">同一 display 的 default screen 编号。</param>
    /// <returns>root window 标识，失败时可能为零。</returns>
    /// <remarks>不变量：调用方在任何捕获或注入前拒绝零值。</remarks>
    [DllImport("libX11.so.6")]
    private static extern nuint XRootWindow(nint display, int screenNumber);

    /// <summary>
    /// 功能：取得指定 screen 的像素宽度。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开的非零 display。</param>
    /// <param name="screenNumber">同一 display 的 default screen 编号。</param>
    /// <returns>当前 root 像素宽度。</returns>
    /// <remarks>不变量：调用方用 IsCaptureGeometrySafe 验证结果。</remarks>
    [DllImport("libX11.so.6")]
    private static extern int XDisplayWidth(nint display, int screenNumber);

    /// <summary>
    /// 功能：取得指定 screen 的像素高度。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开的非零 display。</param>
    /// <param name="screenNumber">同一 display 的 default screen 编号。</param>
    /// <returns>当前 root 像素高度。</returns>
    /// <remarks>不变量：调用方用 IsCaptureGeometrySafe 验证结果。</remarks>
    [DllImport("libX11.so.6")]
    private static extern int XDisplayHeight(nint display, int screenNumber);

    /// <summary>
    /// 功能：取得 default root 使用的 depth。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开的非零 display。</param>
    /// <param name="screenNumber">同一 display 的 default screen 编号。</param>
    /// <returns>default root depth。</returns>
    /// <remarks>不变量：调用方与同一 display 的 pixmap format 绑定并验证。</remarks>
    [DllImport("libX11.so.6")]
    private static extern int XDefaultDepth(nint display, int screenNumber);

    /// <summary>
    /// 功能：取得 default root 使用的 Visual 指针。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开的非零 display。</param>
    /// <param name="screenNumber">同一 display 的 default screen 编号。</param>
    /// <returns>default Visual 指针，失败时可能为零。</returns>
    /// <remarks>不变量：仅复制固定前缀并验证 TrueColor class 与颜色 mask。</remarks>
    [DllImport("libX11.so.6")]
    private static extern nint XDefaultVisual(nint display, int screenNumber);

    /// <summary>
    /// 功能：列出当前 display 的 depth、像素位数与 scanline 对齐格式。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开的非零 display。</param>
    /// <param name="countReturn">返回数组项数。</param>
    /// <returns>Xlib 分配的 pixmap format 数组，失败时为零。</returns>
    /// <remarks>不变量：项数受 1..256 限制，非零结果始终在 finally 中交给 XFree。</remarks>
    [DllImport("libX11.so.6")]
    private static extern nint XListPixmapFormats(nint display, out int countReturn);

    /// <summary>
    /// 功能：释放 Xlib 返回的通用分配块。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="data">Xlib 分配且尚未释放的非零指针。</param>
    /// <returns>Xlib 状态值；cleanup 路径不依赖该值。</returns>
    /// <remarks>不变量：仅释放 XListPixmapFormats 返回的块。</remarks>
    [DllImport("libX11.so.6")]
    private static extern int XFree(nint data);

    /// <summary>
    /// 功能：查询 root 指针位置与按钮掩码。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开的非零 display。</param>
    /// <param name="window">已验证的 default root window。</param>
    /// <param name="rootReturn">返回实际 root window。</param>
    /// <param name="childReturn">返回指针下的 child window。</param>
    /// <param name="rootXReturn">返回 root X 坐标。</param>
    /// <param name="rootYReturn">返回 root Y 坐标。</param>
    /// <param name="winXReturn">返回相对 window X 坐标。</param>
    /// <param name="winYReturn">返回相对 window Y 坐标。</param>
    /// <param name="maskReturn">返回当前按钮与修饰键 mask。</param>
    /// <returns>查询成功时非零，失败时为零。</returns>
    /// <remarks>不变量：只有成功返回后的 root 坐标会被读取，并再次按当前几何验证。</remarks>
    [DllImport("libX11.so.6")]
    private static extern int XQueryPointer(
        nint display,
        nuint window,
        out nuint rootReturn,
        out nuint childReturn,
        out int rootXReturn,
        out int rootYReturn,
        out int winXReturn,
        out int winYReturn,
        out uint maskReturn);

    /// <summary>
    /// 功能：读取 root 的 ZPixmap 图像。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开且持有全局 gate 的非零 display。</param>
    /// <param name="drawable">已验证的 default root。</param>
    /// <param name="x">捕获起点 X，固定为零。</param>
    /// <param name="y">捕获起点 Y，固定为零。</param>
    /// <param name="width">已通过上限与布局计算的完整 root 宽度。</param>
    /// <param name="height">已通过上限与布局计算的完整 root 高度。</param>
    /// <param name="planeMask">捕获 plane mask，固定为全部位。</param>
    /// <param name="format">图像格式，固定为 ZPixmap。</param>
    /// <returns>Xlib 分配的 XImage 指针，失败时为零。</returns>
    /// <remarks>不变量：返回 header 必须与冻结 depth、bpp、pad、stride 和长度逐字段一致。</remarks>
    [DllImport("libX11.so.6")]
    private static extern nint XGetImage(
        nint display,
        nuint drawable,
        int x,
        int y,
        uint width,
        uint height,
        nuint planeMask,
        int format);

    /// <summary>
    /// 功能：释放 XGetImage 返回的图像与像素缓冲。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="image">XGetImage 返回且尚未销毁的非零指针。</param>
    /// <returns>Xlib 状态值；cleanup 路径不依赖该值。</returns>
    /// <remarks>不变量：仅在 finally 中对非零捕获结果调用一次。</remarks>
    [DllImport("libX11.so.6")]
    private static extern int XDestroyImage(nint image);

    /// <summary>
    /// 功能：把 X11 keysym 映射为当前布局 keycode。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开的非零 display。</param>
    /// <param name="keysym">由固定可移植键白名单得到的非零 keysym。</param>
    /// <returns>当前布局中的 keycode，无法映射时为零。</returns>
    /// <remarks>不变量：调用方拒绝零，并复核目标 level 或修饰键 level 0。</remarks>
    [DllImport("libX11.so.6")]
    private static extern uint XKeysymToKeycode(nint display, nuint keysym);

    /// <summary>
    /// 功能：查询 keycode 指定 Shift level 的 keysym。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开的非零 display。</param>
    /// <param name="keycode">待复核的非零当前布局 keycode。</param>
    /// <param name="index">只允许查询 level 0 或 level 1 的索引。</param>
    /// <returns>该 level 的 keysym，未映射时可能为零。</returns>
    /// <remarks>不变量：目标必须精确位于 level 0/1，修饰键必须精确位于 level 0。</remarks>
    [DllImport("libX11.so.6")]
    private static extern nuint XKeycodeToKeysym(nint display, uint keycode, int index);

    /// <summary>
    /// 功能：把受限 ASCII/X11 键名映射为 keysym。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="value">代码内白名单映射产生的固定 ASCII X11 键名。</param>
    /// <returns>对应 keysym，未知名称返回零。</returns>
    /// <remarks>不变量：不接收用户任意字符串，且零结果在注入前失败关闭。</remarks>
    [DllImport(
        "libX11.so.6",
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern nuint XStringToKeysym([MarshalAs(UnmanagedType.LPStr)] string value);

    /// <summary>
    /// 功能：round-trip 同步 X11 请求并触发临时协议错误 handler。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开并安装临时错误 handler 的 display。</param>
    /// <param name="discard">是否丢弃待处理事件，固定为 false。</param>
    /// <returns>同步成功时非零，失败时为零。</returns>
    /// <remarks>不变量：同步后同时检查脱敏协议错误标记。</remarks>
    [DllImport("libX11.so.6")]
    private static extern int XSync(
        nint display,
        [MarshalAs(UnmanagedType.Bool)] bool discard);

    /// <summary>
    /// 功能：安装或恢复进程级 Xlib 协议错误 handler。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="handler">托管回调函数指针，或需要恢复的前一 handler。</param>
    /// <returns>安装前的进程级 handler 指针。</returns>
    /// <remarks>不变量：仅持有 XlibGate 时切换，并在释放 gate 前恢复。</remarks>
    [DllImport("libX11.so.6")]
    private static extern nint XSetErrorHandler(nint handler);

    /// <summary>
    /// 功能：查询 XTEST 扩展版本和事件基址。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已打开且持有全局 gate 的 display。</param>
    /// <param name="eventBase">返回 XTEST 事件基址。</param>
    /// <param name="errorBase">返回 XTEST 错误基址。</param>
    /// <param name="majorVersion">返回 XTEST major 版本。</param>
    /// <param name="minorVersion">返回 XTEST minor 版本。</param>
    /// <returns>扩展可用时非零，否则为零。</returns>
    /// <remarks>不变量：返回的基址与版本不公开；零值只关闭交互能力。</remarks>
    [DllImport("libXtst.so.6")]
    private static extern int XTestQueryExtension(
        nint display,
        out int eventBase,
        out int errorBase,
        out int majorVersion,
        out int minorVersion);

    /// <summary>
    /// 功能：通过 XTEST 注入绝对鼠标移动。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已审批且持有全局 gate 的 display。</param>
    /// <param name="screenNumber">已重新验证几何的 default screen。</param>
    /// <param name="x">当前 root 内的绝对 X 坐标。</param>
    /// <param name="y">当前 root 内的绝对 Y 坐标。</param>
    /// <param name="delay">服务端延迟，固定为零。</param>
    /// <returns>事件成功排队时非零，否则为零。</returns>
    /// <remarks>不变量：仅在执行边界复验 root 几何与坐标后调用。</remarks>
    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeMotionEvent(
        nint display,
        int screenNumber,
        int x,
        int y,
        nuint delay);

    /// <summary>
    /// 功能：通过 XTEST 注入鼠标按钮状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已审批且持有全局 gate 的 display。</param>
    /// <param name="button">parser 或有限滚轮映射产生的 X11 按钮编号。</param>
    /// <param name="isPress">true 表示按下，false 表示释放。</param>
    /// <param name="delay">服务端延迟，固定为零。</param>
    /// <returns>事件成功排队时非零，否则为零。</returns>
    /// <remarks>不变量：按下成功后所有退出路径都会尽力发送对应释放。</remarks>
    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeButtonEvent(
        nint display,
        uint button,
        [MarshalAs(UnmanagedType.Bool)] bool isPress,
        nuint delay);

    /// <summary>
    /// 功能：通过 XTEST 注入键盘 keycode 状态。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="display">已审批且持有全局 gate 的 display。</param>
    /// <param name="keycode">已验证 level 与唯一性的非零 keycode。</param>
    /// <param name="isPress">true 表示按下，false 表示释放。</param>
    /// <param name="delay">服务端延迟，固定为零。</param>
    /// <returns>事件成功排队时非零，否则为零。</returns>
    /// <remarks>不变量：已按下目标键与修饰键在所有退出路径中反向尽力释放。</remarks>
    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeKeyEvent(
        nint display,
        uint keycode,
        [MarshalAs(UnmanagedType.Bool)] bool isPress,
        nuint delay);
}
