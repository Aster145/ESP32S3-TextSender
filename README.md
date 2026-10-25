# ESP32-S3 TextSender

Windows 桌面串口发送工具，将文本交给 ESP32-S3，由开发板的原生 USB 接口模拟键盘输入。支持编辑框、可编辑常用短语，以及读取所选 ns2jb / HBLauncher 项目的 `V=START` 连接口令。

公开版 v1.7.1 使用 TextKeyboard v1.3 固件。开发板已经运行该固件时，无需重新刷机。

[下载最新 Windows 便携版](https://github.com/Aster145/ESP32S3-TextSender/releases/latest)

## 开始使用

需要 Windows、.NET Framework 4.8，以及运行配套 TextKeyboard v1.3 固件的 ESP32-S3。目标设备应使用英文 / US 键盘布局。

1. 将程序放在可写目录，运行 `TextSender.exe`。从源码使用时，先按下方步骤构建。
2. 双 USB 开发板的 **UART / COM 接口接电脑**，**原生 USB / OTG 接口接收键盘输入的目标设备**。接口用途以开发板标注为准。
3. 选择 UART 对应的串口。若列表为空，检查数据线、开发板连接和 USB 转串口驱动，然后点击“刷新”。关闭占用同一串口的其他工具。
4. 将输入目标置于需要接收文字的位置，在编辑框输入内容，再点击发送；也可以点击常用短语按钮。

启动程序、切换项目和获取口令预览都不会自动发送按键。

## 文本与常用短语

- 支持 ASCII 英文、数字、常见半角符号、换行和制表符；不支持直接发送中文内容。短语按钮名称可以使用中文。
- 单次最多 8192 字节，按换行规范化后的 ASCII 内容计算；追加回车也计入长度。超出长度或包含不支持的字符时，发送前会提示。
- “发送前等待”可设为 0～10 秒，步进 0.1 秒。**0 表示不额外倒计时**，首次打开串口仍需约 1.2 秒初始化。逐字间隔和换行等待另行设置；固件每次按键本身还会保持约 20 毫秒。
- “管理常用短语”支持新增、编辑、删除和为单条短语设置末尾回车。点击短语直接发送，不替换编辑框内容。
- 全局“发送后自动回车”默认关闭。开启后，未以换行结尾的短语会追加回车；与单条短语的追加选项同时开启时只追加一次。单条短语原有的明确追加行为保持不变。
- “清除回车后的自动缩进”仅用于 SmileBASIC 的 EDIT 编辑界面，用来保留原文缩进。普通输入通常不需要勾选。
- “停止”请求固件取消当前输入。“切换目标 Caps Lock”会实际发送 Caps Lock 按键。
- 发送完成后保留串口连接供下一次复用。更换串口、刷新串口列表、遇到通信错误或关闭程序时会释放连接。

程序显示完成，表示固件完成了发送流程；请在目标设备核对实际文字。

## 保存多个项目并切换

点击“添加文件夹…”选择项目目录，也可以直接在“项目目录”中粘贴路径并按回车。支持项目根目录或其 `runs` 目录。

已添加的目录出现在“切换项目”列表中，列表和当前选择会保存。大小写、末尾斜杠，以及同一项目的根目录 / `runs` 两种选法会去重。“移除项目”只移除列表项，不删除文件夹。

程序只读取**当前选中项目**的口令，不会在全部已保存项目中寻找最新文件。目录不可用时会清空预览并提示，不会转用另一项目。

## 获取并发送 V=START

1. 按对应项目自己的流程启动 ns2jb / HBLauncher，等待它生成本次连接口令，并保持主机程序运行。
2. 在 TextSender 中选择本次实际运行的项目。
3. “获取最新”只读取和预览；目标设备准备好后，点击“获取并发送 V=START”。

程序在选中项目的 `runs/switch2-*/connect.txt` 和 `runs/launcher-*/connect.txt` 中，按文件修改时间选择最新会话。发送按钮会重新读取文件，发送整行并追加一次回车，不使用过期预览。该按钮使用当前的时间设置，但不执行 EDIT 自动缩进清理。

最新会话已存在 `connection.json` 或 `result.json`、口令格式无效，或者本机没有对应监听地址和端口时，会提示而不发送。程序只检查本机监听表，不主动连接该网络端口，也不退回更旧的会话。

### HBLauncher 口令导出接入

如果主机程序只在终端显示口令，需要在它现有的 `onWaiting` 回调中，将**本次实际生成的完整 `V=START(...)` 指令**写入当前 `runs/launcher-*` 会话目录的 `connect.txt`：UTF-8 编码、单行文本。回调参数和变量名应以所用主机项目为准，不要硬编码口令。

本仓库提供读取端，不附带第三方主机项目的完整文件，也不会自动修改 ns2jb / HBLauncher。主机项目的安装、启动和目标设备准备步骤仍按其自身说明执行。

## FTP 启动脚本

公开版不预填 FTP 路径，也不附带 FTP 服务或启动脚本。首次点击“启动 FTP”时选择自己的 `.cmd` / `.bat` 文件；之后可以通过“设置 FTP…”更换。

脚本在其所在目录启动，并打开命令窗口显示结果。启动脚本不会自动切换当前项目或发送连接口令。请按所用 FTP 工具的说明准备脚本及运行环境。

## 配置与便携使用

设置保存在 EXE 同目录的 `settings.xml`，包括时间参数、常用短语、自动回车、置顶选项、项目列表、当前项目及 FTP 脚本路径。移动程序时一并保留该文件；目录不可写时，程序会明确提示保存失败。

`settings.example.xml` 是可公开的初始配置，不含预填项目、个人路径或连接口令。实际使用生成的 `settings.xml` 属于本地配置，已被 `.gitignore` 排除；不要将自己的配置或 `connect.txt` 提交到公开仓库。

## 从源码构建

不需要 NuGet 依赖或安装 Visual Studio。`build.cmd` 优先使用系统的 64 位 .NET Framework C# 编译器，找不到时使用 32 位版本。

在仓库目录运行：

```bat
build.cmd
```

输出为 `dist/TextSender.exe`。构建脚本会复制本说明；仅当 `dist/settings.xml` 不存在时，才从 `settings.example.xml` 创建初始配置，不覆盖已有设置。

主要文件：

```text
src/
  TextSender.cs              Windows 界面、串口发送和本地设置
  StartTokenReader.cs        按项目读取与检查连接口令
  ExplorerFolderPicker.cs    原生 Windows 文件夹选择器
firmware/TextKeyboard/
  TextKeyboard.ino           配套 v1.3 固件源码
settings.example.xml         无个人数据的初始配置
build.cmd                    Windows 构建脚本
test.cmd                     构建并运行离线与界面逻辑检查
tests/                       使用隔离配置和会话文件的检查源码
```

完整检查可在命令提示符中运行：

```bat
test.cmd
```

脚本先构建程序，再检查编码与协议参数、口令读取、多项目切换和迁移、短语自动回车，以及 FTP 配置。测试使用隔离的配置和会话文件；不会启动 FTP、打开串口或发送 HID 按键。

也可以单独运行程序内置的离线自检：

```bat
start "" /wait dist\TextSender.exe --test-offline dist\offline-report.txt
type dist\offline-report.txt
```

离线自检不打开串口、不发送 HID 按键。它验证编码、协议参数及设置等逻辑，不代表已在目标设备完成输入测试。

## 固件下载与源码

仓库包含 `firmware/TextKeyboard/TextKeyboard.ino`，发布页提供配套 **TextKeyboard v1.3 / ESP32-S3 / 16MB Flash** 固件：

- [固件压缩包（BIN、源码、刷写说明）](https://github.com/Aster145/ESP32S3-TextSender/releases/download/v1.7.1/ESP32S3-TextKeyboard-v1.3-16MB.zip)
- [直接下载合并 BIN](https://github.com/Aster145/ESP32S3-TextSender/releases/download/v1.7.1/ESP32S3-TextKeyboard-v1.3-16MB.bin)
- [刷写说明](firmware/FLASHING.md)

合并镜像从 **0x0** 写入整个 16MB Flash，会覆盖现有程序和存储数据。此包仅用于匹配配置的 ESP32-S3；已有配套 v1.3 固件可以继续使用，更新 TextSender 不需要重刷。

源码面向 Arduino ESP32 的原生 USB OTG 模式，构建注释要求 `USBMode=0`、`CDCOnBoot=cdc`。UART0 使用 115200 波特率、RX GPIO44、TX GPIO43；原生 USB 同时提供 HID 键盘和 CDC 命令接口。具体开发板的引脚连接、Flash / PSRAM 选项及烧录参数应按板型确认。

电脑与键盘输入目标是不同设备时，使用前述 UART + 原生 USB 双线连接。该仓库不会在启动桌面程序时刷写开发板固件。
