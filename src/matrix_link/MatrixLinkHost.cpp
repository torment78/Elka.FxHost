#include "matrix_link/MatrixLinkHost.h"

#include "engine/AudioThreadPriority.h"
#include "engine/AudioBufferView.h"
#include "engine/RealtimeEngine.h"
#include "matrix_link/MatrixLinkProtocol.h"

#include <windows.h>

#include <algorithm>
#include <array>
#include <sstream>

namespace elka
{
namespace ml = matrixlink;

namespace
{
constexpr wchar_t AsioRegistryKey[] = L"SOFTWARE\\ASIO\\Elka FX Host";
}

MatrixLinkHost::MatrixLinkHost(RealtimeEngine& engine)
    : realtimeEngine(engine)
{
}

MatrixLinkHost::~MatrixLinkHost()
{
    std::wstring ignored;
    stop(ignored);
    std::lock_guard lock(mutex);
    closeMapping();
}

int MatrixLinkHost::configuredChannelCount() noexcept
{
    DWORD value = 0;
    DWORD size = sizeof(value);
    if (RegGetValueW(HKEY_CURRENT_USER, ml::SettingsKey, ml::ChannelCountValue, RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS)
        return ml::sanitizeChannelCount(static_cast<int>(value));
    size = sizeof(value);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, ml::SettingsKey, ml::ChannelCountValue, RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS)
        return ml::sanitizeChannelCount(static_cast<int>(value));
    return ml::DefaultChannels;
}

bool MatrixLinkHost::setConfiguredChannelCount(int requested, std::wstring& status)
{
    const int value = ml::sanitizeChannelCount(requested);
    HKEY key = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, ml::SettingsKey, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr) != ERROR_SUCCESS)
    {
        status = L"Could not save the ASIO channel count.";
        return false;
    }
    const DWORD data = static_cast<DWORD>(value);
    const bool ok = RegSetValueExW(key, ml::ChannelCountValue, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&data), sizeof(data)) == ERROR_SUCCESS;
    RegCloseKey(key);
    if (!ok)
    {
        status = L"Could not save the ASIO channel count.";
        return false;
    }

    std::lock_guard lock(mutex);
    std::wstring ignored;
    if (openMapping(ignored))
    {
        header->host.requestedChannels.store(value, std::memory_order_release);
        header->host.resetGeneration.fetch_add(1, std::memory_order_acq_rel);
    }
    status = L"ASIO channel count set to " + std::to_wstring(value) +
        L". Matrix will reset the driver; if it does not, restart the ASIO device in Matrix.";
    return true;
}

// Writes one driver setting (HKCU) and asks the streaming driver to re-read its settings.
bool MatrixLinkHost::writeSettingsAndSignal(const wchar_t* name, unsigned long type, const void* data, unsigned long bytes)
{
    HKEY key = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, ml::SettingsKey, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr) != ERROR_SUCCESS)
        return false;
    const bool ok = RegSetValueExW(key, name, 0, type, static_cast<const BYTE*>(data), bytes) == ERROR_SUCCESS;
    RegCloseKey(key);
    if (!ok)
        return false;

    std::lock_guard lock(mutex);
    std::wstring ignored;
    if (openMapping(ignored))
        header->host.resetGeneration.fetch_add(1, std::memory_order_acq_rel);
    return true;
}

int MatrixLinkHost::configuredBufferSize() noexcept
{
    for (HKEY hive : { HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE })
    {
        DWORD value = 0;
        DWORD size = sizeof(value);
        if (RegGetValueW(hive, ml::SettingsKey, ml::BufferSizeValue, RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS)
            return ml::sanitizeBufferSize(static_cast<int>(value));
    }
    return ml::DefaultBufferSize;
}

bool MatrixLinkHost::setConfiguredBufferSize(int samples, std::wstring& status)
{
    const DWORD value = static_cast<DWORD>(ml::sanitizeBufferSize(samples));
    if (!writeSettingsAndSignal(ml::BufferSizeValue, REG_DWORD, &value, sizeof(value)))
    {
        status = L"Could not save the ASIO buffer size.";
        return false;
    }
    status = L"ASIO buffer size set to " + std::to_wstring(value) +
        L" samples. Matrix resets the device to use it; if it does not, restart the device in Matrix.";
    return true;
}

int MatrixLinkHost::configuredClockSource(std::wstring& deviceId)
{
    deviceId.clear();
    int mode = 0;
    for (HKEY hive : { HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE })
    {
        DWORD value = 0;
        DWORD size = sizeof(value);
        if (RegGetValueW(hive, ml::SettingsKey, ml::ClockSourceValue, RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS)
        {
            mode = value == 1 ? 1 : 0;
            wchar_t id[512] {};
            DWORD idSize = sizeof(id);
            if (RegGetValueW(hive, ml::SettingsKey, ml::ClockDeviceIdValue, RRF_RT_REG_SZ, nullptr, id, &idSize) == ERROR_SUCCESS)
                deviceId = id;
            break;
        }
    }
    return mode;
}

bool MatrixLinkHost::setConfiguredClockSource(int mode, const std::wstring& deviceId, std::wstring& status)
{
    // Device id first: the driver reads both when it sees the change signal.
    const DWORD idBytes = static_cast<DWORD>((deviceId.size() + 1) * sizeof(wchar_t));
    const DWORD value = mode == 1 ? 1u : 0u;
    HKEY key = nullptr;
    bool ok = RegCreateKeyExW(HKEY_CURRENT_USER, ml::SettingsKey, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr) == ERROR_SUCCESS;
    if (ok)
    {
        ok = RegSetValueExW(key, ml::ClockDeviceIdValue, 0, REG_SZ, reinterpret_cast<const BYTE*>(deviceId.c_str()), idBytes) == ERROR_SUCCESS;
        RegCloseKey(key);
    }
    if (!ok || !writeSettingsAndSignal(ml::ClockSourceValue, REG_DWORD, &value, sizeof(value)))
    {
        status = L"Could not save the ASIO clock setting.";
        return false;
    }
    status = mode == 1 ? L"ASIO clock: following the selected device's clock." : L"ASIO clock: internal (PC timer).";
    return true;
}

MatrixLinkHost::ClockStatus MatrixLinkHost::clockStatus() const noexcept
{
    ClockStatus result;
    std::lock_guard lock(mutex);
    if (header == nullptr)
        return result;
    result.driverPresent = header->driver.streaming.load(std::memory_order_acquire) != 0 &&
        ml::isAlive(header->driver.heartbeatMs.load(std::memory_order_acquire), GetTickCount64());
    result.mode = header->clock.mode.load(std::memory_order_relaxed);
    result.followState = header->clock.followState.load(std::memory_order_relaxed);
    result.partsPerBillion = header->clock.partsPerBillion.load(std::memory_order_relaxed);
    result.preferredBufferSize = header->clock.preferredBufferSize.load(std::memory_order_relaxed);
    result.activeBufferSize = header->driver.blockSize.load(std::memory_order_relaxed);
    result.controlPanelRequests = header->clock.controlPanelRequests.load(std::memory_order_relaxed);
    return result;
}

bool MatrixLinkHost::isDriverRegistered() const noexcept
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, AsioRegistryKey, 0, KEY_READ | KEY_WOW64_64KEY, &key) != ERROR_SUCCESS)
        return false;
    RegCloseKey(key);
    return true;
}

bool MatrixLinkHost::isDriverStreaming() const noexcept
{
    std::lock_guard lock(mutex);
    return header != nullptr &&
        header->driver.streaming.load(std::memory_order_acquire) != 0 &&
        ml::isAlive(header->driver.heartbeatMs.load(std::memory_order_acquire), GetTickCount64());
}

int MatrixLinkHost::driverChannelCount() const noexcept
{
    std::lock_guard lock(mutex);
    if (header != nullptr && header->driver.channels.load(std::memory_order_acquire) > 0)
        return header->driver.channels.load(std::memory_order_acquire);
    return configuredChannelCount();
}

unsigned long long MatrixLinkHost::missedBlocks() const noexcept
{
    std::lock_guard lock(mutex);
    return header != nullptr ? header->driver.missedResponses.load(std::memory_order_relaxed) : 0ull;
}

bool MatrixLinkHost::openMapping(std::wstring& status)
{
    if (header != nullptr)
        return true;

    HANDLE created = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
        static_cast<DWORD>(static_cast<unsigned long long>(ml::TotalBytes) >> 32),
        static_cast<DWORD>(ml::TotalBytes & 0xffffffffull), ml::SharedMemoryName);
    if (created == nullptr)
    {
        status = L"Could not create the FX Host ASIO link (shared memory).";
        return false;
    }

    void* view = MapViewOfFile(created, FILE_MAP_ALL_ACCESS, 0, 0, ml::TotalBytes);
    if (view == nullptr)
    {
        CloseHandle(created);
        status = L"Could not map the FX Host ASIO link.";
        return false;
    }

    mapping = created;
    header = static_cast<ml::LinkHeader*>(view);
    header->magic = ml::Magic;
    header->version = ml::Version;
    header->maxChannels = ml::MaxChannels;
    header->maxBlockSize = ml::MaxBlockSize;
    requestEvent = CreateEventW(nullptr, FALSE, FALSE, ml::RequestEventName);
    if (requestEvent == nullptr)
    {
        // Without the event the service loop would spin at audio priority.
        closeMapping();
        status = L"Could not create the FX Host ASIO link event.";
        return false;
    }
    return true;
}

void MatrixLinkHost::closeMapping() noexcept
{
    if (header != nullptr)
    {
        UnmapViewOfFile(header);
        header = nullptr;
    }
    if (requestEvent != nullptr)
    {
        CloseHandle(static_cast<HANDLE>(requestEvent));
        requestEvent = nullptr;
    }
    if (mapping != nullptr)
    {
        CloseHandle(static_cast<HANDLE>(mapping));
        mapping = nullptr;
    }
}

int MatrixLinkHost::knownSampleRate() const noexcept
{
    const int live = sampleRate.load(std::memory_order_acquire);
    if (live > 0)
        return live;
    std::lock_guard lock(mutex);
    const int driverRate = header != nullptr ? header->driver.sampleRate.load(std::memory_order_acquire) : 0;
    return driverRate > 0 ? driverRate : 48000;
}

bool MatrixLinkHost::detectDriver(std::wstring& status)
{
    const bool registered = isDriverRegistered();
    const bool streaming = isDriverStreaming();
    std::wostringstream message;
    message << L"Elka FX Host ASIO driver: " << (registered ? L"installed" : L"NOT registered (run scripts\\register-asio-driver.ps1 as admin)")
            << L" | " << configuredChannelCount() << L" channels"
            << L" | Matrix " << (streaming ? L"is streaming" : L"is not streaming yet");
    status = message.str();
    return registered;
}

bool MatrixLinkHost::driverInfo(std::wstring& name, int& inputs, int& outputs) const
{
    name = L"Elka FX Host";
    inputs = outputs = driverChannelCount();
    return isDriverRegistered();
}

bool MatrixLinkHost::start(std::wstring& status)
{
    std::lock_guard lock(mutex);
    if (running.load(std::memory_order_acquire))
    {
        status = L"FX Host ASIO link is already running.";
        return true;
    }
    if (!openMapping(status))
        return false;

    if (worker.joinable())
        worker.join();
    if (preparer.joinable())
        preparer.join();

    header->host.pid.store(GetCurrentProcessId(), std::memory_order_relaxed);
    header->host.heartbeatMs.store(GetTickCount64(), std::memory_order_relaxed);
    header->host.running.store(1, std::memory_order_release);
    lastProcessed = header->driver.requestSequence.load(std::memory_order_acquire);
    stopRequested.store(false, std::memory_order_release);
    running.store(true, std::memory_order_release);
    worker = std::thread([this] { serviceLoop(); });
    preparer = std::thread([this] { prepareLoop(); });

    status = L"FX Host ASIO link started. Select \"Elka FX Host\" as an ASIO device in Matrix.";
    return true;
}

void MatrixLinkHost::stop(std::wstring& status) noexcept
{
    {
        std::lock_guard lock(mutex);
        stopRequested.store(true, std::memory_order_release);
        if (requestEvent != nullptr)
            SetEvent(static_cast<HANDLE>(requestEvent));
    }
    if (worker.joinable())
        worker.join();
    if (preparer.joinable())
        preparer.join();

    std::lock_guard lock(mutex);
    if (header != nullptr)
    {
        header->host.running.store(0, std::memory_order_release);
        header->host.heartbeatMs.store(0, std::memory_order_release);
    }
    running.store(false, std::memory_order_release);
    sampleRate.store(0, std::memory_order_release);
    blockSize.store(0, std::memory_order_release);
    status = L"FX Host ASIO link stopped. Matrix now receives silence on FX Out.";
}

void MatrixLinkHost::status(std::wstring& status) const
{
    std::lock_guard lock(mutex);
    if (!running.load(std::memory_order_acquire) || header == nullptr)
    {
        status = L"FX Host ASIO link stopped.";
        return;
    }

    const bool streaming = header->driver.streaming.load(std::memory_order_acquire) != 0 &&
        ml::isAlive(header->driver.heartbeatMs.load(std::memory_order_acquire), GetTickCount64());
    std::wostringstream message;
    if (!streaming)
    {
        message << L"FX Host ASIO link running | waiting for Matrix to start the \"Elka FX Host\" ASIO device";
    }
    else
    {
        message << L"FX Host ASIO link running | " << sampleRate.load() << L" Hz | " << blockSize.load()
                << L" spl | " << channels.load() << L" in / " << channels.load() << L" out"
                << L" | late blocks " << header->driver.missedResponses.load(std::memory_order_relaxed)
                << L" | skipped " << header->host.skippedRequests.load(std::memory_order_relaxed);
    }
    status = message.str();
}

void MatrixLinkHost::serviceLoop() noexcept
{
    ensureAudioThreadPriorityOnce(3);

    while (!stopRequested.load(std::memory_order_acquire))
    {
        WaitForSingleObject(static_cast<HANDLE>(requestEvent), 20);
        if (stopRequested.load(std::memory_order_acquire))
            break;

        header->host.heartbeatMs.store(GetTickCount64(), std::memory_order_release);

        const long long latest = header->driver.requestSequence.load(std::memory_order_acquire);
        if (latest < 0 || header->driver.streaming.load(std::memory_order_acquire) == 0)
        {
            lastProcessed = latest;
            continue;
        }
        if (latest < lastProcessed)
            lastProcessed = latest - 1; // driver restarted its sequence

        long long next = lastProcessed + 1;
        if (latest - next >= ml::RingSlots - 1)
        {
            // Fell behind (debugger break, CPU stall): drop old blocks, serve the newest.
            header->host.skippedRequests.fetch_add(static_cast<unsigned long long>(latest - next), std::memory_order_relaxed);
            next = latest;
        }
        for (; next <= latest; ++next)
            processBlock(next);
        lastProcessed = latest;
    }
}

// Not an audio thread. A Matrix sample-rate change resizes delay/route buffers; doing it
// here keeps allocations off the link thread and lets the callback take the control lock.
void MatrixLinkHost::prepareLoop() noexcept
{
    while (!stopRequested.load(std::memory_order_acquire))
    {
        const int wanted = requestedSampleRate.load(std::memory_order_acquire);
        if (wanted > 0 && wanted != preparedSampleRate.load(std::memory_order_acquire) && prepareRate)
        {
            bool done = false;
            try { done = prepareRate(wanted); }
            catch (...) { done = false; }
            if (done)
            {
                preparedSampleRate.store(wanted, std::memory_order_release);
                continue;
            }
        }
        Sleep(5);
    }
}

void MatrixLinkHost::processBlock(long long sequence) noexcept
{
    auto& request = header->toFx[sequence % ml::RingSlots];
    if (request.sequence.load(std::memory_order_acquire) != sequence)
        return;

    const int frames = std::clamp(request.frames.load(std::memory_order_relaxed), 0, ml::MaxBlockSize);
    const int channelCount = std::clamp(request.channels.load(std::memory_order_relaxed), 0, ml::MaxChannels);
    const int rate = header->driver.sampleRate.load(std::memory_order_acquire);
    if (frames <= 0 || channelCount <= 0 || rate <= 0)
        return;

    // Not prepared for this rate yet (Matrix changed it): answer with silence while the
    // preparer thread resizes the buffers. Never allocate on this thread.
    const bool prepared = rate == preparedSampleRate.load(std::memory_order_acquire);
    if (!prepared)
        requestedSampleRate.store(rate, std::memory_order_release);
    sampleRate.store(rate, std::memory_order_release);
    blockSize.store(frames, std::memory_order_release);
    channels.store(channelCount, std::memory_order_release);

    float* input = ml::toFxSlot(header, sequence);
    float* output = ml::fromFxSlot(header, sequence);
    auto& response = header->fromFx[sequence % ml::RingSlots];
    response.sequence.store(-1, std::memory_order_release);

    std::array<float*, ml::MaxChannels> readPointers {};
    std::array<float*, ml::MaxChannels> writePointers {};
    for (int channel = 0; channel < channelCount; ++channel)
    {
        readPointers[static_cast<size_t>(channel)] = ml::channelData(input, channel);
        writePointers[static_cast<size_t>(channel)] = ml::channelData(output, channel);
        std::fill_n(writePointers[static_cast<size_t>(channel)], frames, 0.0f);
    }

    AudioBufferView buffer {
        rate,
        frames,
        channelCount,
        channelCount,
        readPointers.data(),
        writePointers.data()
    };
    if (prepared)
        realtimeEngine.processInsertAsio(buffer);

    // The driver may have reused the request slot while we processed (we were very late).
    if (request.sequence.load(std::memory_order_acquire) != sequence)
        return;

    response.frames.store(frames, std::memory_order_relaxed);
    response.channels.store(channelCount, std::memory_order_relaxed);
    response.sequence.store(sequence, std::memory_order_release);
    header->host.responseSequence.store(sequence, std::memory_order_release);
    processedBlocks.fetch_add(1, std::memory_order_relaxed);
}
}
