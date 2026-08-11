# PaperBridge

[![Release](https://img.shields.io/github/v/release/sunxiao03/PaperBridge?display_name=tag)](https://github.com/sunxiao03/PaperBridge/releases/latest)
[![License](https://img.shields.io/github/license/sunxiao03/PaperBridge)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4)](#系统要求)

PaperBridge 是一款面向 Windows 的本地优先 PDF 阅读器，适合阅读英文科研论文并进行中文翻译与 AI 辅助理解。

它支持直接在 PDF 原文上划选任意内容进行翻译，同时提供本地文献库、术语表、批注、书签、双语视图和独立 AI 助读窗口。项目起源于核反应堆物理论文的个人阅读需求，目前以开源预览版形式供朋友和感兴趣的用户试用。

## 主要功能

- 高清连续 PDF 阅读、平滑滚动、多标签页、目录和缩略图导航
- 直接在 PDF 原文上划词、划句或选择任意段落进行翻译
- 可收起的文献栏与目录栏，为阅读和译文保留更多空间
- OpenAI、DeepSeek 及 OpenAI 兼容服务支持
- 术语表约束、段落双语视图、左右对照和全文翻译
- 高亮、下划线、批注和页面书签
- 独立 AI 助读窗口，支持选区解释、章节/全文总结和当前文献问答
- 本地 SQLite 文献库、自动数据库快照及完整数据备份/恢复脚本
- API Key 仅保存到 Windows Credential Manager

## 下载与安装

从 [Releases](https://github.com/sunxiao03/PaperBridge/releases/latest) 下载：

- `PaperBridge-0.1.0-win-x64.zip`
- `SHA256SUMS.txt`

下载后先验证文件哈希：

```powershell
Get-FileHash .\PaperBridge-0.1.0-win-x64.zip -Algorithm SHA256
```

然后解压 ZIP，在解压目录中运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-PaperBridge.ps1
```

默认安装到 `%LOCALAPPDATA%\Programs\PaperBridge`，并创建开始菜单快捷方式。详细说明见[安装与卸载](docs/INSTALLATION_AND_UNINSTALL.md)。

> [!IMPORTANT]
> `0.1.0` 是未签名的个人项目预览版，Windows SmartScreen 可能显示警告。请从本仓库 Release 下载，并使用随附的 `SHA256SUMS.txt` 校验安装包。

## 基本使用

1. 启动 PaperBridge 并导入 PDF。
2. 在 PDF 原文上拖选文字，松开鼠标后自动翻译。
3. 在“设置”中选择翻译服务、模型和 Base URL，并保存 API Key。
4. 点击“AI 助读”可打开独立窗口，解释选区、总结章节或就当前文献提问。

翻译和 AI 功能需要用户自行提供服务商 API Key；普通 PDF 阅读、检索、批注和文献管理均在本地完成。

## 隐私与安全

- 项目没有账户系统、遥测、广告、崩溃上报或自动更新检查。
- PDF、数据库、批注和设置默认保存在 `%LOCALAPPDATA%\PaperBridge`。
- API Key 保存在 Windows Credential Manager，不写入仓库、设置 JSON 或 SQLite。
- 只有在用户主动翻译或调用 AI 助读时，所需文本和自定义指令才会发送给所选服务商。
- 机密或受限制文档在调用第三方服务前，应先确认相应数据处理权限。

请阅读[隐私说明](PRIVACY.md)、[安全政策](SECURITY.md)和[已知限制](docs/KNOWN_LIMITATIONS.md)。

## 从源码构建

### 系统要求

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PowerShell

### 构建与测试

```powershell
git clone https://github.com/sunxiao03/PaperBridge.git
cd PaperBridge

dotnet restore .\PaperBridge.slnx
dotnet build .\PaperBridge.slnx --configuration Release --no-restore
dotnet test .\PaperBridge.slnx --configuration Release --no-build --no-restore
```

生成自包含 Windows x64 发布包：

```powershell
.\packaging\Build-Release.ps1
.\packaging\Test-Packaging.ps1
.\packaging\Test-ReleaseSafety.ps1
```

## 项目结构

```text
src/PaperBridge.App             WPF 桌面应用
src/PaperBridge.Application     阅读、翻译与助读用例
src/PaperBridge.Domain          领域模型和核心规则
src/PaperBridge.Infrastructure  PDFium、SQLite、网络和凭据存储
tests/                          自动化测试
packaging/                      构建、安装、备份和卸载脚本
docs/                           产品说明、使用文档和架构决策
```

## AI 开发声明

本项目的源代码均由 **OpenAI Codex** 根据维护者提出的需求编写和修改。维护者负责需求定义、产品决策、人工测试验收、风险判断和版本发布；Codex 负责架构实现、代码修改、自动化测试、工程文档和发布脚本。

AI 生成不代表代码天然正确或安全。本项目仍按普通开源软件的标准接受审查，使用者应结合源码、测试结果和自身场景进行判断。发现问题时，欢迎提交可复现且不包含敏感资料的 Issue。

## 项目状态与贡献

PaperBridge 是个人维护、尽力支持的项目，目前没有长期路线图或商业支持承诺。Issue 和 Pull Request 均可提交，但回复与合并时间不作保证。

提交问题前请：

- 确认问题不属于[已知限制](docs/KNOWN_LIMITATIONS.md)
- 尽量使用非机密、可公开的测试 PDF
- 删除日志中的 API Key、论文敏感内容和个人信息
- 提供 Windows 版本、PaperBridge 版本及复现步骤

## 许可证

本项目采用 [MIT License](LICENSE)。
