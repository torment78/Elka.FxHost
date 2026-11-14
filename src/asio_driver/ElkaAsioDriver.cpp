// Elka FX Host virtual ASIO driver.
//
// A user-mode ASIO driver with no hardware behind it. The host (VB-Audio Matrix, a DAW,
// the loopback test tool) sends audio to the driver's outputs ("FX In A1".."P8"), the
// driver hands every block to the FX Host app through shared memory, and the processed
// block comes back on the driver's inputs ("FX Out A1".."P8") one block later.
// If the FX Host is not running, the inputs carry silence and streaming continues.

#include <windows.h>
#include <objbase.h>
#include <mmsystem.h>
#include <avrt.h>
#include <olectl.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "asio_driver/AsioSdk.h"
#include "asio_driver/ElkaAsioIds.h"
#include "matrix_link/MatrixLinkProtocol.h"

namespace ml = elka::matrixlink;

namespace
{
HMODULE g_module = nullptr;
std::atomic<long> g_objectCount { 0 };
std::atomic<long> g_lockCount { 0 };

constexpr long MinBufferSize = 32;
constexpr long MaxBufferSize = ml::MaxBlockSize;
constexpr long DefaultPreferredBufferSize = 256;
constexpr double SupportedRates[] = { 44100.0, 48000.0, 88200.0, 96000.0, 176400.0, 192000.0 };

DWORD readSettingDword(const wchar_t* name, DWORD fallback) noexcept
{
    DWORD value = 0;
    DWORD size = sizeof(value);
    if (RegGetValueW(HKEY_CURRENT_USER, ml::SettingsKey, name, RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS)
        return value;
    size = sizeof(value);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, ml::SettingsKey, name, RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS)
        return value;
    return fallback;
}

void copyName(char* destination, size_t capacity, const std::string& text) noexcept
{
    if (destination == nullptr || capacity == 0)
        return;
    const size_t count = std::min(capacity - 1, text.size());
    std::memcpy(destination, text.data(), count);
    destination[count] = '\0';
}

class SharedLink
{
public:
    ~SharedLink() { close(false); }

    bool open() noexcept
    {
        if (header != nullptr)
            return true;

        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
            static_cast<DWORD>(static_cast<unsigned long long>(ml::TotalBytes) >> 32),
            static_cast<DWORD>(ml::TotalBytes & 0xffffffffull), ml::SharedMemoryName);
        if (mapping == nullptr)
            return false;

        void* view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, ml::TotalBytes);
        if (view == nullptr)
        {
            close(false);
            return false;
        }

        header = static_cast<ml::LinkHeader*>(view);
        header->magic = ml::Magic;
        header->version = ml::Version;
        header->maxChannels = ml::MaxChannels;
        header->maxBlockSize = ml::MaxBlockSize;
        requestEvent = CreateEventW(nullptr, FALSE, FALSE, ml::RequestEventName);
        return true;
    }

    // Several driver instances can exist at once (a DAW or panel enumerating ASIO drivers
    // while Matrix streams). Only the instance that started streaming owns the shared
    // driver state; the others must never clear it.
    void close(bool owner) noexcept
    {
        if (header != nullptr)
        {
            if (owner)
            {
                header->driver.streaming.store(0, std::memory_order_release);
                header->driver.heartbeatMs.store(0, std::memory_order_release);
                header->driver.pid.store(0, std::memory_order_release);
            }
            UnmapViewOfFile(header);
            header = nullptr;
        }
        if (requestEvent != nullptr)
        {
            CloseHandle(requestEvent);
            requestEvent = nullptr;
        }
        if (mapping != nullptr)
        {
            CloseHandle(mapping);
            mapping = nullptr;
        }
    }

    ml::LinkHeader* header = nullptr;
    HANDLE requestEvent = nullptr;

private:
    HANDLE mapping = nullptr;
};

class ElkaAsioDriver final : public IASIO
{
public:
    ElkaAsioDriver() { g_objectCount.fetch_add(1); }
    ~ElkaAsioDriver()
    {
        stop();
        disposeBuffers();
        link.close(ownsLink);
        ownsLink = false;
        g_objectCount.fetch_sub(1);
    }

    // IUnknown. ASIO hosts query with the driver CLSID as the interface id.
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override
    {
        if (object == nullptr)
            return E_POINTER;
        if (riid == IID_IUnknown || riid == elka::asio::CLSID_ElkaFxHostAsio)
        {
            *object = static_cast<IASIO*>(this);
            AddRef();
            return S_OK;
        }
        *object = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(refCount.fetch_add(1) + 1); }

    ULONG STDMETHODCALLTYPE Release() override
    {
        const long remaining = refCount.fetch_sub(1) - 1;
        if (remaining == 0)
            delete this;
        return static_cast<ULONG>(remaining);
    }

    // IASIO
    ASIOBool init(void* sysHandle) override
    {
        parentWindow = static_cast<HWND>(sysHandle);
        channels = ml::sanitizeChannelCount(static_cast<int>(readSettingDword(ml::ChannelCountValue, ml::DefaultChannels)));
        useInt32 = readSettingDword(L"UseInt32Samples", 0) != 0;
        preferredBufferSize = std::clamp(static_cast<long>(readSettingDword(L"PreferredBufferSize", DefaultPreferredBufferSize)),
                                         MinBufferSize, MaxBufferSize);
        if (!link.open())
            setError("Shared memory unavailable; the driver will output silence.");
        else
        {
            // Read only: a probing instance must not disturb a stream another instance owns.
            observedResetGeneration = link.header->host.resetGeneration.load(std::memory_order_acquire);
        }
        return ASIOTrue;
    }

    void getDriverName(char* name) override { copyName(name, 32, ml::DriverName); }
    long getDriverVersion() override { return 1; }
    void getErrorMessage(char* text) override
    {
        std::lock_guard lock(errorMutex);
        copyName(text, 124, lastError.empty() ? std::string("No error.") : lastError);
    }

    ASIOError start() override
    {
        if (callbacks == nullptr || activeBufferSize <= 0)
            return ASE_NotPresent;
        if (running.load())
            return ASE_OK;

        sequence = 0;
        samplePosition = 0;
        bufferIndex = 0;
        lastSwitchTimeMs = timeGetTime();
        timeInfoMode = callbacks->asioMessage != nullptr &&
            callbacks->asioMessage(kAsioSelectorSupported, kAsioSupportsTimeInfo, nullptr, nullptr) == 1 &&
            callbacks->asioMessage(kAsioSupportsTimeInfo, 0, nullptr, nullptr) == 1;
        resetSupported = callbacks->asioMessage != nullptr &&
            callbacks->asioMessage(kAsioSelectorSupported, kAsioResetRequest, nullptr, nullptr) == 1;

        if (link.header != nullptr)
        {
            // Clear replies left from an earlier stream so none is delivered as current.
            for (int slot = 0; slot < ml::RingSlots; ++slot)
            {
                link.header->toFx[slot].sequence.store(-1, std::memory_order_relaxed);
                link.header->fromFx[slot].sequence.store(-1, std::memory_order_relaxed);
            }
            ownsLink = true;
            auto& driver = link.header->driver;
            driver.pid.store(GetCurrentProcessId(), std::memory_order_relaxed);
            driver.sampleRate.store(static_cast<int>(sampleRate), std::memory_order_relaxed);
            driver.blockSize.store(static_cast<int>(activeBufferSize), std::memory_order_relaxed);
            driver.channels.store(channels, std::memory_order_relaxed);
            driver.requestSequence.store(-1, std::memory_order_relaxed);
            driver.heartbeatMs.store(GetTickCount64(), std::memory_order_relaxed);
            driver.streaming.store(1, std::memory_order_release);
        }

        running.store(true);
        clockThread = std::thread([this] { clockLoop(); });
        return ASE_OK;
    }

    ASIOError stop() override
    {
        running.store(false);
        if (clockThread.joinable())
        {
            // A host may call stop() from inside asioMessage (e.g. answering a reset request),
            // which runs on the clock thread: joining itself would throw and end the host.
            if (clockThread.get_id() == std::this_thread::get_id())
                clockThread.detach();
            else
                clockThread.join();
        }
        if (link.header != nullptr && ownsLink)
            link.header->driver.streaming.store(0, std::memory_order_release);
        return ASE_OK;
    }

    ASIOError getChannels(long* numInputChannels, long* numOutputChannels) override
    {
        if (numInputChannels != nullptr)
            *numInputChannels = channels;
        if (numOutputChannels != nullptr)
            *numOutputChannels = channels;
        return ASE_OK;
    }

    ASIOError getLatencies(long* inputLatency, long* outputLatency) override
    {
        const long block = activeBufferSize > 0 ? activeBufferSize : preferredBufferSize;
        if (inputLatency != nullptr)
            *inputLatency = block;
        if (outputLatency != nullptr)
            *outputLatency = block;
        return ASE_OK;
    }

    ASIOError getBufferSize(long* minSize, long* maxSize, long* preferredSize, long* granularity) override
    {
        if (minSize != nullptr) *minSize = MinBufferSize;
        if (maxSize != nullptr) *maxSize = MaxBufferSize;
        if (preferredSize != nullptr) *preferredSize = preferredBufferSize;
        if (granularity != nullptr) *granularity = -1; // powers of two
        return ASE_OK;
    }

    ASIOError canSampleRate(ASIOSampleRate rate) override
    {
        return isSupportedRate(rate) ? ASE_OK : ASE_NoClock;
    }

    ASIOError getSampleRate(ASIOSampleRate* rate) override
    {
        if (rate == nullptr)
            return ASE_InvalidParameter;
        *rate = sampleRate;
        return ASE_OK;
    }

    ASIOError setSampleRate(ASIOSampleRate rate) override
    {
        if (rate == 0.0) // "external sync" request: we are always internal
            return ASE_OK;
        if (!isSupportedRate(rate))
            return ASE_NoClock;
        sampleRate = rate;
        periodTicks.store(0); // recomputed by the clock thread
        if (link.header != nullptr)
            link.header->driver.sampleRate.store(static_cast<int>(rate), std::memory_order_release);
        return ASE_OK;
    }

    ASIOError getClockSources(ASIOClockSource* clocks, long* numSources) override
    {
        if (clocks == nullptr || numSources == nullptr || *numSources < 1)
            return ASE_InvalidParameter;
        clocks[0].index = 0;
        clocks[0].associatedChannel = -1;
        clocks[0].associatedGroup = -1;
        clocks[0].isCurrentSource = ASIOTrue;
        copyName(clocks[0].name, 32, "Internal");
        *numSources = 1;
        return ASE_OK;
    }

    ASIOError setClockSource(long reference) override { return reference == 0 ? ASE_OK : ASE_InvalidParameter; }

    ASIOError getSamplePosition(ASIOSamples* position, ASIOTimeStamp* timeStamp) override
    {
        if (position == nullptr || timeStamp == nullptr)
            return ASE_InvalidParameter;
        const auto samples = static_cast<unsigned long long>(samplePositionAtSwitch.load());
        const auto nanoseconds = static_cast<unsigned long long>(switchTimeMs.load()) * 1000000ull;
        position->hi = static_cast<unsigned long>(samples >> 32);
        position->lo = static_cast<unsigned long>(samples & 0xffffffffull);
        timeStamp->hi = static_cast<unsigned long>(nanoseconds >> 32);
        timeStamp->lo = static_cast<unsigned long>(nanoseconds & 0xffffffffull);
        return ASE_OK;
    }

    ASIOError getChannelInfo(ASIOChannelInfo* info) override
    {
        if (info == nullptr || info->channel < 0 || info->channel >= channels)
            return ASE_InvalidParameter;
        const bool isInput = info->isInput != ASIOFalse;
        const int channel = static_cast<int>(info->channel);
        info->isActive = isInput
            ? (channel < static_cast<int>(inputBuffers.size()) && inputBuffers[channel].active ? ASIOTrue : ASIOFalse)
            : (channel < static_cast<int>(outputBuffers.size()) && outputBuffers[channel].active ? ASIOTrue : ASIOFalse);
        info->channelGroup = channel / ml::ChannelStep;
        info->type = useInt32 ? ASIOSTInt32LSB : ASIOSTFloat32LSB;
        // Driver outputs carry audio INTO the FX Host; driver inputs bring it back OUT.
        copyName(info->name, 32, ml::channelName(channel, !isInput));
        return ASE_OK;
    }

    ASIOError createBuffers(ASIOBufferInfo* infos, long numChannels, long bufferSize, ASIOCallbacks* hostCallbacks) override
    {
        if (infos == nullptr || hostCallbacks == nullptr || numChannels <= 0)
            return ASE_InvalidParameter;
        if (bufferSize < MinBufferSize || bufferSize > MaxBufferSize)
            return ASE_InvalidMode;
        if (running.load())
            return ASE_InvalidMode;

        disposeBuffers();
        inputBuffers.assign(static_cast<size_t>(channels), {});
        outputBuffers.assign(static_cast<size_t>(channels), {});

        for (long i = 0; i < numChannels; ++i)
        {
            auto& info = infos[i];
            if (info.channelNum < 0 || info.channelNum >= channels)
            {
                disposeBuffers();
                return ASE_InvalidParameter;
            }
            auto& target = info.isInput != ASIOFalse ? inputBuffers[static_cast<size_t>(info.channelNum)]
                                                       : outputBuffers[static_cast<size_t>(info.channelNum)];
            target.storage.assign(static_cast<size_t>(bufferSize) * 2, 0u);
            target.active = true;
            info.buffers[0] = target.storage.data();
            info.buffers[1] = target.storage.data() + bufferSize;
        }

        activeBufferSize = bufferSize;
        callbacks = hostCallbacks;
        if (link.header != nullptr)
            link.header->driver.blockSize.store(static_cast<int>(bufferSize), std::memory_order_release);
        return ASE_OK;
    }

    ASIOError disposeBuffers() override
    {
        stop();
        inputBuffers.clear();
        outputBuffers.clear();
        activeBufferSize = 0;
        callbacks = nullptr;
        return ASE_OK;
    }

    ASIOError controlPanel() override
    {
        const bool hostAlive = link.header != nullptr &&
            ml::isAlive(link.header->host.heartbeatMs.load(), GetTickCount64());
        const std::wstring text =
            L"Elka FX Host virtual ASIO driver\n\n"
            L"Channels: " + std::to_wstring(channels) + L" in / " + std::to_wstring(channels) + L" out (banks A-" +
            std::wstring(1, static_cast<wchar_t>(L'A' + (channels / ml::ChannelStep) - 1)) + L")\n"
            L"Sample rate: " + std::to_wstring(static_cast<int>(sampleRate)) + L" Hz\n"
            L"Buffer: " + std::to_wstring(activeBufferSize > 0 ? activeBufferSize : preferredBufferSize) + L" samples\n"
            L"FX Host app: " + (hostAlive ? std::wstring(L"connected") : std::wstring(L"not running (inputs are silent)")) +
            L"\n\nChange the channel count (32 / 64 / 128) in FX Host settings.";
        MessageBoxW(parentWindow, text.c_str(), L"Elka FX Host ASIO", MB_OK | MB_ICONINFORMATION);
        return ASE_OK;
    }

    ASIOError future(long, void*) override
    {
        return ASE_InvalidParameter; // no time code, input monitoring or transport control
    }

    ASIOError outputReady() override { return ASE_NotPresent; }

private:
    struct ChannelBuffer
    {
        std::vector<uint32_t> storage; // 4-byte samples, two halves
        bool active = false;
    };

    static bool isSupportedRate(double rate) noexcept
    {
        for (double supported : SupportedRates)
            if (std::abs(supported - rate) < 0.5)
                return true;
        return false;
    }

    void setError(const char* text)
    {
        std::lock_guard lock(errorMutex);
        lastError = text;
    }

    void clockLoop() noexcept
    {
        timeBeginPeriod(1);
        DWORD taskIndex = 0;
        HANDLE avrt = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
        if (avrt != nullptr)
            AvSetMmThreadPriority(avrt, AVRT_PRIORITY_CRITICAL);
        else
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);

        HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (timer == nullptr)
            timer = CreateWaitableTimerW(nullptr, TRUE, nullptr);

        LARGE_INTEGER frequency {};
        QueryPerformanceFrequency(&frequency);
        LARGE_INTEGER now {};
        QueryPerformanceCounter(&now);
        long long nextDeadline = now.QuadPart;

        while (running.load(std::memory_order_acquire))
        {
            long long period = periodTicks.load(std::memory_order_relaxed);
            if (period <= 0)
            {
                period = static_cast<long long>(std::llround(
                    static_cast<double>(frequency.QuadPart) * static_cast<double>(activeBufferSize) / sampleRate));
                periodTicks.store(period, std::memory_order_relaxed);
            }

            nextDeadline += period;
            waitUntil(timer, nextDeadline, frequency.QuadPart);

            QueryPerformanceCounter(&now);
            if (now.QuadPart - nextDeadline > period * 4)
                nextDeadline = now.QuadPart; // we were stalled; do not burst-catch-up

            if (!running.load(std::memory_order_acquire))
                break;
            tick();
        }

        if (timer != nullptr)
            CloseHandle(timer);
        if (avrt != nullptr)
            AvRevertMmThreadCharacteristics(avrt);
        timeEndPeriod(1);
    }

    static void waitUntil(HANDLE timer, long long deadline, long long frequency) noexcept
    {
        constexpr long long SpinMicroseconds = 250;
        LARGE_INTEGER now {};
        QueryPerformanceCounter(&now);
        long long remaining = deadline - now.QuadPart;
        const long long spinTicks = frequency * SpinMicroseconds / 1000000;
        if (remaining > spinTicks && timer != nullptr)
        {
            LARGE_INTEGER due {};
            due.QuadPart = -((remaining - spinTicks) * 10000000 / frequency); // relative, 100 ns units
            if (SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE))
                WaitForSingleObject(timer, INFINITE);
        }
        do
        {
            YieldProcessor();
            QueryPerformanceCounter(&now);
        } while (now.QuadPart < deadline);
    }

    void tick() noexcept
    {
        const int block = static_cast<int>(activeBufferSize);
        const int index = bufferIndex;
        const int previous = 1 - index;
        auto* header = link.header;
        const uint64_t nowMs = GetTickCount64();

        if (header != nullptr)
        {
            header->driver.heartbeatMs.store(nowMs, std::memory_order_relaxed);
            postRequest(header, previous, block);
            readResponse(header, index, block, nowMs);
            checkResetRequest(header);
        }
        else
        {
            silenceInputs(index, block);
        }

        samplePositionAtSwitch.store(samplePosition);
        switchTimeMs.store(timeGetTime());

        if (timeInfoMode && callbacks->bufferSwitchTimeInfo != nullptr)
        {
            ASIOTime time {};
            time.timeInfo.speed = 1.0;
            const auto ns = static_cast<unsigned long long>(switchTimeMs.load()) * 1000000ull;
            time.timeInfo.systemTime.hi = static_cast<unsigned long>(ns >> 32);
            time.timeInfo.systemTime.lo = static_cast<unsigned long>(ns & 0xffffffffull);
            const auto samples = static_cast<unsigned long long>(samplePosition);
            time.timeInfo.samplePosition.hi = static_cast<unsigned long>(samples >> 32);
            time.timeInfo.samplePosition.lo = static_cast<unsigned long>(samples & 0xffffffffull);
            time.timeInfo.sampleRate = sampleRate;
            time.timeInfo.flags = kSystemTimeValid | kSamplePositionValid | kSampleRateValid;
            callbacks->bufferSwitchTimeInfo(&time, index, ASIOFalse);
        }
        else if (callbacks->bufferSwitch != nullptr)
        {
            callbacks->bufferSwitch(index, ASIOFalse);
        }

        samplePosition += block;
        bufferIndex = previous;
        ++sequence;
    }

    // The host filled output[previous] during the last bufferSwitch: send it to the FX Host.
    void postRequest(ml::LinkHeader* header, int half, int block) noexcept
    {
        auto& slotHeader = header->toFx[sequence % ml::RingSlots];
        slotHeader.sequence.store(-1, std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_release);
        float* slot = ml::toFxSlot(header, sequence);
        for (int channel = 0; channel < channels; ++channel)
        {
            float* destination = ml::channelData(slot, channel);
            const auto& source = outputBuffers[static_cast<size_t>(channel)];
            if (!source.active)
            {
                std::fill(destination, destination + block, 0.0f);
                continue;
            }
            const uint32_t* samples = source.storage.data() + static_cast<size_t>(half) * block;
            if (useInt32)
            {
                const auto* ints = reinterpret_cast<const int32_t*>(samples);
                for (int i = 0; i < block; ++i)
                    destination[i] = static_cast<float>(ints[i]) * (1.0f / 2147483648.0f);
            }
            else
            {
                std::memcpy(destination, samples, static_cast<size_t>(block) * sizeof(float));
            }
        }
        slotHeader.frames.store(block, std::memory_order_relaxed);
        slotHeader.channels.store(channels, std::memory_order_relaxed);
        slotHeader.sequence.store(sequence, std::memory_order_release);
        header->driver.requestSequence.store(sequence, std::memory_order_release);
        if (link.requestEvent != nullptr)
            SetEvent(link.requestEvent);
    }

    // Fill input[index] with the FX Host's answer to the previous request, or silence.
    void readResponse(ml::LinkHeader* header, int index, int block, uint64_t nowMs) noexcept
    {
        const int64_t wanted = sequence - 1;
        bool delivered = false;
        if (wanted >= 0 && ml::isAlive(header->host.heartbeatMs.load(std::memory_order_relaxed), nowMs))
        {
            const auto& slotHeader = header->fromFx[wanted % ml::RingSlots];
            if (slotHeader.sequence.load(std::memory_order_acquire) == wanted &&
                slotHeader.frames.load(std::memory_order_relaxed) == block)
            {
                const float* slot = ml::fromFxSlot(header, wanted);
                for (int channel = 0; channel < channels; ++channel)
                {
                    auto& target = inputBuffers[static_cast<size_t>(channel)];
                    if (!target.active)
                        continue;
                    uint32_t* samples = target.storage.data() + static_cast<size_t>(index) * block;
                    const float* source = ml::channelData(const_cast<float*>(slot), channel);
                    if (useInt32)
                    {
                        auto* ints = reinterpret_cast<int32_t*>(samples);
                        for (int i = 0; i < block; ++i)
                        {
                            const float clamped = std::clamp(source[i], -1.0f, 1.0f);
                            ints[i] = static_cast<int32_t>(std::lrint(static_cast<double>(clamped) * 2147483647.0));
                        }
                    }
                    else
                    {
                        std::memcpy(samples, source, static_cast<size_t>(block) * sizeof(float));
                    }
                }
                // A torn read is detected if the host overwrote the slot while we copied.
                std::atomic_thread_fence(std::memory_order_acquire);
                delivered = slotHeader.sequence.load(std::memory_order_relaxed) == wanted;
            }
        }

        if (delivered)
        {
            header->driver.deliveredResponses.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        if (wanted >= 0)
            header->driver.missedResponses.fetch_add(1, std::memory_order_relaxed);
        silenceInputs(index, block);
    }

    void silenceInputs(int index, int block) noexcept
    {
        for (auto& target : inputBuffers)
            if (target.active)
                std::fill_n(target.storage.data() + static_cast<size_t>(index) * block, block, 0u);
    }

    void checkResetRequest(ml::LinkHeader* header) noexcept
    {
        const int generation = header->host.resetGeneration.load(std::memory_order_acquire);
        if (generation == observedResetGeneration)
            return;
        observedResetGeneration = generation;
        const int wanted = ml::sanitizeChannelCount(static_cast<int>(readSettingDword(ml::ChannelCountValue, ml::DefaultChannels)));
        if (wanted != channels && resetSupported && callbacks->asioMessage != nullptr)
            callbacks->asioMessage(kAsioResetRequest, 0, nullptr, nullptr);
    }

    std::atomic<long> refCount { 1 };
    HWND parentWindow = nullptr;
    SharedLink link;
    bool ownsLink = false; // true once this instance has started streaming
    int channels = ml::DefaultChannels;
    bool useInt32 = false;
    long preferredBufferSize = DefaultPreferredBufferSize;
    long activeBufferSize = 0;
    double sampleRate = 48000.0;
    ASIOCallbacks* callbacks = nullptr;
    std::vector<ChannelBuffer> inputBuffers;
    std::vector<ChannelBuffer> outputBuffers;
    std::thread clockThread;
    std::atomic<bool> running { false };
    std::atomic<long long> periodTicks { 0 };
    int bufferIndex = 0;
    int64_t sequence = 0;
    long long samplePosition = 0;
    std::atomic<long long> samplePositionAtSwitch { 0 };
    std::atomic<DWORD> switchTimeMs { 0 };
    DWORD lastSwitchTimeMs = 0;
    bool timeInfoMode = false;
    bool resetSupported = false;
    int observedResetGeneration = 0;
    std::mutex errorMutex;
    std::string lastError;
};

class ClassFactory final : public IClassFactory
{
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override
    {
        if (object == nullptr)
            return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *object = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *object = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** object) override
    {
        if (object == nullptr)
            return E_POINTER;
        *object = nullptr;
        if (outer != nullptr)
            return CLASS_E_NOAGGREGATION;
        auto* driver = new (std::nothrow) ElkaAsioDriver();
        if (driver == nullptr)
            return E_OUTOFMEMORY;
        const HRESULT result = driver->QueryInterface(riid, object);
        driver->Release();
        return result;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        if (lock) g_lockCount.fetch_add(1);
        else g_lockCount.fetch_sub(1);
        return S_OK;
    }
};

ClassFactory g_factory;

std::wstring clsidString()
{
    wchar_t text[64] {};
    StringFromGUID2(elka::asio::CLSID_ElkaFxHostAsio, text, 64);
    return text;
}

LSTATUS setString(HKEY root, const std::wstring& path, const wchar_t* name, const std::wstring& value)
{
    HKEY key = nullptr;
    LSTATUS status = RegCreateKeyExW(root, path.c_str(), 0, nullptr, 0, KEY_WRITE | KEY_WOW64_64KEY, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS)
        return status;
    status = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()),
                            static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return status;
}
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** object)
{
    if (clsid != elka::asio::CLSID_ElkaFxHostAsio)
        return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, object);
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return g_objectCount.load() == 0 && g_lockCount.load() == 0 ? S_OK : S_FALSE;
}

extern "C" HRESULT __stdcall DllRegisterServer()
{
    wchar_t path[MAX_PATH] {};
    if (GetModuleFileNameW(g_module, path, MAX_PATH) == 0)
        return SELFREG_E_CLASS;

    const std::wstring clsid = clsidString();
    const std::wstring classKey = L"Software\\Classes\\CLSID\\" + clsid;
    const std::wstring asioKey = std::wstring(L"Software\\ASIO\\") + elka::asio::RegistryName;
    if (setString(HKEY_LOCAL_MACHINE, classKey, nullptr, elka::asio::RegistryName) != ERROR_SUCCESS ||
        setString(HKEY_LOCAL_MACHINE, classKey + L"\\InprocServer32", nullptr, path) != ERROR_SUCCESS ||
        setString(HKEY_LOCAL_MACHINE, classKey + L"\\InprocServer32", L"ThreadingModel", L"Apartment") != ERROR_SUCCESS ||
        setString(HKEY_LOCAL_MACHINE, asioKey, L"CLSID", clsid) != ERROR_SUCCESS ||
        setString(HKEY_LOCAL_MACHINE, asioKey, L"Description", elka::asio::RegistryName) != ERROR_SUCCESS)
        return SELFREG_E_CLASS;
    return S_OK;
}

extern "C" HRESULT __stdcall DllUnregisterServer()
{
    const std::wstring clsid = clsidString();
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, (L"Software\\Classes\\CLSID\\" + clsid).c_str());
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, (std::wstring(L"Software\\ASIO\\") + elka::asio::RegistryName).c_str());
    return S_OK;
}
