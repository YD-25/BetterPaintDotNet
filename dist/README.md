# 安装与使用

适用于 Paint.NET **5.1.12**，Windows 10 / 11 x64。

## 安装 / 卸载

保存正在编辑的图片后，运行 `install.bat`。脚本会查找 Paint.NET 安装目录、关闭软件、复制插件并重新启动。

卸载运行 `uninstall.bat`，按安装清单移除本插件文件。

## 常用操作

- 点击图层下的 R / G / B / A 查看、编辑通道；点击图层行返回完整图像。
- 拖动图层或通道调整顺序，点击眼睛图标切换显示状态。
- 移动工具中按住 `Shift` 拖动，对齐画布边缘、角落或中线；拖动角点时吸附到 `2^n` 像素尺寸。
- 按住 `Shift + Alt` 拖动角点，以中心点缩放并吸附尺寸。
- 底部栏查看源文件格式，悬停查看详情；无法定位源文件时显示“格式未知”。

## 手动指定安装目录

```powershell
.\install.ps1 -PdnRoot "D:\your-path\paint.net"
```

安装日志：`%TEMP%\LayerChannelLive-install.log`。插件诊断日志：`%TEMP%\LayerChannelLive.log`。
