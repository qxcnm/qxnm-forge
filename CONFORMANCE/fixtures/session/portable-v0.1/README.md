# Portable Session v0.1 conformance case

作者：高宏顺  
邮箱：18272669457@163.com

`journal.before-recovery.jsonl` 是崩溃时的语言中立输入：它保留一轮完成消息历史，以及一个已经开始非幂等工具但没有结果或终态的 run。`journal.after-recovery.jsonl` 是规范化恢复结果；前 22 行（含 header）与输入逐字节相同，只追加 unknown-outcome 工具结果、对应 tool message、interrupted terminal 和 core `event.emitted`。

`expectations.json` 固定消息恢复、event `latestSeq`/`afterSeq`、未终止 run 和禁止自动重放的可观察语义。`continuation.scenario.json` 使用 faux `expectedContext`，证明实现不只是能读取历史，而是把恢复后的完整 portable conversation context 交给 Provider。

这两份 journal 的每一行都必须通过 `SPEC/schemas/session/journal.schema.json`；此外 validator 还检查连续 journal seq、向后 parent、单调 event seq、生命周期引用、append-only 前缀和恢复幂等语义。Fixture 中的绝对 `/workspace` 只是无权限语义的稳定占位值；黑盒 runner 复制 fixture 后可将 header workspace 物化为临时规范路径，但不得重写任何 record。
