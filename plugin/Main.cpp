// WhatPluginLookup：在 DAW 里查备注和推荐的小窗口。
// 音频原样通过，零延迟。只读：不改数据库，也不加载其他插件。
// 数据来自 WhatPlugin.Cli 的 lookup 命令，算法和主程序是同一份。
#include <JuceHeader.h>
#include <utility>

namespace
{
/// juce::String 默认把字符串字面量当 ASCII，中文要明确按 UTF-8 转换。
inline juce::String operator""_u (const char* text, size_t length) { return juce::String::fromUTF8 (text, (int) length); }

const juce::String uiFont { "Microsoft YaHei UI" };

juce::Colour hexColour (const juce::var& theme, const char* key, juce::uint32 fallback)
{
    const auto text = theme.getProperty (key, {}).toString();
    if (text.startsWith ("#") && text.length() == 7)
        return juce::Colour::fromString ("ff" + text.substring (1));
    return juce::Colour (fallback);
}

juce::Font makeFont (float size, bool bold = false)
{
    return juce::Font (juce::FontOptions (uiFont, size, bold ? juce::Font::bold : juce::Font::plain));
}

/// 找命令行工具：主程序启动时把位置写在这个文件里。环境变量只用于测试。
juce::File findCli()
{
    const auto fromEnv = juce::SystemStats::getEnvironmentVariable ("WHATPLUGIN_LOOKUP_CLI", {});
    if (fromEnv.isNotEmpty())
        return juce::File (fromEnv);
    const auto pointer = juce::File::getSpecialLocation (juce::File::windowsLocalAppData)
                             .getChildFile ("WhatPlugin").getChildFile ("cli-path.txt");
    return juce::File (pointer.loadFileAsString().trim());
}

/// 后台运行命令行工具。析构前一定会停下，否则插件被卸载时线程还在跑会让 DAW 崩溃。
class Runner final : private juce::Thread
{
public:
    explicit Runner (std::function<void()> finishedCallback)
        : juce::Thread ("WhatPluginLookup"), finished (std::move (finishedCallback)) {}

    ~Runner() override { stopThread (3000); }

    void launch (juce::StringArray commandToRun)
    {
        stopThread (3000);
        command = std::move (commandToRun);
        startThread();
    }

    juce::String takeOutput()
    {
        const juce::ScopedLock lock (outputLock);
        return std::exchange (output, {});
    }

private:
    void run() override
    {
        juce::ChildProcess child;
        juce::MemoryBlock bytes;
        juce::String result;
        if (child.start (command, juce::ChildProcess::wantStdOut))
        {
            const auto deadline = juce::Time::getMillisecondCounter() + 30000;
            char buffer[8192];
            while (! threadShouldExit() && juce::Time::getMillisecondCounter() < deadline)
            {
                const auto got = child.readProcessOutput (buffer, sizeof (buffer));
                if (got > 0)
                    bytes.append (buffer, (size_t) got);
                else if (! child.isRunning())
                    break;
                else
                    wait (10);
            }
            if (child.isRunning())
                child.kill();
            result = juce::String::fromUTF8 (static_cast<const char*> (bytes.getData()), (int) bytes.getSize());
        }
        if (threadShouldExit())
            return;
        {
            const juce::ScopedLock lock (outputLock);
            output = result;
        }
        finished();
    }

    std::function<void()> finished;
    juce::StringArray command;
    juce::CriticalSection outputLock;
    juce::String output;
};

struct PluginEntry
{
    juce::String key, name, vendor, tags, myTags, note, mix, usage, reason, search;
    int rating = 0;
};

struct ChainEntry
{
    juce::String name, note, body;
    int steps = 0;
};

class LookupProcessor final : public juce::AudioProcessor
{
public:
    LookupProcessor()
        : AudioProcessor (BusesProperties()
                              .withInput ("Input", juce::AudioChannelSet::stereo(), true)
                              .withOutput ("Output", juce::AudioChannelSet::stereo(), true)) {}

    bool isBusesLayoutSupported (const BusesLayout& layouts) const override
    {
        const auto out = layouts.getMainOutputChannelSet();
        return out == layouts.getMainInputChannelSet() && ! out.isDisabled();
    }

    void prepareToPlay (double, int) override {}
    void releaseResources() override {}
    // 输入输出是同一块缓冲区，什么都不做就是原样通过。
    void processBlock (juce::AudioBuffer<float>&, juce::MidiBuffer&) override {}
    juce::AudioProcessorEditor* createEditor() override;
    bool hasEditor() const override { return true; }
    const juce::String getName() const override { return "WhatPluginLookup"; }
    bool acceptsMidi() const override { return false; }
    bool producesMidi() const override { return false; }
    double getTailLengthSeconds() const override { return 0.0; }
    int getNumPrograms() override { return 1; }
    int getCurrentProgram() override { return 0; }
    void setCurrentProgram (int) override {}
    const juce::String getProgramName (int) override { return {}; }
    void changeProgramName (int, const juce::String&) override {}
    void getStateInformation (juce::MemoryBlock&) override {}
    void setStateInformation (const void*, int) override {}
};

class LookupEditor final : public juce::AudioProcessorEditor,
                           private juce::AsyncUpdater,
                           private juce::ListBoxModel
{
public:
    explicit LookupEditor (LookupProcessor& processor)
        : AudioProcessorEditor (processor),
          runner ([this] { triggerAsyncUpdate(); })
    {
        look.setDefaultSansSerifTypefaceName (uiFont);
        setLookAndFeel (&look);

        needBox.setTextToShowWhenEmpty ("输入插件名或需求，例如：低延迟的人声压缩。回车推荐"_u, juce::Colours::grey);
        needBox.setFont (makeFont (14.0f));
        needBox.setIndents (10, 0);
        needBox.setJustification (juce::Justification::centredLeft);
        needBox.setSelectAllWhenFocused (false);
        needBox.onTextChange = [this] { recommendMode = false; applyFilter(); };
        needBox.onReturnKey = [this] { startRecommend(); };
        needBox.onEscapeKey = [this] { needBox.clear(); };
        needBox.setTitle ("搜索或描述需求"_u);
        addAndMakeVisible (needBox);

        recommendButton.onClick = [this] { startRecommend(); };
        addAndMakeVisible (recommendButton);
        refreshButton.onClick = [this] { startDump(); };
        addAndMakeVisible (refreshButton);

        pluginsTab.setRadioGroupId (1);
        chainsTab.setRadioGroupId (1);
        pluginsTab.setClickingTogglesState (true);
        chainsTab.setClickingTogglesState (true);
        pluginsTab.setToggleState (true, juce::dontSendNotification);
        // 同组按钮被关掉时也会触发 onClick，只响应被选中的那个。
        pluginsTab.onClick = [this] { if (pluginsTab.getToggleState()) showTab (false); };
        chainsTab.onClick = [this] { if (chainsTab.getToggleState()) showTab (true); };
        addAndMakeVisible (pluginsTab);
        addAndMakeVisible (chainsTab);

        list.setModel (this);
        list.setRowHeight (54);
        list.setTitle ("结果"_u);
        addAndMakeVisible (list);

        detail.setMultiLine (true);
        detail.setReadOnly (true);
        detail.setCaretVisible (false);
        detail.setScrollbarsShown (true);
        detail.setFont (makeFont (13.5f));
        detail.setIndents (14, 12);
        detail.setTitle ("详情"_u);
        addAndMakeVisible (detail);

        status.setFont (makeFont (12.0f));
        addAndMakeVisible (status);

        applyTheme ({});
        setResizable (true, true);
        setResizeLimits (620, 420, 1600, 1200);
        setSize (860, 580);
        startDump();
    }

    ~LookupEditor() override
    {
        cancelPendingUpdate();
        setLookAndFeel (nullptr);
    }

    void paint (juce::Graphics& g) override { g.fillAll (background); }

    void resized() override
    {
        auto area = getLocalBounds().reduced (14);
        status.setBounds (area.removeFromBottom (24));
        area.removeFromBottom (6);

        auto top = area.removeFromTop (38);
        refreshButton.setBounds (top.removeFromRight (64));
        top.removeFromRight (6);
        recommendButton.setBounds (top.removeFromRight (72));
        top.removeFromRight (6);
        needBox.setBounds (top);
        area.removeFromTop (10);

        auto left = area.removeFromLeft (juce::jmax (260, area.getWidth() * 2 / 5));
        auto tabs = left.removeFromTop (30);
        pluginsTab.setBounds (tabs.removeFromLeft (tabs.getWidth() / 2).reduced (0, 0));
        chainsTab.setBounds (tabs);
        left.removeFromTop (6);
        list.setBounds (left);
        area.removeFromLeft (10);
        detail.setBounds (area);
    }

private:
    // ---------- 数据 ----------

    juce::StringArray cliCommand (const juce::StringArray& args) const
    {
        juce::StringArray command { findCli().getFullPathName() };
        command.addArray (args);
        const auto db = juce::SystemStats::getEnvironmentVariable ("WHATPLUGIN_LOOKUP_DB", {});
        if (db.isNotEmpty())
        {
            command.add ("--db");
            command.add (db);
        }
        return command;
    }

    bool cliAvailable()
    {
        if (findCli().existsAsFile())
            return true;
        status.setText ("找不到 WhatPlugin，请先打开一次 WhatPlugin 主程序，再点“刷新”。"_u, juce::dontSendNotification);
        return false;
    }

    void startDump()
    {
        if (! cliAvailable())
            return;
        status.setText ("正在读取…"_u, juce::dontSendNotification);
        runner.launch (cliCommand ({ "lookup", "dump" }));
    }

    void startRecommend()
    {
        // 引号会破坏命令行参数，需求里也用不到。
        const auto need = needBox.getText().removeCharacters ("\"\\").trim();
        if (need.isEmpty() || ! cliAvailable())
            return;
        showTab (false);
        status.setText ("正在推荐…"_u, juce::dontSendNotification);
        runner.launch (cliCommand ({ "lookup", "recommend", need }));
    }

    void handleAsyncUpdate() override
    {
        const auto output = runner.takeOutput();
        const auto json = juce::JSON::parse (output);
        if (! json.getProperty ("ok", false))
        {
            const auto error = json.getProperty ("error", {}).toString();
            status.setText (error.isNotEmpty() ? error : "读取失败，请确认 WhatPlugin 还在原来的位置。"_u, juce::dontSendNotification);
            return;
        }
        // 看内容判断，不记“上一次发了什么”：连续操作时上一个结果可能还在路上。
        if (json.hasProperty ("plugins"))
            loadDump (json);
        else
            loadRecommendation (json);
    }

    void loadDump (const juce::var& json)
    {
        applyTheme (json.getProperty ("theme", {}));
        allPlugins.clear();
        if (auto* items = json.getProperty ("plugins", {}).getArray())
        {
            for (const auto& item : *items)
            {
                PluginEntry entry;
                entry.key = item.getProperty ("key", {}).toString();
                entry.name = item.getProperty ("name", {}).toString();
                entry.vendor = item.getProperty ("vendor", {}).toString();
                entry.tags = item.getProperty ("tags", {}).toString();
                entry.myTags = item.getProperty ("myTags", {}).toString();
                entry.note = item.getProperty ("note", {}).toString();
                entry.mix = item.getProperty ("mix", {}).toString();
                entry.usage = item.getProperty ("usage", {}).toString();
                entry.rating = (int) item.getProperty ("rating", 0);
                entry.search = (entry.name + " " + entry.vendor + " " + entry.tags + " " + entry.myTags + " " + entry.note + " " + entry.mix).toLowerCase();
                allPlugins.push_back (std::move (entry));
            }
        }

        chains.clear();
        if (auto* items = json.getProperty ("chains", {}).getArray())
        {
            for (const auto& item : *items)
            {
                ChainEntry chain;
                chain.name = item.getProperty ("name", {}).toString();
                chain.note = item.getProperty ("note", {}).toString();
                juce::String body;
                if (chain.note.isNotEmpty())
                    body << "注意事项\n"_u << chain.note << "\n\n";
                if (auto* steps = item.getProperty ("steps", {}).getArray())
                {
                    chain.steps = steps->size();
                    int number = 1;
                    for (const auto& step : *steps)
                    {
                        body << number++ << ". " << step.getProperty ("name", {}).toString();
                        const auto state = step.getProperty ("state", {}).toString();
                        if (state.isNotEmpty())
                            body << "（"_u << state << "）"_u;
                        body << "\n";
                        const auto note = step.getProperty ("note", {}).toString();
                        if (note.isNotEmpty())
                            body << "    " << note << "\n";
                    }
                }
                chain.body = body;
                chains.push_back (std::move (chain));
            }
        }

        recommendMode = false;
        applyFilter();
    }

    void loadRecommendation (const juce::var& json)
    {
        shown.clear();
        if (auto* items = json.getProperty ("items", {}).getArray())
        {
            for (const auto& item : *items)
            {
                const auto key = item.getProperty ("key", {}).toString();
                for (const auto& plugin : allPlugins)
                {
                    if (plugin.key == key)
                    {
                        auto entry = plugin;
                        entry.reason = item.getProperty ("reason", {}).toString();
                        shown.push_back (std::move (entry));
                        break;
                    }
                }
            }
        }
        recommendMode = true;
        status.setText (json.getProperty ("summary", {}).toString(), juce::dontSendNotification);
        refreshList();
    }

    void applyFilter()
    {
        if (showingChains)
        {
            refreshList();
            return;
        }
        juce::StringArray words;
        words.addTokens (needBox.getText().toLowerCase(), " ", {});
        words.removeEmptyStrings();

        shown.clear();
        for (const auto& plugin : allPlugins)
        {
            bool all = true;
            for (const auto& word : words)
                all = all && plugin.search.contains (word);
            if (all)
                shown.push_back (plugin);
        }
        status.setText (juce::String (shown.size()) + " 款效果器。双击复制插件名，按回车按需求推荐。只读，要修改请到主程序。"_u, juce::dontSendNotification);
        refreshList();
    }

    void showTab (bool chainsTabShown)
    {
        showingChains = chainsTabShown;
        (chainsTabShown ? chainsTab : pluginsTab).setToggleState (true, juce::dontSendNotification);
        if (chainsTabShown)
        {
            status.setText (chains.empty() ? "还没有插件链，可以在主程序里记录。"_u : juce::String ((int) chains.size()) + " 条插件链"_u, juce::dontSendNotification);
            refreshList();
        }
        else if (! recommendMode)
        {
            applyFilter();
        }
        else
        {
            refreshList();
        }
    }

    void refreshList()
    {
        list.updateContent();
        list.deselectAllRows();
        list.repaint();
        if (getNumRows() > 0)
            list.selectRow (0);
        else
            detail.clear();
    }

    // ---------- 列表 ----------

    int getNumRows() override { return showingChains ? (int) chains.size() : (int) shown.size(); }

    void paintListBoxItem (int row, juce::Graphics& g, int width, int height, bool selected) override
    {
        if (row >= getNumRows())
            return;
        if (selected)
            g.fillAll (accentSoft);
        g.setColour (line);
        g.fillRect (0, height - 1, width, 1);

        juce::String title, sub;
        if (showingChains)
        {
            title = chains[(size_t) row].name;
            sub = juce::String (chains[(size_t) row].steps) + " 步"_u;
        }
        else
        {
            const auto& plugin = shown[(size_t) row];
            title = plugin.name;
            sub = plugin.reason.isNotEmpty() ? plugin.reason : plugin.vendor + "  ·  "_u + plugin.usage;
            if (plugin.rating > 0)
            {
                g.setColour (accent);
                g.setFont (makeFont (12.0f));
                juce::String stars;
                for (int i = 0; i < plugin.rating; ++i)
                    stars << juce::String::charToString ((juce::juce_wchar) 0x2605);
                g.drawText (stars, width - 84, 8, 74, 18, juce::Justification::centredRight);
            }
        }
        g.setColour (text);
        g.setFont (makeFont (13.5f, true));
        g.drawText (title, 12, 7, width - 100, 20, juce::Justification::centredLeft);
        g.setColour (muted);
        g.setFont (makeFont (12.0f));
        g.drawText (sub, 12, 29, width - 24, 18, juce::Justification::centredLeft);
    }

    juce::String getNameForRow (int row) override
    {
        if (row >= getNumRows())
            return {};
        return showingChains ? chains[(size_t) row].name : shown[(size_t) row].name;
    }

    void listBoxItemDoubleClicked (int row, const juce::MouseEvent&) override
    {
        if (showingChains || row < 0 || row >= getNumRows())
            return;
        const auto& name = shown[(size_t) row].name;
        juce::SystemClipboard::copyTextToClipboard (name);
        status.setText ("已复制插件名："_u + name, juce::dontSendNotification);
    }

    void selectedRowsChanged (int row) override
    {
        detail.clear();
        if (row < 0 || row >= getNumRows())
            return;

        juce::String body;
        if (showingChains)
        {
            const auto& chain = chains[(size_t) row];
            body << chain.name << "\n\n" << (chain.body.isNotEmpty() ? chain.body : "这条链还没有插件。"_u);
        }
        else
        {
            const auto& plugin = shown[(size_t) row];
            body << plugin.name << "\n" << plugin.vendor << "\n\n";
            if (plugin.reason.isNotEmpty())
                body << "推荐理由\n"_u << plugin.reason << "\n\n";
            body << "使用感受\n"_u << (plugin.note.isNotEmpty() ? plugin.note : "还没写"_u) << "\n\n";
            if (plugin.mix.isNotEmpty())
                body << "混音属性\n"_u << plugin.mix << "\n\n";
            if (plugin.tags.isNotEmpty())
                body << "功能分类\n"_u << plugin.tags << "\n\n";
            if (plugin.myTags.isNotEmpty())
                body << "我的标签\n"_u << plugin.myTags << "\n\n";
            body << "工程使用\n"_u << plugin.usage;
        }
        detail.setText (body, false);
        detail.moveCaretToTop (false);
    }

    // ---------- 外观 ----------

    void applyTheme (const juce::var& theme)
    {
        background = hexColour (theme, "Bg1", 0xff16181c);
        surface = hexColour (theme, "Surface", 0xff1f2228);
        input = hexColour (theme, "Input", 0xff14161a);
        line = hexColour (theme, "Line", 0xff30343b);
        text = hexColour (theme, "Text", 0xffe8e9ec);
        muted = hexColour (theme, "Muted", 0xff8d929b);
        accent = hexColour (theme, "Accent", 0xffd9a441);
        accentSoft = hexColour (theme, "AccentSoft", 0xff3a3222);
        const auto onAccent = hexColour (theme, "OnAccent", 0xff111111);

        look.setColour (juce::ResizableWindow::backgroundColourId, background);
        look.setColour (juce::ListBox::backgroundColourId, surface);
        look.setColour (juce::ListBox::outlineColourId, line);
        look.setColour (juce::TextEditor::backgroundColourId, input);
        look.setColour (juce::TextEditor::textColourId, text);
        look.setColour (juce::TextEditor::outlineColourId, line);
        look.setColour (juce::TextEditor::focusedOutlineColourId, accent);
        look.setColour (juce::TextEditor::highlightColourId, accentSoft);
        look.setColour (juce::TextEditor::highlightedTextColourId, text);
        look.setColour (juce::CaretComponent::caretColourId, text);
        look.setColour (juce::TextButton::buttonColourId, surface);
        look.setColour (juce::TextButton::buttonOnColourId, accent);
        look.setColour (juce::TextButton::textColourOffId, text);
        look.setColour (juce::TextButton::textColourOnId, onAccent);
        look.setColour (juce::ComboBox::outlineColourId, line);
        look.setColour (juce::Label::textColourId, muted);
        look.setColour (juce::ScrollBar::thumbColourId, line);

        status.setColour (juce::Label::textColourId, muted);
        needBox.setTextToShowWhenEmpty (needBox.getTextToShowWhenEmpty(), muted);
        detail.setColour (juce::TextEditor::backgroundColourId, surface);
        detail.setColour (juce::TextEditor::outlineColourId, line);
        detail.setColour (juce::TextEditor::textColourId, text);
        needBox.applyColourToAllText (text, true);
        detail.applyColourToAllText (text, true);
        sendLookAndFeelChange();
        repaint();
    }

    juce::LookAndFeel_V4 look;
    juce::Colour background, surface, input, line, text, muted, accent, accentSoft;

    juce::TextEditor needBox, detail;
    juce::TextButton recommendButton { "推荐"_u }, refreshButton { "刷新"_u };
    juce::TextButton pluginsTab { "插件"_u }, chainsTab { "插件链"_u };
    juce::ListBox list;
    juce::Label status;

    std::vector<PluginEntry> allPlugins, shown;
    std::vector<ChainEntry> chains;
    bool showingChains = false, recommendMode = false;
    // 放在最后：最先析构，线程停了之后别的成员才释放。
    Runner runner;

    JUCE_DECLARE_NON_COPYABLE_WITH_LEAK_DETECTOR (LookupEditor)
};

juce::AudioProcessorEditor* LookupProcessor::createEditor() { return new LookupEditor (*this); }
} // namespace

juce::AudioProcessor* JUCE_CALLTYPE createPluginFilter() { return new LookupProcessor(); }
