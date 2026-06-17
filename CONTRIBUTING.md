# 贡献指南

感谢你对 OneBox 的兴趣！欢迎通过 Issue 和 Pull Request 参与贡献。

## 反馈 Bug / 提建议

提 Issue 前请先搜索是否已有相同问题。提 Issue 时请包含：

- **环境**：Windows 版本、是否管理员运行
- **复现步骤**：怎么触发的问题
- **预期 vs 实际**：你期望发生什么，实际发生了什么
- **日志**：如有，附上 `%TEMP%\pam_crash.log` 或 `pam_debug.log` 的内容

## 提交 Pull Request

1. Fork 本仓库
2. 创建分支：`git checkout -b feature/你的功能` 或 `fix/你修的bug`
3. 改代码，**遵守 C# 5 语法约束**（见下方）
4. 用 `src\build.bat` 确认能编译通过
5. 运行 `OneBox.exe` 确认功能正常
6. 提交并推送，发 PR 说明改了什么、为什么

## ⚠️ 开发约束

本项目用系统自带的 `csc.exe`（C# 5 编译器）构建，**不能使用 C# 6+ 语法**：

- ❌ 表达式体成员（`Type Prop => value;`）
- ❌ 字符串插值（`$"{x}"`）
- ❌ null 条件运算符（`?.`）
- ❌ `nameof`
- ❌ 自动属性初始化器
- ❌ 本地函数（方法内定义函数，C# 7+）

以下可以用：`var`、lambda（`(s, e) => {...}`）、对象/集合初始化器、LINQ、`async/await`。

## 代码风格

- 缩进 4 空格
- 私有字段用 `_camelCase`
- 异常处理：不要用空 `catch { }` 静默吞掉，至少用 `AppLog.Log` 记录
- 新增独立窗口请复用 `OneBoxWindow.Create(...)` 保持样式统一

## 项目结构

详见 [README.md](README.md) 的"项目结构"章节。
