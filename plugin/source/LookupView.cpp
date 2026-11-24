#include "LookupView.h"

#include "vstgui/lib/cdrawmethods.h"

#include "vstgui/lib/cvstguitimer.h"
#include "vstgui/lib/events.h"

#include <cwctype>

using namespace VSTGUI;

namespace lookup
{
namespace
{
#if defined(__APPLE__)
const char* const uiFont = "PingFang SC";
#else
const char* const uiFont = "Microsoft YaHei UI";
#endif

CColor hex (const Json& theme, const char* key, uint32_t fallback)
{
    const auto text = theme[key].str();
    if (text.size() == 7 && text[0] == '#')
    {
        const auto value = std::strtoul (text.c_str() + 1, nullptr, 16);
        return CColor ((uint8_t) (value >> 16), (uint8_t) (value >> 8), (uint8_t) value, 255);
    }
    return CColor ((uint8_t) (fallback >> 16), (uint8_t) (fallback >> 8), (uint8_t) fallback, 255);
}

std::wstring lower (const std::string& utf8)
{
    auto wide = widen (utf8);
    for (auto& c : wide)
        c = (wchar_t) std::towlower (c);
    return wide;
}

/// 按空格（含全角空格）分词，丢掉空的。
std::vector<std::wstring> words (const std::wstring& text)
{
    std::vector<std::wstring> result;
    std::wstring current;
    for (const auto c : text)
    {
        if (c == L' ' || c == L'\x3000' || c == L'\t')
        {
            if (! current.empty())
                result.push_back (std::move (current));
            current.clear();
        }
        else
            current += c;
    }
    if (! current.empty())
        result.push_back (std::move (current));
    return result;
}

std::string trimmed (const std::string& s)
{
    const auto first = s.find_first_not_of (" \t\r\n");
    if (first == std::string::npos)
        return {};
    return s.substr (first, s.find_last_not_of (" \t\r\n") - first + 1);
}
} // namespace

Theme Theme::fromJson (const Json& json)
{
    // 默认值和主程序的深色主题一致，读到主题前先用它。
    return {
        hex (json, "Bg1", 0x16181c),     hex (json, "Surface", 0x1f2228), hex (json, "Input", 0x14161a),
        hex (json, "Line", 0x30343b),    hex (json, "Text", 0xe8e9ec),    hex (json, "Muted", 0x8d929b),
        hex (json, "Accent", 0xd9a441),  hex (json, "AccentSoft", 0x3a3222), hex (json, "OnAccent", 0x111111),
    };
}

std::string Theme::toJson() const
{
    const std::pair<const char*, CColor> colors[] = {
        { "Bg1", background }, { "Surface", surface }, { "Input", input },
        { "Line", line },      { "Text", text },       { "Muted", muted },
        { "Accent", accent },  { "AccentSoft", accentSoft }, { "OnAccent", onAccent },
    };
    std::string json = "{";
    for (const auto& [key, color] : colors)
    {
        char value[8];
        std::snprintf (value, sizeof value, "#%02X%02X%02X", color.red, color.green, color.blue);
        if (json.size() > 1)
            json += ",";
        json += std::string ("\"") + key + "\":\"" + value + "\"";
    }
    return json + "}";
}

LookupView::LookupView (const CRect& size) : CViewContainer (size)
{
    font14 = makeOwned<CFontDesc> (uiFont, 14);
    font13 = makeOwned<CFontDesc> (uiFont, 13.5);
    font13Bold = makeOwned<CFontDesc> (uiFont, 13.5, kBoldFace);
    font12 = makeOwned<CFontDesc> (uiFont, 12);

    const CRect zero (0, 0, 10, 10);

    needBox = new CTextEdit (zero, this, needTag);
    needBox->setPlaceholderString ("输入插件名或需求，例如：低延迟的人声压缩。回车推荐");
    needBox->setImmediateTextChange (true);
    needBox->setFont (font14);
    needBox->setHoriAlign (kLeftText);
    needBox->setTextInset (CPoint (10, 0));
    needBox->setStyle (CParamDisplay::kRoundRectStyle);
    needBox->setRoundRectRadius (4);
    addView (needBox);

    recommendButton = new CTextButton (zero, this, recommendTag, "推荐");
    refreshButton = new CTextButton (zero, this, refreshTag, "刷新");
    pluginsTab = new CTextButton (zero, this, pluginsTabTag, "插件", CTextButton::kOnOffStyle);
    chainsTab = new CTextButton (zero, this, chainsTabTag, "插件链", CTextButton::kOnOffStyle);
    for (auto* button : { recommendButton, refreshButton, pluginsTab, chainsTab })
    {
        button->setFont (font13);
        button->setRoundRadius (4);
        button->setFrameWidth (1);
        addView (button);
    }
    pluginsTab->setValue (1.f);

    list = new CDataBrowser (zero, this, CScrollView::kVerticalScrollbar | CScrollView::kDontDrawFrame | CScrollView::kAutoHideScrollbars, 10);
    list->setTransparency (false); // CDataBrowser 默认透明，不画背景色
    addView (list);

    detailScroll = new CScrollView (zero, zero, CScrollView::kVerticalScrollbar | CScrollView::kDontDrawFrame | CScrollView::kAutoHideScrollbars, 10);
    detail = new CMultiLineTextLabel (zero);
    detail->setLineLayout (CMultiLineTextLabel::LineLayout::wrap);
    detail->setAutoHeight (true);
    detail->setFont (font13);
    detail->setHoriAlign (kLeftText);
    detail->setTextInset (CPoint (14, 12));
    detail->setStyle (CParamDisplay::kNoFrame);
    detail->setTransparency (true);
    detailScroll->addView (detail);
    addView (detailScroll);

    status = new CTextLabel (zero);
    status->setFont (font12);
    status->setHoriAlign (kLeftText);
    status->setStyle (CParamDisplay::kNoFrame);
    status->setTransparency (true);
    addView (status);

    // 先用上次保存的配色，读不到才用默认的深色；数据读完后再按数据库里的设置更新。
    applyTheme (Theme::fromJson (Json::parse (cachedTheme())));
    layout();
}

LookupView::~LookupView() noexcept { runner.stop(); }

bool LookupView::attached (CView* parent)
{
    if (! CViewContainer::attached (parent))
        return false;
    if (auto* frame = getFrame())
        frame->registerKeyboardHook (this);
    // 结果在后台线程里产生，界面线程定时取。
    timer = makeOwned<CVSTGUITimer> ([this] (CVSTGUITimer*) {
        if (auto output = runner.poll())
            onResult (*output);
    }, 50);
    layout();
    startDump();
    return true;
}

bool LookupView::removed (CView* parent)
{
    if (timer)
        timer->stop();
    timer = nullptr;
    runner.stop();
    if (auto* frame = getFrame())
        frame->unregisterKeyboardHook (this);
    return CViewContainer::removed (parent);
}

CMessageResult LookupView::notify (CBaseObject* sender, IdStringPtr message)
{
    if (sender == needBox && message == kMsgLooseFocus)
    {
        if (needBox->bWasReturnPressed)
        {
            needBox->bWasReturnPressed = false;
            startRecommend();
        }
        return kMessageNotified;
    }
    return CViewContainer::notify (sender, message);
}

void LookupView::setViewSize (const CRect& rect, bool invalid)
{
    CViewContainer::setViewSize (rect, invalid);
    layout();
}

void LookupView::layout()
{
    if (needBox == nullptr)
        return;
    CRect area (0, 0, getWidth(), getHeight());
    area.inset (14, 14);

    CRect statusRect = area;
    statusRect.top = area.bottom - 24;
    status->setViewSize (statusRect, true);
    status->setMouseableArea (statusRect);
    area.bottom = statusRect.top - 6;

    CRect top = area;
    top.bottom = top.top + 38;
    CRect refreshRect = top;
    refreshRect.left = top.right - 64;
    CRect recommendRect = top;
    recommendRect.right = refreshRect.left - 6;
    recommendRect.left = recommendRect.right - 72;
    CRect needRect = top;
    needRect.right = recommendRect.left - 6;
    for (auto [view, rect] : { std::pair<CView*, CRect> { refreshButton, refreshRect }, { recommendButton, recommendRect }, { needBox, needRect } })
    {
        view->setViewSize (rect, true);
        view->setMouseableArea (rect);
    }
    area.top = top.bottom + 10;

    CRect left = area;
    left.right = left.left + std::max<CCoord> (260, area.getWidth() * 2 / 5);
    CRect tabs = left;
    tabs.bottom = tabs.top + 30;
    CRect pluginsRect = tabs;
    pluginsRect.right = tabs.left + tabs.getWidth() / 2;
    CRect chainsRect = tabs;
    chainsRect.left = pluginsRect.right;
    pluginsTab->setViewSize (pluginsRect, true);
    pluginsTab->setMouseableArea (pluginsRect);
    chainsTab->setViewSize (chainsRect, true);
    chainsTab->setMouseableArea (chainsRect);
    left.top = tabs.bottom + 6;
    list->setViewSize (left, true);
    list->setMouseableArea (left);
    list->recalculateLayout (true);

    CRect right = area;
    right.left = left.right + 10;
    detailScroll->setViewSize (right, true);
    detailScroll->setMouseableArea (right);
    // 宽度变了要重新折行：重设一次文字。
    const auto text = detail->getText();
    detail->setViewSize (CRect (0, 0, right.getWidth() - 10, 10), true);
    setDetail (text.getString());
    invalid();
}

void LookupView::applyTheme (const Theme& t)
{
    theme = t;
    setBackgroundColor (t.background);

    needBox->setBackColor (t.input);
    needBox->setFrameColor (t.line);
    needBox->setFontColor (t.text);

    for (auto* button : { recommendButton, refreshButton, pluginsTab, chainsTab })
    {
        button->setGradient (CGradient::create (0, 1, t.surface, t.surface));
        button->setGradientHighlighted (CGradient::create (0, 1, t.accent, t.accent));
        button->setFrameColor (t.line);
        button->setFrameColorHighlighted (t.accent);
        button->setTextColor (t.text);
        button->setTextColorHighlighted (t.onAccent);
    }

    list->setBackgroundColor (t.surface);
    list->setScrollbarWidth (10);
    if (auto* bar = list->getVerticalScrollbar())
    {
        bar->setScrollerColor (t.line);
        bar->setBackgroundColor (t.surface);
        bar->setFrameColor (t.surface);
    }

    detailScroll->setBackgroundColor (t.surface);
    if (auto* bar = detailScroll->getVerticalScrollbar())
    {
        bar->setScrollerColor (t.line);
        bar->setBackgroundColor (t.surface);
        bar->setFrameColor (t.surface);
    }
    detail->setFontColor (t.text);
    status->setFontColor (t.muted);
    invalid();
}

void LookupView::setStatus (const std::string& text) { status->setText (text.c_str()); }

// ---------- 数据 ----------

bool LookupView::cliAvailable()
{
    cli = findCli();
    if (fileExists (cli))
        return true;
    setStatus ("找不到 WhatPlugin，请先打开一次 WhatPlugin 主程序，再点“刷新”。");
    return false;
}

static std::vector<std::string> withDatabase (std::vector<std::string> args)
{
    if (const auto db = testDatabase(); ! db.empty())
    {
        args.push_back ("--db");
        args.push_back (db);
    }
    return args;
}

void LookupView::startDump()
{
    if (! cliAvailable())
        return;
    setStatus ("正在读取…");
    runner.launch (cli, withDatabase ({ "lookup", "dump" }));
}

void LookupView::startRecommend()
{
    // 引号会破坏命令行参数，需求里也用不到。
    std::string need;
    for (const auto c : std::string (needBox->getText().getString()))
        if (c != '"' && c != '\\')
            need += c;
    need = trimmed (need);
    if (need.empty() || ! cliAvailable())
        return;
    showTab (false);
    setStatus ("正在推荐…");
    runner.launch (cli, withDatabase ({ "lookup", "recommend", need }));
}

void LookupView::onResult (const std::string& output)
{
    const auto json = Json::parse (output);
    if (! json["ok"].boolean())
    {
        const auto error = json["error"].str();
        setStatus (! error.empty() ? error : "读取失败，请确认 WhatPlugin 还在原来的位置。");
        return;
    }
    // 看内容判断，不记“上一次发了什么”：连续操作时上一个结果可能还在路上。
    if (json.has ("plugins"))
        loadDump (json);
    else
        loadRecommendation (json);
}

void LookupView::loadDump (const Json& json)
{
    applyTheme (Theme::fromJson (json["theme"]));
    saveCachedTheme (theme.toJson());

    allPlugins.clear();
    for (const auto& item : json["plugins"].items())
    {
        PluginEntry entry;
        entry.key = item["key"].str();
        entry.name = item["name"].str();
        entry.vendor = item["vendor"].str();
        entry.tags = item["tags"].str();
        entry.myTags = item["myTags"].str();
        entry.note = item["note"].str();
        entry.mix = item["mix"].str();
        entry.usage = item["usage"].str();
        entry.rating = (int) item["rating"].num();
        entry.search = lower (entry.name + " " + entry.vendor + " " + entry.tags + " " + entry.myTags + " " + entry.note + " " + entry.mix);
        allPlugins.push_back (std::move (entry));
    }

    chains.clear();
    for (const auto& item : json["chains"].items())
    {
        ChainEntry chain;
        chain.name = item["name"].str();
        std::string body;
        if (const auto note = item["note"].str(); ! note.empty())
            body += "注意事项\n" + note + "\n\n";
        int number = 1;
        for (const auto& step : item["steps"].items())
        {
            body += std::to_string (number++) + ". " + step["name"].str();
            if (const auto state = step["state"].str(); ! state.empty())
                body += "（" + state + "）";
            body += "\n";
            if (const auto note = step["note"].str(); ! note.empty())
                body += "    " + note + "\n";
        }
        chain.steps = (int) item["steps"].items().size();
        chain.body = body;
        chains.push_back (std::move (chain));
    }

    recommendMode = false;
    if (showingChains)
        showTab (true);
    else
        applyFilter();
}

void LookupView::loadRecommendation (const Json& json)
{
    shown.clear();
    for (const auto& item : json["items"].items())
    {
        const auto key = item["key"].str();
        for (const auto& plugin : allPlugins)
        {
            if (plugin.key == key)
            {
                auto entry = plugin;
                entry.reason = item["reason"].str();
                shown.push_back (std::move (entry));
                break;
            }
        }
    }
    recommendMode = true;
    setStatus (json["summary"].str());
    refreshList();
}

void LookupView::applyFilter()
{
    if (showingChains)
    {
        refreshList();
        return;
    }
    const auto query = words (lower (needBox->getText().getString()));
    shown.clear();
    for (const auto& plugin : allPlugins)
    {
        bool all = true;
        for (const auto& word : query)
            all = all && plugin.search.find (word) != std::wstring::npos;
        if (all)
            shown.push_back (plugin);
    }
    setStatus (std::to_string (shown.size()) + " 款效果器。双击复制插件名，按回车按需求推荐。只读，要修改请到主程序。");
    refreshList();
}

void LookupView::showTab (bool chainsShown)
{
    showingChains = chainsShown;
    pluginsTab->setValue (chainsShown ? 0.f : 1.f);
    chainsTab->setValue (chainsShown ? 1.f : 0.f);
    pluginsTab->invalid();
    chainsTab->invalid();
    if (chainsShown)
    {
        setStatus (chains.empty() ? "还没有插件链，可以在主程序里记录。" : std::to_string (chains.size()) + " 条插件链");
        refreshList();
    }
    else if (! recommendMode)
        applyFilter();
    else
        refreshList();
}

void LookupView::refreshList()
{
    list->unselectAll();
    list->recalculateLayout (false);
    list->invalid();
    if (dbGetNumRows (list) > 0)
        list->setSelectedRow (0, true);
    else
        setDetail ("");
}

void LookupView::setDetail (const std::string& text)
{
    // 自动高度只在文字变化时重算：先清空再设置，保证宽度变化后也会重新折行。
    detail->setText ("");
    detail->setText (text.c_str());
    const auto height = std::max<CCoord> (detail->getHeight(), detailScroll->getHeight());
    detailScroll->setContainerSize (CRect (0, 0, detail->getWidth(), height));
    detailScroll->resetScrollOffset();
    detailScroll->invalid();
}

void LookupView::showDetail (int32_t row)
{
    if (row < 0 || row >= dbGetNumRows (list))
    {
        setDetail ("");
        return;
    }
    std::string body;
    if (showingChains)
    {
        const auto& chain = chains[(size_t) row];
        body = chain.name + "\n\n" + (! chain.body.empty() ? chain.body : "这条链还没有插件。");
    }
    else
    {
        const auto& plugin = shown[(size_t) row];
        body = plugin.name + "\n" + plugin.vendor + "\n\n";
        if (! plugin.reason.empty())
            body += "推荐理由\n" + plugin.reason + "\n\n";
        body += "使用感受\n" + (! plugin.note.empty() ? plugin.note : std::string ("还没写")) + "\n\n";
        if (! plugin.mix.empty())
            body += "混音属性\n" + plugin.mix + "\n\n";
        if (! plugin.tags.empty())
            body += "功能分类\n" + plugin.tags + "\n\n";
        if (! plugin.myTags.empty())
            body += "我的标签\n" + plugin.myTags + "\n\n";
        body += "工程使用\n" + plugin.usage;
    }
    setDetail (body);
}

// ---------- 控件 ----------

void LookupView::valueChanged (CControl* control)
{
    switch (control->getTag())
    {
        case needTag:
            // 回车在 notify 里处理：文字是即时同步的，回车时文字没变，不会走到这里。
            recommendMode = false;
            applyFilter();
            break;
        case recommendTag:
            if (control->getValue() > 0.5f)
                startRecommend();
            break;
        case refreshTag:
            if (control->getValue() > 0.5f)
                startDump();
            break;
        case pluginsTabTag:
            showTab (false);
            break;
        case chainsTabTag:
            showTab (true);
            break;
        default: break;
    }
}

void LookupView::onKeyboardEvent (KeyboardEvent& event, CFrame* frame)
{
    if (event.type != EventType::KeyDown || event.virt != VirtualKey::Escape)
        return;
    if (frame->getFocusView() != needBox && needBox->getPlatformTextEdit() == nullptr)
        return;
    needBox->setText ("");
    if (auto edit = needBox->getPlatformTextEdit())
        edit->setText ("");
    recommendMode = false;
    applyFilter();
    event.consumed = true;
}

// ---------- 列表 ----------

int32_t LookupView::dbGetNumRows (CDataBrowser*) { return showingChains ? (int32_t) chains.size() : (int32_t) shown.size(); }

CCoord LookupView::dbGetCurrentColumnWidth (int32_t, CDataBrowser* browser)
{
    return browser->getWidth() - 10;
}

void LookupView::dbDrawCell (CDrawContext* context, const CRect& size, int32_t row, int32_t, int32_t flags, CDataBrowser*)
{
    if (row >= dbGetNumRows (list))
        return;
    if (flags & IDataBrowserDelegate::kRowSelected)
    {
        context->setFillColor (theme.accentSoft);
        context->drawRect (size, kDrawFilled);
    }
    context->setFillColor (theme.line);
    context->drawRect (CRect (size.left, size.bottom - 1, size.right, size.bottom), kDrawFilled);

    std::string title, sub;
    if (showingChains)
    {
        title = chains[(size_t) row].name;
        sub = std::to_string (chains[(size_t) row].steps) + " 步";
    }
    else
    {
        const auto& plugin = shown[(size_t) row];
        title = plugin.name;
        sub = ! plugin.reason.empty() ? plugin.reason : plugin.vendor + "  ·  " + plugin.usage;
        if (plugin.rating > 0)
        {
            std::string stars;
            for (int i = 0; i < plugin.rating; ++i)
                stars += "★";
            context->setFont (font12);
            context->setFontColor (theme.accent);
            context->drawString (stars.c_str(), CRect (size.right - 84, size.top + 8, size.right - 10, size.top + 26), kRightText);
        }
    }

    const CRect titleRect (size.left + 12, size.top + 7, size.right - 100, size.top + 27);
    context->setFont (font13Bold);
    context->setFontColor (theme.text);
    context->drawString (CDrawMethods::createTruncatedText (CDrawMethods::kTextTruncateTail, title.c_str(), font13Bold, titleRect.getWidth()).getPlatformString(),
                         titleRect, kLeftText);

    const CRect subRect (size.left + 12, size.top + 29, size.right - 12, size.top + 47);
    context->setFont (font12);
    context->setFontColor (theme.muted);
    context->drawString (CDrawMethods::createTruncatedText (CDrawMethods::kTextTruncateTail, sub.c_str(), font12, subRect.getWidth()).getPlatformString(),
                         subRect, kLeftText);
}

CMouseEventResult LookupView::dbOnMouseDown (const CPoint&, const CButtonState& buttons, int32_t row, int32_t, CDataBrowser*)
{
    if (buttons.isDoubleClick() && ! showingChains && row >= 0 && row < (int32_t) shown.size())
    {
        const auto& name = shown[(size_t) row].name;
        // 不用 VSTGUI 的剪贴板：它在 Windows 上按字节数算宽字符长度，中文名会出错。
        setStatus (copyToClipboard (name) ? "已复制插件名：" + name : std::string ("复制失败，剪贴板可能被其他程序占用。"));
    }
    return kMouseDownEventHandledButDontNeedMovedOrUpEvents;
}

void LookupView::dbSelectionChanged (CDataBrowser* browser) { showDetail (browser->getSelectedRow()); }
} // namespace lookup
