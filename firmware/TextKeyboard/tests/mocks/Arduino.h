#pragma once
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <string>
#include <vector>

inline uint32_t mockNow = 0;
inline std::vector<std::string> mockTrace;
inline uint32_t millis() { return mockNow; }
inline void delay(uint32_t ms) { mockNow += ms; }
template<class T> T max(T a, T b) { return a > b ? a : b; }
static const int SERIAL_8N1 = 0;
using esp_event_base_t = const char *;

class String {
  std::string value;
public:
  explicit String(const char *text) : value(text) {}
  void trim() {
    const size_t first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) { value.clear(); return; }
    value = value.substr(first, value.find_last_not_of(" \t\r\n") - first + 1);
  }
  bool operator==(const char *other) const { return value == other; }
  bool startsWith(const char *prefix) const { return value.rfind(prefix, 0) == 0; }
  const char *c_str() const { return value.c_str(); }
};

class Stream {
public:
  std::deque<uint8_t> input;
  std::string output;
  bool connected = true;
  unsigned timeout = 5000;
  unsigned payloadReads = 0;
  int available() const { return static_cast<int>(input.size()); }
  int read() {
    if (input.empty()) return -1;
    const int result = input.front(); input.pop_front(); return result;
  }
  size_t readBytes(uint8_t *buffer, size_t count) {
    mockTrace.push_back("payload"); ++payloadReads;
    size_t length = 0;
    while (length < count && available()) buffer[length++] = static_cast<uint8_t>(read());
    if (length != count) mockNow += timeout;
    return length;
  }
  void setTimeout(unsigned ms) { timeout = ms; }
  void print(const char *text) { output += text; }
  void print(int number) { output += std::to_string(number); }
  void println(const char *text) { print(text); output += "\r\n"; }
  void println(int number) { print(number); output += "\r\n"; }
  operator bool() const { return connected; }
  void feed(const std::string &bytes) { for (unsigned char byte : bytes) input.push_back(byte); }
};

class MockSerial : public Stream {
public:
  int rx = -1, tx = -1;
  void setRxBufferSize(size_t) {}
  void begin(unsigned, int = 0, int rxPin = -1, int txPin = -1) { rx = rxPin; tx = txPin; }
};
inline MockSerial Serial, Serial0;
