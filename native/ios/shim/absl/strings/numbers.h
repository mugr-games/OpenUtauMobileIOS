// Minimal stand-in for absl/strings/numbers.h.
//
// worldline only uses absl::SimpleAtoi and absl::SimpleAtod (in
// classic/classic_args.cpp). Cross-compiling all of abseil for iOS just for
// those two functions is not worth it, so this header reproduces their
// contract: leading/trailing ASCII whitespace is ignored, the whole remaining
// input must parse, and false is returned on failure or integer overflow.
#ifndef OPENUTAU_IOS_ABSL_SHIM_NUMBERS_H_
#define OPENUTAU_IOS_ABSL_SHIM_NUMBERS_H_

#include <cctype>
#include <cerrno>
#include <cstdlib>
#include <limits>
#include <string>
#include <string_view>
#include <type_traits>

namespace absl {

namespace numbers_internal {
inline std::string_view StripAsciiWhitespace(std::string_view s) {
  while (!s.empty() && std::isspace(static_cast<unsigned char>(s.front()))) {
    s.remove_prefix(1);
  }
  while (!s.empty() && std::isspace(static_cast<unsigned char>(s.back()))) {
    s.remove_suffix(1);
  }
  return s;
}
}  // namespace numbers_internal

template <typename int_type>
inline bool SimpleAtoi(std::string_view str, int_type* out) {
  static_assert(std::is_integral<int_type>::value, "integer type required");
  std::string s(numbers_internal::StripAsciiWhitespace(str));
  if (s.empty()) return false;
  errno = 0;
  char* end = nullptr;
  if (std::is_signed<int_type>::value) {
    long long v = std::strtoll(s.c_str(), &end, 10);
    if (errno == ERANGE || end != s.c_str() + s.size()) return false;
    if (v < static_cast<long long>(std::numeric_limits<int_type>::min()) ||
        v > static_cast<long long>(std::numeric_limits<int_type>::max())) {
      return false;
    }
    *out = static_cast<int_type>(v);
  } else {
    if (s[0] == '-') return false;
    unsigned long long v = std::strtoull(s.c_str(), &end, 10);
    if (errno == ERANGE || end != s.c_str() + s.size()) return false;
    if (v > static_cast<unsigned long long>(
                std::numeric_limits<int_type>::max())) {
      return false;
    }
    *out = static_cast<int_type>(v);
  }
  return true;
}

inline bool SimpleAtod(std::string_view str, double* out) {
  std::string s(numbers_internal::StripAsciiWhitespace(str));
  if (s.empty()) return false;
  char* end = nullptr;
  double v = std::strtod(s.c_str(), &end);
  if (end != s.c_str() + s.size()) return false;
  *out = v;
  return true;
}

inline bool SimpleAtof(std::string_view str, float* out) {
  double d;
  if (!SimpleAtod(str, &d)) return false;
  *out = static_cast<float>(d);
  return true;
}

}  // namespace absl

#endif  // OPENUTAU_IOS_ABSL_SHIM_NUMBERS_H_
