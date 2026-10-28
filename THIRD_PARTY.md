# 第三方组件与发布内容

- **Architecture & Design**：游戏及其商标、资源归原权利人所有。本项目为非官方翻译补丁，需要用户已有受支持的游戏安装；不提供完整游戏主程序或原始游戏资源包。
- **Windows 微软雅黑**：安装器读取用户当前 Windows 已安装的 `msyh.ttc` 和 `msyhbd.ttc`。仓库、ZIP、PAK 和 Release 安装器均不包含字体文件。
- **repak**：中文数据 PAK 使用 [trumank/repak](https://github.com/trumank/repak) 构建。上游采用 MIT / Apache-2.0 双许可。本仓库没有分发 repak 可执行文件；如需解包核对或重新生成 PAK，请从上游取得工具并遵守其许可。
- **.NET Framework**：安装器使用 Windows 上的 .NET Framework 类库；仓库与安装器没有捆绑该运行时。

`data/payload/` 中的小型 PAK 仅包含本项目两份中文 JSON。`data/manifest.json` 为精确差分及校验数据，不是可单独运行的完整游戏。
