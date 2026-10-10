#pragma once
// Only the keys the interpreter reads. Defaults from Source/Core/Core/Config/MainSettings.cpp at the
// pinned commit: MAIN_LOW_DCBZ_HACK{{System::Main, "Core", "LowDCBZHack"}, false}.
namespace Config {
template <typename T> struct Info { T default_value; };
inline const Info<bool> MAIN_LOW_DCBZ_HACK{false};
template <typename T> T Get(const Info<T>& info) { return info.default_value; }
}
