# LayerChannelLive 一键安装与使用

## 一键安装
双击运行 **`install.bat`**。它会自动执行：
1. 智能探测 Paint.NET 安装目录（支持注册表、运行中进程及默认安装路径）。
2. 安全关闭正在运行的 Paint.NET 实例（防止 DLL 文件被锁占用）。
3. 将编译好的 `LayerChannelLive.dll` 复制到 Paint.NET 的 `Effects` 扩展目录，并记录安装清单。
4. 自动启动 Paint.NET 并加载插件，无需手动在菜单中反复点击激活。

## 一键卸载
双击运行 **`uninstall.bat`**。它会自动关闭 Paint.NET 并依据安装清单精确清理本插件的 DLL 与临时日志，绝不破坏宿主或其它第三方扩展。

## 命令行高级用法
```powershell
# 手动指定 Paint.NET 目录
.\install.ps1 -PdnRoot "D:\ruanjian\paint.net.5.0.13.install.x64"

# 安装后不自动退出或重启 Paint.NET
.\install.ps1 -NoClose

# 完整卸载并清空诊断日志
.\uninstall.ps1 -Purge
```

## 运行环境与兼容性
- 宿主系统：Windows 10 / 11 (x64)
- 软件版本：**Paint.NET 5.1.12**（基于内置 .NET 9.0 运行时）
- 编译目标：`net9.0-windows`

## ⌨️ 常用快捷键操作
| 操作 | 快捷键 | 效果 |
|---|---|---|
| **移动图层/选区吸附** | **按住 `Shift`** + 鼠标拖动 | 自动吸附画布四个角落、四条边缘及居中线，松开立即锁定 |
| **选区 $2^n$ 纹理尺寸吸附** | **按住 `Shift`** + 拖拽角点手柄 | 自动等比卡位贴靠 64、128、256、512、1024、2048 等尺寸 |
| **中心对称缩放吸附** | **按住 `Shift + Alt`** + 拖拽角点手柄 | 基于中心点对称缩放并吸附 $2^n$ 次方尺寸 |
| **自由编辑** | 直接拖动（不按 Shift） | 原生自由平移与缩放，不触发任何吸附 |

