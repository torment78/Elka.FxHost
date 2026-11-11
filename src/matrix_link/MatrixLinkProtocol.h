#pragma once

// Shared-memory contract between the Elka FX Host virtual ASIO driver (loaded inside
// VB-Audio Matrix or any other ASIO host) and the FX Host application.
//
// Zero-filled memory is a valid "nobody attached" state, so either side may create the
// mapping first. Both sides only ever store plain values and sequence numbers; no
// pointers cross the process boundary.

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <string>

#ifdef _MSC_VER
#pragma warning(push)
#pragma warning(disable : 4324) // padding from alignas is intentional
#endif

namespace elka::matrixlink
{
constexpr uint32_t Magic = 0x4B4C4D45u; // "EMLK"
constexpr uint32_t Version = 1;

constexpr int ChannelStep = 8;           // one bank = 8 channels (A1-A8, B1-B8, ...)
constexpr int MinChannels = 8;
constexpr int MaxChannels = 128;         // banks A-P. Raise here to grow toward 512.
constexpr int DefaultChannels = 64;      // Matrix Coconut
constexpr int MaxBlockSize = 2048;
constexpr int RingSlots = 4;
constexpr int HeartbeatTimeoutMs = 500;

constexpr wchar_t SharedMemoryName[] = L"Local\\ElkaFxHost.MatrixLink.v1";
constexpr wchar_t RequestEventName[] = L"Local\\ElkaFxHost.MatrixLink.v1.Request";

// Per-user settings shared by the driver, the app and the installer.
constexpr wchar_t SettingsKey[] = L"Software\\ElkaSoft\\FxHost\\Asio";
constexpr wchar_t ChannelCountValue[] = L"ChannelCount";

constexpr char DriverName[] = "Elka FX Host";

static_assert(std::atomic<int64_t>::is_always_lock_free);
static_assert(std::atomic<int32_t>::is_always_lock_free);

struct alignas(64) SlotHeader
{
    std::atomic<int64_t> sequence;   // block sequence stored in this slot, published last
    std::atomic<int32_t> frames;
    std::atomic<int32_t> channels;
};

struct alignas(64) DriverState
{
    std::atomic<uint32_t> pid;
    std::atomic<int32_t> streaming;       // 1 between ASIO start() and stop()
    std::atomic<int32_t> sampleRate;
    std::atomic<int32_t> blockSize;
    std::atomic<int32_t> channels;
    std::atomic<int64_t> requestSequence; // last block handed to the FX Host
    std::atomic<uint64_t> heartbeatMs;    // GetTickCount64 of the last tick
    std::atomic<uint64_t> missedResponses;// blocks Matrix got silence because FX was late/absent
    std::atomic<uint64_t> deliveredResponses;
};

struct alignas(64) HostState
{
    std::atomic<uint32_t> pid;
    std::atomic<int32_t> running;            // FX Host link thread is serving requests
    std::atomic<int64_t> responseSequence;   // last block written back
    std::atomic<uint64_t> heartbeatMs;
    std::atomic<uint64_t> skippedRequests;   // requests dropped because the host fell behind
    std::atomic<int32_t> requestedChannels;  // 0 = use registry/default
    std::atomic<int32_t> resetGeneration;    // bump to make the driver ask its host to reset
};

struct alignas(4096) LinkHeader
{
    uint32_t magic;
    uint32_t version;
    uint32_t maxChannels;
    uint32_t maxBlockSize;
    DriverState driver;
    HostState host;
    SlotHeader toFx[RingSlots];   // driver -> FX Host (what Matrix sends to "FX In")
    SlotHeader fromFx[RingSlots]; // FX Host -> driver (what Matrix receives from "FX Out")
};

constexpr size_t SlotFloats = static_cast<size_t>(MaxChannels) * MaxBlockSize;
constexpr size_t AudioBytesPerDirection = SlotFloats * RingSlots * sizeof(float);
constexpr size_t TotalBytes = sizeof(LinkHeader) + AudioBytesPerDirection * 2;

inline float* toFxSlot(LinkHeader* header, int64_t sequence) noexcept
{
    auto* base = reinterpret_cast<float*>(reinterpret_cast<std::byte*>(header) + sizeof(LinkHeader));
    return base + static_cast<size_t>(sequence % RingSlots) * SlotFloats;
}

inline float* fromFxSlot(LinkHeader* header, int64_t sequence) noexcept
{
    auto* base = reinterpret_cast<float*>(reinterpret_cast<std::byte*>(header) + sizeof(LinkHeader));
    return base + SlotFloats * RingSlots + static_cast<size_t>(sequence % RingSlots) * SlotFloats;
}

inline float* channelData(float* slot, int channel) noexcept
{
    return slot + static_cast<size_t>(channel) * MaxBlockSize;
}

inline int sanitizeChannelCount(int requested) noexcept
{
    if (requested <= 0)
        return DefaultChannels;
    const int rounded = ((requested + ChannelStep - 1) / ChannelStep) * ChannelStep;
    return rounded < MinChannels ? MinChannels : (rounded > MaxChannels ? MaxChannels : rounded);
}

// Bank letters: A..Z, then AA, AB, ... so the scheme keeps working past 208 channels.
inline std::string bankLetters(int bank)
{
    std::string letters;
    int value = bank;
    do
    {
        letters.insert(letters.begin(), static_cast<char>('A' + value % 26));
        value = value / 26 - 1;
    } while (value >= 0);
    return letters;
}

// "FX In A1" is what Matrix sends into the FX Host (ASIO output channel).
// "FX Out A1" is what the FX Host sends back to Matrix (ASIO input channel).
inline std::string channelName(int channel, bool fxIn)
{
    const int bank = channel / ChannelStep;
    const int index = channel % ChannelStep + 1;
    return std::string(fxIn ? "FX In " : "FX Out ") + bankLetters(bank) + std::to_string(index);
}

inline bool isAlive(uint64_t heartbeatMs, uint64_t nowMs) noexcept
{
    return heartbeatMs != 0 && nowMs >= heartbeatMs && nowMs - heartbeatMs <= HeartbeatTimeoutMs;
}
}

#ifdef _MSC_VER
#pragma warning(pop)
#endif
