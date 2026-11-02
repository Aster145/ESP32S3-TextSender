#pragma once
#include "Arduino.h"
#include <array>

static const char *ARDUINO_USB_HID_KEYBOARD_EVENTS = "keyboard";
static const int ARDUINO_USB_HID_KEYBOARD_LED_EVENT = 0;
static const int HID_REPORT_ID_KEYBOARD = 1;
static const uint8_t KEY_LEFT_SHIFT = 0x81, KEY_HOME = 0xD2, KEY_BACKSPACE = 0xB2;
struct arduino_usb_hid_keyboard_event_data_t { uint8_t capslock; };
struct MockReport { uint8_t id; std::array<uint8_t, 8> bytes; };
inline bool mockUsbReady = true;
inline std::deque<bool> mockSendResults;
inline std::vector<MockReport> mockReports;

class USBHID {
public:
  bool ready() { return mockUsbReady; }
  bool SendReport(uint8_t id, const void *data, size_t length, uint32_t) {
    if (length != 8) throw "Expected one full keyboard report";
    MockReport report = {id, {}};
    std::memcpy(report.bytes.data(), data, 8);
    mockReports.push_back(report);
    mockTrace.push_back(TextKeyboardIsEmpty(report.bytes) ? "release" : "raw");
    if (mockSendResults.empty()) return true;
    const bool result = mockSendResults.front(); mockSendResults.pop_front(); return result;
  }
private:
  static bool TextKeyboardIsEmpty(const std::array<uint8_t, 8> &report) {
    return std::all_of(report.begin(), report.end(), [](uint8_t byte) { return byte == 0; });
  }
};

class USBHIDKeyboard {
public:
  std::vector<uint8_t> characters;
  void onEvent(void (*)(void *, esp_event_base_t, int32_t, void *)) {}
  void begin() {}
  size_t press(uint8_t key) { characters.push_back(key); mockTrace.push_back("legacy-key"); return 1; }
  size_t write(uint8_t key) { return press(key); }
  void releaseAll() { mockTrace.push_back("legacy-release"); }
};
