# ESP32-S3 TextKeyboard v1.4 固件

适用于 **ESP32-S3、16MB Flash**。本固件配置为原生 USB OTG、启用 USB CDC、禁用 PSRAM。UART0 使用 RX GPIO44、TX GPIO43、115200 波特率。

文件 `ESP32S3-TextKeyboard-v1.4-16MB.bin` 是完整合并镜像，大小 16,777,216 字节，烧录起始地址 **0x0**。写入会覆盖板上整个 16MB Flash，包括原有程序和存储数据；请先保存需要保留的内容。不要将此镜像用于其他芯片或不同 Flash 容量的开发板。

v1.4 新增键盘直通报告和 1000 毫秒超时释放，保留原有文本、短语与按键发送协议。使用键盘直通需要升级到 v1.4；只使用原有文本发送功能时，v1.3 仍可继续使用。公开镜像由本仓库源码编译，并将编译路径映射为通用路径。

## 接线

- 开发板 UART / COM 接口连接电脑，用于刷写和 TextSender 控制。
- 原生 USB / OTG 接口连接接收键盘输入的目标设备。
- 确认开发板自身的接口标识及 UART 引脚接线。固件不通过 Wi-Fi 连接目标。

## 刷写

解压固件包，在包含 BIN 的目录打开 PowerShell。关闭占用开发板串口的其他程序。

安装或更新 esptool：

```powershell
py -m pip install --upgrade esptool
```

将下面示例中的 `COM3` 替换为开发板的实际串口号：

```powershell
py -m esptool --chip esp32s3 --port COM3 --baud 921600 write-flash --flash-mode dio --flash-freq 80m --flash-size 16MB 0x0 ESP32S3-TextKeyboard-v1.4-16MB.bin
```

若无法进入下载模式，可按开发板说明操作 BOOT / RESET；若高速写入不稳定，可将波特率改为 115200。

写入成功后重启开发板。重新连接原生 USB / OTG 数据线，在 TextSender 中选择 UART 对应的串口。固件启动本身不会发送按键。

## 编译配置

- Arduino ESP32 core：3.3.8。
- FQBN：`esp32:esp32:esp32s3:USBMode=default,CDCOnBoot=cdc,FlashSize=16M,PSRAM=disabled`。
- Flash：DIO、80MHz、16MB。
- 源码：仓库中的 `firmware/TextKeyboard/TextKeyboard.ino` 与同目录 `RawHidProtocol.h`；固件包内保留 `TextKeyboard` 目录。
- 公开构建在 C/C++ 的 `extra_flags` 中使用 `-ffile-prefix-map`，将本机构建用户目录映射到 `/build`，并使用独立目录执行 clean build。

## 键盘直通协议

`PING` 保留原有 `ESP32S3-TEXTKEY/1` 标识及能力字段，返回 `FW=1.4 HID=1 HID_TIMEOUT=1000` 等信息。

通过 UART0 或 USB CDC 发送以下 ASCII 行命令，末尾加换行：

```text
HID <modifier> <key1> <key2> <key3> <key4> <key5> <key6>
HID OFF
```

`HID` 后必须恰好有 7 个十进制数：修饰键位掩码为 0～255，六个普通键槽为 0（空槽）或 HID Usage 4～223，非零键不可重复。报告保留物理 Shift/Ctrl/Alt/Win 状态，不进行文本发送使用的 Caps Lock 字母补偿。

成功发送完整报告或释放报告后返回 `OK HID`；格式错误返回 `ERR COMMAND`，USB 未就绪或报告发送失败返回 `ERR HID`。`HID OFF` 和全零报告都用于释放全部直通按键。

持键期间，发送端应每 200 毫秒续发当前完整状态；全部松开后不需要续发。最后一次成功的非零报告超过 1000 毫秒没有更新，固件会释放全部直通按键。切换输入通道、USB 断开、CDC 输入关闭或开始原有文本/按键发送时，也会释放直通按键。释放失败时清除内部持键状态并重试全零报告，不重放旧按键。

命令头最多 127 个 ASCII 字节（不含换行）；超长或含无效字符的命令整行拒绝。未收到换行的截断命令不会阻塞超时释放。`TYPE` / `TYPE2` 的文本负载仍使用原有的 8192 字节上限。

已完成构建、镜像检查及实际 sketch 的串口/HID 替身测试；尚未刷写开发板验证键盘直通的目标设备行为。
