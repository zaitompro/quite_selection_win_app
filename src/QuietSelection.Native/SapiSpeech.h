#pragma once
#include <windows.h>
#include <cstdint>
#include <string>

namespace quiet_selection {
std::wstring sapi_voices();
void sapi_synthesize(wchar_t const* id, wchar_t const* text, int speed, uint8_t** data, uint32_t* length);
}
