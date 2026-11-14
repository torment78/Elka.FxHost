#pragma once

#include <atomic>
#include <functional>
#include <mutex>
#include <string>
#include <thread>

namespace elka
{
class RealtimeEngine;

namespace matrixlink
{
struct LinkHeader;
}

// FX Host side of the virtual ASIO driver link: the app's only audio path. Each block the
// Elka FX Host ASIO driver (loaded inside Matrix) receives on "FX In" runs through the
// realtime engine and goes back to the driver's "FX Out" channels.
class MatrixLinkHost final
{
public:
    explicit MatrixLinkHost(RealtimeEngine& engine);
    ~MatrixLinkHost();

    MatrixLinkHost(const MatrixLinkHost&) = delete;
    MatrixLinkHost& operator=(const MatrixLinkHost&) = delete;

    bool detectDriver(std::wstring& status);
    bool driverInfo(std::wstring& name, int& inputs, int& outputs) const;
    // Rebuilds the engine's rate-dependent buffers off the audio thread. Returns false when
    // it could not run right now (e.g. the control lock is busy); it is retried shortly.
    using PrepareRate = std::function<bool(int sampleRate)>;
    void setPrepareCallback(PrepareRate callback) { prepareRate = std::move(callback); }
    // The engine is already prepared for this rate (called under the control lock).
    void markPrepared(int rate) noexcept { preparedSampleRate.store(rate, std::memory_order_release); }

    bool start(std::wstring& status);
    void stop(std::wstring& status) noexcept;
    void status(std::wstring& status) const;
    bool isRunning() const noexcept { return running.load(std::memory_order_acquire); }
    int currentSampleRate() const noexcept { return sampleRate.load(std::memory_order_acquire); }
    int currentBlockSize() const noexcept { return blockSize.load(std::memory_order_acquire); }
    // Rate Matrix runs the driver at, or 48000 before the driver has been opened.
    int knownSampleRate() const noexcept;

    // Channel count the driver exposes to Matrix (32 / 64 / custom up to 128).
    static int configuredChannelCount() noexcept;
    bool setConfiguredChannelCount(int channels, std::wstring& status);
    bool isDriverRegistered() const noexcept;
    bool isDriverStreaming() const noexcept;
    int driverChannelCount() const noexcept;
    unsigned long long missedBlocks() const noexcept;

private:
    bool openMapping(std::wstring& status);
    void closeMapping() noexcept;
    void serviceLoop() noexcept;
    void prepareLoop() noexcept;
    void processBlock(long long sequence) noexcept;

    RealtimeEngine& realtimeEngine;
    mutable std::mutex mutex;
    void* mapping = nullptr;
    void* requestEvent = nullptr;
    matrixlink::LinkHeader* header = nullptr;
    std::thread worker;
    std::thread preparer;
    PrepareRate prepareRate;
    std::atomic<int> requestedSampleRate { 0 };
    std::atomic<bool> running { false };
    std::atomic<bool> stopRequested { false };
    std::atomic<int> sampleRate { 0 };
    std::atomic<int> blockSize { 0 };
    std::atomic<int> channels { 0 };
    std::atomic<unsigned long long> processedBlocks { 0 };
    long long lastProcessed = -1;
    std::atomic<int> preparedSampleRate { 0 };
};
}
