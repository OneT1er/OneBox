# OneBox 原创图标资产

本目录是 OneBox 自有 UI 的单一图标源。每个 SVG 都是 24×24 viewBox，使用原创 path/shape、`currentColor`、约 1.8px 线宽和 round cap/join；不包含字体、文字、位图、网络引用或第三方图标路径。

## 设计 token

```text
viewBox       0 0 24 24
stroke-width  1.8
linecap       round
linejoin      round
主色          #8E8CD8（由宿主通过 currentColor 注入）
暗底          #1C1A28
状态成功      #78C882
状态警告      #F0B450
状态错误      #E85D5D
```

图标保持约 2 px 光学留白，建议在 16 px 控件中以 14–16 px 渲染，在 24 px 槽位中以 20 px 渲染。品牌 `brand.svg` 描绘折叠工具箱的箱体、分隔层和聚合结构，不使用闪电符号。

语义 key、中文 AutomationName 和默认尺寸见 `manifest.json`。运行时建议以 key 查找，不以文件名散落引用。

## 导出

`export-icons.ps1` 使用本机 ImageMagick 的 `magick` 命令生成品牌预览 PNG 和多尺寸 ICO。脚本会在 SVG 输入前使用 1024 density 生成 1024px master，再以 Lanczos 下采样，避免 24px viewBox 直接放大造成模糊。安装 ImageMagick 后执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\export-icons.ps1
```

输出仍限制在本目录：`app-preview.png`、`app.ico`。ICO 规格为 16/20/24/32/40/48/64/128/256 px，32-bit RGBA。

`make-contact-sheet.ps1` 生成 `contact-sheet.svg`，网格顺序与 `manifest.json` 一致，便于视觉回归。

导出完成后运行 `validate-export.ps1`，它会检查 ICO 的 9 个尺寸、32-bit 帧深度，以及 PNG 的 256×256 sRGBA 8-bit 元数据。

## 检查

```powershell
powershell -ExecutionPolicy Bypass -File .\validate-icons.ps1
```

检查 XML 有效性、统一 viewBox/stroke、禁止 `text`/`font`/`image`/外部引用，并扫描 Emoji 字符。
