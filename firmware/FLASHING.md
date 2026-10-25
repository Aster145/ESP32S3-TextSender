# ESP32-S3 TextKeyboard v1.3 固件

适用于 **ESP32-S3、16MB Flash**。本固件配置为原生 USB OTG、启用 USB CDC、禁用 PSRAM。UART0 使用 RX GPIO44、TX GPIO43、115200 波特率。

文件 `ESP32S3-TextKeyboard-v1.3-16MB.bin` 是完整合并镜像，大小 16,777,216 字节，烧录起始地址 **0x0**。写入会覆盖板上整个 16MB Flash，包括原有程序和存储数据；请先保存需要保留的内容。不要将此镜像用于其他芯片或不同 Flash 容量的开发板。

公开镜像使用仓库中的 v1.3 源码重新编译，将编译路径统一为通用路径，固件功能和通信协议保持不变。它不是从某块开发板读取的 Flash 备份。已有配套 v1.3 固件时，无需为了更新 TextSender 而重刷。

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
py -m esptool --chip esp32s3 --port COM3 --baud 921600 write-flash --flash-mode dio --flash-freq 80m --flash-size 16MB 0x0 ESP32S3-TextKeyboard-v1.3-16MB.bin
```

若无法进入下载模式，可按开发板说明操作 BOOT / RESET；若高速写入不稳定，可将波特率改为 115200。

写入成功后重启开发板。重新连接原生 USB / OTG 数据线，在 TextSender 中选择 UART 对应的串口。固件启动本身不会发送按键。

## 编译配置

- Arduino ESP32 core：3.3.8。
- FQBN：`esp32:esp32:esp32s3:USBMode=default,CDCOnBoot=cdc,FlashSize=16M,PSRAM=disabled`。
- Flash：DIO、80MHz、16MB。
- 源码：仓库中的 `firmware/TextKeyboard/TextKeyboard.ino`；固件包内为 `TextKeyboard/TextKeyboard.ino`。
- 公开构建使用编译器的文件路径映射选项去除本机目录信息，不修改 USB 或串口功能。

已完成构建和镜像检查；本次上传未重新刷写开发板或进行新的目标设备输入测试。
