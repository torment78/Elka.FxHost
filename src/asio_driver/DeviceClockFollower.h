#pragma once

// Follows the sample clock of a real audio device (for example the interface Matrix
// uses as its master), so the virtual driver delivers blocks at that device's rate
// instead of the PC timer's. No audio is taken from the device: a silent shared-mode
// render stream keeps the device clock running, and IAudioClock reports the device's
// position against the performance counter. The ratio (device seconds per PC second)
// is measured over a sliding window and handed to the driver's clock thread.

#include <windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <deque>
#include <string>
#include <thread>

namespace elka::asio
{
enum class ClockFollowState : int
{
    Off = 0,          // internal (PC timer)
    Measuring = 1,    // device opened, collecting the first seconds
    Locked = 2,       // following the device clock
    NotFound = 3,     // the chosen device does not exist (unplugged / renamed)
    Unavailable = 4,  // the device could not be opened in shared mode (busy, exclusive)
};

class DeviceClockFollower
{
public:
    ~DeviceClockFollower() { stop(); }

    void start(const std::wstring& deviceId)
    {
        stop();
        stopRequested.store(false, std::memory_order_release);
        ratioValue.store(1.0, std::memory_order_release);
        stateValue.store(static_cast<int>(ClockFollowState::Measuring), std::memory_order_release);
        worker = std::thread([this, deviceId] { run(deviceId); });
    }

    void stop() noexcept
    {
        stopRequested.store(true, std::memory_order_release);
        if (worker.joinable())
            worker.join();
        ratioValue.store(1.0, std::memory_order_release);
        stateValue.store(static_cast<int>(ClockFollowState::Off), std::memory_order_release);
    }

    // Device seconds per PC second; 1.0 when not locked. The clock thread divides its
    // block period by this, so a device that runs fast gets blocks slightly sooner.
    double ratio() const noexcept { return ratioValue.load(std::memory_order_acquire); }
    ClockFollowState state() const noexcept { return static_cast<ClockFollowState>(stateValue.load(std::memory_order_acquire)); }

private:
    struct Sample
    {
        double deviceSeconds;
        double pcSeconds;
    };

    template <typename T>
    static void release(T*& object) noexcept
    {
        if (object != nullptr)
        {
            object->Release();
            object = nullptr;
        }
    }

    void fail(ClockFollowState state) noexcept
    {
        ratioValue.store(1.0, std::memory_order_release);
        stateValue.store(static_cast<int>(state), std::memory_order_release);
    }

    void run(std::wstring deviceId) noexcept
    {
        const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        IMMDeviceEnumerator* enumerator = nullptr;
        IMMDevice* device = nullptr;
        IAudioClient* client = nullptr;
        IAudioRenderClient* render = nullptr;
        IAudioClock* clock = nullptr;
        WAVEFORMATEX* format = nullptr;

        auto cleanup = [&]() noexcept {
            if (client != nullptr)
                client->Stop();
            release(clock);
            release(render);
            release(client);
            release(device);
            release(enumerator);
            if (format != nullptr)
                CoTaskMemFree(format);
            if (SUCCEEDED(com))
                CoUninitialize();
        };

        if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator))))
        {
            fail(ClockFollowState::Unavailable);
            cleanup();
            return;
        }

        const HRESULT found = deviceId.empty()
            ? enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device)
            : enumerator->GetDevice(deviceId.c_str(), &device);
        DWORD deviceState = 0;
        if (FAILED(found) || device == nullptr || FAILED(device->GetState(&deviceState)) || deviceState != DEVICE_STATE_ACTIVE)
        {
            fail(ClockFollowState::NotFound);
            cleanup();
            return;
        }

        constexpr REFERENCE_TIME BufferDuration = 2000000; // 200 ms of silence
        UINT32 bufferFrames = 0;
        if (FAILED(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client))) ||
            FAILED(client->GetMixFormat(&format)) ||
            FAILED(client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, BufferDuration, 0, format, nullptr)) ||
            FAILED(client->GetBufferSize(&bufferFrames)) ||
            FAILED(client->GetService(__uuidof(IAudioRenderClient), reinterpret_cast<void**>(&render))) ||
            FAILED(client->GetService(__uuidof(IAudioClock), reinterpret_cast<void**>(&clock))))
        {
            fail(ClockFollowState::Unavailable);
            cleanup();
            return;
        }

        UINT64 frequency = 0;
        if (FAILED(clock->GetFrequency(&frequency)) || frequency == 0)
        {
            fail(ClockFollowState::Unavailable);
            cleanup();
            return;
        }

        auto fillSilence = [&]() noexcept {
            UINT32 padding = 0;
            if (FAILED(client->GetCurrentPadding(&padding)) || padding >= bufferFrames)
                return;
            const UINT32 frames = bufferFrames - padding;
            BYTE* data = nullptr;
            if (SUCCEEDED(render->GetBuffer(frames, &data)))
                render->ReleaseBuffer(frames, AUDCLNT_BUFFERFLAGS_SILENT);
        };

        fillSilence();
        if (FAILED(client->Start()))
        {
            fail(ClockFollowState::Unavailable);
            cleanup();
            return;
        }

        // Measure over a sliding window. Short windows react fast but are noisy;
        // 30 s gives a ratio good to well under 1 ppm on a steady device.
        constexpr double MinimumWindowSeconds = 3.0;
        constexpr double LockedWindowSeconds = 10.0;
        constexpr double MaximumWindowSeconds = 30.0;
        std::deque<Sample> samples;
        double smoothed = 1.0;
        bool haveSmoothed = false;
        // The device position starts late after Start(); samples from the first moments
        // skew the fit by hundreds of ppm, so they are skipped.
        constexpr ULONGLONG SettleMilliseconds = 2000;
        const ULONGLONG startedAt = GetTickCount64();

        while (!stopRequested.load(std::memory_order_acquire))
        {
            fillSilence();

            UINT64 position = 0;
            UINT64 qpcPosition = 0; // 100 ns units
            if (GetTickCount64() - startedAt >= SettleMilliseconds &&
                SUCCEEDED(clock->GetPosition(&position, &qpcPosition)) && qpcPosition != 0 && position != 0)
            {
                const Sample sample { static_cast<double>(position) / static_cast<double>(frequency),
                                      static_cast<double>(qpcPosition) * 1e-7 };
                // A device position that jumps backwards (device reset) restarts the window.
                if (!samples.empty() && sample.deviceSeconds < samples.back().deviceSeconds)
                    samples.clear();
                samples.push_back(sample);
                while (samples.size() > 2 && samples.back().pcSeconds - samples.front().pcSeconds > MaximumWindowSeconds)
                    samples.pop_front();

                const double span = samples.back().pcSeconds - samples.front().pcSeconds;
                if (span >= MinimumWindowSeconds && samples.size() >= 8)
                {
                    // Least-squares slope of device time against PC time over the whole window.
                    // Windows updates a shared-mode position in engine-period steps, so a
                    // two-point estimate jitters by tens of ppm; the fit averages that out.
                    const double t0 = samples.front().pcSeconds;
                    const double d0 = samples.front().deviceSeconds;
                    double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
                    for (const auto& point : samples)
                    {
                        const double x = point.pcSeconds - t0;
                        const double y = point.deviceSeconds - d0;
                        sumX += x; sumY += y; sumXX += x * x; sumXY += x * y;
                    }
                    const double n = static_cast<double>(samples.size());
                    const double denominator = n * sumXX - sumX * sumX;
                    const double measured = denominator > 0 ? (n * sumXY - sumX * sumY) / denominator : 0.0;
                    // Real crystals differ from the PC timer by well under 0.1 %; anything
                    // outside that is a glitch (device stalled, power state), not a clock.
                    if (measured > 0.999 && measured < 1.001)
                    {
                        smoothed = haveSmoothed ? smoothed + (measured - smoothed) * 0.2 : measured;
                        haveSmoothed = true;
                        // Until the window is long enough, keep the PC timer (ratio 1.0):
                        // an early estimate is less accurate than no correction at all.
                        const bool locked = span >= LockedWindowSeconds;
                        if (locked)
                            ratioValue.store(smoothed, std::memory_order_release);
                        stateValue.store(static_cast<int>(locked ? ClockFollowState::Locked : ClockFollowState::Measuring),
                                         std::memory_order_release);
                    }
                    else
                    {
                        samples.clear();
                    }
                }
            }

            // ~10 checks a second keeps the 200 ms silence buffer topped up.
            for (int wait = 0; wait < 10 && !stopRequested.load(std::memory_order_acquire); ++wait)
                Sleep(10);
        }

        cleanup();
    }

    std::thread worker;
    std::atomic<bool> stopRequested { false };
    std::atomic<double> ratioValue { 1.0 };
    std::atomic<int> stateValue { 0 };
};
}
