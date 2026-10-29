# Architecture & Design 简体中文补丁

适用于 Steam 游戏 **Architecture & Design / ArchitectureDesign**，当前 **R4 支持 Build 26277848**。这是非官方汉化项目。

**[下载最新一键安装器](https://github.com/long13021925243-lang/ArchitectureDesign-ZhCN/releases/latest/download/ArchitectureDesign_ZhCN_Setup.exe)** · **[查看所有版本](https://github.com/long13021925243-lang/ArchitectureDesign-ZhCN/releases)**

## 安装

1. 退出游戏，从 Release 下载 `ArchitectureDesign_ZhCN_Setup.exe`。
2. 把它放进游戏根目录，也就是包含 `ArchitectureDesign.exe` 的文件夹。
3. 双击，点击“安装 / 更新汉化”。成功后正常从 Steam 启动游戏。

安装器是单文件，使用时无需 Python 或命令行。需要 64 位 Windows、.NET Framework 4.x，以及 Windows 已安装的微软雅黑常规和粗体字体（`msyh.ttc`、`msyhbd.ttc`）。缺少字体时会在替换游戏文件前停止，并提示安装简体中文补充字体。仅在提示写入权限不足时，以管理员身份运行。

安装时从当前电脑的 Windows 字体目录复制字体。下载文件和仓库均不包含 Windows 字体，也不包含完整游戏程序。

## 汉化范围

- 主菜单、载入、环境选择、地块尺寸、门窗、洞口、楼板、墙、楼梯、屋顶、路径和栏杆。
- 家具及材质分类、材质显示名称、地形与植被、天气、声音、退出保存确认等。
- 教程、操作说明及部分更新日志。

分类和材质通过专用显示映射翻译，保留内部资源 ID。快捷键、技术名、游戏品牌和模型编号（例如 `Sofa-05`）保留。未知名称保持原文；与内置词条同名的自定义名称可能匹配显示翻译。

R4 重新定位了旧版全部译文和显示调用，并新增照片保存提示、“今天／昨天／最新”、中文日期格式及专用月份表。验证范围与未覆盖分支见 [验证说明](docs/VERIFICATION.md)。

## 备份、校验和恢复

安装器提供“校验汉化”和“恢复备份”。本版最初安装前的文件保存在游戏根目录的 `ArchitectureDesign_ZhCN_Backup/26277848/`。旧 Build 的备份目录会保留。恢复会回到对应 Build 的最初备份；如果安装前已留有更早补丁资源，恢复不代表清除所有历史汉化。

补丁不修改玩家建筑存档。请保留完整备份目录。安装失败时会尝试回滚本次替换；安装日志为游戏根目录的 `ArchitectureDesign_ZhCN_Installer.log`。

安装器检查完整主程序 SHA-256。Steam 验证还原了同一受支持版本时可以重装；游戏更新为未知版本后，需要重新适配。不要强行跳过校验或手动套用旧偏移。

| 游戏 Build | 补丁 |
| --- | --- |
| 26277848 | 当前 R4 / v4.0.0 |
| 26257309 | 历史 R3.1 / v3.1.0 |
| 26271635 | 未发布适配版 |

## 从源码构建

要求 Windows、Python 3.9+（仅标准库）和 .NET Framework 的 C# 编译器：

```powershell
python tools/build.py
```

生成文件位于 `dist/`。构建输入是已审计的差分清单、安装器源码、中文 JSON 与只包含这两份 JSON 的小型 PAK。无需提供完整游戏程序，也无需下载字体。

`data/manifest.json` 记录原始及目标主程序哈希、精确字节差分、追加节、资源哈希和兼容旧版。要独立验证差分，需要自己提供合法安装中的原始主程序：

```powershell
python tools/verify_patch.py --original "完整路径/ArchitectureDesign-Win64-Shipping.exe"
```

此命令不会修改原文件。`tools/build.py --help` 和 `tests/test_installer.py --help` 提供更多选项。重新生成 PAK 时不能假定 repak 产生相同的二进制字节；正式构建使用清单校验的固定 PAK。

## 项目内容

| 路径 | 内容 |
| --- | --- |
| `src/Installer.cs` | 安装、升级、校验、备份与回滚 |
| `data/manifest.json` | 当前版本差分和兼容清单 |
| `data/localized-data/` | 中文材质与更新日志数据 |
| `data/*mapping.json`、`data/translations.json` | 翻译与显示映射 |
| `tools/`、`tests/` | 构建、差分验证与仿真安装测试 |
| `inspection/` | 独立存档巡检与截图 OCR 脚本 |

发现遗漏时，请在 Issue 中附上游戏 Build、当前补丁版本、进入该界面的步骤和截图。请不要上传个人存档、完整游戏程序或含私人路径的日志。

第三方工具与字体处理说明见 [THIRD_PARTY.md](THIRD_PARTY.md)。
