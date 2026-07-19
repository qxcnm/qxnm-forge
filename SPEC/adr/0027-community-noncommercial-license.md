# ADR 0027：Community 采用非商业源码许可

- 状态：Accepted
- 日期：2026-07-19
- 项目：QXNM Forge
- 作者：高宏顺 `<18272669457@163.com>`

## 背景

Community 版本用于公开源码、建立用户群和展示产品能力，但作者不希望第三方无偿把它用于收费服务、公司业务、商业交付或转售。此前 ADR 0026 中的 MIT 选择授权范围过宽，无法形成清晰的 Community→Pro/商业授权转化路径。

## 决策

`open/` 从本发行起采用 **PolyForm Noncommercial License 1.0.0**，并携带以下 Required Notice：

```text
Required Notice: Copyright (c) 2026 高宏顺. QXNM Forge Community is licensed for noncommercial use only. Any commercial use requires a separate written commercial license from 高宏顺 <18272669457@163.com>.
```

Community 允许许可证正文列明的个人学习、研究、实验、爱好项目和非商业组织用途。未获得作者单独书面许可时，不授权公司业务、收费 SaaS/API、商业产品集成、客户交付、转售、代运营或其他商业用途。

Rust package 使用 `license-file = "../LICENSE"`，不得继续声明 SPDX `MIT`。README 必须把 Community 描述为“源码公开/源码可见、仅限非商业使用”，不能把它表述为 OSI 定义的 open source。

Pro 继续使用独立专有许可证。需要商业使用、OEM、私有部署或定制授权时，联系高宏顺 `<18272669457@163.com>`，最终范围以双方书面协议为准。

Provider/model manifest 中对历史参考资料的 `MIT` 标记是第三方来源归属，不是 QXNM Forge Community 的发行许可，必须继续保留并验证。

## 后果

- 公开仓库仍可被查看、研究和按许可证用于非商业目的，但第三方不能仅凭公开可读源码开展商业利用。
- “源码公开”不再等同“宽松开源”；市场和文档必须避免误导。
- 许可证变更适用于携带本许可证的当前及后续发行；已通过其他书面协议获得的权利不受本 ADR 替代。
- 许可证不是技术 DRM，商业保护仍需要合同、Pro 权益、服务端能力和品牌/渠道运营共同完成。

