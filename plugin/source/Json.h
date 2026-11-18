// 读命令行工具输出的 JSON。只需要读，不需要写；格式由 WhatPlugin.Cli 生成，结构固定。
#pragma once

#include <map>
#include <memory>
#include <string>
#include <vector>

namespace lookup
{
class Json
{
public:
    enum class Type { Null, Bool, Number, String, Array, Object };

    /// 解析失败返回 Null。
    static Json parse (const std::string& text);

    Type type() const { return kind; }
    bool isObject() const { return kind == Type::Object; }

    /// 对象的成员；没有或不是对象时返回 Null。
    const Json& operator[] (const std::string& key) const;
    bool has (const std::string& key) const;

    const std::vector<Json>& items() const;
    const std::map<std::string, Json>& members() const;

    /// 字符串原样返回，数字和布尔转成文字，其余为空。
    std::string str() const;
    double num (double fallback = 0) const;
    bool boolean (bool fallback = false) const;

private:
    Type kind = Type::Null;
    bool flag = false;
    double number = 0;
    std::string text;
    std::shared_ptr<std::vector<Json>> array;
    std::shared_ptr<std::map<std::string, Json>> object;

    friend class JsonParser;
};
} // namespace lookup
