// WhatPluginLookup：在 DAW 里查备注和推荐的小窗口。
// 音频原样通过，零延迟。只读：不改数据库，也不加载其他插件。
// 只用 VST3 SDK（MIT）和 VSTGUI（BSD），不用其他插件框架。
#include "LookupView.h"

#include "public.sdk/source/main/pluginfactory.h"
#include "public.sdk/source/vst/vstsinglecomponenteffect.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/vsttypes.h"
#include "vstgui/plugin-bindings/vst3editor.h"

#include <cstring>

using namespace Steinberg;

namespace lookup
{
// 和以前用 JUCE 做的版本相同的 ID：工程里已经插着的 WhatPluginLookup 不会变成缺失。
static const FUID processorUid (0xABCDEF01, 0x9182FAEB, 0x50566C74, 0x50566C6B);

class LookupEffect final : public Vst::SingleComponentEffect, public VSTGUI::VST3EditorDelegate
{
public:
    static FUnknown* createInstance (void*) { return (Vst::IAudioProcessor*) new LookupEffect(); }

    tresult PLUGIN_API initialize (FUnknown* context) override
    {
        const auto result = SingleComponentEffect::initialize (context);
        if (result != kResultOk)
            return result;
        addAudioInput (STR16 ("Input"), Vst::SpeakerArr::kStereo);
        addAudioOutput (STR16 ("Output"), Vst::SpeakerArr::kStereo);
        return kResultOk;
    }

    /// 输入输出同样的声道布局都可以，不能为空。
    tresult PLUGIN_API setBusArrangements (Vst::SpeakerArrangement* inputs, int32 numIns,
                                           Vst::SpeakerArrangement* outputs, int32 numOuts) override
    {
        if (numIns != 1 || numOuts != 1 || inputs[0] != outputs[0] || inputs[0] == Vst::SpeakerArr::kEmpty)
            return kResultFalse;
        if (auto* in = FCast<Vst::AudioBus> (audioInputs.at (0)))
            in->setArrangement (inputs[0]);
        if (auto* out = FCast<Vst::AudioBus> (audioOutputs.at (0)))
            out->setArrangement (outputs[0]);
        return kResultTrue;
    }

    tresult PLUGIN_API canProcessSampleSize (int32 size) override
    {
        return size == Vst::kSample32 || size == Vst::kSample64 ? kResultTrue : kResultFalse;
    }

    uint32 PLUGIN_API getLatencySamples() override { return 0; }
    uint32 PLUGIN_API getTailSamples() override { return 0; }

    /// 原样通过：宿主给的输入输出可能是同一块缓冲区，也可能不是。
    tresult PLUGIN_API process (Vst::ProcessData& data) override
    {
        if (data.numInputs == 0 || data.numOutputs == 0 || data.numSamples <= 0)
            return kResultOk;
        auto& in = data.inputs[0];
        auto& out = data.outputs[0];
        const auto channels = std::min (in.numChannels, out.numChannels);
        const auto bytes = (size_t) data.numSamples * (data.symbolicSampleSize == Vst::kSample64 ? sizeof (double) : sizeof (float));
        for (int32 c = 0; c < channels; ++c)
        {
            void* src = data.symbolicSampleSize == Vst::kSample64 ? (void*) in.channelBuffers64[c] : (void*) in.channelBuffers32[c];
            void* dst = data.symbolicSampleSize == Vst::kSample64 ? (void*) out.channelBuffers64[c] : (void*) out.channelBuffers32[c];
            if (src != nullptr && dst != nullptr && src != dst)
                std::memcpy (dst, src, bytes);
        }
        for (int32 c = channels; c < out.numChannels; ++c)
        {
            void* dst = data.symbolicSampleSize == Vst::kSample64 ? (void*) out.channelBuffers64[c] : (void*) out.channelBuffers32[c];
            if (dst != nullptr)
                std::memset (dst, 0, bytes);
        }
        out.silenceFlags = in.silenceFlags;
        return kResultOk;
    }

    // 没有需要保存的状态。以前版本存在工程里的数据直接忽略。
    tresult PLUGIN_API setState (IBStream*) override { return kResultOk; }
    tresult PLUGIN_API getState (IBStream*) override { return kResultOk; }

    IPlugView* PLUGIN_API createView (FIDString name) override
    {
        if (FIDStringsEqual (name, Vst::ViewType::kEditor))
            return new VSTGUI::VST3Editor (this, "view", "lookup.uidesc");
        return nullptr;
    }

    VSTGUI::CView* createCustomView (VSTGUI::UTF8StringPtr name, const VSTGUI::UIAttributes&,
                                     const VSTGUI::IUIDescription*, VSTGUI::VST3Editor*) override
    {
        if (name != nullptr && std::strcmp (name, "LookupView") == 0)
            return new LookupView (VSTGUI::CRect (0, 0, 860, 580));
        return nullptr;
    }
};
} // namespace lookup

BEGIN_FACTORY_DEF ("WhatPlugin", "", "")

DEF_CLASS2 (INLINE_UID_FROM_FUID (lookup::processorUid),
            PClassInfo::kManyInstances,
            kVstAudioEffectClass,
            "WhatPluginLookup",
            0,
            "Fx|Tools",
            "1.0.0",
            kVstVersionString,
            lookup::LookupEffect::createInstance)

END_FACTORY
