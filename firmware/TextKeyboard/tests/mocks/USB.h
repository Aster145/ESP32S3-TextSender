#pragma once
class MockUsb {
public:
  bool connected = true;
  void productName(const char *) {}
  void begin() {}
  operator bool() const { return connected; }
};
inline MockUsb USB;
