#pragma once
namespace fmt { struct format_parse_context {}; template <class T, class C = char, class = void> struct formatter { constexpr auto parse(format_parse_context& c){return c;} template<class F> auto format(const T&, F& c) const {return c;} }; }
