#include "Json.h"

#include <cstdlib>

namespace lookup
{
namespace
{
const Json nullValue;
const std::vector<Json> emptyArray;
const std::map<std::string, Json> emptyObject;

void appendUtf8 (std::string& out, unsigned long cp)
{
    if (cp < 0x80)
        out += (char) cp;
    else if (cp < 0x800)
    {
        out += (char) (0xC0 | (cp >> 6));
        out += (char) (0x80 | (cp & 0x3F));
    }
    else if (cp < 0x10000)
    {
        out += (char) (0xE0 | (cp >> 12));
        out += (char) (0x80 | ((cp >> 6) & 0x3F));
        out += (char) (0x80 | (cp & 0x3F));
    }
    else
    {
        out += (char) (0xF0 | (cp >> 18));
        out += (char) (0x80 | ((cp >> 12) & 0x3F));
        out += (char) (0x80 | ((cp >> 6) & 0x3F));
        out += (char) (0x80 | (cp & 0x3F));
    }
}
} // namespace

class JsonParser
{
public:
    explicit JsonParser (const std::string& s) : text (s) {}

    bool parse (Json& out)
    {
        skipSpace();
        if (! value (out, 0))
            return false;
        skipSpace();
        return pos == text.size();
    }

private:
    const std::string& text;
    size_t pos = 0;

    void skipSpace()
    {
        while (pos < text.size() && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\n' || text[pos] == '\r'))
            ++pos;
        // 开头可能有 UTF-8 BOM。
        if (pos == 0 && text.compare (0, 3, "\xEF\xBB\xBF") == 0)
        {
            pos = 3;
            skipSpace();
        }
    }

    bool literal (const char* word)
    {
        const auto length = std::char_traits<char>::length (word);
        if (text.compare (pos, length, word) != 0)
            return false;
        pos += length;
        return true;
    }

    bool value (Json& out, int depth)
    {
        if (depth > 64 || pos >= text.size())
            return false;
        switch (text[pos])
        {
            case '{': return objectValue (out, depth);
            case '[': return arrayValue (out, depth);
            case '"': out.kind = Json::Type::String; return stringValue (out.text);
            case 't': out.kind = Json::Type::Bool; out.flag = true; return literal ("true");
            case 'f': out.kind = Json::Type::Bool; out.flag = false; return literal ("false");
            case 'n': out.kind = Json::Type::Null; return literal ("null");
            default: return numberValue (out);
        }
    }

    bool numberValue (Json& out)
    {
        const char* start = text.c_str() + pos;
        char* end = nullptr;
        const auto n = std::strtod (start, &end);
        if (end == start)
            return false;
        pos += (size_t) (end - start);
        out.kind = Json::Type::Number;
        out.number = n;
        return true;
    }

    bool hex4 (unsigned long& cp)
    {
        if (pos + 4 > text.size())
            return false;
        cp = 0;
        for (int i = 0; i < 4; ++i)
        {
            const char c = text[pos++];
            cp <<= 4;
            if (c >= '0' && c <= '9') cp |= (unsigned long) (c - '0');
            else if (c >= 'a' && c <= 'f') cp |= (unsigned long) (c - 'a' + 10);
            else if (c >= 'A' && c <= 'F') cp |= (unsigned long) (c - 'A' + 10);
            else return false;
        }
        return true;
    }

    bool stringValue (std::string& out)
    {
        ++pos; // "
        out.clear();
        while (pos < text.size())
        {
            const char c = text[pos++];
            if (c == '"')
                return true;
            if (c != '\\')
            {
                out += c;
                continue;
            }
            if (pos >= text.size())
                return false;
            switch (text[pos++])
            {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u':
                {
                    unsigned long cp = 0;
                    if (! hex4 (cp))
                        return false;
                    // 代理对：😀
                    if (cp >= 0xD800 && cp <= 0xDBFF && text.compare (pos, 2, "\\u") == 0)
                    {
                        pos += 2;
                        unsigned long low = 0;
                        if (! hex4 (low))
                            return false;
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                    }
                    appendUtf8 (out, cp);
                    break;
                }
                default: return false;
            }
        }
        return false;
    }

    bool arrayValue (Json& out, int depth)
    {
        ++pos; // [
        out.kind = Json::Type::Array;
        out.array = std::make_shared<std::vector<Json>>();
        skipSpace();
        if (pos < text.size() && text[pos] == ']')
        {
            ++pos;
            return true;
        }
        while (true)
        {
            skipSpace();
            Json item;
            if (! value (item, depth + 1))
                return false;
            out.array->push_back (std::move (item));
            skipSpace();
            if (pos >= text.size())
                return false;
            if (text[pos] == ',') { ++pos; continue; }
            if (text[pos] == ']') { ++pos; return true; }
            return false;
        }
    }

    bool objectValue (Json& out, int depth)
    {
        ++pos; // {
        out.kind = Json::Type::Object;
        out.object = std::make_shared<std::map<std::string, Json>>();
        skipSpace();
        if (pos < text.size() && text[pos] == '}')
        {
            ++pos;
            return true;
        }
        while (true)
        {
            skipSpace();
            std::string key;
            if (pos >= text.size() || text[pos] != '"' || ! stringValue (key))
                return false;
            skipSpace();
            if (pos >= text.size() || text[pos] != ':')
                return false;
            ++pos;
            skipSpace();
            Json item;
            if (! value (item, depth + 1))
                return false;
            (*out.object)[key] = std::move (item);
            skipSpace();
            if (pos >= text.size())
                return false;
            if (text[pos] == ',') { ++pos; continue; }
            if (text[pos] == '}') { ++pos; return true; }
            return false;
        }
    }
};

Json Json::parse (const std::string& text)
{
    Json result;
    JsonParser parser (text);
    if (! parser.parse (result))
        return {};
    return result;
}

const Json& Json::operator[] (const std::string& key) const
{
    if (kind != Type::Object)
        return nullValue;
    const auto it = object->find (key);
    return it != object->end() ? it->second : nullValue;
}

bool Json::has (const std::string& key) const { return kind == Type::Object && object->count (key) > 0; }

const std::vector<Json>& Json::items() const { return kind == Type::Array ? *array : emptyArray; }

const std::map<std::string, Json>& Json::members() const { return kind == Type::Object ? *object : emptyObject; }

std::string Json::str() const
{
    switch (kind)
    {
        case Type::String: return text;
        case Type::Bool: return flag ? "true" : "false";
        case Type::Number:
        {
            if (number == (double) (long long) number)
                return std::to_string ((long long) number);
            return std::to_string (number);
        }
        default: return {};
    }
}

double Json::num (double fallback) const { return kind == Type::Number ? number : fallback; }

bool Json::boolean (bool fallback) const { return kind == Type::Bool ? flag : fallback; }
} // namespace lookup
