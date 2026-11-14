// FX Host has no dry path: on the ASIO link (processInsertAsio) FX In never reaches
// FX Out unless a line is drawn. Checks: no routes -> all outputs silent; an In->Out
// line copies only that channel; nothing else leaks.
#include "engine/RealtimeEngine.h"
#include "plugins/RealtimePluginProcessor.h"
#include <array>
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <vector>

using namespace elka;

// Halves its input; writes the way the real plugin host does (clear or add per route).
class HalfGain final : public RealtimePluginProcessor
{
public:
    int calls = 0;
    bool process(AudioBufferView buffer, const PluginRoutingView& routing) noexcept override
    {
        ++calls;
        std::vector<float> result(static_cast<size_t>(buffer.samplesPerFrame), 0.0f);
        for (int r = 0; r < routing.inputRouteCount; ++r)
            for (int s = 0; s < buffer.samplesPerFrame; ++s) result[s] += routing.inputRoutes[r].source[s] * 0.5f;
        for (int r = 0; r < routing.outputRouteCount; ++r)
        {
            const auto& route = routing.outputRoutes[r];
            for (int s = 0; s < buffer.samplesPerFrame; ++s)
                route.destination[s] = (route.clearDestination ? 0.0f : route.destination[s]) + result[s];
        }
        return true;
    }
};

static int checks = 0;
static void check(bool ok, const char* message)
{
    ++checks;
    if (!ok) { std::cerr << "FAIL: " << message << '\n'; std::exit(1); }
}

int main()
{
    constexpr int frames = 64;
    constexpr int channels = 64;
    auto engine = std::make_unique<RealtimeEngine>();
    std::vector<std::vector<float>> in(channels, std::vector<float>(frames));
    std::vector<std::vector<float>> out(channels, std::vector<float>(frames, 9.0f));
    std::array<float*, channels> read {}, write {};
    for (int ch = 0; ch < channels; ++ch)
    {
        for (int i = 0; i < frames; ++i) in[ch][i] = 0.01f * (ch + 1);
        read[ch] = in[ch].data();
        write[ch] = out[ch].data();
    }
    AudioBufferView buffer { 48000, frames, channels, channels, read.data(), write.data() };

    auto silentExcept = [&](int keep) {
        for (int ch = 0; ch < channels; ++ch)
            for (int i = 0; i < frames; ++i)
                if (ch != keep && out[ch][i] != 0.0f) return false;
        return true;
    };

    engine->processInsertAsio(buffer);
    check(silentExcept(-1), "no lines: every FX Out is silent");

    // FX In A1 -> FX Out A4 (no VST).
    DirectAudioRoute route { 0, 3, 0, 100, false };
    engine->setPluginPassthroughRoutes(CallbackStreamKind::InputInsert, &route, 1);
    for (int pass = 0; pass < 4; ++pass) engine->processInsertAsio(buffer);
    check(silentExcept(3), "In A1 -> Out A4: only A4 carries audio");
    bool copied = true;
    for (int i = 0; i < frames; ++i) copied = copied && out[3][i] == in[0][i];
    check(copied, "Out A4 equals In A1");

    engine->setPluginPassthroughRoutes(CallbackStreamKind::InputInsert, nullptr, 0);
    for (int pass = 0; pass < 4; ++pass) engine->processInsertAsio(buffer);
    check(silentExcept(-1), "line removed: silent again");

    // FX In A1 -> VST (half gain) -> FX Out A2.
    HalfGain plugin;
    PluginInputRoute pluginIn { static_cast<int>(PluginRouteEndpointKind::VoiceMeeterChannel), 0, -1, -1, 0 };
    PluginOutputRoute pluginOut { static_cast<int>(PluginRouteEndpointKind::VoiceMeeterChannel), 0, 1, -1, -1 };
    engine->setPluginSlot(0, &plugin, CallbackStreamKind::InputInsert, &pluginIn, 1, &pluginOut, 1, true, false, true);
    for (int pass = 0; pass < 4; ++pass) engine->processInsertAsio(buffer);
    check(plugin.calls > 0, "VST is processed on the ASIO link");
    bool halved = true;
    for (int i = 0; i < frames; ++i) halved = halved && out[1][i] == in[0][i] * 0.5f;
    if (!halved) std::cerr << "Out A2[0]=" << out[1][0] << " expected " << in[0][0] * 0.5f << std::endl;
    check(halved, "In A1 -> VST -> Out A2 carries the processed audio");
    check(silentExcept(1), "VST route: only Out A2 carries audio");
    engine->clearPluginSlot(0);

    // Channel delay on the ASIO link delays what the cable delivers (FX In A1 -> FX Out A4, 1 ms).
    {
        engine->prepareDelayBuffers(48000);
        DirectAudioRoute line { 0, 3, 0, 100, false };
        engine->setPluginPassthroughRoutes(CallbackStreamKind::InputInsert, &line, 1);
        engine->setChannelEnabled(CallbackStreamKind::InputInsert, 3, true);
        engine->setChannelDelayMilliseconds(CallbackStreamKind::InputInsert, 3, 1);
        // An impulse on In A1 must arrive 48 samples (1 ms) later on Out A4.
        std::vector<float> heard;
        for (int block = 0; block < 4; ++block)
        {
            for (int i = 0; i < frames; ++i) in[0][i] = (block == 0 && i == 0) ? 1.0f : 0.0f;
            engine->processInsertAsio(buffer);
            heard.insert(heard.end(), out[3].begin(), out[3].end());
        }
        int peak = 0;
        for (int i = 1; i < static_cast<int>(heard.size()); ++i) if (std::abs(heard[i]) > std::abs(heard[peak])) peak = i;
        if (peak != 48) std::cerr << "delay peak at sample " << peak << std::endl;
        check(peak == 48 && std::abs(heard[peak]) > 0.5f, "1 ms channel delay delays the cable by 48 samples");
        engine->setChannelDelayMilliseconds(CallbackStreamKind::InputInsert, 3, 0);
        engine->setPluginPassthroughRoutes(CallbackStreamKind::InputInsert, nullptr, 0);
    }

    std::cout << "DryPathChecks: " << checks << " checks passed\n";
    return 0;
}
