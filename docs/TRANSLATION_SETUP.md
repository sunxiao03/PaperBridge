# 翻译服务设置与人工冒烟测试

PaperBridge 阶段 2 支持 OpenAI、DeepSeek 和使用 OpenAI Chat Completions 协议的兼容服务。程序不会内置或自动读取环境变量中的 API Key。

## 配置

1. 启动 PaperBridge，点击左侧“翻译服务设置”；
2. 选择 OpenAI、DeepSeek 或 OpenAI 兼容服务；
3. 确认 HTTPS Base URL 和模型名称；
4. 输入 API Key；
5. 按需填写“高级：自定义 AI 指令”，然后保存。

API Key 只进入 Windows Credential Manager，凭据目标名称以 `PaperBridge/translation/` 开头。非敏感设置保存在本地 `Settings/translation.json`，该文件不包含密钥字段。点击“删除密钥”并保存后可删除当前服务商的凭据。

## 使用

打开文献后，右侧翻译栏会载入当前阅读页的可提取英文文本。可以直接编辑文本或选择内容，然后使用：

- 单词：翻译光标所在单词；
- 选区：翻译明确选择的文字；
- 句子：翻译光标所在句子；
- 段落：以空行为段落边界；
- 当前页：翻译右侧原文框的全部页面文本。

页面发生变化后，可点击“载入当前页”刷新文本。新请求会取消该标签的旧请求；切换或关闭标签也会取消相关任务。失败后可重试，缓存命中不会再次调用 API。

## 可选真实 API 冒烟测试

真实联网验证不是自动测试条件，也不要把密钥写入命令行、脚本、配置示例或 Issue。

1. 使用界面保存测试账户的 API Key；
2. 导入项目生成的 `output/pdf/pdfium-text-layer-sample.pdf`；
3. 载入第一页并选择 `The effective multiplication factor is unity.`；
4. 点击“句子”，确认返回中文且状态显示模型名称；
5. 再次翻译相同句子，确认状态显示“缓存命中”；
6. 开始一个较长请求后切换标签，确认旧标签不再显示新结果；
7. 完成后在设置中删除测试密钥。

服务商会收到本次翻译所需的原文、有限页面上下文、相关术语约束和用户自定义指令。程序不记录 Authorization Header，也不在异常消息中包含响应正文。
