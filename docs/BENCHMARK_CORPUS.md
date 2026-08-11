# PaperBridge 基准 PDF 语料清单

日期：2026-08-10

公开仓库只保存项目自行生成、允许再分发的固定样本。真实论文若许可证不允许再分发，只在本机保存，并在本清单记录来源、版面特征和 SHA-256，不提交 PDF 内容。

## 项目生成样本

| 标识 | 页数 | 版面与用途 | SHA-256 | 仓库状态 |
|---|---:|---|---|---|
| `pdfium-text-layer-sample` | 2 | 单栏、简单双栏、表格、目录、元数据、字符坐标集成测试 | `bc2f4b4a19a62e46a9b7447d7e82085b82c5b8acb037076399c58c2ed8d6978d` | 提交到 `output/pdf` |
| `paperbridge-500-page-benchmark` | 500 | 单栏、双栏和表格循环出现；长文档与多标签资源基准 | `b335c949e73ca641d6dc8c42c5d34bc4dc03d3ddc92cb67c51f6ceaffe7b819f` | 本地生成，PDF 被 `.gitignore` 排除 |

500 页样本由 `tests/TestData/create_long_pdf_fixture.py` 确定性生成，不含任何外部论文内容。生成依赖记录在 `tests/TestData/requirements-fixtures.txt`。

## 真实语料缺口

以下样本仍需从作者、期刊的开放获取页面或用户合法持有的文献中选择。加入本机验收集时填写实际页数、公开获取地址或内部来源说明以及 SHA-256。

| 待补标识 | 要求 | 当前状态 |
|---|---|---|
| `real-short-single-column` | 1–20 页，正常文本层，单栏 | 缺失 |
| `real-two-column` | 双栏、脚注、参考文献 | 缺失 |
| `real-formula-dense` | 公式密集，含上下标和希腊字母 | 缺失 |
| `real-figure-table-dense` | 图表密集，含跨栏浮动对象 | 缺失 |
| `real-rotated-pages` | 至少包含一页旋转页面 | 缺失 |
| `real-100-200-pages` | 论文集或技术报告 | 缺失 |
| `real-about-500-pages` | 约 500 页真实长文档 | 缺失 |
| `real-abnormal-text-order` | 文本层存在顺序或字体映射异常 | 缺失 |

真实语料不得包含敏感、受出口管制或无权用于本地测试的材料。
