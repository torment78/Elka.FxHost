// MatrixLinkLoopback: pretends to be Matrix.
//
// Loads ElkaFxHost.Asio.dll directly (no registration needed), opens every channel,
// plays a 1 kHz tone into "FX In A1" and measures what comes back on "FX Out A1".
//
//   MatrixLinkLoopback.exe [--seconds N] [--buffer N] [--rate N] [--channel N]
//                          [--fake-host] [--driver path\to\ElkaFxHost.Asio.dll]
//
// --fake-host serves the link inside this process (copies FX In -> FX Out), so the driver
// can be tested without the FX Host app. Without it, start the FX Host app and press
// "Start ASIO Link" first; unrouted channels pass through, so the tone should return.

#include <windows.h>
#include <objbase.h>

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "asio_driver/AsioSdk.h"
#include "asio_driver/ElkaAsioIds.h"
#include "matrix_link/MatrixLinkProtocol.h"

namespace ml = elka::matrixlink;

namespace
{
struct Options
{
    int seconds = 5;
    long buffer = 256;
    double rate = 48000.0;
    int channel = 0;
    bool fakeHost = false;
    std::wstring driverPath;
};

IASIO* g_driver = nullptr;
std::vector<ASIOBufferInfo> g_buffers;
long g_bufferSize = 0;
int g_channels = 0;
int g_testChannel = 0;
double g_rate = 48000.0;
std::atomic<long long> g_switches { 0 };
long long g_phase = 0;
std::atomic<double> g_returnPeak { 0.0 };
std::atomic<double> g_returnSumSquares { 0.0 };
std::atomic<long long> g_returnSamples { 0 };
std::atomic<long long> g_firstSoundSwitch { -1 };
std::atomic<double> g_otherChannelPeak { 0.0 };

void* bufferFor(bool input, int channel, long half)
{
    for (auto& info : g_buffers)
        if ((info.isInput != ASIOFalse) == input && info.channelNum == channel)
            return info.buffers[half];
    return nullptr;
}

void bufferSwitch(long index, ASIOBool)
{
    const long long count = g_switches.fetch_add(1);

    // Write the tone into the driver output (FX In) for the test channel, silence elsewhere.
    for (int channel = 0; channel < g_channels; ++channel)
    {
        auto* out = static_cast<float*>(bufferFor(false, channel, index));
        if (out == nullptr)
            continue;
        if (channel != g_testChannel)
        {
            std::memset(out, 0, sizeof(float) * static_cast<size_t>(g_bufferSize));
            continue;
        }
        for (long i = 0; i < g_bufferSize; ++i)
            out[i] = 0.5f * static_cast<float>(std::sin(2.0 * 3.14159265358979 * 1000.0 * static_cast<double>(g_phase + i) / g_rate));
    }
    g_phase += g_bufferSize;

    // Read the driver input (FX Out) back.
    double peak = 0.0;
    double sum = 0.0;
    double other = 0.0;
    for (int channel = 0; channel < g_channels; ++channel)
    {
        const auto* in = static_cast<const float*>(bufferFor(true, channel, index));
        if (in == nullptr)
            continue;
        for (long i = 0; i < g_bufferSize; ++i)
        {
            const double value = std::fabs(static_cast<double>(in[i]));
            if (channel == g_testChannel)
            {
                peak = value > peak ? value : peak;
                sum += value * value;
            }
            else if (value > other)
            {
                other = value;
            }
        }
    }
    if (peak > g_returnPeak.load()) g_returnPeak.store(peak);
    if (other > g_otherChannelPeak.load()) g_otherChannelPeak.store(other);
    g_returnSumSquares.store(g_returnSumSquares.load() + sum);
    g_returnSamples.fetch_add(g_bufferSize);
    if (peak > 0.01 && g_firstSoundSwitch.load() < 0)
        g_firstSoundSwitch.store(count);
}

void sampleRateChanged(ASIOSampleRate) {}

long asioMessage(long selector, long value, void*, double*)
{
    switch (selector)
    {
    case kAsioSelectorSupported:
        return value == kAsioResetRequest || value == kAsioEngineVersion || value == kAsioSupportsTimeInfo ? 1 : 0;
    case kAsioEngineVersion:
        return 2;
    case kAsioResetRequest:
        std::printf("  driver asked for a reset (channel count changed)\n");
        return 1;
    case kAsioSupportsTimeInfo:
        return 0; // exercise the plain bufferSwitch path
    default:
        return 0;
    }
}

ASIOTime* bufferSwitchTimeInfo(ASIOTime*, long index, ASIOBool direct)
{
    bufferSwitch(index, direct);
    return nullptr;
}

// Minimal stand-in for the FX Host app: copy every FX In block to FX Out.
void fakeHostLoop(std::atomic<bool>& stop)
{
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
        static_cast<DWORD>(static_cast<unsigned long long>(ml::TotalBytes) >> 32),
        static_cast<DWORD>(ml::TotalBytes & 0xffffffffull), ml::SharedMemoryName);
    auto* header = mapping ? static_cast<ml::LinkHeader*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, ml::TotalBytes)) : nullptr;
    HANDLE event = CreateEventW(nullptr, FALSE, FALSE, ml::RequestEventName);
    if (header == nullptr || event == nullptr)
    {
        std::printf("fake host: could not open shared memory\n");
        return;
    }

    header->host.pid.store(GetCurrentProcessId());
    header->host.running.store(1);
    long long last = -1;
    while (!stop.load())
    {
        WaitForSingleObject(event, 20);
        header->host.heartbeatMs.store(GetTickCount64());
        const long long latest = header->driver.requestSequence.load();
        if (latest < 0)
            continue;
        if (latest < last)
            last = latest - 1;
        for (long long seq = last + 1; seq <= latest; ++seq)
        {
            auto& request = header->toFx[seq % ml::RingSlots];
            if (request.sequence.load() != seq)
                continue;
            const int frames = request.frames.load();
            const int channels = request.channels.load();
            auto& response = header->fromFx[seq % ml::RingSlots];
            response.sequence.store(-1);
            for (int channel = 0; channel < channels; ++channel)
                std::memcpy(ml::channelData(ml::fromFxSlot(header, seq), channel),
                            ml::channelData(ml::toFxSlot(header, seq), channel), sizeof(float) * static_cast<size_t>(frames));
            response.frames.store(frames);
            response.channels.store(channels);
            response.sequence.store(seq);
        }
        last = latest;
    }
    header->host.running.store(0);
    header->host.heartbeatMs.store(0);
    UnmapViewOfFile(header);
    CloseHandle(mapping);
    CloseHandle(event);
}

std::wstring defaultDriverPath()
{
    wchar_t path[MAX_PATH] {};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring result(path);
    result = result.substr(0, result.find_last_of(L"\\/") + 1);
    return result + L"ElkaFxHost.Asio.dll";
}

Options parse(int argc, wchar_t** argv)
{
    Options options;
    for (int i = 1; i < argc; ++i)
    {
        const std::wstring arg = argv[i];
        auto next = [&]() { return i + 1 < argc ? std::wstring(argv[++i]) : std::wstring(); };
        if (arg == L"--seconds") options.seconds = std::stoi(next());
        else if (arg == L"--buffer") options.buffer = std::stol(next());
        else if (arg == L"--rate") options.rate = std::stod(next());
        else if (arg == L"--channel") options.channel = std::stoi(next());
        else if (arg == L"--fake-host") options.fakeHost = true;
        else if (arg == L"--driver") options.driverPath = next();
    }
    if (options.driverPath.empty())
        options.driverPath = defaultDriverPath();
    return options;
}
}

int wmain(int argc, wchar_t** argv)
{
    const Options options = parse(argc, argv);
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    HMODULE module = LoadLibraryW(options.driverPath.c_str());
    if (module == nullptr)
    {
        std::wprintf(L"FAIL: could not load %ls (error %lu)\n", options.driverPath.c_str(), GetLastError());
        return 2;
    }
    using GetClassObject = HRESULT(__stdcall*)(REFCLSID, REFIID, void**);
    auto getClassObject = reinterpret_cast<GetClassObject>(GetProcAddress(module, "DllGetClassObject"));
    IClassFactory* factory = nullptr;
    if (getClassObject == nullptr || FAILED(getClassObject(elka::asio::CLSID_ElkaFxHostAsio, IID_IClassFactory, reinterpret_cast<void**>(&factory))))
    {
        std::printf("FAIL: DllGetClassObject\n");
        return 2;
    }
    if (FAILED(factory->CreateInstance(nullptr, elka::asio::CLSID_ElkaFxHostAsio, reinterpret_cast<void**>(&g_driver))))
    {
        std::printf("FAIL: CreateInstance\n");
        return 2;
    }
    factory->Release();

    if (g_driver->init(GetConsoleWindow()) != ASIOTrue)
    {
        std::printf("FAIL: init\n");
        return 2;
    }

    char name[64] {};
    g_driver->getDriverName(name);
    long inputs = 0;
    long outputs = 0;
    g_driver->getChannels(&inputs, &outputs);
    long minSize = 0, maxSize = 0, preferred = 0, granularity = 0;
    g_driver->getBufferSize(&minSize, &maxSize, &preferred, &granularity);
    std::printf("Driver: %s | %ld in / %ld out | buffer %ld-%ld (preferred %ld)\n", name, inputs, outputs, minSize, maxSize, preferred);

    for (long channel = 0; channel < outputs; channel += 8)
    {
        ASIOChannelInfo out {};
        out.channel = channel;
        out.isInput = ASIOFalse;
        ASIOChannelInfo in {};
        in.channel = channel;
        in.isInput = ASIOTrue;
        g_driver->getChannelInfo(&out);
        g_driver->getChannelInfo(&in);
        std::printf("  bank %2ld: driver out \"%s\"  /  driver in \"%s\"  (type %ld)\n", channel / 8, out.name, in.name, out.type);
    }

    if (g_driver->setSampleRate(options.rate) != ASE_OK)
        std::printf("WARN: sample rate %.0f not accepted\n", options.rate);
    g_rate = options.rate;
    g_channels = static_cast<int>(inputs);
    g_testChannel = options.channel;
    g_bufferSize = options.buffer;

    for (int channel = 0; channel < g_channels; ++channel)
    {
        g_buffers.push_back({ ASIOTrue, channel, { nullptr, nullptr } });
        g_buffers.push_back({ ASIOFalse, channel, { nullptr, nullptr } });
    }
    ASIOCallbacks callbacks { bufferSwitch, sampleRateChanged, asioMessage, bufferSwitchTimeInfo };
    if (g_driver->createBuffers(g_buffers.data(), static_cast<long>(g_buffers.size()), g_bufferSize, &callbacks) != ASE_OK)
    {
        std::printf("FAIL: createBuffers\n");
        return 2;
    }
    long inLatency = 0, outLatency = 0;
    g_driver->getLatencies(&inLatency, &outLatency);

    std::atomic<bool> stopFake { false };
    std::thread fake;
    if (options.fakeHost)
        fake = std::thread([&] { fakeHostLoop(stopFake); });

    const auto started = std::chrono::steady_clock::now();
    g_driver->start();
    std::this_thread::sleep_for(std::chrono::seconds(options.seconds));
    g_driver->stop();
    const double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();

    stopFake.store(true);
    if (fake.joinable())
        fake.join();

    const long long switches = g_switches.load();
    const double expected = elapsed * g_rate / static_cast<double>(g_bufferSize);
    const double rms = g_returnSamples.load() > 0 ? std::sqrt(g_returnSumSquares.load() / static_cast<double>(g_returnSamples.load())) : 0.0;
    std::printf("\nRan %.2f s at %.0f Hz / %ld spl: %lld buffer switches (expected ~%.0f, clock %.2f%%)\n",
                elapsed, g_rate, g_bufferSize, switches, expected, expected > 0 ? 100.0 * static_cast<double>(switches) / expected : 0.0);
    std::printf("Reported latency: %ld in / %ld out samples\n", inLatency, outLatency);
    std::printf("Return on FX Out %s: peak %.3f, rms %.3f (sent peak 0.500, rms 0.354)\n",
                ml::channelName(g_testChannel, false).c_str(), g_returnPeak.load(), rms);
    if (g_firstSoundSwitch.load() >= 0)
        std::printf("First audio returned after %lld buffer switches (%.1f ms)\n",
                    g_firstSoundSwitch.load(), 1000.0 * static_cast<double>(g_firstSoundSwitch.load() * g_bufferSize) / g_rate);
    std::printf("Loudest other FX Out channel: %.3f\n", g_otherChannelPeak.load());

    g_driver->disposeBuffers();
    g_driver->Release();
    FreeLibrary(module);
    CoUninitialize();

    const bool clockOk = expected > 0 && std::fabs(static_cast<double>(switches) / expected - 1.0) < 0.05;
    const bool audioOk = g_returnPeak.load() > 0.4 && g_returnPeak.load() < 0.6;
    if (!clockOk)
        std::printf("RESULT: FAIL (driver clock off)\n");
    else if (!audioOk)
        std::printf("RESULT: no tone returned. Is the FX Host link running (or use --fake-host)?\n");
    else
        std::printf("RESULT: PASS\n");
    return clockOk && audioOk ? 0 : 1;
}
