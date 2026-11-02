#include <Arduino.h>
#include <atomic>
#include "USB.h"
#include "USBHIDKeyboard.h"
#include "RawHidProtocol.h"

// Native USB: CDC commands + HID keyboard. UART0: alternative command input.
// Build with USBMode=0, CDCOnBoot=cdc. No keystrokes are sent on boot.
USBHIDKeyboard keyboard;
// USBHID instances share the existing interface and transmit state in core 3.3.8.
// Its checked SendReport API is needed: USBHIDKeyboard::sendReport returns void.
USBHID rawHid;
static uint8_t textBuffer[8192];
static Stream *channel = &Serial;
static Stream *rawOwner = nullptr;
static TextKeyboard::CommandLine usbLine, uartLine;
static const uint32_t rawTimeoutMs = 1000;
static const uint32_t rawRetryMs = 50;
static bool rawHeld = false;
static bool rawReleasePending = false;
static uint32_t rawLastUpdate = 0;
static uint32_t rawLastReleaseAttempt = 0;
static std::atomic<bool> targetCapsLock{false};
static std::atomic<bool> targetLedsSeen{false};

static void keyboardEvent(void *, esp_event_base_t base, int32_t id, void *data) {
  if (base == ARDUINO_USB_HID_KEYBOARD_EVENTS && id == ARDUINO_USB_HID_KEYBOARD_LED_EVENT) {
    const auto *event = static_cast<arduino_usb_hid_keyboard_event_data_t *>(data);
    targetCapsLock.store(event->capslock, std::memory_order_relaxed);
    targetLedsSeen.store(true, std::memory_order_relaxed);
  }
}

static void reply(const char *message) {
  channel->println(message);
}

static bool sendRawReport(const uint8_t report[8]) {
  return rawHid.ready() && rawHid.SendReport(HID_REPORT_ID_KEYBOARD, report, 8, 50);
}

static bool releaseRawKeys(bool force) {
  if (!force && !rawHeld && !rawReleasePending) return true;
  // Never retain a pressed report after a failure/disconnect. Only retry release.
  rawHeld = false;
  rawOwner = nullptr;
  const uint8_t empty[8] = {};
  const bool sent = sendRawReport(empty);
  rawReleasePending = !sent;
  rawLastReleaseAttempt = millis();
  return sent;
}

static void serviceRawWatchdog() {
  const uint32_t now = millis();
  if (rawHeld && (static_cast<uint32_t>(now - rawLastUpdate) >= rawTimeoutMs ||
                  !USB || (rawOwner == &Serial && !Serial))) {
    releaseRawKeys(false);
  } else if (rawReleasePending && rawHid.ready() &&
             static_cast<uint32_t>(now - rawLastReleaseAttempt) >= rawRetryMs) {
    releaseRawKeys(false);
  }
}

static bool handleRawCommand(const char *command) {
  uint8_t report[8];
  const TextKeyboard::HidCommand kind = TextKeyboard::parseHidCommand(command, report);
  if (kind == TextKeyboard::HidCommand::NotHid) return false;
  if (kind == TextKeyboard::HidCommand::Invalid) {
    releaseRawKeys(false);
    reply("ERR COMMAND");
  } else if (kind == TextKeyboard::HidCommand::Off) {
    reply(releaseRawKeys(true) ? "OK HID" : "ERR HID");
  } else {
    // A timed-out transfer may still have reached the host. Clear it before
    // accepting another nonzero report rather than replaying stale key state.
    if (rawReleasePending && !releaseRawKeys(false)) {
      reply("ERR HID");
      return true;
    }
    if (!sendRawReport(report)) {
      releaseRawKeys(true);
      reply("ERR HID");
      return true;
    }
    rawHeld = TextKeyboard::reportHasKeys(report);
    rawOwner = rawHeld ? channel : nullptr;
    rawLastUpdate = millis();
    rawReleasePending = false;
    reply("OK HID");
  }
  return true;
}

static bool cancelled() {
  while (channel->available()) {
    if (channel->read() == 0x18) return true;
  }
  return channel == &Serial && !Serial;
}

static bool waitOrCancel(uint32_t ms) {
  const uint32_t start = millis();
  while (millis() - start < ms) {
    if (cancelled()) return false;
    delay(1);
  }
  return true;
}

// Keep key-down visible for at least one 60 Hz target update. write() emits
// press and release back-to-back, which can disappear between target polls.
static bool tap(uint8_t key) {
  if (!keyboard.press(key)) { keyboard.releaseAll(); return false; }
  delay(20);
  keyboard.releaseAll();
  return true;
}

// Select only the newly created indentation to the left of the caret.
// Replacing with a space before Backspace is essential: when indentation is
// empty, a bare Backspace would join this line to the preceding line.
static bool clearAutoIndent(uint32_t gap) {
  if (!keyboard.press(KEY_LEFT_SHIFT) || !keyboard.press(KEY_HOME)) {
    keyboard.releaseAll();
    return false;
  }
  delay(30);
  keyboard.releaseAll();
  delay(gap);
  if (!tap(' ')) return false;
  delay(gap);
  if (!tap(KEY_BACKSPACE)) return false;
  return true;
}

void setup() {
  Serial.setRxBufferSize(16384);
  Serial.begin(115200);
  Serial.setTimeout(5000);
  Serial0.setRxBufferSize(16384);
  Serial0.begin(115200, SERIAL_8N1, 44, 43);
  Serial0.setTimeout(5000);
  USB.productName("ESP32-S3 Text Keyboard");
  keyboard.onEvent(keyboardEvent);
  keyboard.begin();
  USB.begin();
}

static void processCommand(const char *line) {
  String command(line);
  command.trim();
  if (command == "PING") {
    channel->print("ESP32S3-TEXTKEY/1 FW=1.4 TYPE2=1 KEY_HOLD=20 ASCII MAX=8192 INPUT=USB+UART0 CAPS_FIX=1 CAPS=");
    channel->print(targetCapsLock.load(std::memory_order_relaxed) ? 1 : 0);
    channel->print(" LED_SEEN=");
    channel->print(targetLedsSeen.load(std::memory_order_relaxed) ? 1 : 0);
    channel->println(" HID=1 HID_TIMEOUT=1000");
    return;
  }
  if (handleRawCommand(command.c_str())) return;
  // Legacy text/key actions may wait for payloads. Release passthrough first,
  // and do not begin a blocking operation while a release is still pending.
  if (!releaseRawKeys(false)) { reply("ERR HID"); return; }
  unsigned int key = 0, repeat = 0;
  char trailing = 0;
  if (sscanf(command.c_str(), "KEY %u %u %c", &key, &repeat, &trailing) == 2 &&
      key >= 0x88 && key <= 0xFF && repeat > 0 && repeat <= 100) {
    for (unsigned int i = 0; i < repeat; i++) {
      if (!tap(static_cast<uint8_t>(key))) { reply("ERR HID"); return; }
      delay(30);
    }
    keyboard.releaseAll();
    reply("DONE");
    return;
  }
  unsigned int delayMs = 0, count = 0;
  unsigned int keyGap = 12, lineGap = 0, noIndent = 0;
  char extra = 0;
  bool valid;
  const bool extended = command.startsWith("TYPE2 ");
  if (extended) {
    valid = sscanf(command.c_str(), "TYPE2 %u %u %u %u %u %c",
                   &delayMs, &count, &keyGap, &lineGap, &noIndent, &extra) == 5;
  } else {
    valid = sscanf(command.c_str(), "TYPE %u %u %c", &delayMs, &count, &extra) == 2;
  }
  if (!valid || delayMs > 10000 || count == 0 || count > sizeof(textBuffer) ||
      keyGap < 1 || keyGap > 1000 || lineGap > 3000 || noIndent > 1) {
    reply("ERR COMMAND");
    return;
  }
  reply("READY");
  const size_t received = channel->readBytes(textBuffer, count);
  if (received != count) { reply("ERR TIMEOUT"); return; }
  for (size_t i = 0; i < count; i++) {
    const uint8_t c = textBuffer[i];
    if (!((c >= 32 && c <= 126) || c == '\n' || c == '\r' || c == '\t')) {
      reply("ERR ASCII_ONLY");
      return;
    }
  }
  reply("QUEUED");
  if (!waitOrCancel(delayMs)) { reply("CANCELLED"); return; }
  for (size_t i = 0; i < count; i++) {
    if (cancelled()) { keyboard.releaseAll(); reply("CANCELLED"); return; }
    // Treat CRLF and lone CR as a single Enter key.
    uint8_t c = textBuffer[i];
    if (c == '\r') {
      if (i + 1 < count && textBuffer[i + 1] == '\n') i++;
      c = '\n';
    }
    // The US layout maps uppercase to Shift+letter. Caps Lock inverts that
    // on the receiving host, so invert only the alphabetic mapping here.
    // Leave the host's Caps Lock state and digit/symbol mappings unchanged.
    if (targetCapsLock.load(std::memory_order_relaxed)) {
      if (c >= 'a' && c <= 'z') c -= 'a' - 'A';
      else if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    }
    if (!(extended ? tap(c) : keyboard.write(c) != 0)) { keyboard.releaseAll(); reply("ERR HID"); return; }
    uint32_t pause = c == '\n' ? max(keyGap, lineGap) : keyGap;
    if (c == '\n' && noIndent && pause < 100) pause = 100;
    if (!waitOrCancel(pause)) { keyboard.releaseAll(); reply("CANCELLED"); return; }
    if (c == '\n' && noIndent) {
      // This short edit sequence is atomic with respect to cancel, so Stop
      // cannot leave Shift held or the temporary replacement space behind.
      if (!clearAutoIndent(30)) { keyboard.releaseAll(); reply("ERR HID"); return; }
      if (!waitOrCancel(max(keyGap, 30U))) { keyboard.releaseAll(); reply("CANCELLED"); return; }
    }
  }
  keyboard.releaseAll();
  reply("DONE");
}

void loop() {
  serviceRawWatchdog();
  Stream *next = nullptr;
  if (Serial0.available()) next = &Serial0;
  else if (Serial.available()) next = &Serial;
  else { delay(1); return; }
  if (next != channel) {
    releaseRawKeys(false);
    channel = next;
  }
  TextKeyboard::CommandLine &line = channel == &Serial0 ? uartLine : usbLine;
  // Bounded work keeps watchdog servicing independent of input traffic.
  for (size_t budget = 0; budget < 64 && channel->available(); ++budget) {
    const int byte = channel->read();
    if (byte < 0) break;
    const TextKeyboard::CommandLine::Result result = line.push(static_cast<uint8_t>(byte));
    if (result == TextKeyboard::CommandLine::Result::Complete) {
      processCommand(line.text);
      return;
    }
    if (result == TextKeyboard::CommandLine::Result::Rejected ||
        result == TextKeyboard::CommandLine::Result::Cancelled) {
      releaseRawKeys(false);
      reply(result == TextKeyboard::CommandLine::Result::Cancelled ? "CANCELLED" : "ERR COMMAND");
      return;
    }
  }
}
