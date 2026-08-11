# PaperBridge

PaperBridge 是一个面向 Windows 的本地英文科研论文阅读与中文翻译工具，优先服务核反应堆物理论文阅读。

项目首先满足个人使用，再以容易安装、容易审查、没有内置密钥的方式发布到 GitHub，供感兴趣的朋友试用。

## 当前状态

阶段 0–6 已完成。除本地文献库、按需 PDF 阅读、基础翻译和专业术语系统外，当前版本还提供翻译浮窗、右侧栏、段落双语派生视图、左右对照、页面/段落映射、版面置信度与明确降级、有界预翻译及可取消全文翻译。原文高亮、下划线、批注和页面书签保存为本地可验证锚点。AI 助读支持选区解释、章节/全文总结和当前论文有据问答；页码、英文片段和章节由本地 BM25 检索与引用校验确定，不接受模型伪造的证据。机器译文和用户编辑稿分层保存；API Key 只保存到 Windows Credential Manager。

完整范围和验收规则见：

- [产品规格](docs/PRODUCT_SPEC.md)
- [执行计划](docs/EXECUTION_PLAN.md)
- [技术基线决策](docs/adr/0001-technology-baseline.md)
- [PDFium 集成决策](docs/adr/0002-pdfium-integration.md)
- [本地文献库决策](docs/adr/0003-local-library-and-storage.md)
- [PDF 元数据与导航决策](docs/adr/0004-pdf-metadata-outline-and-thumbnails.md)
- [多标签与 PDFium 并发决策](docs/adr/0005-multi-tab-and-pdfium-concurrency.md)
- [文献分类与移除决策](docs/adr/0006-library-classification-and-removal.md)
- [阶段 0 性能基线](docs/PERFORMANCE_BASELINE.md)
- [基准 PDF 语料清单](docs/BENCHMARK_CORPUS.md)
- [翻译服务设置与人工冒烟测试](docs/TRANSLATION_SETUP.md)
- [基础翻译管线决策](docs/adr/0007-translation-pipeline-and-secrets.md)
- [术语系统使用说明](docs/GLOSSARY_SYSTEM.md)
- [术语数据与约束决策](docs/adr/0008-glossary-system.md)
- [双语阅读与全文翻译](docs/BILINGUAL_TRANSLATION.md)
- [版面降级与翻译调度决策](docs/adr/0009-bilingual-layout-and-scheduling.md)
- [高亮、批注与书签](docs/ANNOTATIONS_AND_BOOKMARKS.md)
- [批注锚点与安全迁移决策](docs/adr/0010-annotation-anchors-and-migration.md)
- [AI 阅读辅助](docs/AI_READING_ASSISTANT.md)
- [当前文档检索与可验证引用决策](docs/adr/0011-current-document-retrieval-and-citations.md)

## 技术基线

- Windows 10/11 x64
- C# / .NET 10 LTS / WPF
- PDFium
- SQLite + FTS5
- 自包含 Windows x64 发布

## 安全原则

真实 API Key 只存入 Windows Credential Manager。仓库、日志、数据库、测试数据和构建产物不得包含真实密钥。

## 许可证

[MIT](LICENSE)

## 0.1.0 发布候选

阶段 7 冻结版本为 `0.1.0`。发布候选采用 Windows x64 自包含 ZIP，并附带可审查的安装、升级、备份、恢复和卸载 PowerShell 脚本。默认卸载仅移除程序，保留 `%LOCALAPPDATA%\PaperBridge` 与 Windows Credential Manager 凭据；删除数据或凭据必须显式选择。

- [安装与卸载](docs/INSTALLATION_AND_UNINSTALL.md)
- [备份与恢复](docs/BACKUP_AND_RECOVERY.md)
- [已知限制](docs/KNOWN_LIMITATIONS.md)
- [发布说明](docs/RELEASE_NOTES_0.1.0.md)
- [隐私说明](PRIVACY.md)
- [安全政策](SECURITY.md)
- [支持边界](SUPPORT.md)
- [发布检查表](docs/RELEASE_CHECKLIST.md)

本地构建候选：

```powershell
.\packaging\Build-Release.ps1
.\packaging\Test-Packaging.ps1
.\packaging\Test-ReleaseSafety.ps1
```

仓库不会猜测或自动创建 GitHub remote；发布必须在人工验收后单独授权。
