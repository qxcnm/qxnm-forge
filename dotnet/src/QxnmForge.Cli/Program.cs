using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using QxnmForge.Agent;
using QxnmForge.Daemon;
using QxnmForge.Domain;
using QxnmForge.Provider;
using QxnmForge.Serialization;
using QxnmForge.Session;
using QxnmForge.Tools;

namespace QxnmForge.Cli;

/// <summary>
/// 功能：提供 qxnm-forge 独立 .NET CLI、单次纯文本 run 与 stdio daemon 入口。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
internal static class Program
{
    /// <summary>
    /// 功能：解析命令、执行对应异步模式并映射 portable 进程退出码。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="args">命令行参数；不会接受 Provider secret。</param>
    /// <returns>0 成功、2 用法错误、4 Provider 不可用、5 run 失败、6 取消、8 内部错误。</returns>
    private static async Task<int> Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "daemon" => await RunDaemonAsync(args[1..], CancellationToken.None).ConfigureAwait(false),
                "run" => await RunOnceAsync(args[1..], CancellationToken.None).ConfigureAwait(false),
                "session" when args.Length > 1 && args[1] == "import-pi-v3" =>
                    await RunPiV3ImportAsync(args[2..], CancellationToken.None).ConfigureAwait(false),
                "sponsors" => await RunSponsorsAsync(args[1..], CancellationToken.None).ConfigureAwait(false),
                "auth" => await RunAuthAsync(args[1..], CancellationToken.None).ConfigureAwait(false),
                _ => Usage(),
            };
        }
        catch (OperationCanceledException)
        {
            return 6;
        }
        catch (ProviderUnavailableException)
        {
            await Console.Error.WriteLineAsync("provider/model is unavailable").ConfigureAwait(false);
            return 4;
        }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }
        catch (PiV3ImportException exception)
        {
            await Console.Error.WriteLineAsync("PI v3 import failed safely: " + exception.Kind).ConfigureAwait(false);
            return exception.ExitCode;
        }
        catch (ProviderIdentityAdvertisementException)
        {
            await Console.Error.WriteLineAsync(
                "Provider identity advertisement configuration was rejected safely.").ConfigureAwait(false);
            return 8;
        }
        catch (ProviderExecutableRouteException)
        {
            await Console.Error.WriteLineAsync(
                "Provider executable route configuration was rejected safely.").ConfigureAwait(false);
            return 8;
        }
        catch (SponsoredCatalogException exception)
        {
            await Console.Error.WriteLineAsync(
                "推广 Provider 目录操作安全失败：" + exception.Kind).ConfigureAwait(false);
            return 8;
        }
        catch (ProviderCommercialStateException exception)
        {
            await Console.Error.WriteLineAsync(
                "Provider 商业状态操作安全失败：" + exception.Kind).ConfigureAwait(false);
            return 8;
        }
        catch (Exception exception) when (exception is IOException or JsonException or JournalCorruptException or JournalIncompatibleException)
        {
            await Console.Error.WriteLineAsync("qxnm-forge-dotnet failed safely: " + exception.GetType().Name).ConfigureAwait(false);
            return 8;
        }
    }

    /// <summary>
    /// 功能：运行离线 PI Session v3 clean-room 一次性导入并只输出三个安全结果字段。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="args">import-pi-v3 portable 参数；source 路径不会写入输出或持久化。</param>
    /// <param name="cancellationToken">读取、持久化与发布取消信号。</param>
    /// <returns>目标原子发布后返回 0；失败由 Main 映射 portable 退出码。</returns>
    private static async Task<int> RunPiV3ImportAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParsePiV3ImportOptions(args);
        var result = await PiV3Importer.ImportAsync(
            new PiV3ImportOptions(
                options.Source,
                options.Workspace,
                options.StateDirectory,
                options.Session,
                options.Conformance),
            cancellationToken).ConfigureAwait(false);
        if (options.Format == "json")
        {
            var output = JsonSerializer.Serialize(
                new
                {
                    result.Status,
                    result.SessionId,
                    result.ReportArtifactId,
                },
                JsonDefaults.Options);
            await Console.Out.WriteLineAsync(output).ConfigureAwait(false);
        }
        else
        {
            await Console.Out.WriteLineAsync(
                $"status={result.Status} sessionId={result.SessionId} reportArtifactId={result.ReportArtifactId}").ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// 功能：严格解析 import-pi-v3 必需路径、可选新 Session、输出格式和 conformance 开关。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="args">子命令后的 argv；未知或重复参数均拒绝。</param>
    /// <returns>不含 secret 的不可变导入 CLI 选项。</returns>
    /// <exception cref="ArgumentException">参数缺失、重复、未知或 format 非法。</exception>
    private static PiV3CliOptions ParsePiV3ImportOptions(string[] args)
    {
        string? source = null;
        string? workspace = null;
        string? stateDirectory = null;
        string? session = null;
        var format = "text";
        var formatProvided = false;
        var conformance = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--source":
                    source = AssignOnce(source, RequireOptionValue(args, ref index, "--source"), "--source");
                    break;
                case "--workspace":
                    workspace = AssignOnce(workspace, RequireOptionValue(args, ref index, "--workspace"), "--workspace");
                    break;
                case "--state-dir":
                    stateDirectory = AssignOnce(stateDirectory, RequireOptionValue(args, ref index, "--state-dir"), "--state-dir");
                    break;
                case "--session":
                    session = AssignOnce(session, RequireOptionValue(args, ref index, "--session"), "--session");
                    break;
                case "--format":
                    if (formatProvided)
                    {
                        throw new ArgumentException("--format may only be provided once");
                    }

                    formatProvided = true;
                    format = RequireOptionValue(args, ref index, "--format");
                    if (format is not ("text" or "json"))
                    {
                        throw new ArgumentException("--format must be text or json");
                    }

                    break;
                case "--conformance":
                    if (conformance)
                    {
                        throw new ArgumentException("--conformance may only be provided once");
                    }

                    conformance = true;
                    break;
                case "--no-color":
                    break;
                default:
                    throw new ArgumentException("unknown PI import argument");
            }
        }

        if (source is null || workspace is null || stateDirectory is null)
        {
            throw new ArgumentException("PI import requires --source, --workspace and --state-dir");
        }

        return new PiV3CliOptions(source, workspace, stateDirectory, session, format, conformance);
    }

    /// <summary>
    /// 功能：只允许一个 CLI 参数值赋给对应槽位。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="current">此前值或 null。</param>
    /// <param name="value">本次非空值。</param>
    /// <param name="name">安全参数名。</param>
    /// <returns>首次提供的值。</returns>
    /// <exception cref="ArgumentException">同一参数重复出现。</exception>
    private static string AssignOnce(string? current, string value, string name)
    {
        if (current is not null)
        {
            throw new ArgumentException(name + " may only be provided once");
        }

        return value;
    }

    /// <summary>
    /// 功能：运行 stdout 仅含协议帧的 headless stdio daemon。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="args">daemon 选项。</param>
    /// <param name="cancellationToken">进程生命周期取消信号。</param>
    /// <returns>clean EOF 后返回 0。</returns>
    private static async Task<int> RunDaemonAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, allowPrompt: false);
        if (!options.Stdio)
        {
            throw new ArgumentException("daemon mode requires --stdio");
        }

        var environmentConformance =
            string.Equals(Environment.GetEnvironmentVariable("QXNM_FORGE_CONFORMANCE"), "1", StringComparison.Ordinal);
        var conformance = options.Conformance || environmentConformance;
        var (sessionsRoot, ephemeral) = ResolveSessionsRoot(options.StateDirectory, conformance);
        try
        {
            await using var repository = new SessionRepository(sessionsRoot, options.Workspace);
            await using var providerRegistry = ProviderRegistryFactory.CreateFromEnvironment(
                conformanceMode: conformance,
                stateRoot: sessionsRoot,
                workspace: options.Workspace);
            using var toolRegistry = new ToolRegistry(
                options.Workspace,
                options.Conformance,
                environmentConformance);
            var agent = new AgentService(
                repository,
                providerRegistry,
                toolRegistry,
                ResolveApprovalTimeout(options.Conformance, environmentConformance));
            var daemon = new StdioDaemon(repository, agent, conformance);
            await using var protocolOutput = OpenProtocolOutput();
            await daemon.RunAsync(
                Console.OpenStandardInput(),
                protocolOutput,
                cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (ephemeral)
            {
                TryDeleteDirectory(sessionsRoot);
            }
        }
    }

    /// <summary>
    /// 功能：执行一次纯文本 Agent run，仅把 assistant 文本写 stdout。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="args">portable run 选项和 prompt。</param>
    /// <param name="cancellationToken">run 取消信号。</param>
    /// <returns>portable CLI 退出码。</returns>
    private static async Task<int> RunOnceAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, allowPrompt: true);
        var prompt = options.Prompt.Count == 0
            ? await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)
            : string.Join(' ', options.Prompt);
        if (prompt.Length == 0)
        {
            throw new ArgumentException("run requires a prompt on argv or stdin");
        }

        var sessionsRoot = ResolveApplicationStateRoot(options.StateDirectory);
        var apiFamily = options.ApiFamily;
        if (options.Provider != "faux" && apiFamily is null)
        {
            apiFamily = new InstalledSponsoredRouteStore(sessionsRoot)
                .ResolveFamily(options.Provider, options.Model);
        }

        await using var repository = new SessionRepository(sessionsRoot, options.Workspace);
        using var toolRegistry = new ToolRegistry(options.Workspace);
        await using var providerRegistry = ProviderRegistryFactory.CreateFromEnvironment(
            stateRoot: sessionsRoot,
            workspace: options.Workspace);
        var agent = new AgentService(repository, providerRegistry, toolRegistry);
        var sessionId = options.Session ?? "session-" + Guid.NewGuid().ToString("N");
        if (options.Provider == "faux")
        {
            if (options.Model != "faux-v1" || apiFamily is not null)
            {
                throw new ProviderUnavailableException(options.Provider, options.Model);
            }

            var scenario = new FauxScenario(
                "0.1",
                "cli-run",
                0,
                [new FauxTextStep("Faux response: " + prompt)],
                QxnmForge.Domain.Usage.Zero);
            await repository.ConfigureFauxAsync(sessionId, scenario, cancellationToken).ConfigureAwait(false);
        }

        var accepted = await agent.AcceptAsync(
            sessionId,
            new InputMessage("user", [new TextContent(prompt)]),
            new ProviderSelection(options.Provider, options.Model, apiFamily),
            cancellationToken).ConfigureAwait(false);

        var exitCode = 5;
        await foreach (var portableEvent in agent.RunAsync(accepted, cancellationToken).ConfigureAwait(false))
        {
            if (portableEvent.Type == "message.delta")
            {
                await Console.Out.WriteAsync(ExtractDeltaText(portableEvent.Data).AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            exitCode = portableEvent.Type switch
            {
                "run.completed" => 0,
                "run.cancelled" or "run.interrupted" => 6,
                "run.failed" => 5,
                _ => exitCode,
            };
        }

        await Console.Out.WriteLineAsync().ConfigureAwait(false);
        return exitCode;
    }

    /// <summary>
    /// 功能：解析纵切面支持的 portable CLI 参数，并拒绝未知参数与 secret flags。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="args">模式之后的参数。</param>
    /// <param name="allowPrompt">是否把非 flag token 视为 prompt。</param>
    /// <returns>不可变 CLI 选项。</returns>
    /// <exception cref="ArgumentException">未知选项、缺值或非法 prompt 位置。</exception>
    private static CliOptions ParseOptions(string[] args, bool allowPrompt)
    {
        var workspace = Directory.GetCurrentDirectory();
        string? stateDirectory = null;
        string? session = null;
        var provider = "faux";
        var model = "faux-v1";
        string? apiFamily = null;
        var conformance = false;
        var stdio = false;
        var prompt = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--workspace":
                    workspace = RequireOptionValue(args, ref index, "--workspace");
                    break;
                case "--state-dir":
                    stateDirectory = AssignOnce(
                        stateDirectory,
                        RequireOptionValue(args, ref index, "--state-dir"),
                        "--state-dir");
                    break;
                case "--session":
                    session = RequireOptionValue(args, ref index, "--session");
                    break;
                case "--provider":
                    provider = RequireOptionValue(args, ref index, "--provider");
                    break;
                case "--model":
                    model = RequireOptionValue(args, ref index, "--model");
                    break;
                case "--api-family":
                    apiFamily = AssignOnce(
                        apiFamily,
                        RequireOptionValue(args, ref index, "--api-family"),
                        "--api-family");
                    break;
                case "--conformance":
                    conformance = true;
                    break;
                case "--stdio":
                    stdio = true;
                    break;
                case "--no-color":
                    break;
                default:
                    if ((args[index].Length > 0 && args[index][0] == '-') || !allowPrompt)
                    {
                        throw new ArgumentException("unknown CLI argument: " + args[index]);
                    }

                    prompt.Add(args[index]);
                    break;
            }
        }

        workspace = Path.GetFullPath(workspace);
        if (!Directory.Exists(workspace))
        {
            throw new ArgumentException("workspace must exist");
        }

        return new CliOptions(
            workspace,
            stateDirectory,
            session,
            provider,
            model,
            apiFamily,
            conformance,
            stdio,
            prompt);
    }

    /// <summary>
    /// 功能：取得需要值的 CLI 选项后一个 token。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="args">完整模式参数。</param>
    /// <param name="index">当前选项索引；成功后推进到值索引。</param>
    /// <param name="name">用于安全诊断的选项名。</param>
    /// <returns>非空选项值。</returns>
    private static string RequireOptionValue(string[] args, ref int index, string name)
    {
        if (++index >= args.Length || string.IsNullOrEmpty(args[index]))
        {
            throw new ArgumentException(name + " requires a value");
        }

        return args[index];
    }

    /// <summary>
    /// 功能：选择显式 session root，或为 conformance 进程创建隔离临时根。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="explicitStateDirectory">可选 CLI state root。</param>
    /// <param name="conformance">是否运行一致性模式。</param>
    /// <returns>路径及是否应在退出时尽力清理。</returns>
    private static (string Path, bool Ephemeral) ResolveSessionsRoot(
        string? explicitStateDirectory,
        bool conformance)
    {
        if (!string.IsNullOrEmpty(explicitStateDirectory))
        {
            return (Path.GetFullPath(explicitStateDirectory), false);
        }

        var configured = Environment.GetEnvironmentVariable("QXNM_FORGE_SESSION_ROOT");
        if (!string.IsNullOrEmpty(configured))
        {
            return (Path.GetFullPath(configured), false);
        }

        return conformance
            ? (Path.Combine(Path.GetTempPath(), "qxnm-forge-dotnet-conformance", Guid.NewGuid().ToString("N")), true)
            : (ResolveApplicationStateRoot(null), false);
    }

    /// <summary>
    /// 功能：按显式 CLI、环境和平台用户目录顺序选择工作区外状态根。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="explicitStateDirectory">可选 `--state-dir`。</param>
    /// <returns>不根据当前工作目录构造的绝对状态路径。</returns>
    /// <remarks>优先级为显式参数、QXNM_FORGE_SESSION_ROOT、LOCALAPPDATA/XDG_STATE_HOME/HOME、系统临时目录。</remarks>
    private static string ResolveApplicationStateRoot(string? explicitStateDirectory)
    {
        if (!string.IsNullOrEmpty(explicitStateDirectory))
        {
            return Path.GetFullPath(explicitStateDirectory);
        }

        var configured = Environment.GetEnvironmentVariable("QXNM_FORGE_SESSION_ROOT");
        if (!string.IsNullOrEmpty(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                return Path.Combine(local, "qxnm-forge", "state");
            }
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return Path.Combine(xdg, "qxnm-forge");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home)
            ? Path.Combine(home, ".local", "state", "qxnm-forge")
            : Path.Combine(Path.GetTempPath(), "qxnm-forge-state");
    }

    /// <summary>
    /// 功能：执行工作区外 Provider CredentialStore 的 set/list/remove 子命令。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="args">`auth` 后的严格 argv；绝不接受 secret 参数。</param>
    /// <param name="cancellationToken">stdin 读取取消信号。</param>
    /// <returns>成功返回 0；失败由 Main 统一映射。</returns>
    private static async Task<int> RunAuthAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("auth requires set, list or remove");
        }

        switch (args[0])
        {
            case "set":
                {
                    var parsed = ParseSponsorArguments(
                        args[1..],
                        ["--provider", "--workspace", "--state-dir"],
                        []);
                    var workspace = ResolveAuthWorkspace(GetSponsorOption(parsed, "--workspace"));
                    var store = new ProviderCredentialStore(
                        ResolveApplicationStateRoot(GetSponsorOption(parsed, "--state-dir")),
                        workspace);
                    var secret = await ReadSecretFromStandardInputAsync(cancellationToken).ConfigureAwait(false);
                    store.Set(RequireSponsorOption(parsed, "--provider"), secret);
                    await Console.Out.WriteLineAsync(
                        "Provider credential 已保存；不会写入 workspace 或 Session。").ConfigureAwait(false);
                    return 0;
                }

            case "list":
                {
                    var parsed = ParseSponsorArguments(
                        args[1..],
                        ["--workspace", "--state-dir"],
                        ["--json"]);
                    var workspace = ResolveAuthWorkspace(GetSponsorOption(parsed, "--workspace"));
                    var providers = new ProviderCredentialStore(
                        ResolveApplicationStateRoot(GetSponsorOption(parsed, "--state-dir")),
                        workspace).List();
                    if (parsed.Flags.Contains("--json"))
                    {
                        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                            new { providers },
                            JsonDefaults.Options)).ConfigureAwait(false);
                    }
                    else
                    {
                        if (providers.Count == 0)
                        {
                            await Console.Out.WriteLineAsync(
                                "当前没有 stored Provider credential。").ConfigureAwait(false);
                        }

                        foreach (var provider in providers)
                        {
                            await Console.Out.WriteLineAsync(provider + ": configured").ConfigureAwait(false);
                        }
                    }

                    return 0;
                }

            case "remove":
                {
                    var parsed = ParseSponsorArguments(
                        args[1..],
                        ["--provider", "--workspace", "--state-dir"],
                        []);
                    var workspace = ResolveAuthWorkspace(GetSponsorOption(parsed, "--workspace"));
                    var removed = new ProviderCredentialStore(
                        ResolveApplicationStateRoot(GetSponsorOption(parsed, "--state-dir")),
                        workspace).Remove(RequireSponsorOption(parsed, "--provider"));
                    await Console.Out.WriteLineAsync(
                        removed ? "Provider credential 已移除。" : "Provider credential 不存在。").ConfigureAwait(false);
                    return 0;
                }

            default:
                throw new ArgumentException("unknown auth command: " + args[0]);
        }
    }

    /// <summary>
    /// 功能：规范 auth 的显式 workspace，缺省使用当前目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="workspace">可选 CLI 路径。</param>
    /// <returns>已存在目录的绝对路径。</returns>
    private static string ResolveAuthWorkspace(string? workspace)
    {
        var result = Path.GetFullPath(workspace ?? Directory.GetCurrentDirectory());
        return Directory.Exists(result)
            ? result
            : throw new ArgumentException("workspace must exist");
    }

    /// <summary>
    /// 功能：从重定向 stdin 有界读取一个 API key，避免 secret 出现在 argv 或终端回显。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cancellationToken">异步读取取消信号。</param>
    /// <returns>去掉一个结尾 LF/CRLF 后的 1..16384 字符 secret。</returns>
    /// <exception cref="ArgumentException">stdin 未重定向、空值、多行、NUL 或超限。</exception>
    private static async Task<string> ReadSecretFromStandardInputAsync(
        CancellationToken cancellationToken)
    {
        if (!Console.IsInputRedirected)
        {
            throw new ArgumentException(
                "auth set 必须从重定向 stdin 读取 credential，以避免终端回显");
        }

        var builder = new System.Text.StringBuilder(capacity: 256);
        var buffer = new char[4096];
        while (true)
        {
            var read = await Console.In.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
            Array.Clear(buffer, 0, read);
            if (builder.Length > 16_386)
            {
                throw new ArgumentException("credential 输入无效");
            }
        }

        if (builder.Length >= 2 && builder[^2] == '\r' && builder[^1] == '\n')
        {
            builder.Length -= 2;
        }
        else if (builder.Length >= 1 && builder[^1] == '\n')
        {
            builder.Length--;
        }

        var secret = builder.ToString();
        if (secret.Length is < 1 or > 16_384 || secret.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("credential 输入无效");
        }

        return secret;
    }


    /// <summary>
    /// 功能：执行双实现一致的推广目录发布、列表和本地 route 子命令。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="args">`sponsors` 后的严格 argv；不接受私钥内容或 API key。</param>
    /// <param name="cancellationToken">远程刷新取消信号。</param>
    /// <returns>成功返回 0；失败由 Main 映射且不回显敏感路径或正文。</returns>
    private static async Task<int> RunSponsorsAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException(
                "sponsors requires keygen, sign, verify, configure, list, use, installed or remove");
        }

        switch (args[0])
        {
            case "keygen":
                {
                    var parsed = ParseSponsorArguments(
                        args[1..],
                        ["--key-id", "--private-key", "--public-key"],
                        []);
                    SponsoredCatalogPublisher.GenerateKeyPair(
                        RequireSponsorOption(parsed, "--key-id"),
                        RequireSponsorOption(parsed, "--private-key"),
                        RequireSponsorOption(parsed, "--public-key"));
                    await Console.Out.WriteLineAsync(
                        "推广目录签名密钥已创建；请离线保管私钥且只分发公钥。").ConfigureAwait(false);
                    return 0;
                }

            case "sign":
                {
                    var parsed = ParseSponsorArguments(
                        args[1..],
                        ["--catalog", "--private-key", "--public-key", "--output"],
                        []);
                    SponsoredCatalogPublisher.Sign(
                        RequireSponsorOption(parsed, "--catalog"),
                        RequireSponsorOption(parsed, "--private-key"),
                        RequireSponsorOption(parsed, "--public-key"),
                        RequireSponsorOption(parsed, "--output"));
                    await Console.Out.WriteLineAsync(
                        "推广目录 envelope 已签名，可上传到已配置的 HTTPS 地址。").ConfigureAwait(false);
                    return 0;
                }

            case "verify":
                {
                    var parsed = ParseSponsorArguments(
                        args[1..],
                        ["--envelope", "--public-key"],
                        ["--json"]);
                    var catalog = SponsoredCatalogPublisher.Verify(
                        RequireSponsorOption(parsed, "--envelope"),
                        RequireSponsorOption(parsed, "--public-key"));
                    if (parsed.Flags.Contains("--json"))
                    {
                        await Console.Out.WriteLineAsync(
                            JsonSerializer.Serialize(catalog, JsonDefaults.Options)).ConfigureAwait(false);
                    }
                    else
                    {
                        await Console.Out.WriteLineAsync(
                            $"推广目录验证通过：version={catalog.CatalogVersion} entries={catalog.Entries.Count}").ConfigureAwait(false);
                    }

                    return 0;
                }

            case "configure":
                {
                    var parsed = ParseSponsorArguments(
                        args[1..],
                        ["--catalog-url", "--public-key", "--state-dir"],
                        []);
                    var stateRoot = ResolveApplicationStateRoot(GetSponsorOption(parsed, "--state-dir"));
                    using var service = new SponsoredCatalogService(stateRoot);
                    service.Configure(
                        RequireSponsorOption(parsed, "--catalog-url"),
                        RequireSponsorOption(parsed, "--public-key"));
                    await Console.Out.WriteLineAsync(
                        "推广目录来源已配置；远端不能更换本地验签公钥。").ConfigureAwait(false);
                    return 0;
                }

            case "list":
                {
                    var parsed = ParseSponsorArguments(
                        args[1..],
                        ["--state-dir"],
                        ["--offline", "--json"]);
                    var stateRoot = ResolveApplicationStateRoot(GetSponsorOption(parsed, "--state-dir"));
                    using var service = new SponsoredCatalogService(stateRoot);
                    var loaded = await service.LoadAsync(
                        parsed.Flags.Contains("--offline"),
                        cancellationToken).ConfigureAwait(false);
                    if (loaded.Warning is not null)
                    {
                        await Console.Error.WriteLineAsync("警告：" + loaded.Warning).ConfigureAwait(false);
                    }

                    if (parsed.Flags.Contains("--json"))
                    {
                        var output = JsonSerializer.Serialize(
                            new
                            {
                                origin = CatalogOriginText(loaded.Origin),
                                loaded.Catalog,
                                loaded.Warning,
                            },
                            JsonDefaults.Options);
                        await Console.Out.WriteLineAsync(output).ConfigureAwait(false);
                        return 0;
                    }

                    if (loaded.Catalog is null)
                    {
                        await Console.Out.WriteLineAsync("尚未配置推广 Provider 目录。").ConfigureAwait(false);
                        return 0;
                    }

                    if (loaded.Catalog.Entries.Count == 0)
                    {
                        await Console.Out.WriteLineAsync("当前没有推广 Provider。").ConfigureAwait(false);
                    }

                    foreach (var entry in loaded.Catalog.Entries)
                    {
                        await Console.Out.WriteLineAsync($"[推广] {entry.DisplayName} ({entry.ApiFamily})").ConfigureAwait(false);
                        await Console.Out.WriteLineAsync("  " + entry.Description).ConfigureAwait(false);
                        await Console.Out.WriteLineAsync("  API: " + entry.ApiBaseUrl).ConfigureAwait(false);
                        await Console.Out.WriteLineAsync("  注册: " + entry.SignupUrl).ConfigureAwait(false);
                        await Console.Out.WriteLineAsync("  披露: " + entry.CommissionDisclosure).ConfigureAwait(false);
                    }

                    return 0;
                }

            case "use":
                {
                    if (args.Length < 2 || args[1].StartsWith('-'))
                    {
                        throw new ArgumentException("sponsors use requires an entry ID");
                    }

                    var entryId = args[1];
                    var parsed = ParseSponsorArguments(
                        args[2..],
                        ["--model", "--state-dir"],
                        ["--accept-disclosure", "--offline"]);
                    if (!parsed.Flags.Contains("--accept-disclosure"))
                    {
                        throw new ArgumentException(
                            "安装推广 Provider 必须显式提供 --accept-disclosure");
                    }

                    var stateRoot = ResolveApplicationStateRoot(GetSponsorOption(parsed, "--state-dir"));
                    using var service = new SponsoredCatalogService(stateRoot);
                    var loaded = await service.LoadAsync(
                        parsed.Flags.Contains("--offline"),
                        cancellationToken).ConfigureAwait(false);
                    var catalog = loaded.Catalog ?? throw new ArgumentException(
                        "尚未配置推广 Provider 目录");
                    var entry = catalog.Entries.FirstOrDefault(
                        item => string.Equals(item.Id, entryId, StringComparison.Ordinal))
                        ?? throw new ArgumentException("推广目录中没有该条目");
                    await Console.Out.WriteLineAsync(
                        $"[推广] {entry.DisplayName} ({entry.ApiFamily})").ConfigureAwait(false);
                    await Console.Out.WriteLineAsync(
                        "披露: " + entry.CommissionDisclosure).ConfigureAwait(false);
                    var route = new InstalledSponsoredRouteStore(stateRoot).Install(
                        catalog,
                        entryId,
                        RequireSponsorOption(parsed, "--model"));
                    await Console.Out.WriteLineAsync(
                        $"已安装 Provider：{route.ProviderId}；请继续执行 auth set。").ConfigureAwait(false);
                    return 0;
                }

            case "installed":
                {
                    var parsed = ParseSponsorArguments(
                        args[1..],
                        ["--state-dir"],
                        ["--json"]);
                    var routes = new InstalledSponsoredRouteStore(
                        ResolveApplicationStateRoot(GetSponsorOption(parsed, "--state-dir"))).List();
                    if (parsed.Flags.Contains("--json"))
                    {
                        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                            new { schemaVersion = "0.1", routes },
                            JsonDefaults.Options)).ConfigureAwait(false);
                    }
                    else
                    {
                        if (routes.Count == 0)
                        {
                            await Console.Out.WriteLineAsync(
                                "当前没有已安装的推广 Provider。").ConfigureAwait(false);
                        }

                        foreach (var route in routes)
                        {
                            await Console.Out.WriteLineAsync(
                                $"[推广] {route.DisplayName} => {route.ProviderId}").ConfigureAwait(false);
                            await Console.Out.WriteLineAsync(
                                "  family: " + route.ApiFamily).ConfigureAwait(false);
                            await Console.Out.WriteLineAsync(
                                "  models: " + string.Join(", ", route.Models)).ConfigureAwait(false);
                            await Console.Out.WriteLineAsync(
                                "  披露: " + route.CommissionDisclosure).ConfigureAwait(false);
                        }
                    }

                    return 0;
                }

            case "remove":
                {
                    if (args.Length < 2 || args[1].StartsWith('-'))
                    {
                        throw new ArgumentException("sponsors remove requires an entry ID");
                    }

                    var parsed = ParseSponsorArguments(args[2..], ["--state-dir"], []);
                    var removed = new InstalledSponsoredRouteStore(
                        ResolveApplicationStateRoot(GetSponsorOption(parsed, "--state-dir")))
                        .Remove(args[1]);
                    await Console.Out.WriteLineAsync(
                        removed
                            ? "已移除本地推广 Provider route。"
                            : "本地没有该推广 Provider route。").ConfigureAwait(false);
                    return 0;
                }

            default:
                throw new ArgumentException("unknown sponsors command: " + args[0]);
        }
    }

    /// <summary>
    /// 功能：严格解析推广目录子命令的命名值和布尔 flags。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="args">子命令后的 argv。</param>
    /// <param name="valueNames">允许携带下一 token 的选项。</param>
    /// <param name="flagNames">允许的无值 flags。</param>
    /// <returns>大小写敏感且无重复的参数集合。</returns>
    /// <exception cref="ArgumentException">未知、重复或缺值参数。</exception>
    private static SponsorCliArguments ParseSponsorArguments(
        string[] args,
        IReadOnlyCollection<string> valueNames,
        IReadOnlyCollection<string> flagNames)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (valueNames.Contains(name))
            {
                if (!values.TryAdd(name, RequireOptionValue(args, ref index, name)))
                {
                    throw new ArgumentException(name + " may only be provided once");
                }
            }
            else if (flagNames.Contains(name))
            {
                if (!flags.Add(name))
                {
                    throw new ArgumentException(name + " may only be provided once");
                }
            }
            else
            {
                throw new ArgumentException("unknown sponsors argument: " + name);
            }
        }

        return new SponsorCliArguments(values, flags);
    }

    /// <summary>
    /// 功能：取得推广子命令必需值并生成一致的用法错误。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">严格解析结果。</param>
    /// <param name="name">必需选项名。</param>
    /// <returns>非空值。</returns>
    /// <exception cref="ArgumentException">选项缺失。</exception>
    private static string RequireSponsorOption(SponsorCliArguments arguments, string name)
    {
        return arguments.Values.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException(name + " is required");
    }

    /// <summary>
    /// 功能：取得推广子命令可选值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="arguments">严格解析结果。</param>
    /// <param name="name">可选参数名。</param>
    /// <returns>未提供时 null。</returns>
    private static string? GetSponsorOption(SponsorCliArguments arguments, string name)
    {
        return arguments.Values.GetValueOrDefault(name);
    }

    /// <summary>
    /// 功能：把 .NET enum 映射为与 Rust JSON 一致的 snake_case 来源值。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="origin">内部来源枚举。</param>
    /// <returns>稳定 wire 文本。</returns>
    private static string CatalogOriginText(SponsoredCatalogOrigin origin)
    {
        return origin switch
        {
            SponsoredCatalogOrigin.Unconfigured => "unconfigured",
            SponsoredCatalogOrigin.Remote => "remote",
            SponsoredCatalogOrigin.Cache => "cache",
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
    }

    /// <summary>
    /// 功能：仅在字面 CLI 与环境 conformance 双门成立时读取有界审批短 timeout。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="cliConformance">argv 是否包含独立 `--conformance` 元素。</param>
    /// <param name="environmentConformance">`QXNM_FORGE_CONFORMANCE` 是否精确为 1。</param>
    /// <returns>严格十进制 1..3,600,000 毫秒配置，其他情况返回固定五分钟。</returns>
    /// <remarks>不变量：任一单门都不会读取 timeout 环境；符号、空白、Unicode 数字、零、溢出和越界值均拒绝。</remarks>
    private static TimeSpan ResolveApprovalTimeout(
        bool cliConformance,
        bool environmentConformance)
    {
        var fallback = TimeSpan.FromMinutes(5);
        if (!(cliConformance && environmentConformance))
        {
            return fallback;
        }

        var value = Environment.GetEnvironmentVariable("QXNM_FORGE_APPROVAL_TIMEOUT_MS");
        if (string.IsNullOrEmpty(value) ||
            !value.All(static character => character is >= '0' and <= '9') ||
            !long.TryParse(value, out var milliseconds) ||
            milliseconds is < 1 or > 3_600_000)
        {
            return fallback;
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// 功能：为 POSIX stdout 创建无用户态缓冲的协议流，使 write/flush 成功真实对应 pipe 交付。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>不拥有标准句柄、可由 daemon 独占写入的输出流。</returns>
    /// <remarks>不变量：POSIX 固定绑定 fd 1 且 bufferSize=1；Windows 保留运行时标准输出实现。</remarks>
    private static Stream OpenProtocolOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            return Console.OpenStandardOutput();
        }

        return new FileStream(
            new SafeFileHandle(new IntPtr(1), ownsHandle: false),
            FileAccess.Write,
            bufferSize: 1,
            isAsync: false);
    }

    /// <summary>
    /// 功能：从内部 event data DTO 提取文本 delta，不把整个事件打印到 stdout。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="data">message.delta data。</param>
    /// <returns>delta.text，缺失时为空串。</returns>
    private static string ExtractDeltaText(object data)
    {
        var element = JsonSerializer.SerializeToElement(data, JsonDefaults.Options);
        return element.TryGetProperty("delta", out var delta) &&
               delta.TryGetProperty("text", out var text)
            ? text.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// 功能：尽力移除本进程独占 conformance 临时 session，不影响正常持久化目录。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <param name="path">仅由 ResolveSessionsRoot 生成的临时路径。</param>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不改变已完成协议结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 临时目录清理失败不改变已完成协议结果。
        }
    }

    /// <summary>
    /// 功能：输出最小安全用法到 stderr。
    /// 作者：高宏顺
    /// 邮箱：18272669457@163.com
    /// </summary>
    /// <returns>portable 用法错误退出码 2。</returns>
    private static int Usage()
    {
        Console.Error.WriteLine("Usage: qxnm-forge-dotnet daemon --stdio [--conformance] [--workspace PATH] [--state-dir PATH]");
        Console.Error.WriteLine("       qxnm-forge-dotnet run [--workspace PATH] [--state-dir PATH] [--session ID] [PROMPT]");
        Console.Error.WriteLine("       qxnm-forge-dotnet session import-pi-v3 --source FILE --workspace PATH --state-dir PATH [--session ID] [--format text|json] [--conformance]");
        Console.Error.WriteLine("       qxnm-forge-dotnet sponsors keygen|sign|verify|configure|list|use|installed|remove [OPTIONS]");
        Console.Error.WriteLine("       qxnm-forge-dotnet auth set|list|remove [OPTIONS]  # secret only via stdin");
        return 2;
    }
}

/// <summary>
/// 功能：保存 PI v3 import CLI 的显式路径、目标 ID、输出格式和测试模式。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Source">只读 source 文件。</param>
/// <param name="Workspace">已存在工作区。</param>
/// <param name="StateDirectory">显式状态根。</param>
/// <param name="Session">可选新 Session ID。</param>
/// <param name="Format">text 或 json。</param>
/// <param name="Conformance">是否请求固定合成夹具输出。</param>
internal sealed record PiV3CliOptions(
    string Source,
    string Workspace,
    string StateDirectory,
    string? Session,
    string Format,
    bool Conformance);

/// <summary>
/// 功能：保存纵切面 CLI 支持的无 secret 选项。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Workspace">绝对存在的工作区。</param>
/// <param name="StateDirectory">可选工作区外状态根。</param>
/// <param name="Session">可选 session ID。</param>
/// <param name="Provider">Provider ID。</param>
/// <param name="Model">模型 ID。</param>
/// <param name="ApiFamily">可选显式 API family。</param>
/// <param name="Conformance">是否启用测试方法。</param>
/// <param name="Stdio">是否请求 stdio transport。</param>
/// <param name="Prompt">有序 prompt token。</param>
internal sealed record CliOptions(
    string Workspace,
    string? StateDirectory,
    string? Session,
    string Provider,
    string Model,
    string? ApiFamily,
    bool Conformance,
    bool Stdio,
    IReadOnlyList<string> Prompt);

/// <summary>
/// 功能：保存严格解析且不包含 secret 值的推广目录 CLI 参数。
/// 作者：高宏顺
/// 邮箱：18272669457@163.com
/// </summary>
/// <param name="Values">路径、URL、keyId 等命名值。</param>
/// <param name="Flags">offline/json 布尔开关。</param>
internal sealed record SponsorCliArguments(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlySet<string> Flags);
