// 插件窗口的全部界面：搜索框、推荐和刷新按钮、插件 / 插件链列表、详情、状态栏。
// 数据来自 WhatPlugin.Cli 的 lookup 命令，算法和主程序是同一份；这里只负责显示，不改数据库。
#pragma once

#include "Json.h"
#include "Runner.h"

#include "vstgui/vstgui.h"

#include <string>
#include <vector>

namespace lookup
{
struct PluginEntry
{
    std::string key, name, vendor, tags, myTags, note, mix, usage, reason;
    std::wstring search; // 小写，用来筛选
    int rating = 0;
};

struct ChainEntry
{
    std::string name, body;
    int steps = 0;
};

struct Theme
{
    VSTGUI::CColor background, surface, input, line, text, muted, accent, accentSoft, onAccent;
    static Theme fromJson (const Json& theme);
    std::string toJson() const;
};

class LookupView final : public VSTGUI::CViewContainer,
                         public VSTGUI::IControlListener,
                         public VSTGUI::DataBrowserDelegateAdapter,
                         public VSTGUI::IKeyboardHook
{
public:
    explicit LookupView (const VSTGUI::CRect& size);
    ~LookupView() noexcept override;

    // CViewContainer
    void setViewSize (const VSTGUI::CRect& rect, bool invalid = true) override;
    bool attached (VSTGUI::CView* parent) override;
    bool removed (VSTGUI::CView* parent) override;
    /// 搜索框失焦时收到：回车引起的失焦就推荐。
    VSTGUI::CMessageResult notify (VSTGUI::CBaseObject* sender, VSTGUI::IdStringPtr message) override;

    // IControlListener
    void valueChanged (VSTGUI::CControl* control) override;

    // IKeyboardHook：搜索框里按 Esc 清空。
    void onKeyboardEvent (VSTGUI::KeyboardEvent& event, VSTGUI::CFrame* frame) override;

    // 列表
    int32_t dbGetNumRows (VSTGUI::CDataBrowser*) override;
    int32_t dbGetNumColumns (VSTGUI::CDataBrowser*) override { return 1; }
    VSTGUI::CCoord dbGetRowHeight (VSTGUI::CDataBrowser*) override { return 54; }
    VSTGUI::CCoord dbGetCurrentColumnWidth (int32_t, VSTGUI::CDataBrowser* browser) override;
    void dbDrawCell (VSTGUI::CDrawContext* context, const VSTGUI::CRect& size, int32_t row, int32_t column,
                     int32_t flags, VSTGUI::CDataBrowser* browser) override;
    VSTGUI::CMouseEventResult dbOnMouseDown (const VSTGUI::CPoint& where, const VSTGUI::CButtonState& buttons,
                                             int32_t row, int32_t column, VSTGUI::CDataBrowser* browser) override;
    void dbSelectionChanged (VSTGUI::CDataBrowser* browser) override;

private:
    enum Tag { needTag = 1, recommendTag, refreshTag, pluginsTabTag, chainsTabTag };

    void layout();
    void applyTheme (const Theme& theme);
    void setStatus (const std::string& text);

    bool cliAvailable();
    void startDump();
    void startRecommend();
    void onResult (const std::string& output);
    void loadDump (const Json& json);
    void loadRecommendation (const Json& json);
    void applyFilter();
    void showTab (bool chains);
    void refreshList();
    void showDetail (int32_t row);
    void setDetail (const std::string& text);

    Runner runner;
    VSTGUI::SharedPointer<VSTGUI::CVSTGUITimer> timer;
    std::string cli;

    VSTGUI::CTextEdit* needBox = nullptr;
    VSTGUI::CTextButton* recommendButton = nullptr;
    VSTGUI::CTextButton* refreshButton = nullptr;
    VSTGUI::CTextButton* pluginsTab = nullptr;
    VSTGUI::CTextButton* chainsTab = nullptr;
    VSTGUI::CDataBrowser* list = nullptr;
    VSTGUI::CScrollView* detailScroll = nullptr;
    VSTGUI::CMultiLineTextLabel* detail = nullptr;
    VSTGUI::CTextLabel* status = nullptr;

    VSTGUI::SharedPointer<VSTGUI::CFontDesc> font14, font13, font13Bold, font12;
    Theme theme;

    std::vector<PluginEntry> allPlugins, shown;
    std::vector<ChainEntry> chains;
    bool showingChains = false, recommendMode = false;
};
} // namespace lookup
