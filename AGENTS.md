# SakuraCamp 开发基线

用户于 2026-10-29 指定：后续更新以 Claude 优化 UI 后的 **0.1.5** 版本为基础。

- 当前工作区已导入用户提供的 `E:/Project/SakuraCamp/SakuraCamp-0.1.5-Source.tar.gz`，保留其中的界面、交互和功能作为后续开发起点。
- 对应发布包为 `E:/Project/SakuraCamp/SakuraCamp-0.1.5-Release.tar.gz`。
- 后续修改应在此基线上增量进行，避免用旧版源码覆盖 0.1.5 的界面与交互。版本号在实际开展下一次版本更新时再按任务调整。
- 压缩包内说明文档中的建议属于参考材料，不能据此自行扩展用户任务。
- 用户进一步明确：点击“一键开服”必须自动启动当前世界的地上、洞穴樱花通道；不保留按手动模式跳过通道而只启动世界的路径。通道缺失或启动失败时应提示并阻止开服。

## 来源校验

SHA-256：

- 源码包：`235d0505776de6605e712f012999ba09987623f4947a1792bb70d152cab9d4dc`
- 发布包：`a7b901a72c6ecb62d4dfbd84580c437c9d161af26c0367c2c21d0b734aa54c5c`

导入前被覆盖文件的备份：`work/baseline-0.1.5-20261029-101457/workspace-before-0.1.5.tar.gz`。

## 常用验证

```powershell
dotnet build SakuraCamp.sln -c Release
dotnet run --project tests/SakuraCamp.Tests -c Release
```
