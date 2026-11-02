#pragma once

#include <stddef.h>
#include <stdint.h>
#include <string.h>

namespace TextKeyboard {

enum class HidCommand { NotHid, Invalid, Off, Report };

inline bool commandSpace(char c) {
  return c == ' ' || c == '\t' || c == '\r';
}

// Commands use decimal HID usages, not Arduino's ASCII/KEY_* values.
inline HidCommand parseHidCommand(const char *command, uint8_t report[8]) {
  memset(report, 0, 8);
  if (strncmp(command, "HID", 3) != 0 ||
      (command[3] != 0 && !commandSpace(command[3]))) return HidCommand::NotHid;
  const char *p = command + 3;
  while (commandSpace(*p)) ++p;
  if (strncmp(p, "OFF", 3) == 0) {
    p += 3;
    while (commandSpace(*p)) ++p;
    return *p == 0 ? HidCommand::Off : HidCommand::Invalid;
  }
  for (size_t index = 0; index < 7; ++index) {
    if (*p < '0' || *p > '9') return HidCommand::Invalid;
    unsigned value = 0;
    do {
      value = value * 10 + static_cast<unsigned>(*p++ - '0');
      if (value > 255) return HidCommand::Invalid;
    } while (*p >= '0' && *p <= '9');
    if (index != 0) {
      if (value != 0 && (value < 4 || value > 0xDF)) return HidCommand::Invalid;
      for (size_t earlier = 2; value != 0 && earlier <= index; ++earlier)
        if (report[earlier] == value) return HidCommand::Invalid;
    }
    report[index == 0 ? 0 : index + 1] = static_cast<uint8_t>(value);
    if (index < 6 && !commandSpace(*p)) return HidCommand::Invalid;
    while (commandSpace(*p)) ++p;
  }
  return *p == 0 ? HidCommand::Report : HidCommand::Invalid;
}

inline bool reportHasKeys(const uint8_t report[8]) {
  if (report[0]) return true;
  for (size_t i = 2; i < 8; ++i) if (report[i]) return true;
  return false;
}

// Read at most one bounded line at a time, without Stream::readStringUntil.
// A malformed/oversized line is discarded through its newline, never truncated
// into a valid HID report. Each input channel owns its own instance.
struct CommandLine {
  enum class Result { Incomplete, Complete, Rejected, Cancelled };
  static const size_t Capacity = 128;
  char text[Capacity] = {};
  size_t length = 0;
  bool rejected = false;

  Result push(uint8_t byte) {
    if (byte == 0x18) {
      length = 0;
      rejected = false;
      return Result::Cancelled;
    }
    if (byte == '\n') {
      text[length] = 0;
      const Result result = rejected ? Result::Rejected : Result::Complete;
      length = 0;
      rejected = false;
      return result;
    }
    if (rejected) return Result::Incomplete;
    if ((byte < 0x20 && byte != '\t' && byte != '\r') || byte > 0x7E ||
        length == Capacity - 1) {
      rejected = true;
      return Result::Incomplete;
    }
    text[length++] = static_cast<char>(byte);
    return Result::Incomplete;
  }
};

} // namespace TextKeyboard
