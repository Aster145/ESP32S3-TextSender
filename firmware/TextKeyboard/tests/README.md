# 固件离线检查

`raw_hid_test.cpp` 直接包含实际 `TextKeyboard.ino`，使用 `mocks` 目录替代串口、时钟和 USB HID。检查命令解析、物理报告、心跳与超时、断开/失败后的释放重试、截断和超长命令，以及原有 TYPE / TYPE2 / KEY 路径，不打开真实设备。

在 Visual Studio 的 x64 Native Tools 命令提示符中，进入 `firmware\TextKeyboard` 后运行：

```bat
cl /nologo /std:c++17 /EHsc /W4 /D_CRT_SECURE_NO_WARNINGS /Itests\mocks tests\raw_hid_test.cpp /Fe:"%TEMP%\TextKeyboardRawHidTest.exe" /Fo:"%TEMP%\TextKeyboardRawHidTest.obj"
"%TEMP%\TextKeyboardRawHidTest.exe"
```

测试替身不证明目标 USB 主机的枚举、键位识别或实时输入表现。实际固件另使用 Arduino ESP32 core 3.3.8 编译。
