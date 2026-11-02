// Compile the actual sketch against local substitutes. No serial/USB handles.
#include <cstdlib>
#include <iostream>
#include <stdexcept>
#include "../TextKeyboard.ino"

static unsigned checks = 0;
static void check(bool ok, const char *name) {
  ++checks;
  if (!ok) throw std::runtime_error(name);
}
static bool emptyReport(const MockReport &report) {
  return std::all_of(report.bytes.begin(), report.bytes.end(), [](uint8_t value) { return value == 0; });
}
static void reset() {
  Serial = MockSerial(); Serial0 = MockSerial(); USB.connected = true;
  mockNow = 0; mockUsbReady = true; mockSendResults.clear(); mockReports.clear(); mockTrace.clear();
  keyboard.characters.clear(); usbLine = {}; uartLine = {}; channel = &Serial; rawOwner = nullptr;
  rawHeld = false; rawReleasePending = false; rawLastUpdate = 0; rawLastReleaseAttempt = 0;
  targetCapsLock.store(false); targetLedsSeen.store(false);
}
static void drain() {
  unsigned steps = 0;
  while (Serial.available() || Serial0.available()) {
    if (++steps > 10000) throw std::runtime_error("Input loop did not terminate");
    loop();
  }
}
static void command(Stream &source, const std::string &text) { source.feed(text + "\n"); drain(); }
static void hold(Stream &source = Serial0) {
  command(source, "HID 2 4 0 0 0 0 0");
  check(rawHeld, "A successful nonzero report must arm the watchdog");
}

int main() {
  try {
    reset(); setup();
    check(mockReports.empty() && keyboard.characters.empty(), "No startup keystrokes");
    check(Serial0.rx == 44 && Serial0.tx == 43, "Original UART pins retained");
    command(Serial0, "PING");
    check(Serial0.output.find("ESP32S3-TEXTKEY/1 FW=1.4 TYPE2=1 KEY_HOLD=20 ASCII MAX=8192 INPUT=USB+UART0 CAPS_FIX=1") == 0,
          "PING retains legacy fields and updates firmware version");
    check(Serial0.output.find(" HID=1 HID_TIMEOUT=1000\r\n") != std::string::npos, "PING advertises raw HID");

    uint8_t parsed[8];
    check(TextKeyboard::parseHidCommand("HID 255 4 5 6 7 8 223", parsed) == TextKeyboard::HidCommand::Report,
          "Accept modifier and usage bounds");
    check(parsed[0] == 255 && parsed[1] == 0 && parsed[7] == 223, "Modifier/reserved/six-key layout");
    check(TextKeyboard::parseHidCommand("HID\t0 0 0 0 0 0 0\r", parsed) == TextKeyboard::HidCommand::Report,
          "Accept repeated empty slots and ASCII whitespace");
    const char *invalid[] = {
      "HID", "HID 0 4 0 0 0 0", "HID 0 4 0 0 0 0 0 0", "HID -1 4 0 0 0 0 0",
      "HID +1 4 0 0 0 0 0", "HID 256 4 0 0 0 0 0", "HID 0 1 0 0 0 0 0",
      "HID 0 2 0 0 0 0 0", "HID 0 3 0 0 0 0 0", "HID 0 224 0 0 0 0 0",
      "HID 0 255 0 0 0 0 0", "HID 0 0x04 0 0 0 0 0", "HID 0 4 0 4 0 0 0",
      "HID 99999999999999999999 4 0 0 0 0 0", "HID 0 4 0 0 0 0 0junk", "HID OFF extra"
    };
    for (const char *text : invalid) {
      reset(); command(Serial0, text);
      check(Serial0.output == "ERR COMMAND\r\n", text);
      check(mockReports.empty(), "Invalid input must not press a partial report");
    }

    reset(); targetCapsLock.store(true); hold();
    check(mockReports.back().id == HID_REPORT_ID_KEYBOARD, "Uses the existing keyboard report ID");
    check(mockReports.back().bytes == std::array<uint8_t, 8>{2, 0, 4, 0, 0, 0, 0, 0},
          "Physical modifiers/usages are not altered by Caps Lock");
    mockNow = 999; loop(); check(rawHeld && mockReports.size() == 1, "Held before the deadline");
    mockNow = 1000; loop(); check(!rawHeld && emptyReport(mockReports.back()), "Release at 1000ms");

    reset(); hold();
    for (unsigned time = 200; time <= 1000; time += 200) { mockNow = time; hold(); }
    check(mockReports.size() == 6 && std::none_of(mockReports.begin(), mockReports.end(), emptyReport),
          "200ms heartbeats do not insert key-up reports");
    mockNow = 1999; loop(); check(rawHeld, "Deadline follows the last successful refresh");
    mockNow = 2000; loop(); check(!rawHeld, "Refreshed report eventually expires");

    reset(); hold(); command(Serial0, "HID 0 0 0 0 0 0 0");
    const size_t afterZero = mockReports.size(); mockNow = 5000; loop();
    check(!rawHeld && !rawReleasePending && mockReports.size() == afterZero, "Zero report needs no keepalive");
    reset(); hold(); command(Serial0, "HID OFF");
    check(!rawHeld && emptyReport(mockReports.back()) && Serial0.output == "OK HID\r\nOK HID\r\n", "HID OFF releases and acknowledges");
    reset(); hold(); command(Serial0, "HID 0 4 4 0 0 0 0");
    check(!rawHeld && emptyReport(mockReports.back()), "Malformed HID releases an existing hold");

    reset(); mockUsbReady = false; command(Serial0, "HID 2 4 0 0 0 0 0");
    check(Serial0.output == "ERR HID\r\n" && !rawHeld && rawReleasePending, "Not-ready is an error with no retained held state");
    check(mockReports.empty(), "No transfer while USB is not ready");
    mockUsbReady = true; mockNow = 50; loop();
    check(!rawReleasePending && emptyReport(mockReports.back()), "Reconnect retries only a release");

    reset(); mockSendResults = {false, false}; command(Serial0, "HID 2 4 0 0 0 0 0");
    check(Serial0.output == "ERR HID\r\n" && !rawHeld && rawReleasePending, "Failed press and failed release are not acknowledged");
    check(mockReports.size() == 2 && emptyReport(mockReports[1]), "A possibly delivered failed report is followed by release");
    mockNow = 50; loop();
    check(!rawReleasePending && mockReports.size() == 3 && emptyReport(mockReports.back()), "Failed release retry succeeds without replaying keys");

    reset(); hold(); mockSendResults = {false}; mockNow = 1000; loop();
    check(!rawHeld && rawReleasePending && rawOwner == nullptr, "Watchdog release failure clears internal key ownership");
    mockNow = 1050; loop(); check(!rawReleasePending && emptyReport(mockReports.back()), "Watchdog release is retried");
    reset(); mockUsbReady = false; command(Serial0, "HID OFF");
    check(Serial0.output == "ERR HID\r\n" && rawReleasePending, "HID OFF cannot falsely acknowledge an unready release");

    reset(); hold(); USB.connected = false; mockUsbReady = false; loop();
    check(!rawHeld && rawReleasePending, "Target USB disconnect clears held state");
    USB.connected = true; mockUsbReady = true; mockNow = 50; loop();
    check(emptyReport(mockReports.back()) && !rawReleasePending, "Target reconnect receives release, never old keys");
    reset(); hold(Serial); Serial.connected = false; loop();
    check(!rawHeld && emptyReport(mockReports.back()), "CDC input close releases its keys");

    reset(); hold(); command(Serial, "PING");
    check(!rawHeld && emptyReport(mockReports.back()) && !Serial.output.empty(), "Changing UART to CDC releases before handling the command");
    reset(); hold(Serial); command(Serial0, "PING");
    check(!rawHeld && emptyReport(mockReports.back()), "Changing CDC to UART also releases");
    reset(); hold(); mockNow = 900; command(Serial0, "PING"); mockNow = 1000; loop();
    check(!rawHeld, "PING is not a HID keepalive");

    for (const char *header : {"TYPE 0 1\n", "TYPE2 0 1 1 0 0\n"}) {
      reset(); hold(); Serial0.feed(std::string(header) + "x"); drain();
      check(!rawHeld && keyboard.characters == std::vector<uint8_t>{'x'}, "Original TYPE and TYPE2 still send text");
      check(mockTrace.size() > 2 && mockTrace[1] == "release" && mockTrace[2] == "payload", "Raw keys are released before blocking payload reads");
      check(Serial0.output.find("READY\r\nQUEUED\r\nDONE\r\n") != std::string::npos, "Legacy text acknowledgements remain");
    }
    reset(); hold(); command(Serial0, "KEY 176 1");
    check(!rawHeld && keyboard.characters == std::vector<uint8_t>{176}, "Original KEY action remains after raw release");
    reset(); hold(); mockUsbReady = false; Serial0.feed("x"); processCommand("TYPE2 0 1 1 0 0");
    check(rawReleasePending && Serial0.payloadReads == 0, "Release failure prevents a blocking TYPE payload read");

    reset(); hold(); Serial0.feed("HID 2 5"); drain();
    check(mockNow == 0 && rawHeld, "Truncated lines do not block or replace the prior report");
    mockNow = 1000; loop(); check(!rawHeld, "Watchdog runs with a truncated line pending");
    command(Serial0, ""); check(Serial0.output.find("ERR COMMAND") != std::string::npos, "Truncated report is rejected at newline");
    reset(); hold(); Serial0.feed("HID 0 5 0 0 0 0 0" + std::string(200, ' ')); drain();
    check(mockNow == 0 && mockReports.size() == 1, "Oversized unterminated lines are bounded and never typed");
    mockNow = 1000; loop(); command(Serial0, ""); command(Serial0, "PING");
    check(!rawHeld && Serial0.output.find("ERR COMMAND") != std::string::npos && Serial0.output.find("FW=1.4") != std::string::npos,
          "Overflow releases on time and recovers at the next line");
    reset(); command(Serial0, "PING" + std::string(123, ' '));
    check(Serial0.output.find("FW=1.4") != std::string::npos, "127-character command limit is inclusive");
    reset(); command(Serial0, "PING" + std::string(124, ' '));
    check(Serial0.output == "ERR COMMAND\r\n", "128-character command cannot be silently truncated");
    reset(); command(Serial0, std::string("HID 0 4 0 0 0 0 0\0junk", 22));
    check(Serial0.output == "ERR COMMAND\r\n" && mockReports.empty(), "Embedded NUL cannot hide command suffixes");
    reset(); hold(); Serial0.feed(std::string(1, '\x18')); drain();
    check(!rawHeld && emptyReport(mockReports.back()) && Serial0.output.find("CANCELLED") != std::string::npos, "CAN releases passthrough immediately");
    reset(); mockNow = UINT32_MAX - 500; hold(); mockNow += 1000; loop();
    check(!rawHeld, "Watchdog survives millis rollover");

    std::cout << "PASS " << checks << " checks; mocked UART/CDC/HID only; no hardware opened.\n";
    return 0;
  } catch (const std::exception &error) {
    std::cerr << "FAIL: " << error.what() << "\n";
    return 1;
  }
}
