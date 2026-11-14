# ChuImage 项目上下文

## 项目定位与范围

- ChuImage 是基于 [ImageGlass](https://github.com/d2phap/ImageGlass) 二次开发的独立桌面图片查看器，官网为 https://image.withqq.com 。保留上游源码中的版权和许可声明；许可见根目录 `LICENSE`。
- 以本仓库 `source/` 为客户端代码基线，对应上游新一代跨平台源码（上游称 v10），不使用 `v9/`。除非用户明确要求比较或迁移，不要从 v9 推断实现。
- 上游 `develop` 是最新开发分支，`prod` 是稳定分支。这里的 `source/` 已包含 ChuImage 修改；不要假定它与上游最新提交完全一致。同步上游前先核对差异，保留本项目修改。
- 面向用户的项目说明和构建命令见根目录 `README.md`；修改客户端前还应阅读 `source/CLAUDE.md` 和 `source/.editorconfig`。文档与实际代码不一致时，以当前源码和构建配置为准。

## 工作区目录

| 路径 | 用途 |
| --- | --- |
| `source/` | 桌面客户端、平台代码、资源和打包脚本。解决方案为 `source/ImageGlass.slnx`。 |
| `lumina-backend/` | 官网及相关 Go 服务，独立仓库；官网入口为 https://image.withqq.com 。先检查该仓库当前路由与页面，再修改或部署。 |
| `project-skill/` | 项目私有 skill，不提交到公共仓库。macOS 发布流程见 `project-skill/chu-image/SKILL.md`。 |
| `qiniu/` | 七牛上传工具，独立 Go 项目；接口和配置说明见其 `README.md`。发布流程可能用到它。 |

后三个目录在当前工作区是本地符号链接，并被根目录 `.gitignore` 忽略；其他环境可能不存在或指向不同位置。不要把符号链接、私有 skill、配置、凭证或发布产物提交到公共仓库。跨仓库操作前分别检查路径、Git 状态和实际内容。

## 客户端架构

- 技术栈为 C#、.NET 11、Avalonia 和 SkiaSharp；依赖版本集中在 `source/Directory.Packages.props`，各项目配置使用预览版 C# 功能。
- `ImageGlass.Lib/` 是共享核心：`Common/Photoing/` 负责图片、编解码和缓存，`UI/Viewer/` 负责查看与渲染，`UI/Gallery/`、`UI/Toolbar/` 负责相应界面，`Settings/` 管理配置，`Windows/` 和 `ViewModels/` 实现窗口和视图模型，`Tools/`、`Plugins/` 提供扩展能力。
- `ImageGlass.Win32/`、`ImageGlass.Mac/`、`ImageGlass.Linux/` 各自提供 `Program.cs` 和平台服务实现；共享服务接口在 `ImageGlass.Lib/Common/ServiceProviders/`。跨平台功能优先放共享层，平台差异通过服务接口实现。
- `source/__assets/__app/` 存放运行时随包资源；各平台的打包资源和脚本位于 `source/__assets/{win,mac,linux}/`。调试运行时可能需要将 `__app/` 内容复制到可执行文件旁，具体参见 `source/CLAUDE.md`。
- 各平台发布启用 AOT 和裁剪；修改序列化、反射、原生互操作时需考虑兼容性。图片渲染与缓存涉及并发及原生资源生命周期，沿用现有锁、租约和释放模式。界面优先复用现有控件、主题资源和本地化机制。
- 版本统一定义在 `source/Directory.Build.props` 的 `IgVersion`；`Directory.Build.targets` 生成构建信息。不要只修改单个平台项目中的版本。

## 构建、验证与发布

- 构建要求 .NET 11 SDK 和目标平台原生工具；macOS 还需要 Xcode Command Line Tools。常用 `dotnet restore source/ImageGlass.slnx`，各平台 `dotnet publish` 示例见根目录 `README.md`，VS Code 任务见 `source/.vscode/tasks.json`。AOT 发布及打包可能有平台、架构、签名等额外要求。
- 按改动范围运行相应平台构建并验证相关交互；涉及跨平台共享代码时考虑其他平台。不能运行时说明环境限制，不要声称已验证。
- macOS 正式打包使用 `source/__assets/mac/script-pack-mac-zip.sh`，使用临时签名且不公证；上传及官网更新流程以私有 skill 和实际环境为准。该 skill 中的外部项目路径和页面路径可能因工作区布局或官网演进而过时，执行前先确认；本工作区优先检查上述根目录链接。不要把构建、上传或修改源码等同于已部署上线。
- 七牛配置包含凭证，不得输出、复制或提交。上传工具成功后返回 JSON，既有对象 key 不会覆盖；应检查结果，并准确对应平台和架构。
