// FX Host has no dry path: on the ASIO link (processInsertAsio) FX In never reaches
// FX Out unless a line is drawn. Checks: no routes -> all outputs silent; an In->Out
// line copies only that channel; nothing else leaks.
#include "engine/RealtimeEngine.h"
#include <array>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <vector>

using namespace elka;

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

    std::cout << "DryPathChecks: " << checks << " checks passed\n";
    return 0;
}
