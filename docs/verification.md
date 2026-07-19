# Rust/.NET 验证说明

作者：高宏顺  
邮箱：18272669457@163.com

vNext 只以 Rust 与 .NET 为 active implementations。默认门禁不得读取真实 Provider 凭据，
不得访问付费模型；使用 faux、literal loopback mock、脱敏 fixture 和运行时临时密钥。

## Rust

```sh
cargo fmt --manifest-path rust/Cargo.toml --all -- --check
cargo clippy --manifest-path rust/Cargo.toml --workspace --all-targets --locked --offline -- -D warnings
cargo test --manifest-path rust/Cargo.toml --workspace --locked --offline
```

## .NET

```sh
dotnet format dotnet/QxnmForge.slnx --verify-no-changes --no-restore
dotnet build dotnet/QxnmForge.slnx --configuration Release --no-restore -warnaserror
dotnet test dotnet/QxnmForge.slnx --configuration Release --no-restore -warnaserror
```

## 共同门禁

`CONFORMANCE/` 中 runner 只通过 CLI/daemon argv 黑盒调用实现，不导入 Rust/.NET 运行时代码。
签名推广目录专项命令见
[`sponsored-provider-commercialization.md`](sponsored-provider-commercialization.md)。

工作区外 CredentialStore、双向 JSON 互操作、明确披露门和缺密钥 Session 前拒绝：

```sh
python3 CONFORMANCE/commercial_state_runner.py \
  --rust-command-json '["./rust/target/debug/qxnm-forge"]' \
  --dotnet-command-json '["dotnet","./dotnet/src/QxnmForge.Cli/bin/Release/net10.0/qxnm-forge-dotnet.dll"]'
```

当前 runner 汇总 `10 cases / 2 cross-runtime directions / 0 network requests`。它不证明真实
中转站在线、计费正确或 Windows DPAPI；真实站必须另做显式 live smoke。

`implemented` 只表示代码和实现侧测试存在；只有共同适用黑盒案例完整通过后才能提升为
`conformant`。真实 Provider 还需要显式 live opt-in 才能登记 `live-verified`。
