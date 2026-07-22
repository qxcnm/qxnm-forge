use serde_json::json;
use tokio_util::sync::CancellationToken;

use crate::error::{AgentError, ErrorCode};

/// 桌面壳显式启用原生 computer 工具的品牌中立环境门。
pub(crate) const DESKTOP_COMPUTER_ENV: &str = "AGENT_CLIENT_DESKTOP_COMPUTER";

/// desktop computer 实验能力必须同时启用的第二道品牌中立环境门。
pub(crate) const EXPERIMENTAL_DESKTOP_COMPUTER_ENV: &str =
    "AGENT_CLIENT_EXPERIMENTAL_DESKTOP_COMPUTER";

const MAX_CAPTURE_DIMENSION: u32 = 16_384;
const MAX_CAPTURE_PIXELS: u64 = 16_777_216;
const MAX_CAPTURE_RAW_BYTES: u64 = 64 * 1024 * 1024;
pub(crate) const MAX_CAPTURE_PNG_BYTES: usize = 33_554_432;
const MAX_LOCAL_X11_DISPLAY_LENGTH: usize = 64;

/// 功能：描述启动期已探测成功的桌面观察与交互能力。
///
/// 不变量：实例只表示启动期探测结果；每次执行仍重新连接并失败关闭。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
#[derive(Debug, Clone, Copy)]
pub(crate) struct DesktopComputer {
    interaction_available: bool,
}

/// 功能：承载一次完整桌面捕获及其可公开几何信息。
///
/// 不变量：`png` 是完整 PNG，坐标属于同一 X11 root。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) struct DesktopCapture {
    pub(crate) png: Vec<u8>,
    pub(crate) width: u16,
    pub(crate) height: u16,
    pub(crate) pointer_x: i16,
    pub(crate) pointer_y: i16,
}

/// 功能：表示经过工具参数预检的封闭桌面交互动作。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
pub(crate) enum ComputerAction {
    Move {
        x: i16,
        y: i16,
    },
    Click {
        x: i16,
        y: i16,
        button: u8,
        clicks: u8,
    },
    Scroll {
        delta_x: i16,
        delta_y: i16,
    },
    Key {
        key: String,
        modifiers: Vec<String>,
    },
}

impl DesktopComputer {
    /// 功能：在受信任桌面宿主显式启用时探测当前平台 computer backend。
    ///
    /// 输入：无显式参数；读取品牌中立双门、Session 类型、Wayland 与 DISPLAY 环境值。
    /// 输出：屏幕观察不可用时为 None；交互能力独立记录。
    /// 不变量：两道环境门都只接受精确 `1`；Wayland 会话失败关闭；不截图、不注入输入、不弹权限框。
    /// 失败：探测错误按能力缺失处理，不泄露 DISPLAY 或宿主错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn detect() -> Option<Self> {
        if !desktop_environment_enabled(
            std::env::var_os(DESKTOP_COMPUTER_ENV).as_deref(),
            std::env::var_os(EXPERIMENTAL_DESKTOP_COMPUTER_ENV).as_deref(),
            std::env::var_os("XDG_SESSION_TYPE").as_deref(),
            std::env::var_os("WAYLAND_DISPLAY").as_deref(),
            std::env::var_os("DISPLAY").as_deref(),
        ) {
            return None;
        }
        platform::probe().map(|interaction_available| Self {
            interaction_available,
        })
    }

    /// 功能：返回当前 backend 是否真实支持鼠标与键盘注入。
    ///
    /// 输入：启动期探测成功后冻结的能力值。
    /// 输出：XTEST 交互能力已被正向探测时返回 true。
    /// 不变量：只读取冻结字段，不重探测环境、display 或扩展。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) const fn interaction_available(self) -> bool {
        self.interaction_available
    }

    /// 功能：在审批前重连并只读验证当前 desktop capture backend、布局与指针几何。
    ///
    /// 输入：启动期已探测的 computer 能力句柄。
    /// 输出：当前 root 仍满足捕获上限且 pointer 位于真实几何内时成功。
    /// 不变量：不读取像素、不编码 PNG、不注入输入；执行边界仍再次复核。
    /// 失败：连接、布局、pointer 或几何变化统一返回脱敏 backend 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn preflight_observe(self) -> Result<(), AgentError> {
        platform::preflight_observe()
    }

    /// 功能：在审批前重连并只读验证当前 XTEST、动作真实几何与按键映射。
    ///
    /// 输入：启动期能力句柄及已完成 schema 解析的封闭动作。
    /// 输出：XTEST 当前可用、绝对坐标仍位于真实 root 且 key chord 当前可映射时成功。
    /// 不变量：不读取像素、不移动鼠标、不发送按钮或按键；执行边界仍再次复核。
    /// 失败：交互未探测、连接、XTEST、几何或 keymap 变化统一返回脱敏 backend 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn preflight_interact(self, action: &ComputerAction) -> Result<(), AgentError> {
        if !self.interaction_available {
            return Err(unavailable_error());
        }
        platform::preflight_interact(action)
    }

    /// 功能：捕获当前桌面并编码为 PNG。
    ///
    /// 输入：启动期能力句柄与运行级取消令牌。
    /// 输出：PNG 字节、显示尺寸与当前指针坐标。
    /// 不变量：只转发到当前平台边界；取消、布局和资源限制由平台实现再次验证。
    /// 失败：显示连接、读取、像素转换或 PNG 编码失败时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn capture(
        self,
        cancellation: &CancellationToken,
    ) -> Result<DesktopCapture, AgentError> {
        platform::capture(cancellation)
    }

    /// 功能：执行已经过 schema、策略和审批约束的单个桌面动作。
    ///
    /// 输入：封闭的移动、点击、滚动或按键动作，以及运行级取消令牌。
    /// 输出：完整动作及必要释放均成功时返回空成功值。
    /// 不变量：未探测到 XTEST 时绝不尝试输入注入；每次动作后 flush。
    /// 失败：后端消失、键位不可映射或注入失败时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(crate) fn interact(
        self,
        action: &ComputerAction,
        cancellation: &CancellationToken,
    ) -> Result<(), AgentError> {
        if !self.interaction_available {
            return Err(unavailable_error());
        }
        platform::interact(action, cancellation)
    }
}

/// 功能：纯函数判定 desktop computer 双门与显示协议边界。
///
/// 输入：主门、实验门、XDG Session 类型、Wayland display 与 X11 DISPLAY 环境值。
/// 输出：仅当双门精确为 `1`、Session 类型可规范化为 x11、没有 Wayland display 且 DISPLAY 明确为本地 Unix 语法时为 true。
/// 不变量：Session 类型只做 ASCII 大小写与首尾空白规范化；DISPLAY 不做 trim，仅接受 `:N[.S]`/`unix/:N[.S]`；缺失、未知或非 UTF-8 值失败关闭。
/// 失败：本方法不返回错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn desktop_environment_enabled(
    enabled: Option<&std::ffi::OsStr>,
    experimental: Option<&std::ffi::OsStr>,
    session_type: Option<&std::ffi::OsStr>,
    wayland_display: Option<&std::ffi::OsStr>,
    display: Option<&std::ffi::OsStr>,
) -> bool {
    let one = std::ffi::OsStr::new("1");
    enabled == Some(one)
        && experimental == Some(one)
        && session_type
            .and_then(std::ffi::OsStr::to_str)
            .map(str::trim_ascii)
            .is_some_and(|value| value.eq_ignore_ascii_case("x11"))
        && wayland_display.is_none_or(std::ffi::OsStr::is_empty)
        && local_x11_display(display)
}

/// 功能：纯函数验证 desktop computer 只连接保守的本地 Unix X11 DISPLAY。
///
/// 输入：未经信任的 DISPLAY 环境值。
/// 输出：仅 `:N[.S]` 与 `unix/:N[.S]` 返回 true，其中 N/S 为可解析成 u16 的 ASCII 十进制。
/// 不变量：总长不超过 64；不 trim、不接受空段、多点、hostname、localhost、TCP/SSH forwarding、非 ASCII 数字或非 UTF-8。
/// 失败：本方法不返回错误；任何含糊或超界形式均失败关闭且不记录原值。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn local_x11_display(display: Option<&std::ffi::OsStr>) -> bool {
    canonical_local_x11_display(display).is_some()
}

/// 功能：验证并把允许的 DISPLAY 规范化为禁止 TCP fallback 的显式 Unix X11 目标。
///
/// 输入：未经信任的 DISPLAY 环境值。
/// 输出：合法 `:N[.S]`/`unix/:N[.S]` 返回 `unix/:N[.S]`，否则为 None。
/// 不变量：输出只含固定 `unix/:` 前缀、原数字段和可选点；传给 x11rb 后只生成 Unix socket 连接指令。
/// 失败：本方法不返回错误；超长、非 UTF-8、远程或含糊形式失败关闭且不回显原值。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn canonical_local_x11_display(display: Option<&std::ffi::OsStr>) -> Option<String> {
    let display = display.and_then(std::ffi::OsStr::to_str)?;
    if display.is_empty() || display.len() > MAX_LOCAL_X11_DISPLAY_LENGTH {
        return None;
    }
    let numbers = display
        .strip_prefix(':')
        .or_else(|| display.strip_prefix("unix/:"))?;
    let mut components = numbers.split('.');
    let display_number = components.next()?;
    let screen_number = components.next();
    if components.next().is_some() || !local_x11_display_component(display_number) {
        return None;
    }
    if !screen_number.is_none_or(local_x11_display_component) {
        return None;
    }
    Some(format!("unix/:{numbers}"))
}

/// 功能：验证一个 X11 display/screen 数字段是 u16 范围的非空 ASCII 十进制。
///
/// 输入：从已限制总长的 DISPLAY 中切出的单个字段。
/// 输出：全部为 ASCII 数字且数值属于 0..=65535 时返回 true。
/// 不变量：不接受符号、空白、Unicode 数字或空字段。
/// 失败：本方法不返回错误；解析失败保守返回 false。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn local_x11_display_component(component: &str) -> bool {
    !component.is_empty()
        && component.bytes().all(|byte| byte.is_ascii_digit())
        && component.parse::<u16>().is_ok()
}

/// 功能：创建不泄露桌面标识与底层库文本的 computer backend 错误。
///
/// 输入：无。
/// 输出：固定 code、message 与 kind 的结构化 backend 不可用错误。
/// 不变量：不包含 DISPLAY、路径、窗口标题、像素、键名或原生库诊断。
/// 失败：本函数始终构造错误值，本身不返回失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn unavailable_error() -> AgentError {
    AgentError::new(
        ErrorCode::InternalError,
        "desktop computer backend is unavailable",
    )
    .with_details(json!({"kind":"computer_unavailable"}))
}

/// 功能：创建不携带桌面内容的协作取消错误。
///
/// 输入：无。
/// 输出：固定 code、message 与 kind 的结构化取消错误。
/// 不变量：不包含 DISPLAY、路径、窗口标题、像素或动作参数。
/// 失败：本函数始终构造错误值，本身不返回失败。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn cancelled_error() -> AgentError {
    AgentError::new(
        ErrorCode::Cancelled,
        "desktop computer operation was cancelled",
    )
    .with_details(json!({"kind":"cancelled"}))
}

/// 功能：验证一次 root 捕获的尺寸、像素、行跨度与原始 reply 上限。
///
/// 输入：root 宽高、像素位数和 X11 scanline 对齐位数。
/// 输出：安全计算后的每行字节数和完整原始 reply 字节数。
/// 不变量：每维不超过 16384、像素不超过 16777216、原始估算不超过 64 MiB；所有算术均检查溢出。
/// 失败：零尺寸、格式不受支持、任一上限超出或算术溢出时返回脱敏 backend 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validated_capture_layout(
    width: u32,
    height: u32,
    bits_per_pixel: u8,
    scanline_pad: u8,
) -> Result<(usize, usize), AgentError> {
    if width == 0
        || height == 0
        || width > MAX_CAPTURE_DIMENSION
        || height > MAX_CAPTURE_DIMENSION
        || !matches!(bits_per_pixel, 24 | 32)
        || !matches!(scanline_pad, 8 | 16 | 32)
    {
        return Err(unavailable_error());
    }
    let pixels = u64::from(width)
        .checked_mul(u64::from(height))
        .ok_or_else(unavailable_error)?;
    if pixels > MAX_CAPTURE_PIXELS {
        return Err(unavailable_error());
    }
    let pad = u64::from(scanline_pad);
    let row_bits = u64::from(width)
        .checked_mul(u64::from(bits_per_pixel))
        .ok_or_else(unavailable_error)?;
    let row_bytes = row_bits
        .checked_add(pad - 1)
        .ok_or_else(unavailable_error)?
        / pad
        * pad
        / 8;
    let raw_bytes = row_bytes
        .checked_mul(u64::from(height))
        .ok_or_else(unavailable_error)?;
    if raw_bytes > MAX_CAPTURE_RAW_BYTES {
        return Err(unavailable_error());
    }
    Ok((
        usize::try_from(row_bytes).map_err(|_| unavailable_error())?,
        usize::try_from(raw_bytes).map_err(|_| unavailable_error())?,
    ))
}

/// 功能：验证编码后 PNG 不超过 computer 工具的持久化上限。
///
/// 输入：完整 PNG 字节长度。
/// 输出：不超过 33554432 字节时成功。
/// 不变量：比较使用 usize，不发生单位换算或截断。
/// 失败：超限时返回脱敏 backend 错误。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
fn validate_png_length(length: usize) -> Result<(), AgentError> {
    if length > MAX_CAPTURE_PNG_BYTES {
        return Err(unavailable_error());
    }
    Ok(())
}

#[cfg(target_os = "linux")]
mod platform {
    use std::io::{self, Write};

    use png::{BitDepth, ColorType, Encoder};
    use x11rb::CURRENT_TIME;
    use x11rb::connection::Connection;
    use x11rb::protocol::xproto::{
        self, BUTTON_PRESS_EVENT, BUTTON_RELEASE_EVENT, ConnectionExt as _, ImageFormat,
        ImageOrder, KEY_PRESS_EVENT, KEY_RELEASE_EVENT, MOTION_NOTIFY_EVENT,
    };
    use x11rb::protocol::xtest::ConnectionExt as _;
    use x11rb::rust_connection::RustConnection;

    use super::{
        ComputerAction, DesktopCapture, MAX_CAPTURE_DIMENSION, MAX_CAPTURE_PIXELS, cancelled_error,
        canonical_local_x11_display, unavailable_error, validate_png_length,
        validated_capture_layout,
    };
    use crate::error::AgentError;
    use tokio_util::sync::CancellationToken;

    /// 功能：为 PNG encoder 提供严格字节上限的内存 Write sink。
    ///
    /// 不变量：`bytes.len()` 永不超过 limit，且每次扩容只为本次已验证长度请求精确容量。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) struct BoundedPngWriter {
        bytes: Vec<u8>,
        limit: usize,
    }

    impl BoundedPngWriter {
        /// 功能：创建尚未分配 PNG 内容缓冲的有界 writer。
        ///
        /// 输入：允许编码的最大完整字节数。
        /// 输出：长度为零且固定上限的 writer。
        /// 不变量：limit 在实例生命周期内不变。
        /// 失败：本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub(super) const fn new(limit: usize) -> Self {
            Self {
                bytes: Vec::new(),
                limit,
            }
        }

        /// 功能：在 encoder 释放借用后取回完整且未超限的 PNG 字节。
        ///
        /// 输入：消费已完成编码的有界 writer。
        /// 输出：当前 writer 所有已写字节。
        /// 不变量：返回向量长度不超过构造上限。
        /// 失败：本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        pub(super) fn into_bytes(self) -> Vec<u8> {
            self.bytes
        }
    }

    impl Write for BoundedPngWriter {
        /// 功能：仅在整段输入仍落入固定上限时追加 PNG encoder 字节。
        ///
        /// 输入：encoder 生成的下一段压缩字节。
        /// 输出：整段接受时返回其长度；超限时零追加并返回 I/O error。
        /// 不变量：不做部分写入，避免 encoder 重试产生含糊内容；长度计算检查溢出。
        /// 失败：长度溢出、超过上限或精确 reserve 失败时返回不含桌面数据的 I/O error。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
            let new_length = self
                .bytes
                .len()
                .checked_add(buffer.len())
                .filter(|length| *length <= self.limit)
                .ok_or_else(|| io::Error::other("bounded PNG output limit exceeded"))?;
            if self.bytes.capacity() < new_length {
                self.bytes
                    .try_reserve_exact(new_length - self.bytes.len())
                    .map_err(|_| io::Error::other("bounded PNG output allocation failed"))?;
            }
            self.bytes.extend_from_slice(buffer);
            Ok(buffer.len())
        }

        /// 功能：满足内存 writer 的 Write flush 合同。
        ///
        /// 输入：当前有界 writer 的可变借用；没有外部数据输入。
        /// 输出：始终成功，因为所有字节已同步存在于进程内存。
        /// 不变量：不改变缓冲长度、容量或上限。
        /// 失败：本方法不返回错误。
        /// 作者：高宏顺
        /// 邮箱：18272669457@163.com
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    /// 功能：连接已由启动环境门正向证明的原生 X11，并验证 root 可观察，同时独立探测 XTEST。
    ///
    /// 输入：无显式参数；使用调用方已验证且本连接边界再次规范化的当前 DISPLAY。
    /// 输出：观察可用时返回 XTEST 是否可用；观察不可用为 None。
    /// 不变量：仅查询显示与扩展，不读取像素或注入事件。
    /// 失败：连接、root、布局、pointer 或扩展查询失败时保守返回 None，不泄露原生诊断。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn probe() -> Option<bool> {
        let (connection, screen_index) = connect().ok()?;
        validate_observe_backend(&connection, screen_index).ok()?;
        let interaction_available = connection
            .xtest_get_version(2, 2)
            .ok()
            .and_then(|cookie| cookie.reply().ok())
            .is_some();
        Some(interaction_available)
    }

    /// 功能：审批前重连原生 X11 并验证可观察 root，不读取任何像素。
    ///
    /// 输入：无显式参数；每次从当前环境重新取得并验证本地 Unix DISPLAY。
    /// 输出：布局与 pointer 当前均有效时成功。
    /// 不变量：只发送 setup/query_pointer 类只读请求。
    /// 失败：backend、布局或 pointer 异常统一脱敏。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn preflight_observe() -> Result<(), AgentError> {
        let (connection, screen_index) = connect()?;
        validate_observe_backend(&connection, screen_index)
    }

    /// 功能：审批前重连原生 X11 并验证 XTEST、动作几何与 keymap，不注入输入。
    ///
    /// 输入：已完成 schema 解析的封闭动作。
    /// 输出：XTEST 当前可用、坐标有效且 key chord 当前可完整映射时成功。
    /// 不变量：只查询扩展版本、setup geometry 与 keyboard mapping，不发送 XTEST fake input。
    /// 失败：backend、XTEST、geometry 或 keymap 异常统一脱敏。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn preflight_interact(action: &ComputerAction) -> Result<(), AgentError> {
        let (connection, screen_index) = connect()?;
        validate_interact_backend(&connection, screen_index, action)
    }

    /// 功能：从 X11 root 读取完整像素、转换为 RGB 并编码 PNG。
    ///
    /// 输入：贯穿连接、像素复制和编码边界的运行级取消令牌。
    /// 输出：不包含主机路径的内存 PNG 与几何信息。
    /// 不变量：执行时重新验证本地连接、root 几何、visual、reply 格式与长度，并在持久化前保持 PNG 上限。
    /// 失败：连接、reply、像素布局或编码异常统一返回 computer_unavailable。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn capture(cancellation: &CancellationToken) -> Result<DesktopCapture, AgentError> {
        ensure_not_cancelled(cancellation)?;
        let (connection, screen_index) = connect()?;
        let setup = connection.setup();
        let screen = setup
            .roots
            .get(screen_index)
            .ok_or_else(unavailable_error)?;
        if screen.root == 0 {
            return Err(unavailable_error());
        }
        let width = screen.width_in_pixels;
        let height = screen.height_in_pixels;
        let expected_format = setup
            .pixmap_formats
            .iter()
            .find(|format| format.depth == screen.root_depth)
            .ok_or_else(unavailable_error)?;
        let visual = validated_root_visual(screen, expected_format.bits_per_pixel)?;
        validated_capture_layout(
            u32::from(width),
            u32::from(height),
            expected_format.bits_per_pixel,
            expected_format.scanline_pad,
        )?;
        ensure_not_cancelled(cancellation)?;
        let pointer = connection
            .query_pointer(screen.root)
            .map_err(|_| unavailable_error())?
            .reply()
            .map_err(|_| unavailable_error())?;
        if !pointer.same_screen
            || !coordinate_inside_root(width, height, pointer.root_x, pointer.root_y)
        {
            return Err(unavailable_error());
        }
        ensure_not_cancelled(cancellation)?;
        let image = connection
            .get_image(
                ImageFormat::Z_PIXMAP,
                screen.root,
                0,
                0,
                width,
                height,
                u32::MAX,
            )
            .map_err(|_| unavailable_error())?
            .reply()
            .map_err(|_| unavailable_error())?;
        ensure_not_cancelled(cancellation)?;
        if image.depth != screen.root_depth || image.visual != screen.root_visual {
            return Err(unavailable_error());
        }
        let rgb = decode_rgb(
            &image.data,
            width,
            height,
            expected_format.bits_per_pixel,
            expected_format.scanline_pad,
            setup.image_byte_order,
            visual.red_mask,
            visual.green_mask,
            visual.blue_mask,
            cancellation,
        )?;
        ensure_not_cancelled(cancellation)?;
        let mut png_writer = BoundedPngWriter::new(super::MAX_CAPTURE_PNG_BYTES);
        {
            let mut encoder = Encoder::new(&mut png_writer, u32::from(width), u32::from(height));
            encoder.set_color(ColorType::Rgb);
            encoder.set_depth(BitDepth::Eight);
            let mut writer = encoder.write_header().map_err(|_| unavailable_error())?;
            writer
                .write_image_data(&rgb)
                .map_err(|_| unavailable_error())?;
        }
        let png = png_writer.into_bytes();
        ensure_not_cancelled(cancellation)?;
        validate_png_length(png.len())?;
        Ok(DesktopCapture {
            png,
            width,
            height,
            pointer_x: pointer.root_x,
            pointer_y: pointer.root_y,
        })
    }

    /// 功能：通过 XTEST 执行一个受审批保护的封闭交互动作。
    ///
    /// 输入：已完成字段与边界验证的动作及运行级取消令牌。
    /// 输出：完整动作、必要释放与连接 flush 均成功时返回空成功值。
    /// 不变量：执行边界重新验证 XTEST、root geometry 与 keymap；输入单元失败时尽力补偿释放。
    /// 失败：连接、键位映射或 XTEST flush 失败时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn interact(
        action: &ComputerAction,
        cancellation: &CancellationToken,
    ) -> Result<(), AgentError> {
        ensure_not_cancelled(cancellation)?;
        let (connection, screen_index) = connect()?;
        validate_interact_backend(&connection, screen_index, action)?;
        let screen = connection
            .setup()
            .roots
            .get(screen_index)
            .ok_or_else(unavailable_error)?;
        ensure_not_cancelled(cancellation)?;
        match action {
            ComputerAction::Move { x, y } => {
                move_pointer(&connection, screen.root, *x, *y, cancellation)?;
            }
            ComputerAction::Click {
                x,
                y,
                button,
                clicks,
            } => {
                move_pointer(&connection, screen.root, *x, *y, cancellation)?;
                for _ in 0..*clicks {
                    fake_button(&connection, screen.root, *button, cancellation)?;
                }
            }
            ComputerAction::Scroll { delta_x, delta_y } => {
                scroll_axis(&connection, screen.root, *delta_y, 4, 5, cancellation)?;
                scroll_axis(&connection, screen.root, *delta_x, 6, 7, cancellation)?;
            }
            ComputerAction::Key { key, modifiers } => {
                send_key(&connection, screen.root, key, modifiers, cancellation)?;
            }
        }
        connection.flush().map_err(|_| unavailable_error())?;
        Ok(())
    }

    /// 功能：复核当前 DISPLAY 为本地 Unix 语法后建立默认 X11 连接并返回 screen 索引。
    ///
    /// 输入：进程当前 DISPLAY 环境值；通过 allowlist 后规范化为显式 `unix/:N[.S]` 连接目标。
    /// 输出：仅本地 allowlist 通过且连接成功时返回连接与 screen 索引。
    /// 不变量：每次 preflight/capture/interact 均经此边界重新校验；显式 Unix protocol 禁止 x11rb 的 localhost TCP fallback；不回显 DISPLAY。
    /// 失败：DISPLAY 缺失/改变/含糊、非 UTF-8 或 Unix socket 连接失败统一返回脱敏 backend 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn connect() -> Result<(RustConnection, usize), AgentError> {
        let display = std::env::var_os("DISPLAY");
        let display =
            canonical_local_x11_display(display.as_deref()).ok_or_else(unavailable_error)?;
        x11rb::connect(Some(&display)).map_err(|_| unavailable_error())
    }

    /// 功能：在既有连接上只读复核 root 捕获布局和 pointer 几何。
    ///
    /// 输入：当前 X11 连接与 screen 索引。
    /// 输出：root 格式/TrueColor visual 满足解码与资源上限且 pointer 位于同屏半开区间时成功。
    /// 不变量：不调用 get_image，不读取像素；root visual 必须属于 root depth 且 RGB mask 非零、互斥。
    /// 失败：索引、格式、visual、查询或 pointer 几何异常返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validate_observe_backend(
        connection: &RustConnection,
        screen_index: usize,
    ) -> Result<(), AgentError> {
        let setup = connection.setup();
        let screen = setup
            .roots
            .get(screen_index)
            .ok_or_else(unavailable_error)?;
        if screen.root == 0 {
            return Err(unavailable_error());
        }
        let format = setup
            .pixmap_formats
            .iter()
            .find(|format| format.depth == screen.root_depth)
            .ok_or_else(unavailable_error)?;
        let _ = validated_root_visual(screen, format.bits_per_pixel)?;
        validated_capture_layout(
            u32::from(screen.width_in_pixels),
            u32::from(screen.height_in_pixels),
            format.bits_per_pixel,
            format.scanline_pad,
        )?;
        let pointer = connection
            .query_pointer(screen.root)
            .map_err(|_| unavailable_error())?
            .reply()
            .map_err(|_| unavailable_error())?;
        if !pointer.same_screen
            || !coordinate_inside_root(
                screen.width_in_pixels,
                screen.height_in_pixels,
                pointer.root_x,
                pointer.root_y,
            )
        {
            return Err(unavailable_error());
        }
        Ok(())
    }

    /// 功能：在既有连接上只读复核 XTEST 版本、当前动作 root geometry 与 key chord 映射。
    ///
    /// 输入：当前 X11 连接、screen 索引和封闭动作。
    /// 输出：扩展查询成功、动作坐标有效且 key 动作当前可完整映射时成功。
    /// 不变量：仅查询 setup、XTEST 与 keyboard mapping，不发送任何 fake input。
    /// 失败：XTEST、screen、geometry 或 keymap 异常返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validate_interact_backend(
        connection: &RustConnection,
        screen_index: usize,
        action: &ComputerAction,
    ) -> Result<(), AgentError> {
        connection
            .xtest_get_version(2, 2)
            .map_err(|_| unavailable_error())?
            .reply()
            .map_err(|_| unavailable_error())?;
        let screen = connection
            .setup()
            .roots
            .get(screen_index)
            .ok_or_else(unavailable_error)?;
        if screen.root == 0 {
            return Err(unavailable_error());
        }
        validate_action_geometry(screen.width_in_pixels, screen.height_in_pixels, action)?;
        if let ComputerAction::Key { key, modifiers } = action {
            let _ = resolve_key_action(connection, key, modifiers)?;
        }
        Ok(())
    }

    /// 功能：从 root depth 中查找并验证可由本实现安全解码的 root TrueColor visual。
    ///
    /// 输入：当前连接 setup 中的 screen 描述与 root pixmap 的实际 bits-per-pixel。
    /// 输出：visual ID 精确等于 root_visual 且 RGB mask 可独立解码的 visual。
    /// 不变量：visual 必须属于 root_depth、class=TRUE_COLOR，三个 mask 连续、非零、互斥且不超出像素位宽；不发起 X11 请求。
    /// 失败：depth/visual 缺失、类型不支持或 mask 不可完整解码时返回脱敏 backend 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn validated_root_visual(
        screen: &xproto::Screen,
        bits_per_pixel: u8,
    ) -> Result<&xproto::Visualtype, AgentError> {
        let visual = screen
            .allowed_depths
            .iter()
            .find(|depth| depth.depth == screen.root_depth)
            .and_then(|depth| {
                depth
                    .visuals
                    .iter()
                    .find(|visual| visual.visual_id == screen.root_visual)
            })
            .ok_or_else(unavailable_error)?;
        if visual.class != xproto::VisualClass::TRUE_COLOR
            || !true_color_masks_are_decodable(
                bits_per_pixel,
                visual.red_mask,
                visual.green_mask,
                visual.blue_mask,
            )
        {
            return Err(unavailable_error());
        }
        Ok(visual)
    }

    /// 功能：纯函数验证 RGB mask 可由当前 TrueColor decoder 无歧义缩放。
    ///
    /// 输入：24/32 bits-per-pixel 与三个 visual mask。
    /// 输出：每个 mask 非零、连续、不超出像素位宽且两两不重叠时返回 true。
    /// 不变量：不接受空洞 mask、超位宽位、重叠通道或其他像素位宽；不分配内存。
    /// 失败：本方法不返回错误；任何含糊布局保守返回 false。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn true_color_masks_are_decodable(
        bits_per_pixel: u8,
        red_mask: u32,
        green_mask: u32,
        blue_mask: u32,
    ) -> bool {
        let valid_bits = match bits_per_pixel {
            24 => (1_u32 << 24) - 1,
            32 => u32::MAX,
            _ => return false,
        };
        let mask_is_contiguous = |mask: u32| {
            if mask == 0 || mask & !valid_bits != 0 {
                return false;
            }
            let normalized = mask >> mask.trailing_zeros();
            normalized.count_ones() == normalized.trailing_ones()
        };
        mask_is_contiguous(red_mask)
            && mask_is_contiguous(green_mask)
            && mask_is_contiguous(blue_mask)
            && red_mask & green_mask == 0
            && red_mask & blue_mask == 0
            && green_mask & blue_mask == 0
    }

    /// 功能：在阻塞 X11 边界之间检查运行级协作取消。
    ///
    /// 输入：与 Agent run 绑定的取消令牌。
    /// 输出：未取消时成功。
    /// 不变量：不读取或返回桌面数据。
    /// 失败：取消已发生时返回稳定 Cancelled 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn ensure_not_cancelled(cancellation: &CancellationToken) -> Result<(), AgentError> {
        if cancellation.is_cancelled() {
            return Err(cancelled_error());
        }
        Ok(())
    }

    /// 功能：按执行时真实 root 几何复核绝对坐标动作。
    ///
    /// 输入：当前 root 宽高与已解析动作。
    /// 输出：无坐标动作或坐标落在 `[0,width)`、`[0,height)` 时成功。
    /// 不变量：绝不依赖启动期探测缓存；边界坐标 width/height 本身无效。
    /// 失败：负数、越界或零尺寸 root 返回脱敏 backend 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn validate_action_geometry(
        width: u16,
        height: u16,
        action: &ComputerAction,
    ) -> Result<(), AgentError> {
        if width == 0
            || height == 0
            || u32::from(width) > MAX_CAPTURE_DIMENSION
            || u32::from(height) > MAX_CAPTURE_DIMENSION
            || u64::from(width) * u64::from(height) > MAX_CAPTURE_PIXELS
        {
            return Err(unavailable_error());
        }
        let coordinates = match action {
            ComputerAction::Move { x, y } | ComputerAction::Click { x, y, .. } => Some((*x, *y)),
            ComputerAction::Scroll { .. } | ComputerAction::Key { .. } => None,
        };
        if let Some((x, y)) = coordinates {
            if !coordinate_inside_root(width, height, x, y) {
                return Err(unavailable_error());
            }
        }
        Ok(())
    }

    /// 功能：纯函数判定有符号绝对坐标是否位于真实 root 半开区间。
    ///
    /// 输入：当前 root 宽高及 X11 有符号坐标。
    /// 输出：仅 `[0,width)` 与 `[0,height)` 内返回 true。
    /// 不变量：负数、零尺寸及右/下边界本身均返回 false。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn coordinate_inside_root(width: u16, height: u16, x: i16, y: i16) -> bool {
        let Ok(x) = u16::try_from(x) else {
            return false;
        };
        let Ok(y) = u16::try_from(y) else {
            return false;
        };
        x < width && y < height
    }

    /// 功能：把 X11 ZPixmap 按 visual mask 转换为紧凑 RGB8。
    ///
    /// 输入：reply 字节、尺寸、像素格式、行对齐、字节序与 RGB mask。
    /// 输出：恰好 `width * height * 3` 字节。
    /// 不变量：仅接受 24/32 bit TrueColor 风格布局；reply 长度必须精确等于逻辑像素数据按 X11 4 字节 wire padding 向上取整后的长度。
    /// 失败：布局不受支持、wire 数据截断/超长或 padding 长度计算溢出时返回脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[allow(clippy::too_many_arguments)]
    pub(super) fn decode_rgb(
        data: &[u8],
        width: u16,
        height: u16,
        bits_per_pixel: u8,
        scanline_pad: u8,
        byte_order: ImageOrder,
        red_mask: u32,
        green_mask: u32,
        blue_mask: u32,
        cancellation: &CancellationToken,
    ) -> Result<Vec<u8>, AgentError> {
        if !true_color_masks_are_decodable(bits_per_pixel, red_mask, green_mask, blue_mask) {
            return Err(unavailable_error());
        }
        let bytes_per_pixel = usize::from(bits_per_pixel / 8);
        let (row_bytes, expected) = validated_capture_layout(
            u32::from(width),
            u32::from(height),
            bits_per_pixel,
            scanline_pad,
        )?;
        let wire_expected = expected
            .checked_add(3)
            .map(|length| length & !3)
            .ok_or_else(unavailable_error)?;
        if data.len() != wire_expected {
            return Err(unavailable_error());
        }
        let output_len = usize::from(width)
            .checked_mul(usize::from(height))
            .and_then(|pixels| pixels.checked_mul(3))
            .ok_or_else(unavailable_error)?;
        let mut output = Vec::new();
        output
            .try_reserve_exact(output_len)
            .map_err(|_| unavailable_error())?;
        for y in 0..usize::from(height) {
            ensure_not_cancelled(cancellation)?;
            let row = &data[y * row_bytes..(y + 1) * row_bytes];
            for x in 0..usize::from(width) {
                let start = x * bytes_per_pixel;
                let bytes = &row[start..start + bytes_per_pixel];
                let pixel = if byte_order == ImageOrder::LSB_FIRST {
                    bytes
                        .iter()
                        .enumerate()
                        .fold(0_u32, |value, (index, byte)| {
                            value | (u32::from(*byte) << (index * 8))
                        })
                } else {
                    bytes
                        .iter()
                        .fold(0_u32, |value, byte| (value << 8) | u32::from(*byte))
                };
                output.push(mask_component(pixel, red_mask)?);
                output.push(mask_component(pixel, green_mask)?);
                output.push(mask_component(pixel, blue_mask)?);
            }
        }
        Ok(output)
    }

    /// 功能：按连续 visual mask 把像素分量缩放到 0..255。
    ///
    /// 输入：单个原始像素值与已由 visual 校验确认连续、非零的颜色 mask。
    /// 输出：线性缩放后的 8-bit 分量。
    /// 不变量：只读取 mask 覆盖位并使用 u64 中间值，结果不得超过 255。
    /// 失败：空 mask 返回 backend 不可用。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn mask_component(pixel: u32, mask: u32) -> Result<u8, AgentError> {
        if mask == 0 {
            return Err(unavailable_error());
        }
        let shift = mask.trailing_zeros();
        let maximum = mask >> shift;
        let value = (pixel & mask) >> shift;
        u8::try_from((u64::from(value) * 255) / u64::from(maximum)).map_err(|_| unavailable_error())
    }

    /// 功能：发送绝对指针移动事件。
    ///
    /// 输入：当前连接、root、已验证半开几何内的绝对坐标及取消令牌。
    /// 输出：服务端确认唯一 motion 事件时成功。
    /// 不变量：发送前检查取消；事件必须经过 checked X11 cookie 确认。
    /// 失败：取消、构造、传输或服务端错误返回结构化脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn move_pointer(
        connection: &RustConnection,
        root: xproto::Window,
        x: i16,
        y: i16,
        cancellation: &CancellationToken,
    ) -> Result<(), AgentError> {
        ensure_not_cancelled(cancellation)?;
        checked_fake_input(connection, MOTION_NOTIFY_EVENT, 0, root, x, y)
    }

    /// 功能：发送一次完整鼠标按钮按下与释放。
    ///
    /// 输入：当前连接、root、封闭按钮编号及取消令牌。
    /// 输出：press 与 release 均经服务端确认时成功。
    /// 不变量：press 已尝试后任一步失败都会尽力再次 release 并 flush，补偿错误不覆盖原错误。
    /// 失败：取消、press/release 构造、传输或服务端错误返回首个脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn fake_button(
        connection: &RustConnection,
        root: xproto::Window,
        button: u8,
        cancellation: &CancellationToken,
    ) -> Result<(), AgentError> {
        ensure_not_cancelled(cancellation)?;
        if let Err(error) = checked_fake_input(connection, BUTTON_PRESS_EVENT, button, root, 0, 0) {
            best_effort_button_release(connection, root, button);
            return Err(error);
        }
        if let Err(error) = checked_fake_input(connection, BUTTON_RELEASE_EVENT, button, root, 0, 0)
        {
            best_effort_button_release(connection, root, button);
            return Err(error);
        }
        Ok(())
    }

    /// 功能：在保留原始按钮注入错误时尽力再次释放并 flush。
    ///
    /// 输入：当前 X11 连接、root 与可能仍按下的按钮编号。
    /// 输出：无；补偿结果不得覆盖调用方保存的原始失败。
    /// 不变量：不检查取消，确保 press/release 原子单元不会因协作取消被拆开。
    /// 失败：所有补偿错误被有意忽略且不写入日志。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn best_effort_button_release(connection: &RustConnection, root: xproto::Window, button: u8) {
        let _ = checked_fake_input(connection, BUTTON_RELEASE_EVENT, button, root, 0, 0);
        let _ = connection.flush();
    }

    /// 功能：把有符号滚动格数映射为 X11 滚轮按钮事件。
    ///
    /// 输入：当前连接、root、有界格数、正负方向按钮及取消令牌。
    /// 输出：绝对格数对应的全部按钮 press/release 单元成功时返回空成功值。
    /// 不变量：每格复用带补偿的按钮原子单元；零格不发送事件。
    /// 失败：任一格取消或注入失败时立即返回首个脱敏错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn scroll_axis(
        connection: &RustConnection,
        root: xproto::Window,
        delta: i16,
        positive_button: u8,
        negative_button: u8,
        cancellation: &CancellationToken,
    ) -> Result<(), AgentError> {
        let button = if delta >= 0 {
            positive_button
        } else {
            negative_button
        };
        for _ in 0..delta.unsigned_abs() {
            fake_button(connection, root, button, cancellation)?;
        }
        Ok(())
    }

    /// 功能：发送带显式修饰键的单个可移植按键。
    ///
    /// 输入：当前连接、root、封闭命名键、唯一修饰键集合及取消令牌。
    /// 输出：映射、目标键与全部修饰键释放成功时返回空成功值。
    /// 不变量：解析前后均检查取消；只把已验证且互不冲突的 keycode 交给注入边界。
    /// 失败：键名或当前布局不可映射时失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn send_key(
        connection: &RustConnection,
        root: xproto::Window,
        key: &str,
        modifiers: &[String],
        cancellation: &CancellationToken,
    ) -> Result<(), AgentError> {
        ensure_not_cancelled(cancellation)?;
        let (keycode, modifier_keycodes) = resolve_key_action(connection, key, modifiers)?;
        ensure_not_cancelled(cancellation)?;
        send_keycode(connection, root, keycode, &modifier_keycodes)
    }

    /// 功能：只读解析一个受限 key chord 在当前 X11 keyboard mapping 中的完整 keycode 集合。
    ///
    /// 输入：当前连接、schema 已限制的 named key 与唯一 modifiers。
    /// 输出：目标 keycode 及按下顺序的显式/隐式 Shift modifier keycodes。
    /// 不变量：只调用 GetKeyboardMapping，不注入或按下任何键；目标位于 Shift 层且调用方未给 shift 时补充左 Shift。
    /// 失败：键名、修饰键或当前映射缺失/异常时返回脱敏 backend 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn resolve_key_action(
        connection: &RustConnection,
        key: &str,
        modifiers: &[String],
    ) -> Result<(u8, Vec<u8>), AgentError> {
        let mut modifier_keycodes = modifiers
            .iter()
            .map(|modifier| {
                keycode_for_keysym(connection, modifier_keysym(modifier)?, false)
                    .map(|value| value.0)
            })
            .collect::<Result<Vec<_>, _>>()?;
        let keysym = named_keysym(key)?;
        let (keycode, needs_shift) = keycode_for_keysym(connection, keysym, true)?;
        if needs_shift && !modifiers.iter().any(|modifier| modifier == "shift") {
            modifier_keycodes.push(keycode_for_keysym(connection, 0xffe1, false)?.0);
        }
        if !key_chord_keycodes_are_distinct(keycode, &modifier_keycodes) {
            return Err(unavailable_error());
        }
        Ok((keycode, modifier_keycodes))
    }

    /// 功能：纯函数验证一个已解析 key chord 不会复用目标或 modifier keycode。
    ///
    /// 输入：目标 keycode 与显式/隐式 modifier keycodes。
    /// 输出：modifier 两两唯一且均不等于目标时返回 true。
    /// 不变量：使用固定 256 位栈上表覆盖完整 u8 keycode 空间，不分配内存。
    /// 失败：本方法不返回错误；任何重复或目标冲突保守返回 false。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn key_chord_keycodes_are_distinct(keycode: u8, modifiers: &[u8]) -> bool {
        let mut seen = [false; 256];
        for modifier in modifiers {
            if *modifier == keycode || seen[usize::from(*modifier)] {
                return false;
            }
            seen[usize::from(*modifier)] = true;
        }
        true
    }

    /// 功能：发送修饰键、目标键及反向释放序列。
    ///
    /// 输入：当前连接、root、已验证目标 keycode 与唯一且不冲突的 modifier keycodes。
    /// 输出：所有 press/release 事件经服务端确认时返回空成功值。
    /// 不变量：记录所有已尝试 modifier；目标及修饰键发生失败后均按安全顺序尽力释放。
    /// 失败：任一注入或释放失败时返回首个脱敏错误，补偿失败不泄露 keymap。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn send_keycode(
        connection: &RustConnection,
        root: xproto::Window,
        keycode: u8,
        modifiers: &[u8],
    ) -> Result<(), AgentError> {
        let mut attempted_modifiers = Vec::new();
        for modifier in modifiers {
            attempted_modifiers.push(*modifier);
            if let Err(error) =
                checked_fake_input(connection, KEY_PRESS_EVENT, *modifier, root, 0, 0)
            {
                best_effort_key_release(connection, root, keycode, &attempted_modifiers, false);
                return Err(error);
            }
        }
        if let Err(error) = checked_fake_input(connection, KEY_PRESS_EVENT, keycode, root, 0, 0) {
            best_effort_key_release(connection, root, keycode, &attempted_modifiers, true);
            return Err(error);
        }
        if let Err(error) = checked_fake_input(connection, KEY_RELEASE_EVENT, keycode, root, 0, 0) {
            best_effort_key_release(connection, root, keycode, &attempted_modifiers, true);
            return Err(error);
        }
        let mut first_release_error = None;
        for modifier in modifiers.iter().rev() {
            if let Err(error) =
                checked_fake_input(connection, KEY_RELEASE_EVENT, *modifier, root, 0, 0)
            {
                first_release_error.get_or_insert(error);
            }
        }
        if let Some(error) = first_release_error {
            best_effort_key_release(connection, root, keycode, &attempted_modifiers, true);
            return Err(error);
        }
        Ok(())
    }

    /// 功能：在任一 chord 注入失败后尽力释放目标键和全部已尝试修饰键。
    ///
    /// 输入：当前 X11 连接、root、目标 keycode、可能已尝试按下的修饰键及目标 press 是否已尝试。
    /// 输出：无；调用方继续返回最先观察到的原始错误。
    /// 不变量：目标 press 未尝试时绝不释放目标键；随后逆序释放已尝试修饰键并最终 flush；不观察协作取消。
    /// 失败：补偿错误被有意忽略且不得泄露键名或 display 信息。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn best_effort_key_release(
        connection: &RustConnection,
        root: xproto::Window,
        keycode: u8,
        modifiers: &[u8],
        target_press_attempted: bool,
    ) {
        for_each_compensating_keycode(keycode, modifiers, target_press_attempted, |detail| {
            let _ = checked_fake_input(connection, KEY_RELEASE_EVENT, detail, root, 0, 0);
        });
        let _ = connection.flush();
    }

    /// 功能：按安全补偿顺序枚举实际允许释放的目标/修饰 keycode。
    ///
    /// 输入：目标 keycode、已尝试 modifiers、目标 press 是否已尝试及无副作用回调。
    /// 输出：目标已尝试时先枚举目标，随后始终逆序枚举 modifiers。
    /// 不变量：target_press_attempted=false 时回调绝不收到目标 keycode；函数自身不连接 X11、不分配内存。
    /// 失败：本方法不返回错误；回调负责处理单个释放结果。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn for_each_compensating_keycode(
        keycode: u8,
        modifiers: &[u8],
        target_press_attempted: bool,
        mut release: impl FnMut(u8),
    ) {
        if target_press_attempted {
            release(keycode);
        }
        for modifier in modifiers.iter().rev() {
            release(*modifier);
        }
    }

    /// 功能：发送一次 XTEST 事件并同步检查对应 VoidCookie 的服务端结果。
    ///
    /// 输入：连接、事件类型、detail、root 与绝对/占位坐标。
    /// 输出：请求已获服务端成功确认时返回成功。
    /// 不变量：每一个输入注入 VoidCookie 都必须经本边界 `.check()`，错误文本不外泄。
    /// 失败：请求构造、传输或服务端 X11 error 均映射为脱敏 backend 错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn checked_fake_input(
        connection: &RustConnection,
        event_type: u8,
        detail: u8,
        root: xproto::Window,
        x: i16,
        y: i16,
    ) -> Result<(), AgentError> {
        connection
            .xtest_fake_input(event_type, detail, CURRENT_TIME, root, x, y, 0)
            .map_err(|_| unavailable_error())?
            .check()
            .map_err(|_| unavailable_error())
    }

    /// 功能：在当前键盘映射中查找 keysym 的 base 或首个 Shift 层 keycode。
    ///
    /// 输入：当前连接、目标 keysym 与是否允许 level 1 Shift 映射。
    /// 输出：优先 level 0；仅显式允许时回退 level 1 并返回 needs_shift=true。
    /// 不变量：level 2/3 及更高映射永不接受；modifier 调用方必须传 false。
    /// 失败：映射缺失、仅存在高层映射或 reply 异常时失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn keycode_for_keysym(
        connection: &RustConnection,
        keysym: u32,
        allow_shifted: bool,
    ) -> Result<(u8, bool), AgentError> {
        let setup = connection.setup();
        let count = setup
            .max_keycode
            .checked_sub(setup.min_keycode)
            .and_then(|value| value.checked_add(1))
            .ok_or_else(unavailable_error)?;
        let mapping = connection
            .get_keyboard_mapping(setup.min_keycode, count)
            .map_err(|_| unavailable_error())?
            .reply()
            .map_err(|_| unavailable_error())?;
        let per_keycode = usize::from(mapping.keysyms_per_keycode);
        if per_keycode == 0 {
            return Err(unavailable_error());
        }
        let expected_keysyms = usize::from(count)
            .checked_mul(per_keycode)
            .ok_or_else(unavailable_error)?;
        if mapping.keysyms.len() != expected_keysyms {
            return Err(unavailable_error());
        }
        mapped_keycode_for_keysym(
            setup.min_keycode,
            &mapping.keysyms,
            per_keycode,
            keysym,
            allow_shifted,
        )
        .ok_or_else(unavailable_error)
    }

    /// 功能：在已验证长度的扁平 X11 keyboard mapping 中纯函数查找 base/Shift keycode。
    ///
    /// 输入：最小 keycode、逐 keycode keysyms、每键层数、目标 keysym 与是否允许 Shift 层。
    /// 输出：全局优先 level 0；允许且没有 base 映射时才返回 level 1，其他层忽略。
    /// 不变量：不把 level 2/3 的 ModeSwitch/AltGr 映射误判为普通 Shift；keycode 加法检查 u8 溢出。
    /// 失败：本方法不返回错误；空/非整块 mapping、缺失或溢出返回 None。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn mapped_keycode_for_keysym(
        min_keycode: u8,
        keysyms: &[u32],
        keysyms_per_keycode: usize,
        keysym: u32,
        allow_shifted: bool,
    ) -> Option<(u8, bool)> {
        if keysyms_per_keycode == 0 || keysyms.len() % keysyms_per_keycode != 0 {
            return None;
        }
        let highest_level = usize::from(allow_shifted);
        for level in 0..=highest_level {
            for (index, symbols) in keysyms.chunks(keysyms_per_keycode).enumerate() {
                if symbols.get(level) == Some(&keysym) {
                    let offset = u8::try_from(index).ok()?;
                    let keycode = min_keycode.checked_add(offset)?;
                    return Some((keycode, level == 1));
                }
            }
        }
        None
    }

    /// 功能：把受限可移植键名映射为 X11 keysym。
    ///
    /// 输入：schema 已限制但仍视为不可信的命名键字符串。
    /// 输出：仅固定可移植 allowlist 对应的 X11 keysym。
    /// 不变量：不接受单字符、别名、大小写折叠或 Unicode 近似值。
    /// 失败：未知键名返回脱敏 backend 不可用错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn named_keysym(key: &str) -> Result<u32, AgentError> {
        let value = match key {
            "enter" => 0xff0d,
            "tab" => 0xff09,
            "escape" => 0xff1b,
            "backspace" => 0xff08,
            "delete" => 0xffff,
            "home" => 0xff50,
            "left" => 0xff51,
            "up" => 0xff52,
            "right" => 0xff53,
            "down" => 0xff54,
            "page_up" => 0xff55,
            "page_down" => 0xff56,
            "end" => 0xff57,
            "space" => 0x20,
            _ => return Err(unavailable_error()),
        };
        Ok(value)
    }

    /// 功能：把受限修饰键名映射为左侧 X11 keysym。
    ///
    /// 输入：schema 已限制但仍视为不可信的修饰键字符串。
    /// 输出：仅 shift/control/alt/meta 对应的左侧 X11 keysym。
    /// 不变量：映射集合封闭且不做大小写、别名或 Unicode 规范化。
    /// 失败：未知修饰键返回脱敏 backend 不可用错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    fn modifier_keysym(modifier: &str) -> Result<u32, AgentError> {
        match modifier {
            "shift" => Ok(0xffe1),
            "control" => Ok(0xffe3),
            "alt" => Ok(0xffe9),
            "meta" => Ok(0xffeb),
            _ => Err(unavailable_error()),
        }
    }
}

#[cfg(not(target_os = "linux"))]
mod platform {
    use super::{ComputerAction, DesktopCapture, unavailable_error};
    use crate::error::AgentError;
    use tokio_util::sync::CancellationToken;

    /// 功能：未实现平台不广告 desktop computer 能力。
    ///
    /// 输入：无。
    /// 输出：固定返回 None。
    /// 不变量：不读取环境、不访问 display、不广告观察或交互能力。
    /// 失败：本方法不返回错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) const fn probe() -> Option<bool> {
        None
    }

    /// 功能：未实现平台的观察预检始终失败关闭。
    ///
    /// 输入：无。
    /// 输出：不产生观察能力或副作用，固定返回 backend 不可用错误。
    /// 不变量：不读取环境、像素或宿主标识。
    /// 失败：每次调用均以固定脱敏错误失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn preflight_observe() -> Result<(), AgentError> {
        Err(unavailable_error())
    }

    /// 功能：未实现平台的交互预检始终失败关闭。
    ///
    /// 输入：已解析动作；仅为保持跨平台签名而接收且不读取。
    /// 输出：不产生输入副作用，固定返回 backend 不可用错误。
    /// 不变量：不读取环境、keymap 或动作内容。
    /// 失败：每次调用均以固定脱敏错误失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn preflight_interact(_action: &ComputerAction) -> Result<(), AgentError> {
        Err(unavailable_error())
    }

    /// 功能：未实现平台的捕获边界始终失败关闭。
    ///
    /// 输入：运行级取消令牌；仅为保持跨平台签名而接收且不读取。
    /// 输出：不生成 PNG，固定返回 backend 不可用错误。
    /// 不变量：不读取环境、像素或宿主标识。
    /// 失败：每次调用均以固定脱敏错误失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn capture(_cancellation: &CancellationToken) -> Result<DesktopCapture, AgentError> {
        Err(unavailable_error())
    }

    /// 功能：未实现平台的交互边界始终失败关闭。
    ///
    /// 输入：已解析动作与运行级取消令牌；仅为保持跨平台签名而接收且不读取。
    /// 输出：不注入任何输入，固定返回 backend 不可用错误。
    /// 不变量：不读取环境、keymap、动作内容或宿主标识。
    /// 失败：每次调用均以固定脱敏错误失败关闭。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    pub(super) fn interact(
        _action: &ComputerAction,
        _cancellation: &CancellationToken,
    ) -> Result<(), AgentError> {
        Err(unavailable_error())
    }
}

#[cfg(test)]
mod tests {
    use std::ffi::OsStr;
    #[cfg(unix)]
    use std::ffi::OsString;
    #[cfg(target_os = "linux")]
    use std::io::Write as _;
    #[cfg(unix)]
    use std::os::unix::ffi::OsStringExt;

    use tokio_util::sync::CancellationToken;

    use super::{
        ComputerAction, DesktopComputer, MAX_CAPTURE_PNG_BYTES, canonical_local_x11_display,
        desktop_environment_enabled, local_x11_display, validate_png_length,
        validated_capture_layout,
    };
    use crate::error::ErrorCode;

    /// 功能：验证 desktop computer 只在双门与明确原生 X11 证据同时存在时启用。
    ///
    /// 输入：不修改进程环境的纯 OsStr 组合，包括缺门、未知 Session、Wayland、远程 DISPLAY 和大小写/空白 X11。
    /// 输出：仅两道门精确为 1、trim 后 ASCII 大小写等价 x11、Wayland display 空且 DISPLAY 明确为本地 Unix 语法时接受。
    /// 不变量：测试不连接 display、不截图、不注入输入。
    /// 失败：任一 fail-closed 组合被错误接受时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn desktop_environment_requires_double_gate_and_positive_x11() {
        let one = Some(OsStr::new("1"));
        assert!(desktop_environment_enabled(
            one,
            one,
            Some(OsStr::new(" X11 ")),
            None,
            Some(OsStr::new(":0"))
        ));
        assert!(desktop_environment_enabled(
            one,
            one,
            Some(OsStr::new("x11")),
            Some(OsStr::new("")),
            Some(OsStr::new("unix/:65535.65535"))
        ));
        for (enabled, experimental, session, wayland, display) in [
            (
                None,
                one,
                Some(OsStr::new("x11")),
                None,
                Some(OsStr::new(":0")),
            ),
            (
                one,
                None,
                Some(OsStr::new("x11")),
                None,
                Some(OsStr::new(":0")),
            ),
            (
                Some(OsStr::new("true")),
                one,
                Some(OsStr::new("x11")),
                None,
                Some(OsStr::new(":0")),
            ),
            (one, one, None, None, Some(OsStr::new(":0"))),
            (one, one, Some(OsStr::new("")), None, Some(OsStr::new(":0"))),
            (
                one,
                one,
                Some(OsStr::new("wayland")),
                None,
                Some(OsStr::new(":0")),
            ),
            (
                one,
                one,
                Some(OsStr::new("tty")),
                None,
                Some(OsStr::new(":0")),
            ),
            (
                one,
                one,
                Some(OsStr::new("\u{00a0}x11\u{00a0}")),
                None,
                Some(OsStr::new(":0")),
            ),
            (
                one,
                one,
                Some(OsStr::new("x11")),
                Some(OsStr::new("wayland-0")),
                Some(OsStr::new(":0")),
            ),
            (one, one, Some(OsStr::new("x11")), None, None),
            (
                one,
                one,
                Some(OsStr::new("x11")),
                None,
                Some(OsStr::new("localhost:10.0")),
            ),
        ] {
            assert!(!desktop_environment_enabled(
                enabled,
                experimental,
                session,
                wayland,
                display
            ));
        }
    }

    /// 功能：验证 X11 DISPLAY allowlist 只接受有界的本地 Unix 数字语法。
    ///
    /// 输入：0/65535 边界、可选 screen、unix/ 前缀，以及超界、空段、多点、空白、hostname/TCP/SSH 反例。
    /// 输出：仅 `:N[.S]` 与 `unix/:N[.S]` 的 u16 ASCII 数字段被接受。
    /// 不变量：纯函数不读取环境、不连接 display，也不记录输入原值。
    /// 失败：远程或含糊 DISPLAY 被接受，或合法闭区间边界被拒绝时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn local_x11_display_is_strict_and_bounded() {
        for display in [
            ":0",
            ":0.0",
            ":65535",
            ":65535.65535",
            "unix/:0",
            "unix/:0.0",
            "unix/:65535.65535",
        ] {
            assert!(local_x11_display(Some(OsStr::new(display))), "{display}");
        }
        for display in [
            "",
            ":",
            ":.0",
            ":0.",
            ":0.1.2",
            ":65536",
            ":0.65536",
            " :0",
            ":0 ",
            ": 0",
            "localhost:0",
            "hostname:0",
            "localhost:10.0",
            "tcp/hostname:0",
            "unix:0",
            "unix/:",
            "unix/:0.",
        ] {
            assert!(!local_x11_display(Some(OsStr::new(display))), "{display}");
        }
        let overlong_zero = format!(":{}", "0".repeat(64));
        assert!(!local_x11_display(Some(OsStr::new(&overlong_zero))));
        assert!(!local_x11_display(None));
        assert_eq!(
            canonical_local_x11_display(Some(OsStr::new(":0.1"))),
            Some("unix/:0.1".to_owned())
        );
        assert_eq!(
            canonical_local_x11_display(Some(OsStr::new("unix/:65535"))),
            Some("unix/:65535".to_owned())
        );
    }

    /// 功能：验证非 UTF-8 DISPLAY 在 Unix 上失败关闭。
    ///
    /// 输入：以合法本地前缀开头但包含非法 UTF-8 尾字节的 OsString。
    /// 输出：pure allowlist 返回 false。
    /// 不变量：不做 lossy 转换、不连接 display、不回显原字节。
    /// 失败：非 UTF-8 环境值被错误接受时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(unix)]
    #[test]
    fn local_x11_display_rejects_non_utf8() {
        let display = OsString::from_vec(vec![b':', b'0', 0xff]);
        assert!(!local_x11_display(Some(display.as_os_str())));
    }

    /// 功能：验证 root 捕获上限在分配前按维度、像素、raw 和 PNG 精确闭区间执行。
    ///
    /// 输入：恰好达到冻结上限及逐项超界的纯整数布局。
    /// 输出：边界值接受，16385 维、超像素、非法格式和 33554433 字节 PNG 拒绝。
    /// 不变量：测试不分配对应图像缓冲，也不调用 X11。
    /// 失败：checked arithmetic 或闭区间边界漂移时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[test]
    fn capture_limits_are_checked_before_allocation() {
        assert_eq!(
            validated_capture_layout(16_384, 1_024, 32, 32),
            Ok((65_536, 67_108_864))
        );
        assert!(validated_capture_layout(16_385, 1, 32, 32).is_err());
        assert!(validated_capture_layout(8_192, 2_049, 32, 32).is_err());
        assert!(validated_capture_layout(1, 1, 16, 32).is_err());
        assert!(validate_png_length(MAX_CAPTURE_PNG_BYTES).is_ok());
        assert!(validate_png_length(MAX_CAPTURE_PNG_BYTES + 1).is_err());
    }

    /// 功能：验证 PNG Write sink 在越界段到达时零部分写入且保持原上限长度。
    ///
    /// 输入：4 字节 writer、恰好 4 字节内容和额外 1 字节。
    /// 输出：边界写入成功，越界写入返回 I/O error，最终内容未被污染。
    /// 不变量：测试只使用小型 synthetic bytes，不编码或分配桌面图像。
    /// 失败：writer 部分接受越界段或扩展长度时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[test]
    fn bounded_png_writer_stops_before_overlimit_allocation() {
        let mut writer = super::platform::BoundedPngWriter::new(4);
        writer.write_all(b"png!").expect("boundary write must fit");
        assert!(writer.write_all(b"x").is_err());
        assert_eq!(writer.into_bytes(), b"png!");
    }

    /// 功能：验证 X11 RGB decoder 只接受逻辑布局加规范 4 字节 wire padding 的精确 reply 长度。
    ///
    /// 输入：1x1 24-bit synthetic pixel 的 3 字节逻辑数据加 1 字节 wire padding，以及缺 padding 的 3 字节与多一 wire word 的 8 字节反例。
    /// 输出：仅 4 字节规范 reply 解码为一个 RGB 像素，短/长 reply 均失败关闭。
    /// 不变量：测试不连接 X11、不分配桌面尺寸缓冲，mask 使用连续 TrueColor 布局。
    /// 失败：decoder 接受非规范 reply 长度或拒绝精确长度时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[test]
    fn rgb_decoder_requires_exact_reply_length() {
        let cancellation = CancellationToken::new();
        let exact = [0_u8; 4];
        let decoded = super::platform::decode_rgb(
            &exact,
            1,
            1,
            24,
            8,
            x11rb::protocol::xproto::ImageOrder::LSB_FIRST,
            0x00ff_0000,
            0x0000_ff00,
            0x0000_00ff,
            &cancellation,
        )
        .expect("exact reply length must decode");
        assert_eq!(decoded, vec![0, 0, 0]);
        for invalid in [&exact[..3], &[0_u8; 8][..]] {
            assert!(
                super::platform::decode_rgb(
                    invalid,
                    1,
                    1,
                    24,
                    8,
                    x11rb::protocol::xproto::ImageOrder::LSB_FIRST,
                    0x00ff_0000,
                    0x0000_ff00,
                    0x0000_00ff,
                    &cancellation,
                )
                .is_err()
            );
        }
    }

    /// 功能：验证 TrueColor mask 只有连续、互斥且位于像素位宽内才可解码。
    ///
    /// 输入：标准 24/32-bit RGB mask，以及空洞、越位、重叠、零 mask 和非法位宽反例。
    /// 输出：仅两个标准布局返回 true。
    /// 不变量：纯函数测试不连接 X11、不读取像素也不分配图像缓冲。
    /// 失败：mask_component 无法安全解释的布局被接受时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[test]
    fn true_color_masks_must_be_contiguous_disjoint_and_within_pixel_width() {
        assert!(super::platform::true_color_masks_are_decodable(
            24,
            0x00ff_0000,
            0x0000_ff00,
            0x0000_00ff
        ));
        assert!(super::platform::true_color_masks_are_decodable(
            32,
            0x00ff_0000,
            0x0000_ff00,
            0x0000_00ff
        ));
        for (bits_per_pixel, red_mask, green_mask, blue_mask) in [
            (24, 0x00fa_0000, 0x0000_ff00, 0x0000_00ff),
            (24, 0x0100_0000, 0x0000_ff00, 0x0000_00ff),
            (24, 0x00ff_0000, 0x00ff_0000, 0x0000_00ff),
            (24, 0, 0x0000_ff00, 0x0000_00ff),
            (16, 0x0000_f800, 0x0000_07e0, 0x0000_001f),
        ] {
            assert!(!super::platform::true_color_masks_are_decodable(
                bits_per_pixel,
                red_mask,
                green_mask,
                blue_mask
            ));
        }
    }

    /// 功能：验证 key chord 补偿不会释放尚未尝试 press 的目标键。
    ///
    /// 输入：目标 keycode、两个已尝试 modifier 及 target press false/true 两种状态。
    /// 输出：false 仅逆序枚举 modifiers；true 先枚举目标再逆序枚举 modifiers。
    /// 不变量：纯函数回调测试不连接 X11、不发送输入事件。
    /// 失败：未尝试目标被枚举或 modifier 补偿顺序漂移时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[test]
    fn key_compensation_skips_unattempted_target_press() {
        let mut released = Vec::new();
        super::platform::for_each_compensating_keycode(42, &[11, 12], false, |keycode| {
            released.push(keycode);
        });
        assert_eq!(released, [12, 11]);

        released.clear();
        super::platform::for_each_compensating_keycode(42, &[11, 12], true, |keycode| {
            released.push(keycode);
        });
        assert_eq!(released, [42, 12, 11]);
    }

    /// 功能：验证 keyboard mapping 只把 level 0/1 解释为 base/Shift。
    ///
    /// 输入：三个 synthetic keycode 的四层映射，覆盖先出现 Shift 后出现 base、仅 Shift 及仅 level2/3。
    /// 输出：全局优先 base；目标可回退 Shift；modifier 禁止 Shift；高层映射始终拒绝。
    /// 不变量：纯函数测试不查询 X11，也不把 ModeSwitch/AltGr 层折叠为 Shift。
    /// 失败：level 选择、base 优先级或 malformed mapping 边界漂移时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[test]
    fn keyboard_mapping_accepts_only_base_and_explicit_shift_levels() {
        let mapping = [
            0, 0x20, 0x22, 0x22, // keycode 8
            0x20, 0, 0, 0, // keycode 9
            0, 0x21, 0, 0, // keycode 10
        ];
        assert_eq!(
            super::platform::mapped_keycode_for_keysym(8, &mapping, 4, 0x20, true),
            Some((9, false))
        );
        assert_eq!(
            super::platform::mapped_keycode_for_keysym(8, &mapping, 4, 0x20, false),
            Some((9, false))
        );
        assert_eq!(
            super::platform::mapped_keycode_for_keysym(8, &mapping, 4, 0x21, true),
            Some((10, true))
        );
        assert_eq!(
            super::platform::mapped_keycode_for_keysym(8, &mapping, 4, 0x21, false),
            None
        );
        assert_eq!(
            super::platform::mapped_keycode_for_keysym(8, &mapping, 4, 0x22, true),
            None
        );
        assert_eq!(
            super::platform::mapped_keycode_for_keysym(8, &mapping[..11], 4, 0x20, true),
            None
        );
    }

    /// 功能：验证解析后的 key chord 拒绝 modifier 重复或复用目标 keycode。
    ///
    /// 输入：唯一 modifiers、重复 modifiers 与目标冲突三类纯 u8 keycode。
    /// 输出：仅空/唯一且不冲突的 modifier 集合返回 true。
    /// 不变量：测试不依赖键名、X11 keymap 或输入注入。
    /// 失败：可能产生含糊 press/release 所有权的 chord 被接受时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[test]
    fn key_chord_keycodes_must_be_unique_and_distinct_from_target() {
        assert!(super::platform::key_chord_keycodes_are_distinct(42, &[]));
        assert!(super::platform::key_chord_keycodes_are_distinct(
            42,
            &[11, 12]
        ));
        assert!(!super::platform::key_chord_keycodes_are_distinct(
            42,
            &[11, 11]
        ));
        assert!(!super::platform::key_chord_keycodes_are_distinct(
            42,
            &[11, 42]
        ));
    }

    /// 功能：验证已取消的阻塞捕获在连接 X11 前返回稳定 Cancelled。
    ///
    /// 输入：已取消令牌和 synthetic DesktopComputer 探测结果。
    /// 输出：不依赖 DISPLAY 的 Cancelled 错误。
    /// 不变量：测试不读取像素、不注入鼠标键盘。
    /// 失败：实现先连接 backend 或错误码漂移时测试失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[test]
    fn cancelled_capture_fails_before_backend_access() {
        let cancellation = CancellationToken::new();
        cancellation.cancel();
        let error = DesktopComputer {
            interaction_available: true,
        }
        .capture(&cancellation)
        .err()
        .expect("cancelled capture must fail");
        assert_eq!(error.code, ErrorCode::Cancelled);
        let error = DesktopComputer {
            interaction_available: true,
        }
        .interact(&ComputerAction::Move { x: 0, y: 0 }, &cancellation)
        .expect_err("cancelled interaction must fail before backend access");
        assert_eq!(error.code, ErrorCode::Cancelled);
    }

    /// 功能：验证所有交互动作先约束真实 root，绝对坐标再使用半开区间而非 schema 上限推断。
    ///
    /// 输入：100x50 root 的角点、右/下边界和负坐标，以及 scroll/key 的零尺寸与超限 root。
    /// 输出：仅安全 root 中 0..99、0..49 内绝对坐标以及无需坐标的动作被接受。
    /// 不变量：纯函数测试不连接 X11。
    /// 失败：边界/负坐标或任一动作的非法 root 被接受时断言失败。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    #[cfg(target_os = "linux")]
    #[test]
    fn root_coordinates_use_half_open_geometry() {
        assert!(super::platform::coordinate_inside_root(100, 50, 0, 0));
        assert!(super::platform::coordinate_inside_root(100, 50, 99, 49));
        assert!(!super::platform::coordinate_inside_root(100, 50, 100, 49));
        assert!(!super::platform::coordinate_inside_root(100, 50, 99, 50));
        assert!(!super::platform::coordinate_inside_root(100, 50, -1, 0));

        let scroll = ComputerAction::Scroll {
            delta_x: 0,
            delta_y: 1,
        };
        let key = ComputerAction::Key {
            key: "escape".to_owned(),
            modifiers: Vec::new(),
        };
        assert!(super::platform::validate_action_geometry(100, 50, &scroll).is_ok());
        assert!(super::platform::validate_action_geometry(100, 50, &key).is_ok());
        assert!(super::platform::validate_action_geometry(0, 50, &scroll).is_err());
        assert!(super::platform::validate_action_geometry(100, 0, &key).is_err());
        assert!(super::platform::validate_action_geometry(16_384, 1_025, &scroll).is_err());
    }
}
