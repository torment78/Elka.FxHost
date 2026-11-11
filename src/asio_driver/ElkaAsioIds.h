#pragma once

#include <guiddef.h>

namespace elka::asio
{
// {8F3C2A71-5B4E-4D2A-9C61-3E7A1F0B6D42}
inline constexpr GUID CLSID_ElkaFxHostAsio =
    { 0x8f3c2a71, 0x5b4e, 0x4d2a, { 0x9c, 0x61, 0x3e, 0x7a, 0x1f, 0x0b, 0x6d, 0x42 } };

// Name under HKLM\SOFTWARE\ASIO, which is what Matrix and DAWs list.
inline constexpr wchar_t RegistryName[] = L"Elka FX Host";
}
